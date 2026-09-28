# Фильтры полок — мод для Anime Shop Simulator

![Anime Shop Simulator](https://img.shields.io/badge/Anime%20Shop%20Simulator-1.0.6-f6a800)
![Version](https://img.shields.io/badge/version-0.10.3-1685d1)
![WolfCore](https://img.shields.io/badge/requires-WolfCore-1685d1)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3-7952b3)
![License](https://img.shields.io/badge/license-MIT-2ea44f)

**Фильтры полок** — мод для **Anime Shop Simulator** на базе **MelonLoader** и **WolfCore**. Он добавляет правила автоматической выкладки для отдельных секций мебели.

Мод помогает сохранять порядок в магазине: игрок заранее указывает, какой товар разрешено размещать в каждой секции, а выкладчики учитывают выбранные правила.

## Скачать

Готовая сборка находится в **[последнем выпуске](https://github.com/Midfr0st/AnimeShopSimulator-ShelfFilters/releases/latest)**.

Для работы также необходим [WolfCore](https://github.com/Midfr0st/AnimeShopSimulator-WolfCore).

## Возможности

- оставить секцию под штатным управлением игры;
- привязать конкретный товар;
- разрешить временную замену, если назначенного товара нет;
- сделать секцию свободной для любого совместимого товара;
- запретить автоматическую выкладку;
- выбирать товар из каталога уже разблокированных товаров;
- показывать возле ценника отдельный индикатор выбранного правила;
- автоматически читать новые товары и DLC из данных игры.

Фильтр действует только на автоматическую работу выкладчиков. Игрок по-прежнему может раскладывать товары вручную.

## Установка

1. Полностью закройте игру.
2. Установите [MelonLoader](https://github.com/LavaGang/MelonLoader).
3. Установите [WolfCore](https://github.com/Midfr0st/AnimeShopSimulator-WolfCore/releases/latest).
4. Скачайте `AnimeShopShelfFilters.dll` из [Releases](https://github.com/Midfr0st/AnimeShopSimulator-ShelfFilters/releases/latest).
5. Поместите обе DLL в папку игры:

   ```text
   Anime Shop Simulator\Mods\
   ├─ WolfCore.dll
   └─ AnimeShopShelfFilters.dll
   ```

6. Запустите игру.

Подробности: [INSTALLATION.ru.md](docs/INSTALLATION.ru.md).

## Использование

1. Наведите прицел на секцию, куда может выкладываться товар.
2. Нажмите `F6` — это клавиша по умолчанию.
3. Выберите правило и при необходимости конкретный товар.

Клавиша, отображение индикаторов и другие параметры находятся в `Esc` → `Моды` → `Фильтры полок`.

## Данные пользователя

```text
Anime Shop Simulator\UserData\AnimeShopShelfFilters.json
Anime Shop Simulator\UserData\AnimeShopShelfFilters.settings.json
```

Первый файл содержит правила секций, второй — настройки мода. Игровые сохранения мод не изменяет.

## Совместимость и ограничения

- Anime Shop Simulator `1.0.6`;
- MelonLoader `0.7.3`;
- WolfCore `0.2.5`;
- Windows x64, Unity IL2CPP.

Этот мод глубже остальных взаимодействует со штатным поиском заданий выкладчика. После обновлений игры его нужно отдельно проверять на коробках, лежащих товарах, одежде, подушках и дакимакурах.

## Если что-то не работает

См. [решение проблем](docs/TROUBLESHOOTING.ru.md). Для отчёта приложите `MelonLoader\Latest.log` и создайте обращение в [GitHub Issues](https://github.com/Midfr0st/AnimeShopSimulator-ShelfFilters/issues).

## Связанные проекты

- [WolfCore](https://github.com/Midfr0st/AnimeShopSimulator-WolfCore) — обязательное ядро и меню настроек;
- [Статистика товаров](https://github.com/Midfr0st/AnimeShopSimulator-ProductStatistics);
- [Отзывы о магазине](https://github.com/Midfr0st/AnimeShopSimulator-ShopReviews);
- [Расписание работников](https://github.com/Midfr0st/AnimeShopSimulator-EmployeeSchedules).

## Лицензия

Проект распространяется по условиям [MIT License](LICENSE). Это неофициальная пользовательская модификация, не связанная с разработчиками или издателем игры. Мод предоставляется «как есть» и используется на свой риск.
