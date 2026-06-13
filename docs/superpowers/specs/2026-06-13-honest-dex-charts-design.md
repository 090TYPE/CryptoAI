# Дизайн: Честные DEX-графики (без фабрикации)

> Дата: 2026-06-13 · Ветка: `feature/honest-dex-charts`

## Проблема

DEX-график подмешивает **синтетические** (выдуманные) данные в нескольких местах,
из-за чего фабрикованный график выглядит как реальный (а для разных токенов —
почти одинаково). Для торгового терминала это риск: пользователь принимает решения
по фейковому графику.

Источники фабрикации (найдены при отладке):
- `RecordPriceSample` при пустой истории сразу сеет синтетические якоря
  (`SeedSyntheticHistory`) — у **каждого** токена.
- `BuildLocalCandles` (Source 2) при <2 реальных сэмплов строит полностью
  синтетическое окно (`BuildSyntheticWindow`), и добавляет синтетический префикс-бэкфилл.
- `LoadChartAsync` Source 4 («synthetic bootstrap») — полностью синтетический график.
- `NormalizeDexCandlesForDisplay` интерполирует синтетические свечи в промежутки —
  даже поверх **реальных** данных.

После фикса живого OHLCV (GeckoTerminal, Source 1) реальные данные доступны для
большинства токенов, поэтому честный режим не оставляет пользователя без графиков
в типичном случае.

## Принцип (согласовано)

Показывать **только реальные** данные: живой GeckoTerminal OHLCV (Source 1),
on-chain Swap-реконструкция (Source 3), реальные накопленные тики (Source 2).
Когда реальных данных нет — **честное пустое состояние** (через существующий
`ClearChart`), а не выдуманные свечи.

## Изменения

### 1. `RecordPriceSample` — убрать синтетический сид
Удалить блок `if (history.Count == 0) SeedSyntheticHistory(...)` (строки ~927-930).
История токена = только реальные тики (по одному за рефреш).

### 2. `BuildLocalCandles` (Source 2) — только реальные сэмплы
Убрать обе синтетические ветки:
- `samples.Count < 2 → BuildSyntheticWindow(...)` → вместо этого вернуть пусто
  (`diagnostics = "Local samples are still too sparse to form candles."`).
- ветку синтетического префикс-бэкфилла (`syntheticPrefix`) удалить.
Оставить: реальные сэмплы → `DexCandleBuilder.Bucketize(...)`.

### 3. Вынести построитель свечей (для тестов)
`BucketizeSamples` + `AlignTime` (сейчас private static в `DexTradingViewModel`) →
новый чистый класс `Services/DexCandleBuilder.cs`:
```csharp
public static class DexCandleBuilder
{
    public static IReadOnlyList<DexOhlcvPoint> Bucketize(
        IReadOnlyList<DexPriceSample> samples,
        DateTime fromUtc, DateTime toUtc, TimeSpan bucketSize, int maxCandles);
}
```
`DexTradingViewModel` зовёт `DexCandleBuilder.Bucketize(...)`. Чистая логика → юнит-тесты.

### 4. `LoadChartAsync` Source 4 (synthetic bootstrap) — честное пустое
Заменить генерацию синтетики на:
```csharp
ClearChart("No live chart data for this pair yet — collecting live ticks; on-chain scan running.",
    "No live OHLCV, on-chain history or local samples available yet.");
TriggerBackgroundOnChainScan(selectedToken.TokenInfo);
return;
```
(Sources 1/2/3 пробуются раньше и остаются как есть.)

### 5. `NormalizeDexCandlesForDisplay` — без интерполяции
Убрать цикл, вставляющий синтетические свечи в промежутки. Оставить только
фильтр валидных O/H/L/C + сортировку (реальные свечи как есть). Применяется ко всем
источникам — больше не «дорисовывает» реальные данные.

### 6. Удалить мёртвый синтетический код
После 1-5 неиспользуемыми станут: `SeedSyntheticHistory`, `BuildSyntheticWindow`,
`BuildSyntheticAnchors`, `ExpandSyntheticSamples` (+ возможные хелперы `Interpolate`/
`InterpolateAnchoredPrice`/`ReverseChange`/`BuildCompoundedPrice`, если больше нигде
не используются). Удалить те, что станут unused (проверить компилятором/grep).

## Поток (после)

```
выбор токена → LoadChartAsync
  Source 1: GeckoTerminal live OHLCV  → есть ≥2? показать (NormalizeDisplay без интерполяции)
  Source 2: реальные локальные тики   → ≥2? показать
  Source 3: on-chain Swap reconstruct → ≥2? показать
  иначе → ClearChart("No live chart data…") + фоновый on-chain скан
```

## Тесты

`DexCandleBuilderTests` (xUnit, Core.Tests):
- Пустой вход → пусто.
- Сэмплы в одном бакете → одна свеча (O=первый, C=последний, H=max, L=min).
- Сэмплы в нескольких бакетах → правильное число свечей, границы.
- Пустые бакеты между сэмплами → flat-свеча по lastClose (поведение `BucketizeSamples`).
- `maxCandles` ограничивает количество.

Плюс smoke: токен с живым OHLCV → реальный график; токен без данных → честное
«No live chart data», без фейковой пилы; разные токены — разные графики.

## Вне scope
- Изменение Source 1/3 (живой/on-chain) — они уже реальны.
- Полное переписывание `NormalizeDexCandlesForDisplay` сверх удаления интерполяции.
