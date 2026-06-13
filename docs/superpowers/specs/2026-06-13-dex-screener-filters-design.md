# Дизайн: DEX-скринер с фильтрами (фича C)

> Дата: 2026-06-13 · Ветка: `feature/dex-screener-filters`
> Часть пакета доработок DEX (A — AI-вердикт ✅, B — свечи ✅, **C — скринер**).
> Спека только для **C**.

## Цель

Список токенов в панели **DEX Market** (страница Trading) сейчас показывает то, что
вернул API, без фильтрации/сортировки. Добавить клиентские фильтры (сеть, мин.
ликвидность, мин. объём) и сортировку поверх загруженного набора.

## Объём (согласовано)

Фильтры: **сеть**, **мин. ликвидность**, **мин. 24h объём**; **сортировка** (объём /
ликвидность / 24h изменение). Все — компактные **дропдауны** (числовых полей избегаем:
в узкой 350px-панели чище и без обрезания). Без «возраста» и моментум-диапазона
(возраст недостоверен на этом списке — `ObservedFirstSeenUtc` = «когда мы впервые
увидели», ≈ сейчас).

## Текущее состояние (проверено)

- `DexTradingViewModel.Tokens` (`ObservableCollection<DexTokenItemViewModel>`) —
  видимый список. Наполняется в `LoadTokensAsync(loader, successMessage)`:
  `Tokens.Clear()` → для каждого `DexTokenInfo` создаётся `DexTokenItemViewModel` →
  `Tokens.Add(item)`. Загрузка: `RefreshAsync` → `_dexClient.GetLatestTokensAsync()`;
  `SearchAsync` → `_dexClient.SearchTokensAsync(SearchText)`. Без клиентских фильтров.
- `DexTokenInfo` поля: `ChainId` (`"bsc"/"ethereum"/"base"/"solana"/"tron"`), `DexId`,
  `LiquidityUsd`, `Volume24h`, `PriceChange24h`, `MarketCap`, …
- Панель `DEX Market` в `TradingDeskView.axaml`: H2 «DEX Market» → поиск →
  кнопки SEARCH/REFRESH (Grid `*,*`) → инфо выбранного токена → плитки Price/Liquidity →
  список токенов (ListBox по `DexTradingVM.Tokens`).

## Компоненты

### 1. `DexTokenFilter` (новый, чистый статический класс)
`Services/DexTokenFilter.cs`. Единственная ответственность — отфильтровать и
отсортировать набор:

```csharp
public static class DexTokenFilter
{
    // chainId == null/empty → без фильтра по сети (сравнение case-insensitive по ChainId).
    // sortMode: "Volume" | "Liquidity" | "Change" (иначе → Volume). Сортировка по убыванию.
    public static IReadOnlyList<DexTokenInfo> Apply(
        IReadOnlyList<DexTokenInfo> tokens,
        string? chainId,
        decimal minLiquidity,
        decimal minVolume,
        string sortMode);
}
```
Чистая → табличные юнит-тесты.

### 2. `DexTradingViewModel` (дополнения)
- Бэкинг `private IReadOnlyList<DexTokenInfo> _loadedTokens = []` — сырые токены из
  последней загрузки.
- Свойства фильтров (ReactiveUI, сеттер вызывает `ApplyTokenFilter()`):
  - `string SelectedChainFilter` (default `"All"`), коллекция `ChainFilterOptions`
    = `["All","BSC","Ethereum","Base","Solana","Tron"]`.
  - `string SelectedMinLiquidity` (default `"Any"`), `MinLiquidityOptions`
    = `["Any","$10k","$50k","$100k","$500k"]`.
  - `string SelectedMinVolume` (default `"Any"`), `MinVolumeOptions`
    = `["Any","$10k","$50k","$250k","$1M"]`.
  - `string SelectedSortMode` (default `"Volume"`), `SortModeOptions`
    = `["Volume","Liquidity","24h Change"]`.
- `ApplyTokenFilter()`: маппит выбранные значения → (chainId?, minLiquidity, minVolume,
  sortMode), зовёт `DexTokenFilter.Apply(_loadedTokens, …)`, пересобирает `Tokens`
  (Clear + Add по отфильтрованному), восстанавливает `SelectedToken` если он остался
  (иначе первый из отфильтрованных или null), пишет статус
  («No tokens match filters» при пустом).
- Рефактор `LoadTokensAsync`: вместо прямого заполнения `Tokens` — сохранить
  `_loadedTokens = tokens` и вызвать `ApplyTokenFilter()` (внутри UI-потока, как сейчас).
  Так фильтр держится при авто-рефреше и поиске.

Маппинг порогов: `"Any"→0`, `"$10k"→10000`, `"$50k"→50000`, `"$100k"→100000`,
`"$250k"→250000`, `"$500k"→500000`, `"$1M"→1000000`.
Маппинг сети: `"All"→null`, иначе `"BSC"→"bsc"`, `"Ethereum"→"ethereum"`, `"Base"→"base"`,
`"Solana"→"solana"`, `"Tron"→"tron"`.
Маппинг сорта: `"24h Change"→"Change"`, иначе значение как есть.

### 3. UI (TradingDeskView.axaml, панель DEX Market)
После Grid с кнопками SEARCH/REFRESH, перед инфо выбранного токена — 2 ряда по 2
дропдауна (`ComboBox`), привязанных к свойствам VM:
- Ряд 1: `Chain` (`SelectedChainFilter`/`ChainFilterOptions`) · `Sort`
  (`SelectedSortMode`/`SortModeOptions`)
- Ряд 2: `Min Liq` (`SelectedMinLiquidity`/`MinLiquidityOptions`) · `Min Vol`
  (`SelectedMinVolume`/`MinVolumeOptions`)

## Поток данных

```
Refresh/Search → LoadTokensAsync → _loadedTokens = tokens → ApplyTokenFilter()
                                                              → DexTokenFilter.Apply(...)
                                                              → rebuild Tokens
смена любого фильтра (сеттер) → ApplyTokenFilter() → rebuild Tokens
```

## Обработка краёв

- Пусто после фильтра → `Tokens` пуст, статус «No tokens match filters».
- Выбранный токен отфильтрован → `SelectedToken` = первый из отфильтрованных или null
  (как уже делает `LoadTokensAsync` через сопоставление по адресу).
- Фильтры применяются и к результатам поиска (поиск грузит в `_loadedTokens`, фильтр сверху).
- Авто-рефреш сохраняет выбранные фильтры (они живут в VM, не сбрасываются).

## Тесты

`DexTokenFilterTests` (xUnit, в `CryptoAITerminal.Core.Tests`):
- Фильтр по сети (только указанная сеть; `null` → все).
- Мин. ликвидность отсекает ниже порога; `0` → все.
- Мин. объём отсекает ниже порога; `0` → все.
- Сорт по Volume / Liquidity / Change — по убыванию.
- Неизвестный `sortMode` → сортировка по Volume.
- Комбинация фильтров.

Плюс: `dotnet build` зелёный; smoke-run — выбрать сеть/порог/сорт → список сужается и
переупорядочивается; «All/Any» возвращает полный список.

## Вне scope
- Возраст, моментум-диапазон.
- Числовые поля порогов (используем дропдауны-пресеты).
- Фильтры на нерендерящихся DEX-панелях `MainWindow.axaml`.
