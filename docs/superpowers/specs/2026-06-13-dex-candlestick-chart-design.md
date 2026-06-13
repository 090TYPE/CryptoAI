# Дизайн: Свечной DEX-график (фича B)

> Дата: 2026-06-13 · Ветка: `feature/dex-candlestick-chart`
> Часть пакета доработок DEX (A — AI-вердикт ✅, **B — свечи на DEX-графике**, C — DEX-скринер).
> Спека только для **B**.

## Цель

Заменить простой линейный DEX-график на полноценный свечной — с теми же возможностями,
что уже есть на CEX-вкладке этой же страницы (свечи, MA/Bollinger, VWAP, volume profile,
pan/zoom). Данные (OHLCV-свечи) уже есть; контрол уже написан и принимает тот же тип.

## Объём (согласовано: минимальная замена)

Заменить контрол. Без панели инструментов рисования, без `PersistenceKey` (вид сбрасывается
при смене токена — желаемо). Кнопки таймфреймов остаются как есть.

## Текущее состояние

- `TradingDeskView.axaml` (видимая Trading-страница), секция DEX-графика, строки **1283–1288**:
  ```xml
  <Border Classes="SoftPanel,ChartCanvasHost" Padding="8">
    <ctrl:DexPriceChart Height="320"
                        Candles="{Binding DexTradingVM.ChartCandles}"
                        ShowVwap="True" />
  </Border>
  ```
  `DexPriceChart` рисует **линию** (хоть и получает OHLCV).
- Кнопки таймфреймов 15M/1H/4H/1D/1W (строки 1257–1281) → `DexTradingVM.SelectChartRangeCommand`
  пересобирают `DexTradingVM.ChartCandles` (`ObservableCollection<DexOhlcvPoint>`).
- Тот же `CexCandlestickChart` уже используется для CEX на этой странице (строка 589),
  привязан к `ActiveTradingCandles`, с тулбаром инструментов.

## Контракт `CexCandlestickChart` (проверено)

- `Candles` : `IReadOnlyList<DexOhlcvPoint>` (совместимо с `ChartCandles`).
- `ShowVwap` : bool, **default true**.
- `ShowVolumeProfile` : bool, **default true**.
- `ToolMode` : string, **default "Cursor"** (pan/zoom колесом и перетаскиванием).
- `PersistenceKey` / `ClearDrawingsVersion` / `ResetViewVersion` — опциональны; не используем.

## Изменение (единственное)

В `TradingDeskView.axaml` строки 1285–1287 заменить:
```xml
<ctrl:CexCandlestickChart Height="320"
                          Candles="{Binding DexTradingVM.ChartCandles}" />
```
Дефолты дают: свечи + MA/Bollinger + VWAP + volume profile + pan/zoom.

## Что НЕ трогаем

- Кнопки таймфреймов и `DexTradingVM` — без изменений.
- `DexPriceChart.cs` — не удаляем (ещё ссылается из нерендерящихся DEX-панелей `MainWindow.axaml`).
  Мёртвые копии в `MainWindow.axaml` вне scope.

## Обработка краёв

- Пустые/малочисленные свечи → `CexCandlestickChart` сам показывает пустое состояние
  (уже обрабатывается, как на CEX).
- Смена токена/таймфрейма → `ChartCandles` перезаполняется, контрол перерисовывается
  по `AffectsRender(CandlesProperty)`.

## Тесты

Логики для юнит-тестов нет — это замена XAML-контрола. Проверка:
- `dotnet build` зелёный (Avalonia компилирует XAML на сборке — ошибки привязок всплывут тут).
- Smoke-run: Trading → DEX → выбрать токен → виден **свечной** график (не линия);
  переключение таймфреймов 15M/1H/4H/1D/1W работает; pan/zoom колесом/перетаскиванием работает.

## Вне scope
- Инструменты рисования на DEX-графике (отдельная итерация, если понадобится).
- Чистка мёртвых `DexPriceChart` в `MainWindow.axaml`.
