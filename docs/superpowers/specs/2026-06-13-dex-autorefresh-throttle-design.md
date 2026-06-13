# Дизайн: Укрощение авто-рефреша DEX (rate-limit)

> Дата: 2026-06-13 · Ветка: `fix/dex-autorefresh-throttle`

## Проблема

`AutoRefreshAsync` срабатывает каждые **3с** и при выбранной сети вызывает
`RefreshAsync` → `GetMomentumScoutTokensAsync`, который шлёт **8 последовательных**
поисковых запросов к DexScreener (по числу строк в `GetMomentumScoutSearchQueries`).
Итог: ~**160 запросов/мин** только для DEX-списка (лимит DexScreener ~300/мин, и
снайпер тоже скаутит) → риск HTTP 429 и «мигания»/перетасовки списка.

Внесено фиксом BSC/Tron (пер-чейн загрузка через scout).

## Принцип (согласовано)

Дорогой пер-чейн скаут **не** запускается на 3-секундном таймере. Список выбранной
сети грузится один раз — при выборе сети и по кнопке REFRESH. Авто-рефреш остаётся
только для дешёвых путей: лента «All» (1 запрос) и активный поиск (1 запрос).

## Компоненты

### 1. `Services/DexRefreshPolicy.cs` (новый, чистый)
```csharp
public static class DexRefreshPolicy
{
    public enum AutoRefreshAction { Skip, ReloadLatest, Search }

    /// <param name="chainId">Resolved chain id; null/empty = the "All" latest feed.</param>
    public static AutoRefreshAction NextAutoRefresh(string? chainId, string? searchText);
}
```
Логика:
- `searchText` непустой → `Search` (дешёвый одиночный запрос; имеет приоритет).
- иначе `chainId` пустой/null («All») → `ReloadLatest` (дешёвая лента, 1 запрос).
- иначе (конкретная сеть) → `Skip` (без авто-рескана).

Чистая функция → табличные юнит-тесты.

### 2. `DexTradingViewModel.AutoRefreshAsync`
Заменить текущую if/else:
```csharp
private async Task AutoRefreshAsync()
{
    if (IsLoading)
    {
        return;
    }

    switch (DexRefreshPolicy.NextAutoRefresh(ChainIdForFilter(_selectedChainFilter), SearchText))
    {
        case DexRefreshPolicy.AutoRefreshAction.Search:
            await SearchAsync();
            break;
        case DexRefreshPolicy.AutoRefreshAction.ReloadLatest:
            await RefreshAsync();
            break;
        // Skip: specific chain uses the multi-request scout — loaded on chain-select
        // and the REFRESH button, never on the fast timer.
    }
}
```

## Не трогаем
- `RefreshAsync` (по-прежнему скаут при выбранной сети — но вызывается только при
  смене сети и по REFRESH).
- `SearchAsync`, загрузку чарта, фильтры.

## Эффект
При выбранной сети — 8 запросов **один раз** вместо ~160/мин. «All» и поиск без
изменений (≤ ~20 запр/мин).

## Тесты
`DexRefreshPolicyTests` (xUnit, Core.Tests):
- `searchText` непустой (с сетью и без) → `Search`.
- нет поиска, `chainId` null/"" → `ReloadLatest`.
- нет поиска, `chainId` = "bsc" → `Skip`.

Плюс smoke: выбрать BSC → список грузится один раз и дальше не пере-скаутится сам;
REFRESH перезагружает; «All» продолжает авто-обновляться.

## Вне scope
- Отдельный дешёвый рефреш цены только выбранного токена (можно позже).
- Изменение интервала таймера.
