using System.Reflection;
using HarmonyLib;
using Il2CppProject.Code.Core.Services;
using Il2CppProject.Code.Core.Utils.DataDefinition;
using Il2CppProject.Code.Gameplay.AI.Employee;
using Il2CppProject.Code.Gameplay.Definitions;
using Il2CppProject.Code.Gameplay.Interactions.Shelfs;
using Il2CppProject.Code.Gameplay.Player.Controllers;
using Il2CppProject.Code.Gameplay.Player.Products;
using Il2CppProject.Code.Gameplay.Services;
using Il2CppProject.Code.Gameplay.UI.Menu;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;
using UnityEngine;

[assembly: MelonInfo(typeof(AnimeShopShelfFilters.ShelfFiltersMod), "Anime Shop: Shelf Filters", "0.10.3", "WolfMods")]

namespace AnimeShopShelfFilters;

public sealed class ShelfFiltersMod : MelonMod
{
    private const string WolfModId = "wolfmod.shelf_filters";
    private const float TargetDistance = 8f;
    private const int MaxWolfMenuRegistrationAttempts = 4;
    private const float WolfMenuRetryDelay = 1.5f;
    private const float DependencyErrorDisplaySeconds = 30f;
    // New furniture can appear at runtime, so keep a slow safety refresh. Rule
    // changes and scene changes refresh immediately and do not wait for this.
    private const float IndicatorRefreshSeconds = 30f;
    private ProductPricePlace? _target;
    private ProductPricePlace? _windowTarget;
    private Il2CppSystem.IDisposable? _gameplayInputBlock;
    private readonly List<ProductOption> _products = new();
    private MenuPage _page;
    private bool _windowOpen;
    private bool _previousCursorVisible;
    private CursorLockMode _previousCursorLockMode;
    private int _productPage;
    private string _catalogMessage = string.Empty;
    private bool _guiErrorReported;
    private GUIStyle? _windowStyle;
    private GUIStyle? _titleStyle;
    private GUIStyle? _statusStyle;
    private GUIStyle? _productCardStyle;
    private GUIStyle? _toastStyle;
    private GUIStyle? _dependencyErrorStyle;
    private string _toast = string.Empty;
    private float _toastUntil;
    private float _nextIndicatorRefreshAt;
    private float _nextWolfMenuRegistrationAttempt;
    private float _dependencyErrorUntil;
    private int _wolfMenuRegistrationAttempts;
    private bool _runtimeInitialized;
    private bool _dependencyFailed;
    private bool _dependencyNotificationStarted;
    private bool _waitingForMenuKey;
    private MenuView? _mainMenuView;
    private float _nextMainMenuLookup;
    private static bool _blockGameEscape;
    private static int _suppressEscapeThroughFrame = -1;
    internal static bool FeatureEnabled { get; private set; }

    internal static bool ShouldBlockGameEscape =>
        _blockGameEscape || Time.frameCount <= _suppressEscapeThroughFrame;

    public override void OnInitializeMelon()
    {
        FeatureEnabled = false;
        TryInitializeWithWolfMenu();
    }

    public override void OnDeinitializeMelon()
    {
        CloseWindow();
        ShelfBindingIndicators.ClearAll();
        ShelfSceneCache.Reset();
        SortingPatches.ResetRuntimeState();
        WolfModBridge.Unregister(WolfModId);
    }

    public override void OnApplicationQuit()
    {
        CloseWindow();
        ShelfBindingIndicators.ClearAll();
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        ShelfBindingIndicators.ClearAll();
        ShelfSceneCache.Reset();
        SortingPatches.ResetRuntimeState();
        _nextIndicatorRefreshAt = 0f;
        _mainMenuView = null;
        _nextMainMenuLookup = 0f;
    }

    public override void OnUpdate()
    {
        if (!_runtimeInitialized)
        {
            if (!_dependencyFailed && Time.unscaledTime >= _nextWolfMenuRegistrationAttempt)
                TryInitializeWithWolfMenu();
            return;
        }

        var settingsVisible = WolfModBridge.IsSettingsOpen(WolfModId);
        if (_waitingForMenuKey && !settingsVisible)
            _waitingForMenuKey = false;

        if (_waitingForMenuKey)
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                _suppressEscapeThroughFrame = Time.frameCount + 1;
                WolfModBridge.SuppressEscapeForCurrentFrame();
                _waitingForMenuKey = false;
                Toast("Изменение клавиши отменено.");
            }
            else if (KeyNames.TryReadPressed(out var pressedKey))
            {
                ModSettings.SetMenuKey(pressedKey);
                _waitingForMenuKey = false;
                Toast($"Новая клавиша меню: {KeyNames.Display(pressedKey)}.");
            }

            return;
        }

        if (!FeatureEnabled)
            return;

        if (ModSettings.ShowShelfIndicators && Time.unscaledTime >= _nextIndicatorRefreshAt)
        {
            _nextIndicatorRefreshAt = Time.unscaledTime + IndicatorRefreshSeconds;
            ShelfBindingIndicators.RefreshAll();
        }

        if (Input.GetKeyDown(ModSettings.MenuKey))
        {
            if (_windowOpen)
                CloseWindow();
            else
                OpenWindow();
        }
        else if (_windowOpen && Input.GetKeyDown(KeyCode.Escape))
        {
            _suppressEscapeThroughFrame = Time.frameCount + 1;
            if (_page != MenuPage.Actions)
                _page = MenuPage.Actions;
            else
                CloseWindow();
        }
    }

    public override void OnGUI()
    {
        if (_dependencyFailed)
        {
            if (!IsMainMenuVisible())
                return;

            if (!_dependencyNotificationStarted)
            {
                _dependencyNotificationStarted = true;
                _dependencyErrorUntil = Time.unscaledTime + DependencyErrorDisplaySeconds;
            }

            if (Time.unscaledTime < _dependencyErrorUntil)
                DrawDependencyError();
            return;
        }

        if (!FeatureEnabled)
            return;

        // Closed HUD elements are non-interactive and only need the repaint pass.
        // Unity invokes OnGUI several times per frame for different event phases.
        if (!_windowOpen && Event.current != null && Event.current.type != EventType.Repaint)
            return;

        EnsureStyles();

        if (_windowOpen)
        {
            try
            {
                DrawWindow();
            }
            catch (Exception exception)
            {
                if (!_guiErrorReported)
                {
                    _guiErrorReported = true;
                    MelonLogger.Error($"Shelf Filters: ошибка окна, оно будет закрыто: {exception}");
                }

                CloseWindow();
                Toast("Окно фильтра было закрыто из-за ошибки интерфейса. Подробности записаны в журнал MelonLoader.");
            }
        }

        if (!string.IsNullOrEmpty(_toast) && Time.unscaledTime < _toastUntil)
        {
            var width = Math.Min(560f, Screen.width - 24f);
            var x = (Screen.width - width) / 2f;
            GUI.Box(new Rect(x, 22f, width, 46f), _toast, _toastStyle!);
        }
    }

    private void TryInitializeWithWolfMenu()
    {
        _wolfMenuRegistrationAttempts++;
        if (WolfModBridge.TryRegister(
                WolfModId,
                "Фильтры полок",
                "0.10.3",
                "Политики секций полок: строгие товары, временные замены и контроль автоматической выкладки.",
                SetFeatureEnabled,
                DrawWolfModSettings,
                () => _waitingForMenuKey))
        {
            try
            {
                ModSettings.Initialize();
                RuleStore.Initialize();
                SortingPatches.Install();
                _runtimeInitialized = true;
                LoggerInstance.Msg(
                    $"Shelf Filters 0.10.3 загружен. Наведитесь на секцию полки и нажмите {KeyNames.Display(ModSettings.MenuKey)}.");
            }
            catch
            {
                WolfModBridge.Unregister(WolfModId);
                FeatureEnabled = false;
                throw;
            }
            return;
        }

        if (_wolfMenuRegistrationAttempts < MaxWolfMenuRegistrationAttempts)
        {
            _nextWolfMenuRegistrationAttempt = Time.unscaledTime + WolfMenuRetryDelay;
            return;
        }

        _dependencyFailed = true;
        FeatureEnabled = false;
        LoggerInstance.Error(
            $"Shelf Filters отключён: WolfCore не найден или несовместим. " +
            $"Установите WolfCore.dll и перезапустите игру. {WolfModBridge.LastError}");
    }

    private void DrawDependencyError()
    {
        _dependencyErrorStyle ??= new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            wordWrap = true,
            padding = new RectOffset(18, 18, 12, 12)
        };
        _dependencyErrorStyle.normal.textColor = new Color(1f, 0.86f, 0.86f, 1f);

        var width = Math.Min(720f, Screen.width - 48f);
        var height = 112f;
        var bottomClearance = Math.Max(130f, Screen.height * 0.12f);
        GUI.depth = -20000;
        GUI.Box(
            new Rect(24f, Screen.height - height - bottomClearance, width, height),
            "Shelf Filters отключён\nWolfCore не найден или несовместим. Установите WolfCore.dll и перезапустите игру.",
            _dependencyErrorStyle);
    }

    private bool IsMainMenuVisible()
    {
        try
        {
            if ((_mainMenuView == null || !_mainMenuView) && Time.unscaledTime >= _nextMainMenuLookup)
            {
                _nextMainMenuLookup = Time.unscaledTime + 2f;
                _mainMenuView = UnityEngine.Object.FindObjectOfType<MenuView>();
            }

            return _mainMenuView != null && _mainMenuView.isActiveAndEnabled &&
                   _mainMenuView.gameObject.activeInHierarchy;
        }
        catch
        {
            return false;
        }
    }

    private void OpenWindow()
    {
        _target = FindAimedShelfPlace();
        if (_target == null)
        {
            Toast(
                $"Наведите центр экрана на нужную секцию полки и нажмите {KeyNames.Display(ModSettings.MenuKey)}.");
            return;
        }

        _windowTarget = _target;
        _products.Clear();
        _page = MenuPage.Actions;
        _catalogMessage = string.Empty;
        _productPage = 0;
        _guiErrorReported = false;

        _previousCursorVisible = Cursor.visible;
        _previousCursorLockMode = Cursor.lockState;
        _windowOpen = true;
        _blockGameEscape = true;

        try
        {
            _gameplayInputBlock = AllServices.Get<InputService>()?.ClaimGameplayInputBlock();
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"Shelf Filters: не удалось запросить штатную блокировку ввода: {exception.Message}");
        }

        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
    }

    private void CloseWindow()
    {
        if (!_windowOpen)
            return;

        _windowOpen = false;
        _waitingForMenuKey = false;
        _blockGameEscape = false;
        _windowTarget = null;
        if (_gameplayInputBlock != null)
        {
            try
            {
                _gameplayInputBlock.Dispose();
            }
            catch (Exception exception)
            {
                MelonLogger.Warning($"Shelf Filters: ошибка снятия блокировки ввода: {exception.Message}");
            }
            finally
            {
                _gameplayInputBlock = null;
            }
        }
        Cursor.visible = _previousCursorVisible;
        Cursor.lockState = _previousCursorLockMode;
    }

    private void DrawWindow()
    {
        if (_windowTarget == null)
        {
            CloseWindow();
            return;
        }

        var closeAfterDraw = false;
        var width = _page switch
        {
            MenuPage.Actions => 520f,
            _ => Math.Min(760f, Screen.width - 30f)
        };
        var desiredHeight = _page switch
        {
            MenuPage.Actions => 570f,
            _ => 680f
        };
        var height = Math.Min(desiredHeight, Screen.height - 50f);
        var rect = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
        GUI.Box(rect, GUIContent.none, _windowStyle!);

        GUILayout.BeginArea(new Rect(rect.x + 16f, rect.y + 14f, rect.width - 32f, rect.height - 28f));
        if (_page == MenuPage.Actions)
            closeAfterDraw = DrawActionsPage();
        else
            closeAfterDraw = DrawProductsPage();
        GUILayout.EndArea();

        if (closeAfterDraw)
            CloseWindow();
    }

    private bool DrawActionsPage()
    {
        GUILayout.Label("Политика секции полки", _titleStyle!);
        GUILayout.Label($"Сейчас: {RuleStore.Describe(_windowTarget!)}", _statusStyle!);
        GUILayout.Space(12f);

        if (GUILayout.Button("По правилам игры (убрать правило мода)", GUILayout.Height(42f)))
        {
            RuleStore.Clear(_windowTarget!);
            Toast("Секция снова использует штатную память последнего товара.");
            return true;
        }

        if (ShelfProductResolver.TryResolvePhysical(_windowTarget!, out var currentDefinition))
        {
            var currentName = ProductNames.Resolve(currentDefinition.Id);
            if (GUILayout.Button($"Привязать лежащий товар\n{currentName}", GUILayout.Height(58f)))
            {
                RuleStore.SetExact(
                    _windowTarget!,
                    currentDefinition.Id,
                    currentName,
                    ModSettings.DefaultAllowTemporarySubstitute);
                Toast($"Закреплено: {currentName}.");
                return true;
            }
        }
        else
        {
            var previousEnabled = GUI.enabled;
            GUI.enabled = false;
            GUILayout.Button("Привязать лежащий товар\nВ выбранной секции товар не найден", GUILayout.Height(58f));
            GUI.enabled = previousEnabled;
        }

        if (GUILayout.Button("Привязать из доступных товаров…", GUILayout.Height(44f)))
        {
            _products.Clear();
            _catalogMessage = string.Empty;
            _productPage = 0;
            AvailableProductCatalog.Fill(_windowTarget!, _products, out _catalogMessage);
            _page = MenuPage.Products;
        }

        if (RuleStore.TryGetExact(_windowTarget!, out _, out var allowTemporarySubstitute))
        {
            var substituteLabel = allowTemporarySubstitute
                ? "Временная замена: разрешена"
                : "Временная замена: запрещена";
            if (GUILayout.Button(substituteLabel, GUILayout.Height(42f)))
            {
                RuleStore.SetTemporarySubstitute(_windowTarget!, !allowTemporarySubstitute);
                Toast(!allowTemporarySubstitute
                    ? "При отсутствии назначенного товара разрешена временная замена."
                    : "Секция будет строго ждать назначенный товар.");
                return true;
            }
        }

        if (GUILayout.Button("Свободная секция\nИгнорировать память последнего товара", GUILayout.Height(54f)))
        {
            RuleStore.SetFree(_windowTarget!);
            Toast("Секция разрешает любой совместимый товар.");
            return true;
        }

        if (GUILayout.Button("Запретить автоматическую выкладку", GUILayout.Height(42f)))
        {
            RuleStore.SetBlocked(_windowTarget!);
            Toast("Автоматическая выкладка в эту секцию запрещена.");
            return true;
        }

        GUILayout.FlexibleSpace();
        return GUILayout.Button(
            $"Закрыть ({KeyNames.Display(ModSettings.MenuKey)} / Esc)",
            GUILayout.Height(34f));
    }

    private void DrawWolfModSettings()
    {
        EnsureStyles();
        DrawSettingsControls();
    }

    private void DrawSettingsControls()
    {
        GUILayout.Label($"Клавиша открытия: {KeyNames.Display(ModSettings.MenuKey)}", _statusStyle!);
        GUILayout.Space(12f);

        if (_waitingForMenuKey)
        {
            GUILayout.Label(
                "Нажмите новую клавишу на клавиатуре или дополнительную кнопку мыши. Esc — отмена.",
                _statusStyle!);
            if (GUILayout.Button("Отменить ожидание", GUILayout.Height(40f)))
                _waitingForMenuKey = false;
        }
        else if (GUILayout.Button("Изменить клавишу открытия", GUILayout.Height(44f)))
        {
            _waitingForMenuKey = true;
        }

        if (!_waitingForMenuKey &&
            ModSettings.MenuKey != KeyCode.F6 &&
            GUILayout.Button("Вернуть F6", GUILayout.Height(38f)))
        {
            ModSettings.SetMenuKey(KeyCode.F6);
            Toast("Клавиша меню возвращена на F6.");
        }

        GUILayout.Space(14f);
        var showIndicators = GUILayout.Toggle(
            ModSettings.ShowShelfIndicators,
            "Показывать индикаторы привязок возле ценников");
        if (showIndicators != ModSettings.ShowShelfIndicators)
        {
            ModSettings.SetShowShelfIndicators(showIndicators);
            if (showIndicators)
                ShelfBindingIndicators.RefreshAll();
            else
                ShelfBindingIndicators.ClearAll();
        }

        var previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && ModSettings.ShowShelfIndicators;
        var showEmptyBackgrounds = GUILayout.Toggle(
            ModSettings.ShowEmptyIndicatorBackgrounds,
            "Показывать пустую подложку у непривязанных секций");
        if (showEmptyBackgrounds != ModSettings.ShowEmptyIndicatorBackgrounds)
        {
            ModSettings.SetShowEmptyIndicatorBackgrounds(showEmptyBackgrounds);
            ShelfBindingIndicators.RefreshAll();
        }
        GUI.enabled = previousEnabled;

        var defaultAllowTemporarySubstitute = GUILayout.Toggle(
            ModSettings.DefaultAllowTemporarySubstitute,
            "Новые привязки разрешают временную замену товара");
        if (defaultAllowTemporarySubstitute != ModSettings.DefaultAllowTemporarySubstitute)
            ModSettings.SetDefaultAllowTemporarySubstitute(defaultAllowTemporarySubstitute);

        var showBlockedIndicators = GUILayout.Toggle(
            ModSettings.ShowBlockedIndicators,
            "Показывать красный крест у запрещённых секций");
        if (showBlockedIndicators != ModSettings.ShowBlockedIndicators)
        {
            ModSettings.SetShowBlockedIndicators(showBlockedIndicators);
            ShelfBindingIndicators.RefreshAll();
        }

        GUILayout.Label(
            "Строгая привязка всегда важнее штатной памяти игры. Временная замена используется только тогда, когда назначенного товара нет в доступных коробках.",
            _statusStyle!);

        GUILayout.Label(
            "Подложка не является ценником и не принимает нажатия.",
            _statusStyle!);

        GUILayout.Label(
            "Настройки сохраняются отдельно от игровых сохранений и правил полок.",
            _statusStyle!);
    }

    private void SetFeatureEnabled(bool enabled)
    {
        FeatureEnabled = enabled;
        if (!enabled)
        {
            CloseWindow();
            ShelfBindingIndicators.ClearAll();
            ShelfSceneCache.Reset();
            SortingPatches.ResetRuntimeState();
            return;
        }

        _nextIndicatorRefreshAt = 0f;
        ShelfSceneCache.Reset();
        SortingPatches.ResetRuntimeState();
        ShelfBindingIndicators.RefreshAll();
    }

    private bool DrawProductsPage()
    {
        GUILayout.Label("Выбор доступного товара", _titleStyle!);
        GUILayout.Label($"Доступные для заказа товары: {_products.Count}", _statusStyle!);

        if (!string.IsNullOrWhiteSpace(_catalogMessage))
            GUILayout.Label(_catalogMessage, _statusStyle!);

        const int columns = 3;
        const int rows = 2;
        const int productsPerPage = columns * rows;
        var pageCount = Math.Max(1, (_products.Count + productsPerPage - 1) / productsPerPage);
        _productPage = Math.Clamp(_productPage, 0, pageCount - 1);
        var startIndex = _productPage * productsPerPage;
        var endIndex = Math.Min(startIndex + productsPerPage, _products.Count);
        ProductOption? selectedProduct = null;

        for (var row = 0; row < rows; row++)
        {
            var rowStart = startIndex + row * columns;
            if (rowStart >= endIndex)
                break;

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            for (var column = 0; column < columns; column++)
            {
                var index = rowStart + column;
                if (index >= endIndex)
                {
                    GUILayout.Space(210f);
                    continue;
                }

                var product = _products[index];
                if (GUILayout.Button(product.Content, _productCardStyle!, GUILayout.Width(210f), GUILayout.Height(210f)))
                    selectedProduct = product;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
        }

        if (selectedProduct != null)
        {
            RuleStore.SetExact(
                _windowTarget!,
                selectedProduct.Definition.Id,
                selectedProduct.Name,
                ModSettings.DefaultAllowTemporarySubstitute);
            Toast($"Закреплено: {selectedProduct.Name}.");
            return true;
        }

        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal();
        var previousEnabled = GUI.enabled;
        GUI.enabled = _productPage > 0;
        if (GUILayout.Button("← Предыдущая", GUILayout.Height(32f)))
            _productPage--;
        GUI.enabled = previousEnabled;
        GUILayout.Label($"{_productPage + 1} / {pageCount}", _statusStyle!, GUILayout.Width(70f), GUILayout.Height(32f));
        GUI.enabled = _productPage + 1 < pageCount;
        if (GUILayout.Button("Следующая →", GUILayout.Height(32f)))
            _productPage++;
        GUI.enabled = previousEnabled;
        GUILayout.EndHorizontal();

        GUILayout.Space(6f);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("← Назад", GUILayout.Height(34f)))
            _page = MenuPage.Actions;
        var close = GUILayout.Button(
            $"Закрыть ({KeyNames.Display(ModSettings.MenuKey)})",
            GUILayout.Height(34f));
        GUILayout.EndHorizontal();
        return close;
    }

    private static ProductPricePlace? FindAimedShelfPlace()
    {
        var camera = Camera.main;
        if (camera == null)
            return null;

        var ray = new Ray(camera.transform.position, camera.transform.forward);

        // The game's own yellow outline is the most accurate source of truth.
        // It already accounts for unusual shelf colliders and nested product items.
        var places = ShelfSceneCache.GetPlaces(forceRefresh: true);
        var highlighted = FindGameHighlightedPlace(camera, places);
        if (highlighted != null)
            return highlighted;

        // Empty sections have no product to outline, so raycast only against the
        // ProductPlace volume where an item can actually be placed.
        var placementHit = FindPlacementVolumeHit(ray, places);
        if (placementHit != null)
            return placementHit;

        // Last fallback: accept product/item colliders only when they resolve to
        // a ProductPlace. Shelf bodies and price-label colliders are ignored.
        var hits = Physics.RaycastAll(ray, TargetDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
        ProductPricePlace? nearest = null;
        var nearestHitDistance = float.MaxValue;
        foreach (var hit in hits)
        {
            var place = ResolvePlacementOnly(hit.collider);
            if (place == null || hit.distance >= nearestHitDistance)
                continue;

            nearest = place;
            nearestHitDistance = hit.distance;
        }

        return nearest;
    }

    private static ProductPricePlace? FindGameHighlightedPlace(
        Camera camera,
        IReadOnlyList<ProductPricePlace> places)
    {
        try
        {
            var raycastController = UnityEngine.Object.FindObjectOfType<PlayerRaycastController>();
            if (raycastController == null || raycastController._activeOutlinable == null)
                return null;
            if (raycastController._camera != null && raycastController._camera != camera)
                return null;

            var activeOutline = raycastController._activeOutlinable;
            foreach (var place in places)
            {
                var productPlace = place?.ProductPlace;
                if (!IsUsablePlacement(place, productPlace))
                    continue;

                if (productPlace!.Outlinable == activeOutline ||
                    productPlace.TopItemOutlinable == activeOutline)
                    return place;
            }
        }
        catch
        {
            // Scene transitions can invalidate Unity objects between lookups.
        }

        return null;
    }

    private static ProductPricePlace? FindPlacementVolumeHit(
        Ray ray,
        IReadOnlyList<ProductPricePlace> places)
    {
        ProductPricePlace? nearest = null;
        var nearestDistance = float.MaxValue;
        try
        {
            foreach (var place in places)
            {
                var productPlace = place?.ProductPlace;
                if (!IsUsablePlacement(place, productPlace))
                    continue;

                var placementCollider = productPlace!._boxCollider;
                if (placementCollider == null || !placementCollider.enabled ||
                    !placementCollider.gameObject.activeInHierarchy ||
                    !placementCollider.Raycast(ray, out var hit, TargetDistance) ||
                    hit.distance >= nearestDistance)
                    continue;

                nearest = place;
                nearestDistance = hit.distance;
            }
        }
        catch
        {
            // Fall through to the product/item collider lookup.
        }

        return nearest;
    }

    private static ProductPricePlace? ResolvePlacementOnly(Collider? collider)
    {
        if (collider == null)
            return null;

        var productPlace = collider.GetComponentInParent<ProductPlace>();
        if (IsUsablePlacement(productPlace?.PricePlace, productPlace))
            return productPlace!.PricePlace;

        var item = collider.GetComponentInParent<ProductItem>();
        if (item != null)
        {
            productPlace = item.GetComponentInParent<ProductPlace>();
            if (IsUsablePlacement(productPlace?.PricePlace, productPlace))
                return productPlace!.PricePlace;
        }

        return null;
    }

    private static bool IsUsablePlacement(ProductPricePlace? pricePlace, ProductPlace? productPlace)
    {
        return pricePlace != null &&
               productPlace != null &&
               pricePlace.ProductPlace == productPlace;
    }

    private void Toast(string message)
    {
        _toast = message;
        _toastUntil = Time.unscaledTime + 3.5f;
        LoggerInstance.Msg(message);
    }

    private void EnsureStyles()
    {
        if (_windowStyle != null)
            return;

        _windowStyle = new GUIStyle(GUI.skin.box)
        {
            padding = new RectOffset(12, 12, 12, 12)
        };

        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 21,
            fontStyle = FontStyle.Bold
        };
        _titleStyle.normal.textColor = Color.white;

        _statusStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 15,
            wordWrap = true,
        };
        _statusStyle.normal.textColor = Color.white;

        _productCardStyle = new GUIStyle(GUI.skin.button)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            imagePosition = ImagePosition.ImageAbove,
            wordWrap = true,
            padding = new RectOffset(8, 8, 8, 8)
        };

        _toastStyle = new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            wordWrap = true,
            padding = new RectOffset(10, 10, 6, 6)
        };
        _toastStyle.normal.textColor = Color.white;
    }
}

internal sealed class ShelfFiltersSettingsData
{
    public string MenuKey { get; set; } = nameof(KeyCode.F6);
    public bool ShowShelfIndicators { get; set; } = true;
    public bool ShowEmptyIndicatorBackgrounds { get; set; } = true;
    public bool ShowBlockedIndicators { get; set; } = true;
    public bool DefaultAllowTemporarySubstitute { get; set; }
}

internal static class ModSettings
{
    private static string _path = string.Empty;

    public static KeyCode MenuKey { get; private set; } = KeyCode.F6;
    public static bool ShowShelfIndicators { get; private set; } = true;
    public static bool ShowEmptyIndicatorBackgrounds { get; private set; } = true;
    public static bool ShowBlockedIndicators { get; private set; } = true;
    public static bool DefaultAllowTemporarySubstitute { get; private set; }

    public static void Initialize()
    {
        _path = Path.Combine(MelonEnvironment.UserDataDirectory, "AnimeShopShelfFilters.settings.json");
        MenuKey = KeyCode.F6;
        ShowShelfIndicators = true;
        ShowEmptyIndicatorBackgrounds = true;
        ShowBlockedIndicators = true;
        DefaultAllowTemporarySubstitute = false;

        if (!File.Exists(_path))
        {
            Save();
            return;
        }

        try
        {
            var loaded = JsonConvert.DeserializeObject<ShelfFiltersSettingsData>(File.ReadAllText(_path));
            if (loaded == null)
                return;

            if (Enum.TryParse<KeyCode>(loaded.MenuKey, true, out var loadedKey) && KeyNames.CanAssign(loadedKey))
                MenuKey = loadedKey;
            else
                MelonLogger.Warning("Shelf Filters: сохранённая клавиша недоступна; используется F6.");

            ShowShelfIndicators = loaded.ShowShelfIndicators;
            ShowEmptyIndicatorBackgrounds = loaded.ShowEmptyIndicatorBackgrounds;
            ShowBlockedIndicators = loaded.ShowBlockedIndicators;
            DefaultAllowTemporarySubstitute = loaded.DefaultAllowTemporarySubstitute;

            // Rewrite settings once through the current schema so obsolete
            // pre-1.0.5 recovery options disappear from existing installations.
            Save();
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"Shelf Filters: настройки не прочитаны, используются стандартные: {exception.Message}");
        }

        MelonLogger.Msg(
            $"Shelf Filters: клавиша меню {KeyNames.Display(MenuKey)}, " +
            $"индикаторы привязок {(ShowShelfIndicators ? "включены" : "выключены")}, " +
            $"пустые подложки {(ShowEmptyIndicatorBackgrounds ? "включены" : "выключены")}, " +
            $"кресты запрета {(ShowBlockedIndicators ? "включены" : "выключены")}, " +
            $"временная замена по умолчанию {(DefaultAllowTemporarySubstitute ? "включена" : "выключена")}.");
    }

    public static void SetMenuKey(KeyCode key)
    {
        if (!KeyNames.CanAssign(key))
            return;

        MenuKey = key;
        Save();
    }

    public static void SetShowShelfIndicators(bool show)
    {
        ShowShelfIndicators = show;
        Save();
    }

    public static void SetShowEmptyIndicatorBackgrounds(bool show)
    {
        ShowEmptyIndicatorBackgrounds = show;
        Save();
    }

    public static void SetShowBlockedIndicators(bool show)
    {
        ShowBlockedIndicators = show;
        Save();
    }

    public static void SetDefaultAllowTemporarySubstitute(bool allow)
    {
        DefaultAllowTemporarySubstitute = allow;
        Save();
    }

    private static void Save()
    {
        if (string.IsNullOrWhiteSpace(_path))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var data = new ShelfFiltersSettingsData
            {
                MenuKey = MenuKey.ToString(),
                ShowShelfIndicators = ShowShelfIndicators,
                ShowEmptyIndicatorBackgrounds = ShowEmptyIndicatorBackgrounds,
                ShowBlockedIndicators = ShowBlockedIndicators,
                DefaultAllowTemporarySubstitute = DefaultAllowTemporarySubstitute
            };
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(data, Formatting.Indented));
            File.Move(temporaryPath, _path, true);
        }
        catch (Exception exception)
        {
            MelonLogger.Error($"Shelf Filters: не удалось сохранить настройки: {exception}");
        }
    }
}

internal static class KeyNames
{
    private static readonly KeyCode[] CapturableKeys =
        Enum.GetValues<KeyCode>().Where(CanAssign).Distinct().ToArray();

    public static bool TryReadPressed(out KeyCode key)
    {
        key = KeyCode.None;
        if (!Input.anyKeyDown)
            return false;

        foreach (var candidate in CapturableKeys)
        {
            if (Input.GetKeyDown(candidate))
            {
                key = candidate;
                return true;
            }
        }

        return false;
    }

    public static bool CanAssign(KeyCode key)
    {
        return key != KeyCode.None && key != KeyCode.Escape && key != KeyCode.Mouse0;
    }

    public static string Display(KeyCode key)
    {
        return key switch
        {
            KeyCode.LeftArrow => "←",
            KeyCode.RightArrow => "→",
            KeyCode.UpArrow => "↑",
            KeyCode.DownArrow => "↓",
            KeyCode.Return => "Enter",
            KeyCode.KeypadEnter => "Num Enter",
            KeyCode.LeftControl => "Left Ctrl",
            KeyCode.RightControl => "Right Ctrl",
            KeyCode.LeftShift => "Left Shift",
            KeyCode.RightShift => "Right Shift",
            KeyCode.LeftAlt => "Left Alt",
            KeyCode.RightAlt => "Right Alt",
            KeyCode.Mouse1 => "Mouse 2",
            KeyCode.Mouse2 => "Mouse 3",
            KeyCode.Mouse3 => "Mouse 4",
            KeyCode.Mouse4 => "Mouse 5",
            KeyCode.Mouse5 => "Mouse 6",
            KeyCode.Mouse6 => "Mouse 7",
            _ => key.ToString().Replace("Alpha", string.Empty).Replace("Keypad", "Num ")
        };
    }
}

internal static class WolfModBridge
{
    private const string RegistryTypeName = "WolfCore.WolfModRegistry";

    public static bool Registered { get; private set; }
    public static string LastError { get; private set; } = string.Empty;

    public static bool TryRegister(
        string id,
        string displayName,
        string version,
        string description,
        Action<bool> onEnabledChanged,
        Action drawSettings,
        Func<bool> isCapturingInput)
    {
        if (Registered)
            return true;

        var registry = FindRegistryType();
        if (registry == null)
        {
            LastError = "Тип WolfCore.WolfModRegistry пока не найден.";
            return false;
        }

        try
        {
            var register = registry
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                    method.Name == "Register" &&
                    method.GetParameters().Length == 8 &&
                    method.GetParameters()[0].ParameterType == typeof(string));
            if (register == null)
            {
                LastError = "Установленная версия WolfCore не поддерживает требуемый API.";
                return false;
            }

            register.Invoke(null, new object?[]
            {
                id,
                displayName,
                version,
                description,
                onEnabledChanged,
                drawSettings,
                isCapturingInput,
                true
            });
            Registered = true;
            LastError = string.Empty;
            MelonLogger.Msg("Shelf Filters: подключён к WolfCore.");
            return true;
        }
        catch (Exception exception)
        {
            LastError = exception.GetBaseException().Message;
            return false;
        }
    }

    public static void Unregister(string id)
    {
        if (!Registered)
            return;

        try
        {
            FindRegistryType()?.GetMethod(
                "Unregister",
                BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object?[] { id });
        }
        catch
        {
            // The host may already be shutting down.
        }
        Registered = false;
    }

    public static bool IsSettingsOpen(string id)
    {
        if (!Registered)
            return false;

        try
        {
            return FindRegistryType()?.GetMethod(
                       "IsSettingsOpen",
                       BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object?[] { id }) as bool? == true;
        }
        catch
        {
            return false;
        }
    }

    public static void SuppressEscapeForCurrentFrame()
    {
        if (!Registered)
            return;

        try
        {
            FindRegistryType()?.GetMethod(
                "SuppressEscapeForCurrentFrame",
                BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }
        catch
        {
            // Escape suppression is a convenience; failure is non-fatal.
        }
    }

    private static Type? FindRegistryType()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(assembly.GetName().Name, "WolfCore", StringComparison.Ordinal))
                continue;
            return assembly.GetType(RegistryTypeName, false);
        }
        return null;
    }
}

internal enum MenuPage
{
    Actions,
    Products
}

internal sealed class ProductOption
{
    public ProductOption(ProductDefinition definition, string name)
    {
        Definition = definition;
        Name = name;
        var iconTexture = definition.Icon != null ? definition.Icon.texture : null;
        Content = new GUIContent(name) { image = iconTexture };
    }

    public ProductDefinition Definition { get; }
    public string Name { get; }
    public GUIContent Content { get; }
}

internal static class AvailableProductCatalog
{
    public static void Fill(ProductPricePlace place, List<ProductOption> result, out string message)
    {
        message = string.Empty;
        try
        {
            var service = AllServices.Get<ProductsService>();
            if (service == null || !service.ControllerReady)
            {
                message = "Каталог товаров ещё загружается. Закройте окно и попробуйте снова через несколько секунд.";
                return;
            }

            var available = new Il2CppSystem.Collections.Generic.List<ProductDefinition>();
            service.GetAvailableProducts(available);
            var unlockedIds = new HashSet<int>();
            for (var index = 0; index < available.Count; index++)
            {
                var definition = available[index];
                if (definition != null)
                    unlockedIds.Add(definition.Id);
            }

            // GetAvailableProducts in game 1.0.3 misses several manga. Build the
            // catalogue from the complete runtime definition registry and use the
            // game's own license/DLC checks. New products are therefore discovered
            // automatically after game updates instead of being hard-coded here.
            var definitions = service.ProductsConfig?.ProductDefinitions() ?? service.ProductDefinitions;
            var seen = new HashSet<int>();
            var addedThroughLicenseFallback = 0;
            var incompatible = 0;
            if (definitions == null)
            {
                message = "Полный каталог товаров игры пока не инициализирован.";
                return;
            }

            var definitionsCollection =
                new Il2CppSystem.Collections.Generic.IReadOnlyCollection<ProductDefinition>(definitions.Pointer);
            var definitionCount = definitionsCollection.Count;
            for (var index = 0; index < definitionCount; index++)
            {
                var definition = definitions[index];
                if (definition == null || !seen.Add(definition.Id))
                    continue;

                var unlocked = unlockedIds.Contains(definition.Id);
                if (!unlocked)
                {
                    try
                    {
                        var pickup = service.ProductsConfig?.FindPickupDefinition(definition);
                        var dlcAccessible = pickup == null || service.CanAccess(pickup);
                        unlocked = dlcAccessible && service.HasLicenseBoughtForProduct(definition);
                    }
                    catch
                    {
                        unlocked = false;
                    }

                    if (unlocked)
                        addedThroughLicenseFallback++;
                }

                if (!unlocked)
                    continue;

                if (place.ProductPlace == null || !place.ProductPlace.CanPut(definition.Type))
                {
                    incompatible++;
                    continue;
                }

                result.Add(new ProductOption(definition, ProductNames.Resolve(definition.Id)));
            }

            result.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
            MelonLogger.Msg(
                $"Shelf Filters: каталог секции — определений {definitionCount}, штатно доступно {unlockedIds.Count}, " +
                $"добавлено проверкой лицензии {addedThroughLicenseFallback}, совместимо {result.Count}, отброшено по типу {incompatible}.");
            if (result.Count == 0)
                message = "Для этой секции пока нет разблокированных товаров подходящего типа.";
        }
        catch (Exception exception)
        {
            message = "Не удалось прочитать каталог. Попробуйте открыть окно ещё раз.";
            MelonLogger.Error($"Shelf Filters: ошибка чтения доступных товаров: {exception}");
        }
    }
}

internal static class ShelfProductResolver
{
    public static bool TryResolvePhysical(ProductPricePlace place, out ProductDefinition definition)
    {
        definition = null!;
        if (place?.ProductPlace == null || place.ProductPlace.Count <= 0)
            return false;

        return TryResolve(place, out definition);
    }

    public static bool TryResolve(ProductPricePlace place, out ProductDefinition definition)
    {
        definition = null!;
        var definitionId = ResolveDefinitionId(place);
        if (definitionId == 0)
            return false;

        try
        {
            definition = DataDefinition<ProductDefinition>.GetWithId(definitionId);
            return definition != null;
        }
        catch
        {
            return false;
        }
    }

    private static int ResolveDefinitionId(ProductPricePlace place)
    {
        var productPlace = place.ProductPlace;
        if (productPlace != null && productPlace.Count > 0)
        {
            try
            {
                var variants = productPlace.VariantDefinitionIds;
                if (variants != null)
                {
                    var variantsCollection =
                        new Il2CppSystem.Collections.Generic.IReadOnlyCollection<int>(variants.Pointer);
                    if (variantsCollection.Count > 0 && variants[0] != 0)
                        return variants[0];
                }
            }
            catch
            {
                // Ordinary products may not expose a variant list.
            }
        }

        if (place.ProductInfo != null && place.ProductInfo.DefinitionId != 0)
            return place.ProductInfo.DefinitionId;

        // This is the same resolver the game's own price-editing window uses.
        // It is important for manga and other products rendered as combined/GPU visuals,
        // because those shelves do not necessarily expose ProductItem children.
        try
        {
            if (place.TryResolveProductInfo() &&
                place.ProductInfo != null &&
                place.ProductInfo.DefinitionId != 0)
            {
                return place.ProductInfo.DefinitionId;
            }
        }
        catch
        {
            // Continue with the product GUID and visual-item fallbacks below.
        }

        try
        {
            var service = AllServices.Get<ProductsService>();
            var info = service?.GetProductInfo(place.ResolveCurrentProductId());
            if (info != null && info.DefinitionId != 0)
                return info.DefinitionId;
        }
        catch
        {
            // Continue with the ProductPlace fallbacks below.
        }

        if (productPlace != null && productPlace.Count > 0)
        {
            try
            {
                var service = AllServices.Get<ProductsService>();
                var info = service?.GetProductInfo(productPlace.Id);
                if (info != null && info.DefinitionId != 0)
                    return info.DefinitionId;
            }
            catch
            {
                // Continue with the visual item fallbacks below.
            }

            var items = productPlace.Items;
            if (items != null)
            {
                try
                {
                    var item = items[0];
                    if (item != null)
                    {
                        if (item.ProductInfo != null && item.ProductInfo.DefinitionId != 0)
                            return item.ProductInfo.DefinitionId;
                        if (item.DefinitionIdHint != 0)
                            return item.DefinitionIdHint;
                        if (item.Definition != null && item.Definition.Id != 0)
                            return item.Definition.Id;
                    }
                }
                catch
                {
                    // Some product types use GPU-only visuals and expose no Items[0].
                }
            }
        }

        return place.LastDefinitionId;
    }
}

internal enum ShelfFilterMode
{
    // Keep the old numeric values so 0.7.x JSON files migrate without changes.
    ExactProduct = 0,
    Blocked = 1,
    Free = 2
}

internal sealed class ShelfRule
{
    public ShelfFilterMode Mode { get; set; }
    public int ProductDefinitionId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public bool AllowTemporarySubstitute { get; set; }
}

internal static class RuleStore
{
    private static readonly Dictionary<string, ShelfRule> Rules = new(StringComparer.Ordinal);
    private static string _path = string.Empty;

    public static void Initialize()
    {
        _path = Path.Combine(MelonEnvironment.UserDataDirectory, "AnimeShopShelfFilters.json");
        Load();
    }

    public static bool Allows(ProductPricePlace? place, int productDefinitionId)
    {
        if (!ShelfFiltersMod.FeatureEnabled)
            return true;

        if (place == null || !TryGet(place, out var rule))
            return true;

        return rule.Mode switch
        {
            ShelfFilterMode.Blocked => false,
            ShelfFilterMode.ExactProduct =>
                rule.ProductDefinitionId == productDefinitionId ||
                rule.AllowTemporarySubstitute &&
                !SortingPatches.IsProductAvailable(rule.ProductDefinitionId),
            ShelfFilterMode.Free => true,
            _ => true
        };
    }

    public static void SetExact(
        ProductPricePlace place,
        int definitionId,
        string productName,
        bool allowTemporarySubstitute)
    {
        if (!TryKey(place, out var key))
            return;
        if (!ReloadLatestBeforeMutation())
            return;

        Rules[key] = new ShelfRule
        {
            Mode = ShelfFilterMode.ExactProduct,
            ProductDefinitionId = definitionId,
            ProductName = productName,
            AllowTemporarySubstitute = allowTemporarySubstitute
        };
        Save();
        SortingPatches.InvalidateWaitingDestinationCache();
        SortingPatches.SynchronizeRuntimeShelfMemory(place);
        ShelfBindingIndicators.Refresh(place);
    }

    public static void SetTemporarySubstitute(ProductPricePlace place, bool allow)
    {
        if (!TryKey(place, out var key))
            return;
        if (!ReloadLatestBeforeMutation())
            return;
        if (!Rules.TryGetValue(key, out var rule) || rule.Mode != ShelfFilterMode.ExactProduct)
            return;

        rule.AllowTemporarySubstitute = allow;
        Save();
        SortingPatches.InvalidateWaitingDestinationCache();
        SortingPatches.SynchronizeRuntimeShelfMemory(place);
        ShelfBindingIndicators.Refresh(place);
    }

    public static void SetFree(ProductPricePlace place)
    {
        if (!TryKey(place, out var key))
            return;
        if (!ReloadLatestBeforeMutation())
            return;

        Rules[key] = new ShelfRule { Mode = ShelfFilterMode.Free };
        Save();
        SortingPatches.InvalidateWaitingDestinationCache();
        SortingPatches.SynchronizeRuntimeShelfMemory(place);
        ShelfBindingIndicators.Refresh(place);
    }

    public static void SetBlocked(ProductPricePlace place)
    {
        if (!TryKey(place, out var key))
            return;
        if (!ReloadLatestBeforeMutation())
            return;

        Rules[key] = new ShelfRule { Mode = ShelfFilterMode.Blocked };
        Save();
        SortingPatches.InvalidateWaitingDestinationCache();
        ShelfBindingIndicators.Refresh(place);
    }

    public static void Clear(ProductPricePlace place)
    {
        if (!TryKey(place, out var key))
            return;
        if (!ReloadLatestBeforeMutation())
            return;

        if (Rules.Remove(key))
            Save();
        SortingPatches.InvalidateWaitingDestinationCache();
        ShelfBindingIndicators.Refresh(place);
    }

    public static string Describe(ProductPricePlace place)
    {
        if (!TryGet(place, out var rule))
            return "по правилам игры — память последнего товара";

        if (rule.Mode == ShelfFilterMode.Blocked)
            return "автовыкладка запрещена";

        if (rule.Mode == ShelfFilterMode.Free)
            return "свободная секция — любой совместимый товар";

        var name = string.IsNullOrWhiteSpace(rule.ProductName)
            ? ProductNames.Resolve(rule.ProductDefinitionId)
            : rule.ProductName;
        var substitute = rule.AllowTemporarySubstitute
            ? "; временная замена разрешена"
            : "; строгая привязка";

        if (ShelfProductResolver.TryResolvePhysical(place, out var physicalDefinition) &&
            physicalDefinition.Id != rule.ProductDefinitionId)
        {
            var physicalName = ProductNames.Resolve(physicalDefinition.Id);
            return $"переход с «{physicalName}» на «{name}» после распродажи{substitute}";
        }

        return $"«{name}»{substitute}";
    }

    public static bool TryGetExact(ProductPricePlace? place, out int definitionId)
    {
        return TryGetExact(place, out definitionId, out _);
    }

    public static bool TryGetExact(
        ProductPricePlace? place,
        out int definitionId,
        out bool allowTemporarySubstitute)
    {
        definitionId = 0;
        allowTemporarySubstitute = false;
        if (place == null || !TryGet(place, out var rule) || rule.Mode != ShelfFilterMode.ExactProduct)
            return false;

        definitionId = rule.ProductDefinitionId;
        allowTemporarySubstitute = rule.AllowTemporarySubstitute;
        return definitionId != 0;
    }

    public static bool TryGetRule(ProductPricePlace? place, out ShelfRule rule)
    {
        rule = null!;
        return place != null && TryGet(place, out rule);
    }

    public static bool IsBlocked(ProductPricePlace? place)
    {
        return place != null &&
               TryGet(place, out var rule) &&
               rule.Mode == ShelfFilterMode.Blocked;
    }

    public static bool IsFree(ProductPricePlace? place)
    {
        return place != null &&
               TryGet(place, out var rule) &&
               rule.Mode == ShelfFilterMode.Free;
    }

    private static void Save()
    {
        if (string.IsNullOrWhiteSpace(_path))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(Rules, Formatting.Indented));
            File.Move(temporaryPath, _path, true);
        }
        catch (Exception exception)
        {
            MelonLogger.Error($"Shelf Filters: не удалось сохранить правила: {exception}");
        }
    }

    private static void Load()
    {
        if (TryReadLatest(out var loaded))
        {
            ReplaceRules(loaded);
            MelonLogger.Msg($"Shelf Filters: загружено правил: {Rules.Count}.");
        }
    }

    private static bool ReloadLatestBeforeMutation()
    {
        if (!TryReadLatest(out var loaded))
        {
            MelonLogger.Error(
                "Shelf Filters: изменение правила отменено, чтобы не перезаписать недоступный или повреждённый файл правил.");
            return false;
        }

        ReplaceRules(loaded);
        return true;
    }

    private static bool TryReadLatest(out Dictionary<string, ShelfRule> loaded)
    {
        loaded = new Dictionary<string, ShelfRule>(StringComparer.Ordinal);
        if (!File.Exists(_path))
            return true;

        try
        {
            var deserialized = JsonConvert.DeserializeObject<Dictionary<string, ShelfRule>>(File.ReadAllText(_path));
            if (deserialized == null)
                return true;

            foreach (var pair in deserialized)
                loaded[pair.Key] = pair.Value;
            return true;
        }
        catch (Exception exception)
        {
            MelonLogger.Error($"Shelf Filters: файл правил повреждён и был проигнорирован: {exception}");
            return false;
        }
    }

    private static void ReplaceRules(Dictionary<string, ShelfRule> latest)
    {
        Rules.Clear();
        foreach (var pair in latest)
            Rules[pair.Key] = pair.Value;
    }

    private static bool TryGet(ProductPricePlace place, out ShelfRule rule)
    {
        rule = null!;
        return TryKey(place, out var key) && Rules.TryGetValue(key, out rule!);
    }

    private static bool TryKey(ProductPricePlace place, out string key)
    {
        key = place.PersistentId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(key))
            return true;

        MelonLogger.Warning("Shelf Filters: у секции полки нет постоянного идентификатора; правило не сохранено.");
        return false;
    }
}

internal static class ShelfSceneCache
{
    // Scene-wide discovery is only a fallback for shelves created or moved by
    // the game after the scene event.  Rules and settings refresh indicators
    // directly, so a frequent full Unity object scan is unnecessary.
    private const float DiscoveryIntervalSeconds = 30f;
    private const float FailedDiscoveryRetrySeconds = 2f;
    private static readonly List<ProductPricePlace> Places = new();
    private static readonly List<ProductPricePlace> DiscoveryScratch = new();
    private static float _nextDiscoveryAt;

    public static IReadOnlyList<ProductPricePlace> GetPlaces(bool forceRefresh = false)
    {
        var now = Time.unscaledTime;
        if (forceRefresh || Places.Count == 0 || now >= _nextDiscoveryAt)
            Refresh(now);
        return Places;
    }

    public static void Reset()
    {
        Places.Clear();
        _nextDiscoveryAt = 0f;
    }

    private static void Refresh(float now)
    {
        DiscoveryScratch.Clear();
        try
        {
            foreach (var place in UnityEngine.Object.FindObjectsOfType<ProductPricePlace>())
            {
                if (place != null)
                    DiscoveryScratch.Add(place);
            }

            // Replace the live cache only after a complete discovery. During world
            // restoration Unity can invalidate an IL2CPP wrapper in the middle of
            // enumeration. Keeping the previous complete snapshot prevents a partial
            // scan from being interpreted as dozens of removed shelf sections.
            Places.Clear();
            Places.AddRange(DiscoveryScratch);
            _nextDiscoveryAt = now + DiscoveryIntervalSeconds;
        }
        catch
        {
            // A scene can be replaced while Unity is enumerating its objects. Retain
            // the last good cache and retry shortly without polling every frame.
            _nextDiscoveryAt = now + FailedDiscoveryRetrySeconds;
        }
        finally
        {
            DiscoveryScratch.Clear();
        }
    }
}

internal sealed class ShelfIndicatorEntry
{
    public GameObject BackgroundObject { get; init; } = null!;
    public MeshRenderer BackgroundRenderer { get; init; } = null!;
    public GameObject IconObject { get; init; } = null!;
    public MeshRenderer IconRenderer { get; init; } = null!;
    public MeshRenderer Source { get; init; } = null!;
    public int DefinitionId { get; set; }
    public bool ShowingBlocked { get; set; }
    public bool ShowingFree { get; set; }
}

internal static class ShelfBindingIndicators
{
    private const string BackgroundName = "ShelfFilters_IndicatorBackground";
    private const string IconName = "ShelfFilters_BoundProductIcon";
    private static readonly Dictionary<string, ShelfIndicatorEntry> Entries = new(StringComparer.Ordinal);
    private static readonly int MainTexPropertyId = Shader.PropertyToID("_MainTex");
    private static readonly int BaseMapPropertyId = Shader.PropertyToID("_BaseMap");
    private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
    private static readonly int BaseColorPropertyId = Shader.PropertyToID("_BaseColor");
    private static Texture2D? _backgroundTexture;
    private static Texture2D? _blockedTexture;
    private static Texture2D? _freeTexture;
    private static bool _errorReported;

    public static void RefreshAll()
    {
        if (!ShelfFiltersMod.FeatureEnabled || !ModSettings.ShowShelfIndicators)
        {
            ClearAll();
            return;
        }

        try
        {
            var activeKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var place in ShelfSceneCache.GetPlaces())
            {
                if (place == null || string.IsNullOrWhiteSpace(place.PersistentId))
                    continue;

                activeKeys.Add(place.PersistentId);
                Refresh(place);
            }

            foreach (var staleKey in Entries.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
                Remove(staleKey);
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    public static void Refresh(ProductPricePlace place)
    {
        if (place == null || string.IsNullOrWhiteSpace(place.PersistentId))
            return;

        var key = place.PersistentId;
        var hasExactRule = RuleStore.TryGetExact(place, out var definitionId);
        var isBlocked = RuleStore.IsBlocked(place);
        var isFree = RuleStore.IsFree(place);
        var shouldShow = hasExactRule || isFree ||
                         (isBlocked && ModSettings.ShowBlockedIndicators) ||
                         ModSettings.ShowEmptyIndicatorBackgrounds;
        if (!ShelfFiltersMod.FeatureEnabled || !ModSettings.ShowShelfIndicators || !shouldShow)
        {
            Remove(key);
            return;
        }

        try
        {
            var source = place._icon;
            if (source == null || source.gameObject == null)
                return;

            if (!Entries.TryGetValue(key, out var entry) ||
                entry.BackgroundObject == null ||
                entry.BackgroundRenderer == null ||
                entry.IconObject == null ||
                entry.IconRenderer == null ||
                entry.Source == null ||
                entry.Source != source)
            {
                Remove(key);
                entry = Create(place, source);
                if (entry == null)
                    return;
                Entries[key] = entry;
            }

            UpdateTransform(entry, place, source);
            entry.BackgroundRenderer.sharedMaterial = source.sharedMaterial;
            entry.IconRenderer.sharedMaterial = source.sharedMaterial;
            entry.BackgroundRenderer.sortingLayerID = source.sortingLayerID;
            entry.IconRenderer.sortingLayerID = source.sortingLayerID;
            entry.BackgroundRenderer.sortingOrder = source.sortingOrder + 1;
            entry.IconRenderer.sortingOrder = source.sortingOrder + 2;
            entry.BackgroundObject.SetActive(true);
            entry.BackgroundRenderer.enabled = true;

            if (isBlocked && ModSettings.ShowBlockedIndicators)
            {
                if (!entry.ShowingBlocked || entry.ShowingFree)
                    ApplyTexture(entry.IconRenderer, GetBlockedTexture());
                entry.DefinitionId = 0;
                entry.ShowingBlocked = true;
                entry.ShowingFree = false;
                entry.IconObject.SetActive(true);
                entry.IconRenderer.enabled = true;
            }
            else if (isFree)
            {
                if (!entry.ShowingFree || entry.ShowingBlocked)
                    ApplyTexture(entry.IconRenderer, GetFreeTexture());
                entry.DefinitionId = 0;
                entry.ShowingBlocked = false;
                entry.ShowingFree = true;
                entry.IconObject.SetActive(true);
                entry.IconRenderer.enabled = true;
            }
            else if (hasExactRule && TryResolveIcon(definitionId, out var icon))
            {
                if (entry.ShowingBlocked || entry.ShowingFree || entry.DefinitionId != definitionId)
                {
                    ApplyTexture(entry.IconRenderer, icon.texture);
                    entry.DefinitionId = definitionId;
                }
                entry.ShowingBlocked = false;
                entry.ShowingFree = false;
                entry.IconObject.SetActive(true);
                entry.IconRenderer.enabled = true;
            }
            else
            {
                entry.DefinitionId = 0;
                entry.ShowingBlocked = false;
                entry.ShowingFree = false;
                entry.IconRenderer.enabled = false;
                entry.IconObject.SetActive(false);
            }
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    public static void ClearAll()
    {
        foreach (var entry in Entries.Values)
        {
            if (entry.BackgroundObject != null)
                UnityEngine.Object.Destroy(entry.BackgroundObject);
            if (entry.IconObject != null)
                UnityEngine.Object.Destroy(entry.IconObject);
        }
        Entries.Clear();
    }

    private static ShelfIndicatorEntry? Create(ProductPricePlace place, MeshRenderer source)
    {
        var sourceFilter = source.GetComponent<MeshFilter>();
        if (sourceFilter == null || sourceFilter.sharedMesh == null)
            return null;

        var backgroundObject = CreateVisualObject(
            BackgroundName,
            place,
            source,
            sourceFilter.sharedMesh,
            source.sortingOrder + 1,
            out var backgroundRenderer);
        var iconObject = CreateVisualObject(
            IconName,
            place,
            source,
            sourceFilter.sharedMesh,
            source.sortingOrder + 2,
            out var iconRenderer);
        ApplyTexture(backgroundRenderer, GetBackgroundTexture());
        iconObject.SetActive(false);

        var entry = new ShelfIndicatorEntry
        {
            BackgroundObject = backgroundObject,
            BackgroundRenderer = backgroundRenderer,
            IconObject = iconObject,
            IconRenderer = iconRenderer,
            Source = source,
            DefinitionId = 0,
            ShowingBlocked = false,
            ShowingFree = false
        };
        UpdateTransform(entry, place, source);
        return entry;
    }

    private static GameObject CreateVisualObject(
        string name,
        ProductPricePlace place,
        MeshRenderer source,
        Mesh mesh,
        int sortingOrder,
        out MeshRenderer renderer)
    {
        var visualObject = new GameObject(name)
        {
            layer = source.gameObject.layer,
            hideFlags = HideFlags.DontSave
        };
        // ProductPricePlace survives price-tag visual refreshes. Parenting the mod
        // visual to it keeps the indicator alive when the game hides or replaces the
        // icon container, while still making it follow moved furniture.
        visualObject.transform.SetParent(place.transform, false);

        var filter = visualObject.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;

        renderer = visualObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = source.sharedMaterial;
        renderer.sortingLayerID = source.sortingLayerID;
        renderer.sortingOrder = sortingOrder;
        renderer.enabled = true;
        return visualObject;
    }

    private static void UpdateTransform(
        ShelfIndicatorEntry entry,
        ProductPricePlace place,
        MeshRenderer source)
    {
        var sourceTransform = source.transform;
        SetWorldScale(entry.BackgroundObject.transform, sourceTransform.lossyScale * 1.02f);
        entry.BackgroundObject.transform.rotation = sourceTransform.rotation;
        SetWorldScale(entry.IconObject.transform, sourceTransform.lossyScale * 0.72f);
        entry.IconObject.transform.rotation = sourceTransform.rotation;

        var width = source.localBounds.size.x * Math.Abs(sourceTransform.lossyScale.x);
        if (width <= 0.001f || float.IsNaN(width) || float.IsInfinity(width))
            width = 0.055f;

        // Determine visual "left" from the icon-to-price direction instead of
        // Transform.right. Several shelf prefabs use a negative X scale, which
        // mirrors Transform.right and previously moved the duplicate over the price.
        var horizontalAxis = sourceTransform.TransformVector(Vector3.right);
        if (horizontalAxis.sqrMagnitude <= 0.000001f)
            horizontalAxis = sourceTransform.right;
        horizontalAxis.Normalize();

        var awayFromText = -horizontalAxis;
        var priceText = place._priceText;
        if (priceText != null)
        {
            var toPrice = priceText.transform.position - sourceTransform.position;
            awayFromText = Vector3.Dot(horizontalAxis, toPrice) >= 0f
                ? -horizontalAxis
                : horizontalAxis;
        }

        var position = sourceTransform.position + awayFromText * (width * 1.48f);
        entry.BackgroundObject.transform.position = position;
        entry.IconObject.transform.position = position;
    }

    private static void SetWorldScale(Transform target, Vector3 worldScale)
    {
        var parent = target.parent;
        if (parent == null)
        {
            target.localScale = worldScale;
            return;
        }

        var parentScale = parent.lossyScale;
        target.localScale = new Vector3(
            Math.Abs(parentScale.x) > 0.000001f ? worldScale.x / parentScale.x : worldScale.x,
            Math.Abs(parentScale.y) > 0.000001f ? worldScale.y / parentScale.y : worldScale.y,
            Math.Abs(parentScale.z) > 0.000001f ? worldScale.z / parentScale.z : worldScale.z);
    }

    private static bool TryResolveIcon(int definitionId, out Sprite icon)
    {
        icon = null!;
        try
        {
            var definition = DataDefinition<ProductDefinition>.GetWithId(definitionId);
            if (definition?.Icon == null || definition.Icon.texture == null)
                return false;

            icon = definition.Icon;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyTexture(MeshRenderer renderer, Texture texture)
    {
        var properties = new MaterialPropertyBlock();
        properties.SetTexture(MainTexPropertyId, texture);
        properties.SetTexture(BaseMapPropertyId, texture);
        properties.SetColor(ColorPropertyId, Color.white);
        properties.SetColor(BaseColorPropertyId, Color.white);
        renderer.SetPropertyBlock(properties);
    }

    private static Texture2D GetBackgroundTexture()
    {
        if (_backgroundTexture != null)
            return _backgroundTexture;

        const int size = 64;
        const int border = 5;
        const float outerRadius = 10f;
        const float innerRadius = 6f;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "ShelfFilters_IndicatorBackgroundTexture",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave
        };
        var borderColor = new Color(0.08f, 0.72f, 0.86f, 1f);
        var fillColor = new Color(0.018f, 0.045f, 0.082f, 1f);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var pixelX = x + 0.5f;
                var pixelY = y + 0.5f;
                if (!IsInsideRoundedRectangle(pixelX, pixelY, 0f, size, outerRadius))
                {
                    texture.SetPixel(x, y, Color.clear);
                    continue;
                }

                var insideFill = IsInsideRoundedRectangle(
                    pixelX,
                    pixelY,
                    border,
                    size - border,
                    innerRadius);
                texture.SetPixel(x, y, insideFill ? fillColor : borderColor);
            }
        }
        texture.Apply();
        _backgroundTexture = texture;
        return texture;
    }

    private static Texture2D GetBlockedTexture()
    {
        if (_blockedTexture != null)
            return _blockedTexture;

        const int size = 64;
        const int margin = 10;
        const int outlineWidth = 7;
        const int crossWidth = 4;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "ShelfFilters_BlockedTexture",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave
        };
        var outlineColor = new Color(0.28f, 0.01f, 0.02f, 1f);
        var crossColor = new Color(1f, 0.08f, 0.1f, 1f);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (x < margin || x >= size - margin || y < margin || y >= size - margin)
                {
                    texture.SetPixel(x, y, Color.clear);
                    continue;
                }

                var firstDiagonal = Math.Abs(x - y);
                var secondDiagonal = Math.Abs(x - (size - 1 - y));
                var distance = Math.Min(firstDiagonal, secondDiagonal);
                texture.SetPixel(
                    x,
                    y,
                    distance <= crossWidth
                        ? crossColor
                        : distance <= outlineWidth
                            ? outlineColor
                            : Color.clear);
            }
        }
        texture.Apply();
        _blockedTexture = texture;
        return texture;
    }

    private static Texture2D GetFreeTexture()
    {
        if (_freeTexture != null)
            return _freeTexture;

        const int size = 64;
        const int lineWidth = 4;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "ShelfFilters_FreeTexture",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave
        };
        var shadow = new Color(0.01f, 0.12f, 0.18f, 1f);
        var color = new Color(0.15f, 0.9f, 1f, 1f);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var upperLine = Math.Abs(y - 41) <= lineWidth && x >= 13 && x <= 48;
                var lowerLine = Math.Abs(y - 22) <= lineWidth && x >= 15 && x <= 50;
                var upperArrow = x >= 42 && x <= 53 && Math.Abs(y - (x - 7)) <= lineWidth;
                var lowerArrow = x >= 10 && x <= 21 && Math.Abs(y - (x + 7)) <= lineWidth;
                var mark = upperLine || lowerLine || upperArrow || lowerArrow;
                if (!mark)
                {
                    texture.SetPixel(x, y, Color.clear);
                    continue;
                }

                var edge = Math.Abs(y - 41) == lineWidth || Math.Abs(y - 22) == lineWidth;
                texture.SetPixel(x, y, edge ? shadow : color);
            }
        }
        texture.Apply();
        _freeTexture = texture;
        return texture;
    }

    private static bool IsInsideRoundedRectangle(
        float x,
        float y,
        float minimum,
        float maximum,
        float radius)
    {
        var nearestX = Math.Clamp(x, minimum + radius, maximum - radius);
        var nearestY = Math.Clamp(y, minimum + radius, maximum - radius);
        var deltaX = x - nearestX;
        var deltaY = y - nearestY;
        return deltaX * deltaX + deltaY * deltaY <= radius * radius;
    }

    private static void Remove(string key)
    {
        if (!Entries.TryGetValue(key, out var entry))
            return;

        Entries.Remove(key);
        if (entry.BackgroundObject != null)
            UnityEngine.Object.Destroy(entry.BackgroundObject);
        if (entry.IconObject != null)
            UnityEngine.Object.Destroy(entry.IconObject);
    }

    private static void ReportError(Exception exception)
    {
        if (_errorReported)
            return;

        _errorReported = true;
        MelonLogger.Error($"Shelf Filters: не удалось обновить иконки привязок: {exception}");
    }
}

internal static class ProductNames
{
    public static string Resolve(int definitionId)
    {
        try
        {
            var definition = DataDefinition<ProductDefinition>.GetWithId(definitionId);
            if (definition != null)
            {
                if (!string.IsNullOrWhiteSpace(definition.FullName))
                    return definition.FullName;
                if (!string.IsNullOrWhiteSpace(definition.Name))
                    return definition.Name;
            }
        }
        catch
        {
            // Definitions may not be initialized while a scene is changing.
        }

        return $"товар #{definitionId}";
    }
}

internal static class SortingPatches
{
    private const float EmptyPlacementRetryDelay = 1f;
    private const float NativeAssignmentClearRetryDelay = 1f;
    private const float WaitingTargetRefreshDelay = 1.5f;
    private const float DroppedProductRespawnGraceSeconds = 8f;
    private const float DroppedProductDropRetryDelay = 1.5f;
    private const float FailedPlacementRetryDelay = 12f;

    private readonly struct EmptyPlacementCandidate
    {
        public EmptyPlacementCandidate(
            ProductPricePlace place,
            ShelfProducts shelf,
            Vector3 position,
            int requiredDefinitionId,
            int policyRank,
            bool usesModRule)
        {
            Place = place;
            Shelf = shelf;
            Position = position;
            RequiredDefinitionId = requiredDefinitionId;
            PolicyRank = policyRank;
            UsesModRule = usesModRule;
        }

        public ProductPricePlace Place { get; }
        public ShelfProducts Shelf { get; }
        public Vector3 Position { get; }
        public int RequiredDefinitionId { get; }
        public int PolicyRank { get; }
        public bool UsesModRule { get; }
    }

    private readonly record struct ExactTargetState(
        bool HasTarget,
        bool IsReady,
        ProductPricePlace? Target,
        float ExpiresAt);

    private readonly record struct ProductPlaceSearchState(int DefinitionId, int WorkerId);

    private readonly record struct FailedPlacementTarget(
        int PricePlaceId,
        int ProductPlaceId,
        int DefinitionId,
        int ObservedCount,
        int TaskVersion,
        float ExpiresAt);

    private sealed class WaitingDroppedItem
    {
        public string ProductId { get; init; } = string.Empty;
        public int DefinitionId { get; set; }
        public ProductPricePlace? Target { get; set; }
        public PickupDropped? Pickup { get; set; }
        public long Sequence { get; init; }
        public float AwaitingRespawnUntil { get; set; }
        public float NextDropAttemptAt { get; set; }
        public float NextTargetCheckAt { get; set; }
        public bool IsTargetReady { get; set; }
    }

    private static readonly HarmonyLib.Harmony Harmony = new("ru.midfr.animeshop.shelffilters");
    private static readonly MethodInfo? CancelCurrentTaskAndFindShelfAsyncMethod =
        AccessTools.Method(typeof(EmployeeSortingController), "CancelCurrentTaskAndFindShelfAsync");
    private static DateTime _nextAiSafetyWarningUtc = DateTime.MinValue;
    private static int _suppressedAiSafetyWarnings;
    private static readonly HashSet<int> LastAvailablePickupDefinitionIds = new();
    private static readonly HashSet<int> AvailablePickupDefinitionScratch = new();
    private static readonly List<EmptyPlacementCandidate> EmptyPlacementCandidateScratch = new();
    private static readonly Dictionary<long, float> EmptyPlacementRetryAfter = new();
    private static readonly Dictionary<int, NativeAssignmentClearRequest> NativeAssignmentClearRequests = new();
    private static readonly Dictionary<int, ExactTargetState> NativeWaitingTargetStateCache = new();
    private static readonly Dictionary<int, long> RecoveredFullTargets = new();
    private static readonly Dictionary<int, long> RecoveredDroppedTargets = new();
    private static readonly Dictionary<int, FailedPlacementTarget> FailedPlacementTargets = new();
    private static readonly Dictionary<string, WaitingDroppedItem> WaitingDroppedItems =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> WaitingDroppedRemovalScratch = new();
    private static long _nextWaitingDroppedSequence;
    [ThreadStatic]
    private static int _activeAiProductDefinitionId;
    [ThreadStatic]
    private static int _activeAiWorkerId;
    [ThreadStatic]
    private static EmployeeSortingController? _pendingPlacementWorker;
    [ThreadStatic]
    private static ProductPricePlace? _pendingPlacementPricePlace;
    [ThreadStatic]
    private static int _pendingPlacementProductPlaceId;
    [ThreadStatic]
    private static int _pendingPlacementDefinitionId;
    [ThreadStatic]
    private static int _pendingPlacementFrame;
    [ThreadStatic]
    private static bool _waitingDroppedPickupSearchActive;

    private readonly record struct NativeAssignmentClearRequest(string ProductId, float RetryAfter);

    public static void Install()
    {
        Patch("CollectAvailablePickups", postfix: nameof(CollectAvailablePickupsPostfix));
        Patch("TryFindEmptyPlacementCandidate", nameof(TryFindEmptyPlacementCandidatePrefix));
        Patch("GetConfiguredPlace", nameof(GetConfiguredPlacePrefix));
        Patch("IsValidEmployeePlacement", postfix: nameof(IsValidEmployeePlacementPostfix));
        Patch("FindNextProductPlace", nameof(ProductPlaceSearchScopePrefix), finalizer: nameof(ProductPlaceSearchScopeFinalizer));
        Patch("TryRetargetCurrentPickup", nameof(TryRetargetCurrentPickupPrefix), finalizer: nameof(ProductPlaceSearchScopeFinalizer));
        Patch("FindShelf", nameof(FindShelfPrefix));
        Patch("PlaceChanged", postfix: nameof(RecoverFullCurrentTargetPostfix));
        Patch("CheckShelfs", postfix: nameof(RecoverFullCurrentTargetPostfix));
        Patch("CheckPickups", postfix: nameof(RecoverStaleDroppedTaskPostfix));
        Patch(
            "FindNearestDroppedPickup",
            nameof(WaitingDroppedPickupSearchScopePrefix),
            nameof(FindNearestDroppedPickupPostfix),
            nameof(WaitingDroppedPickupSearchScopeFinalizer));
        Patch("IsDroppedPickupReady", postfix: nameof(IsDroppedPickupReadyPostfix));
        Patch("TryTakeDroppedProductInHands", postfix: nameof(TryTakeDroppedProductInHandsPostfix));
        Patch("TryMoveCarriedDroppedProductToShelf", postfix: nameof(TryMoveCarriedDroppedProductToShelfPostfix));
        Patch("HandleMoveFailed", nameof(HandleMoveFailedPrefix));
        Patch("ReleaseCarriedDroppedProduct", postfix: nameof(ReleaseCarriedDroppedProductPostfix));
        PatchProductPlaceTryAddItem();
        PatchProductPricePlaceIsLocked();
        PatchProductPricePlaceVisuals();
        PatchGameEscape();
    }

    public static void ResetRuntimeState()
    {
        LastAvailablePickupDefinitionIds.Clear();
        AvailablePickupDefinitionScratch.Clear();
        EmptyPlacementCandidateScratch.Clear();
        EmptyPlacementRetryAfter.Clear();
        NativeAssignmentClearRequests.Clear();
        WaitingDroppedItems.Clear();
        WaitingDroppedRemovalScratch.Clear();
        NativeWaitingTargetStateCache.Clear();
        RecoveredFullTargets.Clear();
        RecoveredDroppedTargets.Clear();
        FailedPlacementTargets.Clear();
        _nextWaitingDroppedSequence = 0;
        _activeAiProductDefinitionId = 0;
        _activeAiWorkerId = 0;
        ClearPendingPlacementAttempt();
        _waitingDroppedPickupSearchActive = false;
    }

    public static void InvalidateWaitingDestinationCache()
    {
        NativeWaitingTargetStateCache.Clear();
        foreach (var waiting in WaitingDroppedItems.Values)
            waiting.NextTargetCheckAt = 0f;
    }

    private static void PatchProductPricePlaceVisuals()
    {
        var original = AccessTools.Method(typeof(ProductPricePlace), "ApplyVisuals");
        if (original == null)
            throw new MissingMethodException(typeof(ProductPricePlace).FullName, "ApplyVisuals");

        Harmony.Patch(
            original,
            postfix: new HarmonyMethod(typeof(SortingPatches), nameof(ProductPricePlaceApplyVisualsPostfix)));
        MelonLogger.Msg("Shelf Filters: подключено восстановление индикаторов после обновления ценника.");
    }

    private static void ProductPricePlaceApplyVisualsPostfix(ProductPricePlace __instance)
    {
        if (!ShelfFiltersMod.FeatureEnabled || !ModSettings.ShowShelfIndicators || __instance == null)
            return;

        ShelfBindingIndicators.Refresh(__instance);
    }

    private static void PatchProductPricePlaceIsLocked()
    {
        var original = AccessTools.PropertyGetter(typeof(ProductPricePlace), nameof(ProductPricePlace.IsLocked));
        if (original == null)
            throw new MissingMethodException(typeof(ProductPricePlace).FullName, "get_IsLocked");

        Harmony.Patch(
            original,
            postfix: new HarmonyMethod(typeof(SortingPatches), nameof(ProductPricePlaceIsLockedPostfix)));
        MelonLogger.Msg("Shelf Filters: подключено безопасное исключение секций из поиска выкладчика.");
    }

    private static void PatchGameEscape()
    {
        var original = AccessTools.Method(typeof(InputService), "OnEscInput");
        if (original == null)
            throw new MissingMethodException(typeof(InputService).FullName, "OnEscInput");

        Harmony.Patch(original, prefix: new HarmonyMethod(typeof(SortingPatches), nameof(OnEscInputPrefix)));
        MelonLogger.Msg("Shelf Filters: подключено подавление игрового Esc при открытом меню.");
    }

    private static bool OnEscInputPrefix()
    {
        return !ShelfFiltersMod.ShouldBlockGameEscape;
    }

    private static void CollectAvailablePickupsPostfix(
        Il2CppSystem.Collections.Generic.List<PickupProducts> availablePickupsBuffer,
        Il2CppSystem.Collections.Generic.Dictionary<int, byte> availablePickupDefinitionIds)
    {
        if (!ShelfFiltersMod.FeatureEnabled || availablePickupsBuffer == null)
            return;

        try
        {
            AvailablePickupDefinitionScratch.Clear();
            for (var index = 0; index < availablePickupsBuffer.Count; index++)
            {
                var definition = availablePickupsBuffer[index]?.ProductDefinition;
                if (definition != null)
                    AvailablePickupDefinitionScratch.Add(definition.Id);
            }

            // The native last-product memory only has to be realigned when the set
            // of products that can actually be picked up changes.  Version 0.8.1 did
            // this full shelf pass before every carried-item search, which multiplied
            // the work by employees, boxes and items and caused visible stutter.
            var availabilityChanged =
                !LastAvailablePickupDefinitionIds.SetEquals(AvailablePickupDefinitionScratch);
            if (availabilityChanged)
            {
                LastAvailablePickupDefinitionIds.Clear();
                LastAvailablePickupDefinitionIds.UnionWith(AvailablePickupDefinitionScratch);
                SynchronizeRuntimeShelfMemory();
            }
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("обновление списка доступных товаров", exception);
        }
    }

    private static void Patch(string originalName, string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        var original = AccessTools.Method(typeof(EmployeeSortingController), originalName);
        if (original == null)
            throw new MissingMethodException(typeof(EmployeeSortingController).FullName, originalName);

        Harmony.Patch(
            original,
            prefix == null ? null : new HarmonyMethod(typeof(SortingPatches), prefix),
            postfix == null ? null : new HarmonyMethod(typeof(SortingPatches), postfix),
            finalizer: finalizer == null ? null : new HarmonyMethod(typeof(SortingPatches), finalizer));
        MelonLogger.Msg($"Shelf Filters: подключён обработчик {originalName}.");
    }

    private static void PatchProductPlaceTryAddItem()
    {
        var original = AccessTools.Method(typeof(ProductPlace), "TryAddItem");
        if (original == null)
            throw new MissingMethodException(typeof(ProductPlace).FullName, "TryAddItem");

        Harmony.Patch(
            original,
            postfix: new HarmonyMethod(typeof(SortingPatches), nameof(ProductPlaceTryAddItemPostfix)));
        MelonLogger.Msg("Shelf Filters: подключён контроль отказа физической точки выкладки.");
    }

    private static void WaitingDroppedPickupSearchScopePrefix(out bool __state)
    {
        __state = _waitingDroppedPickupSearchActive;
        _waitingDroppedPickupSearchActive = ShelfFiltersMod.FeatureEnabled;
    }

    private static Exception? WaitingDroppedPickupSearchScopeFinalizer(
        Exception? __exception,
        bool __state)
    {
        _waitingDroppedPickupSearchActive = __state;
        return __exception;
    }

    private static void FindShelfPrefix(EmployeeSortingController __instance)
    {
        if (!ShelfFiltersMod.FeatureEnabled || __instance == null ||
            !__instance.HasCarriedDroppedProduct)
        {
            return;
        }

        try
        {
            var definitionId = __instance.CarriedDroppedProductDefinitionId;
            if (definitionId == 0)
                return;

            // A carried product can be restored directly from the save without
            // passing through TryTakeDroppedProductInHands. Reconcile that state
            // before the normal task search so an employee is not left holding an
            // item whose destination is no longer available.
            var nativeTarget = __instance.FindNearestDroppedReturnPlace(
                definitionId,
                __instance.transform.position);
            if (nativeTarget == null)
                TryDeferCarriedDroppedProduct(__instance, "после загрузки задания");
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("восстановление задания с товаром в руках", exception);
        }
    }

    private static void RecoverFullCurrentTargetPostfix(EmployeeSortingController __instance)
    {
        if (!ShelfFiltersMod.FeatureEnabled || __instance == null)
            return;

        try
        {
            var pickup = __instance._pickup;
            var target = __instance._productPricePlace;
            if (pickup == null || pickup.Count <= 0 || target == null ||
                !IsPlaceAtCapacity(target))
            {
                return;
            }

            // The original controller watches HavePoints here, but that flag can
            // remain true after the last physical product slot has been occupied.
            // In that state a stocker waits indefinitely until a customer frees a
            // slot. Use the real Count/MaxCount pair and handle each task/target
            // combination only once; this is event driven and adds no frame loop.
            var workerId = __instance.GetInstanceID();
            var recoveryKey = ((long)__instance._taskVersion << 32) |
                              (uint)target.GetInstanceID();
            if (RecoveredFullTargets.TryGetValue(workerId, out var previousKey) &&
                previousKey == recoveryKey)
            {
                return;
            }

            if (RecoveredFullTargets.Count > 32)
                RecoveredFullTargets.Clear();
            RecoveredFullTargets[workerId] = recoveryKey;

            var definitionId = pickup.ProductDefinition?.Id ?? 0;
            var productName = ProductNames.Resolve(definitionId);
            var remainingCount = pickup.Count;
            var placedCount = target.ProductPlace?.Count ?? 0;
            var capacity = target.ProductPlace?.MaxCount ?? 0;
            var targetDetails = DescribePlacementTarget(target);

            // Do not replace _productPricePlace while PutProductsAsync is still
            // completing at the old shelf. Doing so lets that coroutine add the
            // next item to a remote shelf without walking there. Returning the box
            // ends the current task cleanly; the game's next search will pick a new
            // destination and run the normal MoveToShelf path.
            if (__instance._isPickupTaken && __instance.TryMoveCurrentPickupToOrigin())
            {
                MelonLogger.Msg(
                    $"Shelf Filters: остаток коробки возвращается без удалённой выкладки — «{productName}», " +
                    $"секция {placedCount}/{capacity}, в коробке осталось {remainingCount}; " +
                    targetDetails);
                return;
            }

            MelonLogger.Warning(
                $"Shelf Filters: отменено задание с заполненной секцией — «{productName}», " +
                $"секция {placedCount}/{capacity}, в коробке осталось {remainingCount}; " +
                targetDetails);
            __instance.AbortCurrentPickupTask();
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("освобождение выкладчика от заполненной секции", exception);
        }
    }

    private static void FindNearestDroppedPickupPostfix(
        EmployeeSortingController __instance,
        Vector3 employeePosition,
        ref PickupDropped __result)
    {
        if (!ShelfFiltersMod.FeatureEnabled || __instance == null || __result == null)
            return;

        try
        {
            if (!TryResolveDroppedIdentity(__result, out var productId, out var definitionId))
                return;

            // This is the authoritative native destination check. It uses the same
            // method the employee would call immediately after taking the item, but
            // performs it before the walk and pickup animations. If there is no
            // destination, the item remains on the floor and the employee is free to
            // choose another task instead of entering the carried-product retry loop.
            var nativeTarget = __instance.FindNearestDroppedReturnPlace(definitionId, employeePosition);
            if (nativeTarget != null)
                return;

            var targetState = ResolvePreferredWaitingTargetState(definitionId);
            var waiting = EnqueueWaitingDroppedItem(
                productId,
                definitionId,
                targetState.Target,
                __result,
                0f);
            waiting.IsTargetReady = false;
            waiting.NextTargetCheckAt = Time.unscaledTime + WaitingTargetRefreshDelay;
            NativeWaitingTargetStateCache[definitionId] = new ExactTargetState(
                targetState.HasTarget,
                false,
                targetState.Target,
                Time.unscaledTime + WaitingTargetRefreshDelay);
            __result = null!;
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("проверка назначения до поднятия товара", exception);
        }
    }

    private static void IsDroppedPickupReadyPostfix(
        EmployeeSortingController __instance,
        PickupDropped droppedPickup,
        ref bool __result)
    {
        // Only filter candidates while the game is actively choosing a loose
        // product. Its separate "is anything still moving?" probe must see the
        // original result, otherwise one deferred item would keep every stocker
        // in the native retry loop instead of letting it continue with other work.
        if (!__result || !_waitingDroppedPickupSearchActive ||
            !ShelfFiltersMod.FeatureEnabled || droppedPickup == null)
        {
            return;
        }

        try
        {
            if (!TryResolveDroppedIdentity(droppedPickup, out var productId, out var definitionId))
                return;

            if (WaitingDroppedItems.TryGetValue(productId, out var waiting))
            {
                waiting.Pickup = droppedPickup;
                waiting.AwaitingRespawnUntil = 0f;
                if (!RefreshWaitingTarget(__instance, waiting, Time.unscaledTime))
                {
                    WaitingDroppedItems.Remove(productId);
                }
                else if (!waiting.IsTargetReady)
                {
                    __result = false;
                    return;
                }
            }
            // Never hide an ordinary floor item merely because another queued item
            // has become ready. Versions 0.9.6-0.9.7 exposed only the oldest ready
            // entry. If that object was unreachable or already targeted by another
            // worker, every other loose product was starved indefinitely. New items
            // now remain visible to the game's own nearest-item search; only this
            // exact product is temporarily hidden while it has no valid destination.
        }
        catch (Exception exception)
        {
            // Fail open: the original game may pick this product if queue metadata
            // is stale. It is preferable to losing the whole employee task loop.
            WarnAiFailOpen("очередь поднятых с пола товаров", exception);
        }
    }

    private static void TryTakeDroppedProductInHandsPostfix(
        PickupDropped droppedPickup,
        bool __result)
    {
        if (!__result)
            return;

        try
        {
            if (droppedPickup != null &&
                TryResolveDroppedIdentity(droppedPickup, out var productId, out _))
            {
                WaitingDroppedItems.Remove(productId);
            }
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("завершение ожидания товара", exception);
        }
    }

    private static void RecoverStaleDroppedTaskPostfix(EmployeeSortingController __instance)
    {
        if (!ShelfFiltersMod.FeatureEnabled || __instance == null ||
            __instance.HasCarriedDroppedProduct)
        {
            return;
        }

        try
        {
            var droppedPickup = __instance._targetDroppedPickup;
            if (droppedPickup == null ||
                !TryResolveDroppedIdentity(droppedPickup, out var productId, out var definitionId) ||
                !WaitingDroppedItems.TryGetValue(productId, out var waiting))
            {
                return;
            }

            waiting.Pickup = droppedPickup;
            waiting.AwaitingRespawnUntil = 0f;
            if (!RefreshWaitingTarget(__instance, waiting, Time.unscaledTime))
            {
                WaitingDroppedItems.Remove(productId);
                return;
            }

            if (waiting.IsTargetReady)
                return;

            // Candidate selection is allowed to reject a loose item, but it must
            // never leave the employee owning the old reservation. That stale
            // task blocks another stocker and can also leave the first employee's
            // visual state at the pickup position until the role is changed.
            var workerId = __instance.GetInstanceID();
            var recoveryKey = ((long)__instance._taskVersion << 32) |
                              (uint)droppedPickup.GetInstanceID();
            if (RecoveredDroppedTargets.TryGetValue(workerId, out var previousKey) &&
                previousKey == recoveryKey)
            {
                return;
            }

            if (RecoveredDroppedTargets.Count > 32)
                RecoveredDroppedTargets.Clear();
            RecoveredDroppedTargets[workerId] = recoveryKey;

            MelonLogger.Warning(
                $"Shelf Filters: сброшена зависшая задача с предметом «{ProductNames.Resolve(definitionId)}»; " +
                "выкладчик продолжит обычный поиск работы.");
            if (CancelCurrentTaskAndFindShelfAsyncMethod == null)
            {
                throw new MissingMethodException(
                    typeof(EmployeeSortingController).FullName,
                    "CancelCurrentTaskAndFindShelfAsync");
            }

            CancelCurrentTaskAndFindShelfAsyncMethod.Invoke(__instance, null);
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("освобождение резервации предмета с пола", exception);
        }
    }

    private static void TryMoveCarriedDroppedProductToShelfPostfix(
        EmployeeSortingController __instance,
        bool __result)
    {
        if (__result || !ShelfFiltersMod.FeatureEnabled || __instance == null)
            return;

        TryDeferCarriedDroppedProduct(__instance, "после неудачного поиска секции");
    }

    private static void HandleMoveFailedPrefix(EmployeeSortingController __instance)
    {
        if (!ShelfFiltersMod.FeatureEnabled || __instance == null)
            return;

        TryDeferCarriedDroppedProduct(__instance, "после ошибки движения к секции");
    }

    private static void ReleaseCarriedDroppedProductPostfix(EmployeeSortingController __instance)
    {
        if (!ShelfFiltersMod.FeatureEnabled || __instance == null ||
            !__instance.HasCarriedDroppedProduct)
        {
            return;
        }

        TryDeferCarriedDroppedProduct(__instance, "при завершении задания");
    }

    private static bool TryDeferCarriedDroppedProduct(
        EmployeeSortingController instance,
        string reason)
    {
        try
        {
            if (!instance.HasCarriedDroppedProduct)
                return false;

            var definitionId = instance.CarriedDroppedProductDefinitionId;
            var productId = instance.CarriedDroppedProductId.ToString();
            if (definitionId == 0 || IsEmptyProductId(productId))
                return false;

            var targetState = ResolvePreferredWaitingTargetState(definitionId);
            var now = Time.unscaledTime;
            var waiting = EnqueueWaitingDroppedItem(
                productId,
                definitionId,
                targetState.Target,
                null,
                now + DroppedProductRespawnGraceSeconds);
            waiting.Pickup = null;
            waiting.IsTargetReady = false;
            if (now < waiting.NextDropAttemptAt)
                return false;

            waiting.NextDropAttemptAt = now + DroppedProductDropRetryDelay;
            if (!instance.TryDropCarriedDroppedProductForDespawn())
                return false;

            waiting.AwaitingRespawnUntil = now + DroppedProductRespawnGraceSeconds;
            MelonLogger.Msg(
                $"Shelf Filters: «{ProductNames.Resolve(definitionId)}» возвращён в очередь {reason}.");
            return true;
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("возврат неподходящего товара в очередь", exception);
            return false;
        }
    }

    private static WaitingDroppedItem EnqueueWaitingDroppedItem(
        string productId,
        int definitionId,
        ProductPricePlace? target,
        PickupDropped? pickup,
        float awaitingRespawnUntil)
    {
        if (!WaitingDroppedItems.TryGetValue(productId, out var waiting))
        {
            waiting = new WaitingDroppedItem
            {
                ProductId = productId,
                DefinitionId = definitionId,
                Target = target,
                Pickup = pickup,
                Sequence = ++_nextWaitingDroppedSequence,
                AwaitingRespawnUntil = awaitingRespawnUntil
            };
            WaitingDroppedItems[productId] = waiting;
            MelonLogger.Msg(
                $"Shelf Filters: «{ProductNames.Resolve(definitionId)}» добавлен в очередь ожидания секции.");
        }
        else
        {
            waiting.DefinitionId = definitionId;
            if (waiting.Target == null)
                waiting.Target = target;
            if (pickup != null)
                waiting.Pickup = pickup;
            if (awaitingRespawnUntil > waiting.AwaitingRespawnUntil)
                waiting.AwaitingRespawnUntil = awaitingRespawnUntil;
        }

        waiting.NextTargetCheckAt = 0f;
        return waiting;
    }

    private static bool RefreshWaitingTarget(
        EmployeeSortingController instance,
        WaitingDroppedItem waiting,
        float now)
    {
        if (now < waiting.NextTargetCheckAt)
            return true;

        waiting.NextTargetCheckAt = now + WaitingTargetRefreshDelay;
        var nativeState = GetNativeWaitingTargetState(instance, waiting.DefinitionId, now);
        if (nativeState.IsReady && nativeState.Target != null)
        {
            waiting.Target = nativeState.Target;
            waiting.IsTargetReady = true;
            return true;
        }

        // Keep the item in the queue even while no compatible section exists.
        // Removing it here was the flaw in 0.9.5: on the next native pass the same
        // item became eligible again and the employee picked it up repeatedly.
        var preferred = ResolvePreferredWaitingTargetState(waiting.DefinitionId);
        waiting.Target = preferred.Target;
        waiting.IsTargetReady = false;
        return true;
    }

    private static ExactTargetState GetNativeWaitingTargetState(
        EmployeeSortingController instance,
        int definitionId,
        float now)
    {
        if (NativeWaitingTargetStateCache.TryGetValue(definitionId, out var cached) &&
            now < cached.ExpiresAt)
        {
            return cached;
        }

        ProductPricePlace? target = null;
        if (instance != null)
        {
            target = instance.FindNearestDroppedReturnPlace(
                definitionId,
                instance.transform.position);
        }

        var resolved = target != null
            ? new ExactTargetState(true, true, target, now + WaitingTargetRefreshDelay)
            : ResolvePreferredWaitingTargetState(definitionId) with
            {
                IsReady = false,
                ExpiresAt = now + WaitingTargetRefreshDelay
            };
        NativeWaitingTargetStateCache[definitionId] = resolved;
        return resolved;
    }

    private static ExactTargetState ResolvePreferredWaitingTargetState(int definitionId)
    {
        var exact = ResolveExactTargetState(definitionId);
        if (exact.HasTarget)
            return exact;

        ProductPricePlace? freeTarget = null;
        ProductPricePlace? rememberedTarget = null;
        foreach (var place in ShelfSceneCache.GetPlaces())
        {
            if (place == null || !place.HavePoints || place.ProductPlace == null ||
                !CanAcceptDefinition(place, definitionId) ||
                !RuleStore.Allows(place, definitionId))
            {
                continue;
            }

            if (place.ProductPlace.Count > 0)
            {
                if (ShelfProductResolver.TryResolvePhysical(place, out var physicalDefinition) &&
                    physicalDefinition.Id == definitionId)
                {
                    return new ExactTargetState(true, false, place, 0f);
                }

                continue;
            }

            if (RuleStore.IsFree(place))
                freeTarget ??= place;
            else if (place.LastDefinitionId == definitionId)
                rememberedTarget ??= place;
        }

        var preferred = freeTarget ?? rememberedTarget;
        return preferred == null
            ? new ExactTargetState(false, false, null, 0f)
            : new ExactTargetState(true, false, preferred, 0f);
    }

    private static ExactTargetState ResolveExactTargetState(int definitionId)
    {
        ProductPricePlace? firstTarget = null;
        foreach (var place in ShelfSceneCache.GetPlaces())
        {
            if (place == null || !RuleStore.TryGetExact(place, out var desiredDefinitionId) ||
                desiredDefinitionId != definitionId)
            {
                continue;
            }

            firstTarget ??= place;
            if (IsExactTargetReady(place, definitionId))
                return new ExactTargetState(true, true, place, 0f);
        }

        return firstTarget == null
            ? new ExactTargetState(false, false, null, 0f)
            : new ExactTargetState(true, false, firstTarget, 0f);
    }

    private static bool IsExactTargetReady(ProductPricePlace place, int definitionId)
    {
        if (place == null || !RuleStore.TryGetExact(place, out var desiredDefinitionId) ||
            desiredDefinitionId != definitionId || !place.HavePoints || place.IsLocked ||
            place.ProductPlace == null || !CanAcceptDefinition(place, definitionId))
        {
            return false;
        }

        if (place.ProductPlace.Count > 0)
        {
            return ShelfProductResolver.TryResolvePhysical(place, out var physicalDefinition) &&
                   physicalDefinition.Id == definitionId;
        }

        if (!PrepareEmptyPlaceForDefinition(place, definitionId))
            return false;

        SetRuntimeDefinition(place, definitionId);
        return true;
    }

    private static bool TryResolveDroppedIdentity(
        PickupDropped droppedPickup,
        out string productId,
        out int definitionId)
    {
        productId = string.Empty;
        definitionId = 0;
        if (droppedPickup == null)
            return false;

        definitionId = droppedPickup.ResolveCurrentDefinitionId();
        var currentProductId = droppedPickup.CurrentProductId;
        productId = currentProductId.ToString();
        if (IsEmptyProductId(productId))
            productId = droppedPickup.ResolveProductId().ToString();

        return definitionId != 0 && !IsEmptyProductId(productId);
    }

    private static bool TryFindEmptyPlacementCandidatePrefix(
        EmployeeSortingController __instance,
        Il2CppSystem.Collections.Generic.List<ShelfProducts> availableShelves,
        Il2CppSystem.Collections.Generic.List<PickupProducts> availablePickups,
        bool requireCompatiblePlace,
        ref ProductPricePlace chosenPlace,
        ref PickupProducts chosenPickup,
        ref ShelfProducts chosenShelf,
        ref bool __result)
    {
        if (!ShelfFiltersMod.FeatureEnabled)
            return true;

        chosenPlace = null!;
        chosenPickup = null!;
        chosenShelf = null!;

        try
        {
            var bestDistance = float.MaxValue;
            var bestPolicyRank = int.MaxValue;
            var bestUsesModRule = false;
            if (availableShelves == null || availablePickups == null)
            {
                __result = false;
                return false;
            }

            var retryKey = ((long)__instance.GetInstanceID() << 1) |
                           (requireCompatiblePlace ? 1L : 0L);
            if (EmptyPlacementRetryAfter.TryGetValue(retryKey, out var retryAfter) &&
                Time.unscaledTime < retryAfter)
            {
                __result = false;
                return false;
            }

            AvailablePickupDefinitionScratch.Clear();
            for (var pickupIndex = 0; pickupIndex < availablePickups.Count; pickupIndex++)
            {
                var availablePickup = availablePickups[pickupIndex];
                var availableDefinition = availablePickup?.ProductDefinition;
                if (availableDefinition != null)
                    AvailablePickupDefinitionScratch.Add(availableDefinition.Id);
            }

            // Shelf/IL2CPP state is invariant during this synchronous search. Build
            // it once instead of re-reading every shelf for every available box.
            // In a large shop this changes the expensive native work from
            // boxes × shelf sections to one pass over the shelf sections.
            EmptyPlacementCandidateScratch.Clear();
            for (var shelfIndex = 0; shelfIndex < availableShelves.Count; shelfIndex++)
            {
                var shelf = availableShelves[shelfIndex];
                if (shelf == null || shelf.Places == null)
                    continue;

                foreach (var place in shelf.Places)
                {
                    if (place == null || !place.HavePoints || place.IsLocked ||
                        place.ProductPlace == null || place.ProductPlace.Count > 0)
                    {
                        continue;
                    }

                    var requiredDefinitionId = 0;
                    var policyRank = 1;
                    var usesModRule = RuleStore.TryGetRule(place, out var rule);
                    if (!usesModRule)
                    {
                        if (place.LastDefinitionId != 0)
                            continue;
                    }
                    else
                    {
                        switch (rule.Mode)
                        {
                            case ShelfFilterMode.Blocked:
                                continue;
                            case ShelfFilterMode.Free:
                                if (!PrepareEmptyPlaceForAvailableDefinition(
                                        place,
                                        AvailablePickupDefinitionScratch,
                                        ref requiredDefinitionId))
                                {
                                    continue;
                                }
                                break;
                            case ShelfFilterMode.ExactProduct:
                                if (AvailablePickupDefinitionScratch.Contains(rule.ProductDefinitionId))
                                {
                                    requiredDefinitionId = rule.ProductDefinitionId;
                                    policyRank = 0;
                                    if (!PrepareEmptyPlaceForExactDefinition(place, requiredDefinitionId))
                                        continue;
                                }
                                else if (rule.AllowTemporarySubstitute)
                                {
                                    policyRank = 2;
                                    if (!PrepareEmptyPlaceForAvailableDefinition(
                                            place,
                                            AvailablePickupDefinitionScratch,
                                            ref requiredDefinitionId))
                                    {
                                        continue;
                                    }
                                }
                                else
                                {
                                    continue;
                                }
                                break;
                            default:
                                continue;
                        }
                    }

                    EmptyPlacementCandidateScratch.Add(new EmptyPlacementCandidate(
                        place,
                        shelf,
                        place.transform.position,
                        requiredDefinitionId,
                        policyRank,
                        usesModRule));
                }
            }

            var workerPosition = __instance.transform.position;
            for (var pickupIndex = 0; pickupIndex < availablePickups.Count; pickupIndex++)
            {
                var pickup = availablePickups[pickupIndex];
                if (pickup == null || pickup.ProductDefinition == null)
                    continue;

                var definition = pickup.ProductDefinition;
                var pickupPosition = pickup.transform.position;
                var pickupDistance = HorizontalDistance(workerPosition, pickupPosition);
                for (var candidateIndex = 0;
                     candidateIndex < EmptyPlacementCandidateScratch.Count;
                     candidateIndex++)
                {
                    var candidate = EmptyPlacementCandidateScratch[candidateIndex];
                    if (candidate.RequiredDefinitionId != 0 &&
                        candidate.RequiredDefinitionId != definition.Id)
                    {
                        continue;
                    }

                    // A filter may narrow the list of valid destinations, but it must
                    // never widen the furniture's physical product compatibility.
                    // The game uses requireCompatiblePlace=false during a relaxed
                    // search pass; treating that as permission to target an
                    // incompatible shelf makes the final native validation reject
                    // the same carried item forever (the stocker "dances" in place).
                    if (!candidate.Place.ProductPlace.CanPut(definition.Type))
                        continue;
                    if (ShouldSkipFailedPlacementTarget(
                            candidate.Place,
                            definition.Id,
                            __instance.GetInstanceID()))
                    {
                        continue;
                    }

                    var distance = pickupDistance + HorizontalDistance(pickupPosition, candidate.Position);
                    if (candidate.PolicyRank > bestPolicyRank ||
                        candidate.PolicyRank == bestPolicyRank && distance >= bestDistance)
                        continue;

                    bestPolicyRank = candidate.PolicyRank;
                    bestDistance = distance;
                    bestUsesModRule = candidate.UsesModRule;
                    chosenPlace = candidate.Place;
                    chosenPickup = pickup;
                    chosenShelf = candidate.Shelf;
                }
            }

            __result = chosenPlace != null && chosenPickup != null;
            if (__result && chosenPlace != null && chosenPickup?.ProductDefinition != null &&
                bestUsesModRule)
            {
                SetRuntimeDefinition(chosenPlace, chosenPickup.ProductDefinition.Id);
            }

            if (__result)
            {
                EmptyPlacementRetryAfter.Remove(retryKey);
            }
            else
            {
                if (EmptyPlacementRetryAfter.Count > 64)
                    EmptyPlacementRetryAfter.Clear();
                EmptyPlacementRetryAfter[retryKey] = Time.unscaledTime + EmptyPlacementRetryDelay;
            }
            return false;
        }
        catch (Exception exception)
        {
            // A stale IL2CPP shelf wrapper must never interrupt the employee task loop.
            // Returning true hands this call back to the untouched game implementation.
            chosenPlace = null!;
            chosenPickup = null!;
            chosenShelf = null!;
            WarnAiFailOpen("поиск пустой секции", exception);
            return true;
        }
    }

    private static void ProductPlaceSearchScopePrefix(
        EmployeeSortingController __instance,
        out ProductPlaceSearchState __state)
    {
        __state = new ProductPlaceSearchState(_activeAiProductDefinitionId, _activeAiWorkerId);
        _activeAiProductDefinitionId = 0;
        _activeAiWorkerId = 0;
        if (!ShelfFiltersMod.FeatureEnabled)
            return;

        try
        {
            var definition = __instance._pickup?.ProductDefinition;
            if (definition != null)
            {
                _activeAiProductDefinitionId = definition.Id;
                _activeAiWorkerId = __instance.GetInstanceID();
            }
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("подготовка поиска следующей секции", exception);
        }
    }

    private static bool TryRetargetCurrentPickupPrefix(
        EmployeeSortingController __instance,
        ref bool __result,
        out ProductPlaceSearchState __state)
    {
        ProductPlaceSearchScopePrefix(__instance, out __state);
        if (!ShelfFiltersMod.FeatureEnabled || __instance == null)
            return true;

        try
        {
            var pickup = __instance._pickup;
            var currentTarget = __instance._productPricePlace;
            if (!__instance._isPickupTaken || pickup == null || pickup.Count <= 0 ||
                currentTarget == null || !RuleStore.TryGetRule(currentTarget, out _) ||
                !IsPlaceAtCapacity(currentTarget))
            {
                return true;
            }

            // PlaceChanged is invoked synchronously from ProductPlace.TryAddItem.
            // The native retarget routine replaces _productPricePlace and starts a
            // new MoveToShelf task before the current PutProductsAsync iteration has
            // finished. The coroutine then immediately uses that replacement on its
            // next iteration and can put an item on a distant Free-filter section
            // without walking there. Returning false here makes the unchanged native
            // PlaceChanged path call TryMoveCurrentPickupToOrigin instead. Its next
            // task performs a fresh search and travels to the new section normally.
            __result = false;
            return false;
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("завершение коробки перед сменой заполненной секции", exception);
            return true;
        }
    }

    private static Exception? ProductPlaceSearchScopeFinalizer(
        Exception? __exception,
        ProductPlaceSearchState __state)
    {
        _activeAiProductDefinitionId = __state.DefinitionId;
        _activeAiWorkerId = __state.WorkerId;
        return __exception;
    }

    private static void ProductPricePlaceIsLockedPostfix(
        ProductPricePlace __instance,
        ref bool __result)
    {
        var definitionId = _activeAiProductDefinitionId;
        if (__result || definitionId == 0 || !ShelfFiltersMod.FeatureEnabled)
            return;

        try
        {
            if (__instance == null || !RuleStore.TryGetRule(__instance, out _))
                return;

            if (ShouldSkipFailedPlacementTarget(__instance, definitionId, _activeAiWorkerId))
            {
                __result = true;
                return;
            }

            // Report a disallowed section as unavailable only to the synchronous AI search.
            // The real IsLocked field is never changed, so async tasks cannot inherit a
            // temporary lock or a stale native wrapper.
            if (!RuleStore.Allows(__instance, definitionId))
            {
                __result = true;
                return;
            }

            // RuleStore describes the player's policy only. It cannot make a manga
            // shelf accept clothes or a regular shelf accept dakimakura. Hide an
            // incompatible destination from every native retarget pass before any
            // remembered product id is reconciled.
            if (!CanAcceptDefinition(__instance, definitionId))
            {
                __result = true;
                return;
            }

            if (__instance.ProductPlace == null || __instance.ProductPlace.Count > 0)
            {
                return;
            }

            // In 1.0.5 FindNextProductPlace can select a mod-managed empty section
            // after the employee already picked up a box. The game's price tag may
            // still remember another product, so its final validation rejects the
            // destination even though our Free/substitute policy allows it. Clear the
            // conflicting native assignment on this task-search pass and keep the
            // place unavailable until the game's own RPC has completed. On the next
            // pass align LastDefinitionId with the product actually being carried.
            if (!PrepareEmptyPlaceForDefinition(__instance, definitionId))
            {
                __result = true;
                return;
            }

            SetRuntimeDefinition(__instance, definitionId);
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("проверка секции при переборе полок", exception);
        }
    }

    public static bool IsProductAvailable(int definitionId)
    {
        return definitionId != 0 && LastAvailablePickupDefinitionIds.Contains(definitionId);
    }

    public static void SynchronizeRuntimeShelfMemory(ProductPricePlace place)
    {
        if (place == null || !ShelfFiltersMod.FeatureEnabled)
            return;

        try
        {
            SynchronizeRuntimeShelfMemoryCore(place);
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("обновление памяти секции", exception);
        }
    }

    private static void SynchronizeRuntimeShelfMemory()
    {
        foreach (var place in ShelfSceneCache.GetPlaces())
            SynchronizeRuntimeShelfMemoryCore(place);
    }

    private static void SynchronizeRuntimeShelfMemoryCore(ProductPricePlace place)
    {
        if (place == null || !RuleStore.TryGetRule(place, out var rule))
            return;

        if (rule.Mode == ShelfFilterMode.Blocked)
            return;

        var isPhysicallyEmpty = place.ProductPlace == null || place.ProductPlace.Count <= 0;
        var physicalDefinitionId = 0;
        if (!isPhysicallyEmpty && ShelfProductResolver.TryResolvePhysical(place, out var physicalDefinition))
            physicalDefinitionId = physicalDefinition.Id;

        if (rule.Mode == ShelfFilterMode.Free)
        {
            place._lastDefinitionId = isPhysicallyEmpty ? 0 : physicalDefinitionId;
            return;
        }

        if (!isPhysicallyEmpty)
        {
            // A different physical product must sell out before an exact policy
            // can switch the section. Keeping its real id prevents mixed stacks.
            if (physicalDefinitionId != 0)
                place._lastDefinitionId = physicalDefinitionId;
            return;
        }

        if (rule.AllowTemporarySubstitute &&
            !LastAvailablePickupDefinitionIds.Contains(rule.ProductDefinitionId))
        {
            // Zero lets the original game consider this physically empty place for
            // a substitute, but only while the assigned product is unavailable.
            place._lastDefinitionId = 0;
            return;
        }

        place._lastDefinitionId = rule.ProductDefinitionId;
    }

    private static bool GetConfiguredPlacePrefix(
        EmployeeSortingController __instance,
        Il2CppSystem.Collections.Generic.List<ShelfProducts> availableShelves,
        Il2CppSystem.Collections.Generic.Dictionary<int, byte> availablePickupDefinitionIds,
        ref ShelfProducts chosenShelf,
        ref ProductPricePlace __result)
    {
        if (!ShelfFiltersMod.FeatureEnabled)
            return true;

        try
        {
            var bestDistance = float.MaxValue;
            var bestPolicyRank = int.MaxValue;
            var bestDefinitionId = 0;
            var bestUsesModRule = false;
            ProductPricePlace bestPlace = null!;
            ShelfProducts bestShelf = null!;

            if (availableShelves != null && availablePickupDefinitionIds != null)
            {
                foreach (var shelf in availableShelves)
                {
                    if (shelf == null || shelf.Places == null)
                        continue;

                    foreach (var place in shelf.Places)
                    {
                        if (place == null || !place.HavePoints || place.IsLocked)
                            continue;
                        if (!TryGetConfiguredCandidate(
                                place,
                                availablePickupDefinitionIds,
                                out var definitionId,
                                out var policyRank,
                                out var usesModRule))
                        {
                            continue;
                        }
                        if (ShouldSkipFailedPlacementTarget(
                                place,
                                definitionId,
                                __instance.GetInstanceID()))
                        {
                            continue;
                        }

                        var candidateDistance = HorizontalDistance(place.transform.position, __instance.transform.position);
                        if (policyRank > bestPolicyRank ||
                            policyRank == bestPolicyRank && candidateDistance >= bestDistance)
                            continue;

                        bestPolicyRank = policyRank;
                        bestDistance = candidateDistance;
                        bestDefinitionId = definitionId;
                        bestUsesModRule = usesModRule;
                        bestPlace = place;
                        bestShelf = shelf;
                    }
                }
            }

            chosenShelf = bestShelf;
            __result = bestPlace;
            if (bestPlace != null && bestUsesModRule)
                SetRuntimeDefinition(bestPlace, bestDefinitionId);
            return false;
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("выбор настроенной секции", exception);
            return true;
        }
    }

    private static bool TryGetConfiguredCandidate(
        ProductPricePlace place,
        Il2CppSystem.Collections.Generic.Dictionary<int, byte> availablePickupDefinitionIds,
        out int definitionId,
        out int policyRank,
        out bool usesModRule)
    {
        definitionId = 0;
        policyRank = int.MaxValue;
        usesModRule = RuleStore.TryGetRule(place, out var rule);
        if (IsPlaceAtCapacity(place))
            return false;

        if (!usesModRule)
        {
            definitionId = place.LastDefinitionId;
            if (definitionId == 0 ||
                !availablePickupDefinitionIds.ContainsKey(definitionId) ||
                !CanAcceptDefinition(place, definitionId))
                return false;

            policyRank = 1;
            return true;
        }

        if (rule.Mode == ShelfFilterMode.Blocked)
            return false;

        var isPhysicallyEmpty = place.ProductPlace == null || place.ProductPlace.Count <= 0;
        var physicalDefinitionId = 0;
        if (!isPhysicallyEmpty && ShelfProductResolver.TryResolvePhysical(place, out var physicalDefinition))
            physicalDefinitionId = physicalDefinition.Id;

        if (rule.Mode == ShelfFilterMode.Free)
        {
            if (isPhysicallyEmpty || physicalDefinitionId == 0 ||
                !availablePickupDefinitionIds.ContainsKey(physicalDefinitionId) ||
                !CanAcceptDefinition(place, physicalDefinitionId))
            {
                return false;
            }

            definitionId = physicalDefinitionId;
            policyRank = 1;
            return true;
        }

        var desiredDefinitionId = rule.ProductDefinitionId;
        var desiredAvailable = desiredDefinitionId != 0 &&
                               availablePickupDefinitionIds.ContainsKey(desiredDefinitionId);
        if (isPhysicallyEmpty)
        {
            if (!desiredAvailable ||
                !PrepareEmptyPlaceForExactDefinition(place, desiredDefinitionId) ||
                !CanAcceptDefinition(place, desiredDefinitionId))
                return false;

            definitionId = desiredDefinitionId;
            policyRank = 0;
            return true;
        }

        if (physicalDefinitionId == desiredDefinitionId && desiredAvailable &&
            CanAcceptDefinition(place, desiredDefinitionId))
        {
            definitionId = desiredDefinitionId;
            policyRank = 0;
            return true;
        }

        if (rule.AllowTemporarySubstitute && !desiredAvailable && physicalDefinitionId != 0 &&
            availablePickupDefinitionIds.ContainsKey(physicalDefinitionId) &&
            CanAcceptDefinition(place, physicalDefinitionId))
        {
            definitionId = physicalDefinitionId;
            policyRank = 2;
            return true;
        }

        return false;
    }

    private static bool IsPlaceAtCapacity(ProductPricePlace place)
    {
        if (place == null || place.ProductPlace == null)
            return false;

        var maxCount = place.ProductPlace.MaxCount;
        return maxCount > 0 && place.ProductPlace.Count >= maxCount;
    }

    private static string DescribePlacementTarget(ProductPricePlace place)
    {
        try
        {
            var hierarchy = new List<string>();
            var current = place.transform;
            for (var depth = 0; current != null && depth < 8; depth++)
            {
                hierarchy.Add(current.name ?? "?");
                current = current.parent;
            }
            hierarchy.Reverse();

            var allowedTypes = new List<string>();
            var availableProducts = place.ProductPlace?._availableProducts;
            if (availableProducts != null)
            {
                for (var index = 0; index < availableProducts.Count; index++)
                    allowedTypes.Add(availableProducts[index].ToString());
            }

            var position = place.transform.position;
            var allowed = allowedTypes.Count == 0
                ? "любой тип по правилам игры"
                : string.Join(",", allowedTypes);
            var persistentId = string.IsNullOrWhiteSpace(place.PersistentId)
                ? "нет"
                : place.PersistentId;

            return
                $"цель=[{string.Join("/", hierarchy)}], id={persistentId}, " +
                $"позиция=({position.x:F2},{position.y:F2},{position.z:F2}), " +
                $"разрешённые типы=[{allowed}], фильтр={RuleStore.Describe(place)}.";
        }
        catch (Exception exception)
        {
            return $"данные цели недоступны ({exception.GetType().Name}).";
        }
    }

    private static bool PrepareEmptyPlaceForExactDefinition(ProductPricePlace place, int definitionId)
    {
        return PrepareEmptyPlaceForDefinition(place, definitionId);
    }

    private static bool PrepareEmptyPlaceForDefinition(ProductPricePlace place, int definitionId)
    {
        var assignedDefinitionId = ResolveNativeAssignedDefinitionId(place, out var hasNativeAssignment);
        if (!hasNativeAssignment || assignedDefinitionId == definitionId)
            return true;

        RequestNativeAssignmentClear(place);
        return false;
    }

    private static bool PrepareEmptyPlaceForAvailableDefinition(
        ProductPricePlace place,
        HashSet<int> availableDefinitionIds,
        ref int requiredDefinitionId)
    {
        var assignedDefinitionId = ResolveNativeAssignedDefinitionId(place, out var hasNativeAssignment);
        if (!hasNativeAssignment)
            return true;

        if (assignedDefinitionId != 0 && availableDefinitionIds.Contains(assignedDefinitionId))
        {
            requiredDefinitionId = assignedDefinitionId;
            return true;
        }

        RequestNativeAssignmentClear(place);
        return false;
    }

    private static int ResolveNativeAssignedDefinitionId(
        ProductPricePlace place,
        out bool hasNativeAssignment)
    {
        hasNativeAssignment = false;
        if (place == null || place.ProductPlace == null || place.ProductPlace.Count > 0)
            return 0;

        try
        {
            // EmployeeSortingController.IsValidEmployeePlacement checks
            // ProductPlace.Id directly. ResolveCurrentProductId may already expose
            // the cleared synchronized price-tag id while ProductPlace.Id still
            // contains the old product for another frame/network tick. Prefer the
            // exact value used by the final native validation to avoid dispatching
            // a worker with a box that the destination will reject on arrival.
            var productId = place.ProductPlace.Id;
            if (IsEmptyProductId(productId.ToString()))
                productId = place.ResolveCurrentProductId();
            if (IsEmptyProductId(productId.ToString()))
                return 0;

            hasNativeAssignment = true;
            var productsService = AllServices.Get<ProductsService>();
            return productsService?.GetProductInfo(productId)?.DefinitionId ?? 0;
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("чтение игровой привязки пустой секции", exception);
            return 0;
        }
    }

    private static void RequestNativeAssignmentClear(ProductPricePlace place)
    {
        if (place == null || place.ProductPlace == null || place.ProductPlace.Count > 0)
            return;

        try
        {
            if (!place.CanClearPriceTag)
                return;

            // ClearPriceTag must be retried while either native assignment is still
            // present. In 1.0.5 ProductPlace.Id and the synchronized price-tag id do
            // not necessarily become empty in the same frame.
            var productId = place.ProductPlace.Id;
            if (IsEmptyProductId(productId.ToString()))
                productId = place.ResolveCurrentProductId();
            var productIdKey = productId.ToString();
            if (IsEmptyProductId(productIdKey))
                return;

            var instanceId = place.GetInstanceID();
            var now = Time.unscaledTime;
            if (NativeAssignmentClearRequests.TryGetValue(instanceId, out var pending) &&
                string.Equals(pending.ProductId, productIdKey, StringComparison.OrdinalIgnoreCase) &&
                now < pending.RetryAfter)
            {
                return;
            }

            if (NativeAssignmentClearRequests.Count > 256)
                NativeAssignmentClearRequests.Clear();
            NativeAssignmentClearRequests[instanceId] =
                new NativeAssignmentClearRequest(productIdKey, now + NativeAssignmentClearRetryDelay);

            // 1.0.5 validates both LastDefinitionId and the native ProductPlace.Id.
            // Clear the stale assignment through the game's own RPC instead of forcing
            // validation to true; the next search pass can then safely apply our rule.
            place.ClearPriceTag();
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("очистка устаревшей игровой привязки", exception);
        }
    }

    private static bool IsEmptyProductId(string? productId)
    {
        return string.IsNullOrWhiteSpace(productId) ||
               string.Equals(
                   productId,
                   "00000000-0000-0000-0000-000000000000",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanAcceptDefinition(ProductPricePlace place, int definitionId)
    {
        if (place.ProductPlace == null || definitionId == 0)
            return false;

        try
        {
            var definition = DataDefinition<ProductDefinition>.GetWithId(definitionId);
            return definition != null && place.ProductPlace.CanPut(definition.Type);
        }
        catch
        {
            return false;
        }
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        left.y = right.y;
        return Vector3.Distance(left, right);
    }

    private static void SetRuntimeDefinition(ProductPricePlace place, int definitionId)
    {
        if (place == null || definitionId == 0)
            return;

        // Native 1.0.5 uses LastDefinitionId as its assigned-product memory. Keep that
        // runtime value aligned with the selected policy so the rest of the stocker
        // task picks up and places the same product that this search selected.
        place._lastDefinitionId = definitionId;
    }

    private static void IsValidEmployeePlacementPostfix(
        EmployeeSortingController __instance,
        ProductPricePlace place,
        int definitionId,
        ref bool __result)
    {
        if (!ShelfFiltersMod.FeatureEnabled)
            return;

        try
        {
            if (!RuleStore.TryGetRule(place, out var rule))
                return;

            if (!RuleStore.Allows(place, definitionId))
            {
                __result = false;
                return;
            }

            if (__result || place == null || !place.HavePoints ||
                place.IsLocked || place.ProductPlace == null ||
                place.ProductPlace.Count > 0 || !CanAcceptDefinition(place, definitionId))
            {
                return;
            }

            // A worker may have received the task one frame before the game's
            // ClearPriceTag RPC finished. Reconcile the same native assignment at
            // the final validation point. Never force placement while an old id is
            // still present; after it is really clear, aligning LastDefinitionId is
            // equivalent to the native configured-shelf path and safely revives the
            // already carried box without a background worker-recovery loop.
            if (!PrepareEmptyPlaceForDefinition(place, definitionId))
                return;

            SetRuntimeDefinition(place, definitionId);
            __result = true;
        }
        catch (Exception exception)
        {
            // Keep the game's original result when a mod-side lookup fails.
            WarnAiFailOpen("проверка выбранной секции", exception);
        }
        finally
        {
            TrackEmployeePlacementAttempt(__instance, place, definitionId, __result);
        }
    }

    private static void TrackEmployeePlacementAttempt(
        EmployeeSortingController instance,
        ProductPricePlace place,
        int definitionId,
        bool isValid)
    {
        ClearPendingPlacementAttempt();
        if (!ShelfFiltersMod.FeatureEnabled || instance == null || place == null ||
            definitionId == 0 || instance._pickup == null || instance._pickup.Count <= 0 ||
            instance._productPricePlace == null ||
            instance._productPricePlace.GetInstanceID() != place.GetInstanceID() ||
            place.ProductPlace == null || !RuleStore.TryGetRule(place, out _))
        {
            return;
        }

        // A false result here is normal: while looking for a destination the game
        // probes many incompatible sections (for example, a dakimakura against a
        // manga shelf). It must not be treated as a broken physical slot. Doing so
        // blacklisted every probed section, restarted the search every frame and
        // could leave all stockers idle while flooding the log. Only arm the
        // one-frame marker after the final validation has accepted the destination;
        // a real ProductPlace.TryAddItem failure below is the recovery trigger.
        if (!isValid)
            return;

        // PutProductsAsync calls ProductPlace.TryAddItem synchronously immediately
        // after this validation. Keep a one-frame marker so its false result can be
        // tied to the correct worker without polling every employee or shelf.
        _pendingPlacementWorker = instance;
        _pendingPlacementPricePlace = place;
        _pendingPlacementProductPlaceId = place.ProductPlace.GetInstanceID();
        _pendingPlacementDefinitionId = definitionId;
        _pendingPlacementFrame = Time.frameCount;
    }

    private static void ProductPlaceTryAddItemPostfix(ProductPlace __instance, ref bool __result)
    {
        var worker = _pendingPlacementWorker;
        var pricePlace = _pendingPlacementPricePlace;
        var matchesCurrentAttempt = worker != null && pricePlace != null && __instance != null &&
                                    _pendingPlacementFrame == Time.frameCount &&
                                    _pendingPlacementProductPlaceId == __instance.GetInstanceID();
        var definitionId = _pendingPlacementDefinitionId;
        ClearPendingPlacementAttempt();

        if (__result || !matchesCurrentAttempt || worker == null || pricePlace == null)
            return;

        try
        {
            RecordFailedPlacementTarget(
                worker,
                pricePlace,
                definitionId,
                "физическая точка отказала при формально свободной секции");
        }
        catch (Exception exception)
        {
            WarnAiFailOpen("фиксация отказа физической точки", exception);
        }
    }

    private static void RecordFailedPlacementTarget(
        EmployeeSortingController worker,
        ProductPricePlace place,
        int definitionId,
        string reason)
    {
        if (worker == null || place == null || place.ProductPlace == null || definitionId == 0)
            return;

        var workerId = worker.GetInstanceID();
        var failed = new FailedPlacementTarget(
            place.GetInstanceID(),
            place.ProductPlace.GetInstanceID(),
            definitionId,
            place.ProductPlace.Count,
            worker._taskVersion,
            Time.unscaledTime + FailedPlacementRetryDelay);

        var shouldLog = !FailedPlacementTargets.TryGetValue(workerId, out var previous) ||
                        previous.PricePlaceId != failed.PricePlaceId ||
                        previous.DefinitionId != failed.DefinitionId ||
                        previous.ObservedCount != failed.ObservedCount ||
                        previous.TaskVersion != failed.TaskVersion;

        if (FailedPlacementTargets.Count > 32)
            FailedPlacementTargets.Clear();
        FailedPlacementTargets[workerId] = failed;

        if (shouldLog)
        {
            var productName = ProductNames.Resolve(definitionId);
            var capacity = place.ProductPlace.MaxCount;
            MelonLogger.Warning(
                $"Shelf Filters: секция временно исключена для выкладчика — «{productName}», " +
                $"состояние {failed.ObservedCount}/{capacity}, причина: {reason}; " +
                DescribePlacementTarget(place));
        }
    }

    private static bool ShouldSkipFailedPlacementTarget(
        ProductPricePlace place,
        int definitionId,
        int workerId)
    {
        if (workerId == 0 || place == null || place.ProductPlace == null ||
            !FailedPlacementTargets.TryGetValue(workerId, out var failed))
        {
            return false;
        }

        if (Time.unscaledTime >= failed.ExpiresAt || failed.DefinitionId != definitionId)
        {
            FailedPlacementTargets.Remove(workerId);
            return false;
        }

        if (failed.PricePlaceId != place.GetInstanceID() ||
            failed.ProductPlaceId != place.ProductPlace.GetInstanceID())
        {
            return false;
        }

        // A sale or another worker changing the section invalidates the failed
        // physical-slot snapshot. It is then safe for the normal game to try again.
        if (place.ProductPlace.Count != failed.ObservedCount)
        {
            FailedPlacementTargets.Remove(workerId);
            return false;
        }

        return true;
    }

    private static void ClearPendingPlacementAttempt()
    {
        _pendingPlacementWorker = null;
        _pendingPlacementPricePlace = null;
        _pendingPlacementProductPlaceId = 0;
        _pendingPlacementDefinitionId = 0;
        _pendingPlacementFrame = -1;
    }

    private static void WarnAiFailOpen(string operation, Exception exception)
    {
        var now = DateTime.UtcNow;
        if (now < _nextAiSafetyWarningUtc)
        {
            _suppressedAiSafetyWarnings++;
            return;
        }

        var suppressed = _suppressedAiSafetyWarnings > 0
            ? $"; пропущено повторов: {_suppressedAiSafetyWarnings}"
            : string.Empty;
        _suppressedAiSafetyWarnings = 0;
        _nextAiSafetyWarningUtc = now.AddSeconds(20);
        MelonLogger.Warning(
            $"Shelf Filters: сбой фильтра ({operation}); выкладчик продолжит штатную работу без фильтра для этого действия. " +
            $"{exception.GetType().Name}: {exception.Message}{suppressed}");
    }

}
