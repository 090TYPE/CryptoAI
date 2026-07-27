# CryptoAI Terminal — сводный список улучшений

Собрано из 116 подтверждённых находок 10 ревьюеров; дубликаты объединены (в скобках у заголовка — все затронутые файлы). Плюс 6 пунктов в разделе «Дополнение к визуалу». Итого 109 пунктов: 45 × S, 52 × M, 12 × L.

> ## Статус: закрыто, кроме одного пункта
>
> Всё ниже — исходный отчёт аудита, оставлен как есть для истории. Текущее состояние:
>
> | | На момент аудита | Сейчас |
> |---|---|---|
> | Тесты | 768 | **869** |
> | Предупреждений сборки | 68 | **0**, включён `TreatWarningsAsErrors` |
> | View, привязанных к `MainWindowViewModel` | 40 | **7** (см. ниже — из них 4 объявили это явно) |
> | View с компилируемыми биндингами | 0 | **все, кроме трёх островков** |
> | Публичных членов у оболочки | — | 878 → **809** |
> | Пустых `catch` без объяснения | 21 | **0** (13 осознанных, у каждого комментарий) |
> | Биндингов цвета мимо `StringToBrush` | 247 | **0 дефектных** (см. ниже) |
> | Использований DI-контейнера | 0 | композиционный корень в `Program.cs` |
>
> **Компилируемые биндинги включены везде.** Было 18 `.axaml` с `x:CompileBindings="False"` —
> около 2200 привязок, которые компилятор не проверял: опечатка в пути давала не ошибку
> сборки, а молча пустой контрол. Теперь у каждого View есть корректный `x:DataType`, у
> каждого `DataTemplate` — тип элемента, а `$parent[...].DataContext.X` получили явные касты.
> Осталось три островка `x:CompileBindings="False"` внутри `PortfolioView` — это привязки на
> `ColumnDefinition`, которая не `StyledElement` и своего DataContext не имеет; плюс 18
> одиночных `{ReflectionBinding}` там, где компилировать нечего в принципе (сеттеры внутри
> `<Style>`, `KeyBinding`, `SolidColorBrush.Color`, ValueTuple-коллекция).
>
> Счётчик «View, привязанных к `MainWindowViewModel`» вырос с 4 до 7 — и это не регресс:
> `SettingsView`, `BotsView`, `RulesView` и `AnalyticsView` всегда сидели на оболочке, просто
> молча. Теперь они это объявляют, и компилятор проверяет каждую их привязку. Зато
> `MarketsView` и `AiSignalsView` из этого списка ушли по-настоящему: им передан их
> собственный владелец (`MarketFeedOwnerViewModel` и `AiSignalDeskViewModel`) в
> `MainWindow.axaml`, и обёртка-`Panel` внутри `MarketsView` больше не нужна.
>
> **Про «247 биндингов цвета мимо `StringToBrush`»: пункт закрыт, но метрика была неверной.**
> Проверил каждое оставшееся место, где `Foreground`/`Background`/`Stroke`/`Fill` привязан без
> конвертера, и посмотрел тип привязанного члена: все они `IBrush`, а не `string`, — конвертер
> им не нужен и был бы поломкой. Остальные 54 «нарушения» в `MainWindow.axaml` — это
> `{Binding $parent[Button].Foreground}` у иконок, наследование цвета кнопки. Дефектных
> мест — ноль.
>
> **`TradingDeskView`: три кластера из четырёх вынесены.** `TradeBlotterViewModel` (лента,
> рабочие ордера, филлы, позиции, сигналы + индекс нижней вкладки), `ChartPanelViewModel`
> (весь тулбар графика: таймфрейм, тип, индикаторы, инструменты рисования, зум, алерт,
> полноэкранный режим — 10 команд) и `CexRightRailViewModel` (табы площадок, карточка позиции,
> CLOSE/REVERSE с их гардами, глубина стакана). Оркестрация осталась в оболочке и
> подключена событиями — тем же приёмом, что у `MarketFeedOwnerViewModel`. Заодно удалено
> 18 мёртвых чип-хелперов тулбара и три неподключённых пары таймфреймов.
> Владельцы привязок в `TradingDeskView` теперь: DexTradingVM 181, DexDeskVM 132,
> ChartPanelVM 75, MarketFeedVM 30, CexRightRailVM 21, TradeBlotterVM 13, WalletVM 8.
>
> **Осталось: тикет ордера** (`OrderTicketViewModel`, ~33 члена) — и это по-прежнему не
> «не успел». Это ядро изменяемого состояния исполнения: сеттеры `SelectedCexMarketMode`,
> `ManualFuturesLeverage`, `SelectedOrderType` дёргают оркестрацию оболочки, значения
> читают `PlacePrimaryOrderAsync`, `ExecuteClosePosition`, `ExecuteReversePosition`, три
> пресета, оценщик рабочих ордеров, сборщики AI-контекста и цепочка из восьми
> `GetCex*BlockedReason`, а `Services/AppActions/MainWindowAppActionContext.cs` тянет их
> напрямую. Переносить надо вместе с исполнением, отдельным проходом; композицию экрана
> (`TradingScreenViewModel`) имеет смысл писать после этого — сейчас в оболочку смотрят
> 43 члена, и прослойка была бы просто прокси на 43 члена.
>
> **`MarketsView` закрыт.** Экран целиком перевели на `MarketFeedOwnerViewModel`: DataContext
> задаётся в `MainWindow.axaml:509`, корневой `x:DataType` — сам владелец, 18 `$parent`-хопов
> переписаны с оболочки на него, промежуточная `Panel` убрана. Оболочки в файле не осталось.
>
> **Мёртвая поверхность вырезана.** 33 публичных члена оболочки, у которых во всём решении
> не было ни одной ссылки кроме объявления: опции выпадающих списков, которых больше нет
> (`SpotExchangeOptions`, `TimeInForceOptions`, `AvailableTradeTimeframes`), четыре
> `*CredentialSourceBadgeBrush`, `OkxPassphraseStatus` (в коде так и было написано — «kept for
> XAML compat»), свойства строк лестницы и чат-пузырей. Плюс 12 чип-свойств таймфрейма и
> 12 чип-свойств инструментов графика с двумя хелперами: `PriceChartWidget` теперь красит
> активную кнопку тем же `Classes.active`, что и деск, а не хардкодом хексов из вьюмодели.
>
> **VWAP и Volume Profile: фича была дописана, но без кнопок.** `CexCandlestickChart` рисует
> VWAP с полосами ±1σ/±2σ и профиль объёма с точкой контроля, обе включены по умолчанию —
> то есть оверлеи всегда висели на графике, и выключить их было нельзя: команды и флаги во
> вьюмодели были, а в тулбаре кнопок не было. Флаги переехали в `ChartPanelViewModel`, в
> группу IND добавлены `VWAP` и `VP`. Заодно на экран выведены два готовых, но нигде не
> показанных лейбла: OHLC последней свечи и подсказка активного инструмента рисования
> (`Channel: click first point, second point, then width.`). `SelectedChartToolPhase` при этом
> удалён — контрол фазу клика не сообщает, так что две из четырёх ветвей подсказки были
> недостижимы.
>
> Дефекты, найденные при живом осмотре и не входившие в аудит: обе карточки в «Operator Guide»
> не имели `Grid.Column` и накладывались (экран показывал одну вместо двух); развязка
> `DashboardView` сломала кнопки размера виджетов — биндинг остался валидным, но указывал
> не на тот объект; `FocusMarket` (чипы SYMBOL на деске) писал в `_selectedShellSection`
> без `SelectedTabIndex` — сайдбар и заголовок уезжали на «Markets», а на экране оставался
> деск; при смене символа поля LIMIT / TP / SL сохраняли цены прежнего символа (лимит 65 000
> на ETHUSDT — это покупка, которая исполнится мгновенно); `DashboardLayoutMath.DefaultLayout()`
> давал виджету `price-stats` одну строку вместо двух из каталога, поэтому после кнопки Reset
> у OVERVIEW обрезало подписи — на это теперь есть тест, сверяющий каталог с раскладкой.
> Все исправлены.

**Базовое состояние на момент аудита (замерено, не оценено):**

- `dotnet test CryptoAITerminal.Core.Tests` — **768/768 зелёные**, 0 падений, 4 с. Регрессий нет; проблема в разделе «Код» №5 — не в падающих тестах, а в непокрытых классах.
- Чистая пересборка решения — **0 ошибок, 68 уникальных предупреждений**: 57 × AVLN5001 (`TextBox.Watermark` устарел — 57 вхождений в 11 файлах, больше всего `SettingsView` 17 и `PortfolioView` 12), 5 × CS0108, 4 nullable (CS8601/CS8602 ×2/CS8604), 1 × CS1998, 1 × CS0219.
- `dotnet build CryptoAITerminal.slnx` пинованным SDK 8.0.422 падает с `MSB4068` — формат `.slnx` требует SDK ≥ 9.0.200 (раздел «Код» №6). Собирать приходится по проектам.
- Визуальная часть построена на анализе AXAML (контрасты посчитаны по WCAG, размеры/отступы/состояния — статистикой по разметке), живой осмотр интерфейса не проводился.

---

## 🔴 Критично (деньги / безопасность / потеря данных)

**1. Binance spot-гейтвей — заглушка-симуляция** — `CryptoAITerminal.Gateway.Binance/BinanceGateway.cs:284`
*Проблема:* `PlaceOrderAsync` не делает ни одного HTTP-вызова: сам ставит `Status = Filled`, генерит Guid и печатает `[SIMULATION]`; `CancelOrderAsync` — no-op, `GetBalanceAsync` всегда возвращает `10000m`. Этот объект подставлен как spot-дефолт в десктопе (`MainWindowViewModel.cs:243, 272, 308, 322, 323, 327`, причём без ключей) и на сервере (`CryptoAITerminal.Executor/GatewayFactory.cs:42`), где `ExchangeBotOrderExecutor.cs:69-81` пишет в БД `status="placed"`. UI и PnL рапортуют исполнение сделок, которых не было.
*Исправить:* реализовать по образцу `BybitGateway.cs:151-177` (`SpotApi.Trading.PlaceOrderAsync` + бросок при `!result.Success`), передать `binanceApiKey/binanceApiSecret` в конструктор. До реализации — `throw new NotSupportedException()` либо возвращать `PaperExchangeGateway` в фабрике.
*Трудоёмкость:* M

**2. Funding-арбитраж: авто-реинвест зацикливается и дублируется** — `CryptoAITerminal.TerminalUI/Services/FundingArbitrageService.cs:385-396`
*Проблема:* `FundingCollectedUsd` пересчитывается с нуля от `pos.OpenedAt` на каждом тике 30-секундного таймера (`FundingArbitrageViewModel.cs:295-296`), затирая сброс `= 0m` из `ReinvestAsync` (строка 432). Плюс `_ = ReinvestAsync(pos)` — fire-and-forget без флага реентерабельности: реальные spot BUY + perp SELL уходят каждые 30 с, а `pos.NotionalUsd += ...` (431) ускоряет рост. Доход считается по `EntryFundingRatePct` «навечно», хотя актуальная ставка лежит рядом в rateMap (379).
*Исправить:* накапливать инкрементально по `LastFundingAccrualUtc` от актуальной ставки (с отрицательными значениями), вход в реинвест через `Interlocked.CompareExchange(ref pos.Reinvesting, 1, 0)` со сбросом в `finally`, мутации `SpotQty/PerpQty/NotionalUsd` — под тем же `lock`, что и `_positions`.
*Трудоёмкость:* M

**3. TpSlManager: `_closed = true` до фактического закрытия — позиция остаётся без стопа навсегда** — `CryptoAITerminal.TerminalUI/Services/TpSlManager.cs:221` (также 211-212, 242, 251)
*Проблема:* Флаг ставится синхронно перед fire-and-forget `FireSpotCloseAsync`, а `HandlePrice` начинается с `if (_closed) return;` (176). Если рыночный ордер упал (429, сеть, minNotional), `catch` только дёргает `OnEvent` — повтора нет, защита выключена навсегда. Не покрыто БАГ-63 (там про постановку TP на старте).
*Исправить:* ввести `_closing`, ставить `_closed` только после подтверждённого филла; в catch сбрасывать `_closing`, поднимать громкое уведомление `OnProtectionLost`, а не строку в лог.
*Трудоёмкость:* M

**4. TpSlManager: трейлинг-стоп теряется — старый SL снят, новый не выставлен** — `CryptoAITerminal.TerminalUI/Services/TpSlManager.cs:290, 293`
*Проблема:* Два дефекта одного узла. (а) Порядок «cancel → place» без отката: при сбое `PlaceStopLossOrderAsync` старый биржевой SL уже снят, `_slOrderId = null`, software-фолбэк закрыт условием `!_usingExchangeTpSl` (226) — позиция с плечом без стопа. (б) `if (!await _slUpdateSem.WaitAsync(0)) return;` молча отбрасывает обновление, при том что `_currentSlPrice = newSl` уже применён на 267/281 — потерянный сдвиг не переигрывается никогда.
*Исправить:* ставить новый стоп до отмены старого; при сбое `_currentSlPrice = oldSlPrice; _usingExchangeTpSl = false;` и переход на software-стоп. Вместо отбрасывания хранить `_pendingSl` + `_slDirty` и повторять проход в `finally`.
*Трудоёмкость:* M

**5. Бейдж «PAPER-ONLY» врёт: Grid/DCA/Rule-боты шлют реальные ордера** — `CryptoAITerminal.TerminalUI/ViewModels/BotsDesk/BotsDeskViewModel.cs:204`, `ViewModels/GridBotViewModel.cs:226-271`, `ViewModels/AIBotViewModel.cs:431-506`, `BotsDesk/BotsDeskViewModel.Live.cs:458`
*Проблема:* `TryApproveLiveExecution` / `GlobalPaperOnlyMode` / `LicenseAllowsLive` вызываются только из ручного CEX, DEX, Sniper и TRON. В `GridBotViewModel`/`AIBotViewModel`/`DcaBotViewModel` слов Paper/TryApprove нет вообще: `_bot = new GridBot(gateway, config); await _bot.StartAsync();` сразу расставляет живую сетку лимиток (`Services/GridBot.cs:82-93`). Пользователь видит зелёный `PAPER-ONLY` (`BotsView.axaml:85`), жмёт ▶ START и теряет деньги.
*Исправить:* первой строкой каждого `StartAsync`: `if (!_walletWorkspace.TryApproveLiveExecution("Grid bot", out var reason)) { BotLog += reason; return; }`. Долгосрочно — единый `IExecutionApprovalGate` перед любым `PlaceOrderAsync` прикладного слоя.
*Трудоёмкость:* M

**6. AI-трейдер в CEX-режиме обходит GlobalPaperOnlyMode** — `CryptoAITerminal.TerminalUI/ViewModels/AiTraderViewModel.cs:298`
*Проблема:* Асимметрия в соседних ветках: DEX делает `var dexLive = LiveEnabled && _dexLiveAllowed();` (275), CEX передаёт `{ LiveEnabled = LiveEnabled }` без стража. Сеттер `LiveEnabled` (177-186) вдобавок пробрасывает значение в уже запущенный сервис, обходя даже DEX-страж. Ордер уходит на `AiTraderAgentService.cs:635`.
*Исправить:* прокинуть `cexLiveAllowed: () => WalletVM.GlobalLiveExecutionEnabled` и `{ LiveEnabled = LiveEnabled && _cexLiveAllowed() }`; продублировать проверку внутри `PlaceOrderTool` перед `_gateway.PlaceOrderAsync`.
*Трудоёмкость:* S

**7. AI-трейдер: Stop не прерывает уже начатый turn — ордера всё равно уходят** — `CryptoAITerminal.TerminalUI/Services/AiTraderAgentService.cs:138`
*Проблема:* `public void Stop() => _loopCts?.Cancel();` не ставит `_killed`. Цикл инструментов `ClaudeAgentRunner.cs:176` не проверяет `ct` между вызовами; `PlaceOrderTool` передаёт `ct` только в `GetQuoteAsync`, который сам его игнорирует (`GetQuoteAsync:773-780`), а `PlaceOrderAsync` (635/643) идёт без токена. UI при этом уже показал «■ stopped» и `IsRunning = false`.
*Исправить:* `public void Stop() { _killed = true; _loopCts?.Cancel(); }`; проверка `ct.IsCancellationRequested` в начале тела `foreach (var (id, name, input) in toolUses)`; `ct.ThrowIfCancellationRequested()` перед вызовом гейтвея в `PlaceOrderTool`/`ClosePositionTool`/`DexBuyTool`/`DexSellTool`.
*Трудоёмкость:* S

**8. Тумблер Auto-Execute в арбитраже: без подтверждения и в обход paper-режима** — `CryptoAITerminal.TerminalUI/Views/ArbView.axaml:22`, `ViewModels/CrossExchangeArbitrageViewModel.cs:234-237`
*Проблема:* Предупреждение «⚠ LIVE ORDERS» (строки 28-33) имеет `IsVisible` на сам `AutoExecute`, т.е. появляется уже после включения. Сеттер сразу пишет `_svc.AutoExecute`, сканер раз в секунду делает `_ = DoExecuteAsync(null, opp)` (393), а в `CrossExchangeArbitrageService.ExecuteArbAsync:196` нет ни одного упоминания paper/TryApprove. Максимум `NotionalUsd` — 1 000 000.
*Исправить:* confirm-диалог с типизацией слова при включении; в `ExecuteArbAsync` первой строкой `return (false, "Global execution guard: PAPER ONLY is active", 0m)`; предупреждение показывать всегда.
*Трудоёмкость:* M

**9. Кнопка «Live Allowed» снимает глобальную защиту одним кликом** — `CryptoAITerminal.TerminalUI/Views/RiskView.axaml:148`
*Проблема:* Обычный `GhostButton` в ряду с «Refresh Wallet» и «Open Logs» вызывает `ApplyGlobalExecutionMode("LIVE")` → `GlobalPaperOnlyMode = false` (`WalletWorkspaceViewModel.cs:1652-1657`) без подтверждения, цветовой маркировки и тоста. В BotsDesk тот же переход требует напечатать `ARM` (`BotsDeskViewModel.cs:482`).
*Исправить:* переиспользовать `AskConfirm`/`ConfirmConfig` с типизацией слова, покрасить кнопку в `#FF6B6B`, показывать тост после переключения; «Paper Mode» оставить как есть.
*Трудоёмкость:* S

**10. Клавиши B и S шлют живой рыночный ордер с любого экрана** — `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml.cs:182`, `Services/HotkeySettings.cs:23-24`
*Проблема:* Обработчик навешан на всё окно (`AddHandler(KeyDownEvent, ..., RoutingStrategies.Tunnel)`, строка 145), фильтры — только «фокус не на TextBox/NumericUpDown/ComboBox» и «нет модификаторов»; `SelectedShellSection` не проверяется. Дефолты — `"B"`/`"S"` без модификатора, `BuyMarketCommand` идёт сразу в исполнение без диалога.
*Исправить:* `if (vm.SelectedShellSection != "trading") return;` в начале обработчика; дефолты на `Ctrl+B`/`Ctrl+S`; arm-состояние в две стадии (паттерн уже есть в `AllPositionsViewModel.CloseAllAsync`).
*Трудоёмкость:* M

**11. Escape отменяет ВСЕ рабочие ордера** — `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml.cs:187`, `Services/HotkeySettings.cs:28`
*Проблема:* `CancelOrders = "Escape"`, обработчик глобальный и ставит `e.Handled = true`, поэтому Escape (а) не доходит до модалок и командной палитры, (б) снимает все working orders, включая биржевые TP/SL (`CancelSingleOrderAsync` → `ActiveFuturesGateway.CancelOrderAsync`, строка 4975). Обратная связь — одна строка `AddLog`.
*Исправить:* дефолт на `Delete`/`Ctrl+Escape`; обрабатывать только в разделе трейдинга и при отсутствии открытых оверлеев; подтверждение при отмене >N ордеров + тост.
*Трудоёмкость:* S

**12. Софтовый SL/TP удаляется из списка ДО отправки ордера** — `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs:6404`
*Проблема:* `ExecuteWorkingOrderAsync` делает `WorkingOrders.Remove(order)` + `PersistSoftwareWorkingOrders()` (6404-6406) ещё до `try` (6411). Любой ранний return (6418, 6440, 6449, 6456) или `catch` (6466) оставляет позицию без стоп-лосса, и восстановить нечего — записи нет ни в памяти, ни на диске.
*Исправить:* флаг `IsExecuting` в `WorkingOrderViewModel`, пропуск таких ордеров в `EvaluateWorkingOrdersAsync`, удаление и персист — только в ветке успеха; в catch сбрасывать флаг и поднимать критический тост.
*Трудоёмкость:* M

**13. Нигде нет округления по stepSize/tickSize и проверки minNotional** — `CryptoAITerminal.TerminalUI/Services/GridBot.cs:77`, `OrderRouter/MarketOrderRouter.cs`, `Services/CopyTradingFollowerService.cs:112`, `Services/FundingArbitrageService.cs:405`
*Проблема:* Grep по `stepSize|tickSize|minNotional|LOT_SIZE|PRICE_FILTER|exchangeInfo` — ноль совпадений. `_spacing = (Upper - Lower) / GridLevels` даёт цену вида `1428.571428571428571428571429`, биржа отвечает `-1013`, ошибка уходит в лог (`GridBot.cs:167, 211`), а бот остаётся `IsRunning`. В copy-trading и funding-арбитраже вместо шага лота — произвольный `Math.Round(..., 6)`.
*Исправить:* `Task<SymbolFilters> GetSymbolFiltersAsync(string symbol)` в `IExchangeGateway` с кэшем на сессию; хелпер `FloorToStep(v, step)`; применять к цене и количеству во всех точках отправки; отказывать явным сообщением при `qty * price < minNotional`.
*Трудоёмкость:* L

**14. Spot market BUY на OKX и Bybit: количество трактуется как сумма в USDT** — `CryptoAITerminal.Gateway.OKX/OKXGateway.cs:127`, `CryptoAITerminal.Gateway.Bybit/BybitGateway.cs:157`
*Проблема:* Для OKX `sz` интерпретируется по `tgtCcy` (дефолт для BUY — quote_ccy), параметр `quantityAsset` не передаётся, хотя в JK.OKX.Net 4.13.0 он есть. У Bybit V5 UTA `marketUnit` по умолчанию `quoteCoin`, параметр в Bybit.Net 6.12.0 доступен. «Купить 500 токенов» превращается в «потратить 500 USDT».
*Исправить:*
```csharp
quantityAsset: order.Type == CoreOrderType.Market ? QuantityAsset.BaseAsset : null   // OKX
marketUnit:   type == NewOrderType.Market ? MarketUnit.BaseAsset : null              // Bybit
```
плюс тест на единицу измерения в запросе.
*Трудоёмкость:* S

**15. Серверный trailing-бот игнорирует kill-switch и allowlist** — `CryptoAITerminal.Executor/BotExecutorService.cs:336`
*Проблема:* В `RunTrailingAsync` (309-352) нет ни `LiveGate.Check`, ни `_allowlist.IsAllowed`, ни `_risk.Check`, ни DailyCapGate — при том что DCA-ветка (98-150) и grid-ветка (205-241) делают всё это. При `mode="live"` `GridGatewayProvider.CreateAsync:50-59` расшифровывает ключ и отдаёт боевой гейтвей, `TrailingBotRunner.cs:123, 128` шлёт stop-loss и рыночный close.
*Исправить:* вынести три проверки в `TryGateLiveAsync(bot, mode, symbol, notional, strategyName, ct)` и вызывать из всех трёх веток.
*Трудоёмкость:* S

**16. AI-трейдер в live не регистрирует убытки — дневной лимит потерь не работает** — `CryptoAITerminal.TerminalUI/Services/AiTraderAgentService.cs:697`
*Проблема:* `_risk.RecordLoss` вызывается только в бумажных ветках (672 и 723). В живой ветке `ClosePositionTool` (677-699) реализованный PnL не считается, поэтому `_dailyLoss` навсегда ноль и проверка `if (_dailyLoss >= _maxDailyLossUsd)` (`RiskManager.cs:44`) не срабатывает никогда. Тот же класс, что закрытый БАГ-02, но в другом сервисе.
*Исправить:* в живой ветке считать PnL по `pos.EntryPrice`/`pos.Quantity`/цене закрытия и вызывать `if (pnl < 0) _risk.RecordLoss(Math.Abs(pnl));` до `return Json`. Тест: два убыточных live-закрытия выше лимита → следующий `PlaceOrderTool` отклоняется.
*Трудоёмкость:* M

**17. TradingBot: лимит позиции = quantity × 50000 — не срабатывает ни на чём дешевле BTC** — `CryptoAITerminal.TerminalUI/Services/TradingBot.cs:68`
*Проблема:* `maxPositionSizeUsd: Math.Max(maxRiskPerTrade * 5, tradeQuantity * 50000)`. `RiskManager` сравнивает `Quantity * currentPrice` с этим потолком (`RiskManager.cs:54-58`), т.е. условие истинно только при `currentPrice > 50000`. Плюс `maxDailyLossUsd: maxRiskPerTrade` подменяет смысл настройки UI.
*Исправить:* принимать `maxPositionSizeUsd` и `maxDailyLossUsd` отдельными параметрами из UI, потолок считать по номиналу, а не по количеству.
*Трудоёмкость:* M

**18. Copy-trading follower: два параллельных поллера + зеркалирование всей истории лидера** — `CryptoAITerminal.TerminalUI/Services/CopyTradingFollowerService.cs:59, 94`
*Проблема:* `Stop()` обнуляет `_cts` не дожидаясь цикла, `ApplyMode` (`CopyTradingViewModel.cs:304/308/312`) делает Stop/Start подряд → второй `PollLoopAsync` проходит guard. `_executedIds` — обычный `HashSet`, `Contains`+`Add` (97-98) неатомарны. Множество не засеивается при старте, а лидер отдаёт весь список (`CopyTradingLeaderService.cs:19 MaxTrades = 100`) — первый опрос шлёт до 100 рыночных ордеров по текущей цене без риск-гейта.
*Исправить:* `StopAsync()` с ожиданием `_loopTask` и `Dispose`; `ConcurrentDictionary` + `TryAdd`; водяной знак `_since = DateTime.UtcNow` при Start и отсечка сделок старше 60 с; прогон через `RiskManager.CanPlaceOrder` и `TryApproveLiveExecution`.
*Трудоёмкость:* M

**19. GridBot.ResumeAsync гоняется с работающим таймером опроса** — `CryptoAITerminal.TerminalUI/Services/GridBot.cs:274`
*Проблема:* `PauseAsync` (266-272) не останавливает `_pollTimer` (в отличие от `StopAsync:291`). `ResumeAsync` сбрасывает `_isPaused = false` первым делом, а `PlaceInitialOrdersAsync` работает вне `_pollLock`. Тик может получить снимок `GetOpenOrdersAsync` без только что размещённых лимиток и посчитать их исполненными (226): выставится sell без инвентаря, а id уйдёт из словаря — `CancelAllOrdersAsync` его больше не отменит, живая лимитка останется на бирже.
*Исправить:* глушить таймер в `PauseAsync`; в `ResumeAsync` взять `_pollLock`, очистить словари, разместить сетку, снять флаг, перезапустить таймер, отпустить лок. В `CancelAllOrdersAsync` подстраховаться реальным `GetOpenOrdersAsync`.
*Трудоёмкость:* M

**20. Экран позиций молча прячет биржи, ответившие ошибкой** — `CryptoAITerminal.TerminalUI/ViewModels/AllPositionsViewModel.cs:235`
*Проблема:* `catch { /* gateway not connected / not implemented → skip silently */ }`, при этом `StatusLabel` (245-248) считает `_gateways.Count`, а не число успешных: «4 exchanges checked» печатается даже когда упали все четыре, а при пустом результате — «No open positions found». Пользователь с открытым шортом видит флэт; CLOSE ALL закроет только попавшее в список.
*Исправить:* собирать `failed.Add((name, ex.Message))`, писать `"2 позиции · Bybit, OKX недоступны — данные неполные"` жёлтым + баннер над таблицей; CLOSE ALL при непустом `failed` дизейблить.
*Трудоёмкость:* M

**21. CredentialsService: ошибка чтения → пустые креды → следующее сохранение стирает всё** — `CryptoAITerminal.TerminalUI/Services/CredentialsService.cs:548`
*Проблема:* `catch { return new(); }` не различает «файла нет» и «DPAPI-расшифровка провалилась». Все сейверы (`SaveBinance:500`, `SaveBybit:509`, `SaveOkx:518`, `SaveKucoin:528`, `SaveAiSettings:480`, `SaveIntegrations:358`, `SaveNotifications:389`) — read-modify-write через `WriteToDisk` (560-569), который делает `File.Move(tmp, FilePath, overwrite: true)` без бэкапа. Один ввод ключа Binance после сбоя уничтожает `DexPrivateKey` (93), SMTP-пароль, Telegram-токены и ключи трёх бирж. `AtomicJsonFile.BackupCorruptFile` в проекте есть, но здесь не вызывается.
*Исправить:* ловить только `FileNotFoundException`; на `CryptographicException`/`JsonException` — бэкап файла, флаг `LoadFailed` и запрет `WriteToDisk` до явного подтверждения пользователя; `File.Copy(FilePath, FilePath + ".bak", overwrite: true)` перед каждой записью.
*Трудоёмкость:* S

**22. CredentialsService: при сбое DPAPI секреты пишутся открытым текстом молча** — `CryptoAITerminal.TerminalUI/Services/CredentialsService.cs:571`
*Проблема:* `TryEncrypt` при исключении `ProtectedData.Protect` возвращает `plaintextJson`, и все ключи бирж, AI-ключи и Telegram-токен ложатся в `%LocalAppData%\CryptoAITerminal\api-credentials.json` без шифрования и без маркера. `TryDecrypt` (590) читает такой файл как «legacy plaintext», поэтому деградация не будет замечена никогда. TRADING_AUDIT.md:18 при этом обещает «✅ БЕЗОПАСНО, DPAPI-шифр».
*Исправить:* не глушить: логировать, выставлять `CredentialsProtectionFailed` и пробрасывать; если plaintext-фолбэк нужен — писать маркер `PLAINTEXT:v1:` и постоянный баннер в Settings.
*Трудоёмкость:* S

**23. Идентичность пользователя на сервере = поле Name лицензии (имя в Telegram)** — `CryptoAITerminal.Server.Api/Program.cs:148`, `Server.Data/UsersRepository.cs:24`
*Проблема:* `var key = "license:" + license.Trim();`, где license — `check.Payload!.Name`, заполняемый из профиля Telegram (`LicenseBot/UpdateHandler.cs:409, 436-438`). Значение неуникально и меняется покупателем за две секунды. Двое «Иванов Петровых» садятся на один uid; атакующий переименовывает профиль, покупает дешёвый план и получает `/api/secrets`, `/api/withdrawals`, `/api/bots` жертвы. `Payload.Machine` сервером не проверяется нигде.
*Исправить:* добавить в `LicenseInfo`/`LicensePayload` непубличное неизменяемое поле `Sub` (GUID заказа или хэш telegramId+соль), выпускать в `LicenseSigner.CreateToken`, ключевать `GetOrCreateByLicenseAsync` по нему; существующие строки мигрировать вручную.
*Трудоёмкость:* M

**24. WebApi fail-open: без токена открыт приём реальных рыночных ордеров** — `CryptoAITerminal.WebApi/Program.cs:26`
*Проблема:* При пустом `CRYPTOAI_WEBAPI_TOKEN` middleware делает `await next(); return;` для всех путей. Сервис слушает `http://0.0.0.0:5180` (`appsettings.json:11`) с `AllowAnyOrigin()` (Program.cs:9). Открыты `/api/orders/market` (118) и `/api/orders/cancel` (135), которые `WebApiQueueProcessor.cs:178` исполняет реальным гейтвеем. Вебхук TradingView: `if (!string.IsNullOrWhiteSpace(tvSecret) && ...)` (170) — без секрета проверка пропускается вовсе, а порт по смыслу смотрит в интернет.
*Исправить:* fail-closed — без токена отдавать 503 на все write-эндпоинты; без `CRYPTOAI_TV_SECRET` вебхук выключать; Kestrel по умолчанию на `127.0.0.1`; CORS убрать.
*Трудоёмкость:* S

**25. `/api/2fa/setup` сбрасывает 2FA одной лицензией — полный обход второго фактора на выводе** — `CryptoAITerminal.Server.Api/Program.cs:403`
*Проблема:* Эндпоинт не требует текущего TOTP-кода, а `TwoFactorRepository.UpsertSecretAsync:15-24` делает `ON CONFLICT (user_id) DO UPDATE SET secret_ciphertext=EXCLUDED..., enabled=false`. Гейт `Verify2faAsync` (173-179) устроен как `if (row is null || !row.Enabled) return true;`, поэтому после одного POST вывод средств (`/api/withdrawals`, 431) проходит без кода.
*Исправить:* при существующей записи с `Enabled == true` требовать валидный текущий код; отдельный метод репозитория, не трогающий `enabled`; audit-запись `2fa_reset`; сделать 2FA обязательной для `/api/withdrawals`.
*Трудоёмкость:* S

**26. Мастер-ключ CRYPTOAI_KEK_B64 обязателен на публичном api-контейнере** — `docker-compose.yml:59`
*Проблема:* Комментарий в том же файле (129-137) утверждает, что KEK держит только executor и что на internet-facing узле его быть не должно. Реально `:?`-переменная объявлена у сервиса `api` за Caddy, и он её использует: `Server.Api/Program.cs:52-54` регистрирует `LocalAesEnvelopeCipher.FromBase64(kekB64)`, 58-64 поднимают `TradingService`, 572 — `MapTradeEndpoints()`, а `Executor/TradingService.cs:70` расшифровывает ключи бирж всех пользователей в памяти этого процесса.
*Исправить:* убрать KEK из `api`; не регистрировать `IEnvelopeCipher`/`ITradingService`/`MapTradeEndpoints` при роли edge (`CRYPTOAI_ROLE=edge`); `/api/trade/*`, `/api/secrets`, `/api/2fa` — на изолированный executor-узел или через `VaultTransitEnvelopeCipher`. Поправить вводящий в заблуждение комментарий.
*Трудоёмкость:* L

**27. Rate limit и дневной AI-бюджет ключуются сырым заголовком X-License** — `CryptoAITerminal.Server.Api/Program.cs:85` (также 265, 296)
*Проблема:* Партиция лимитера и ключ `AiBudget` (`ConcurrentDictionary<string, Counter>(StringComparer.Ordinal)`) берут `Headers["X-License"].ToString()`, тогда как `LicenseTokenValidator` нормализует токен: `Trim()`, `Convert.FromBase64String` (игнорирует пробелы), `Replace('-','+').Replace('_','/')`. Один валидный токен даёт бесконечно много ключей → `RATE_LIMIT_PER_MIN` и `AI_DAILY_TOKENS_PER_LICENSE` (единственный контроль расхода серверного ключа Anthropic/OpenAI) обходятся тривиально.
*Исправить:* валидировать лицензию до лимитера, класть канонический ключ (`Payload.Sub` или SHA-256 от декодированного payload) в `ctx.Items` и использовать его в лимитере, `ForwardAiAsync` и `/api/ai/budget`.
*Трудоёмкость:* M

**28. `HasPrivateApiCredentials` по умолчанию `true` — pre-trade guard fail-open для 6 из 8 гейтвеев** — `CryptoAITerminal.Core/Interfaces/IExchangeGateway.cs:35`
*Проблема:* Default interface member `=> true` переопределён только в `BinanceGateway.cs:22` и `BinanceFuturesGateway.cs:411`. Bybit/OKX/KuCoin (spot+futures) наследуют `true`, поэтому шапка горит зелёным «Private API Ready» (`MainWindowViewModel.cs:2183-2185`), `GetCexExecutionGuardReason` (6928-6936) пропускает, кнопка Buy активна, ордер падает 401 после нажатия, а `RefreshManualAccountStateAsync` (4320) долбит приватные эндпоинты.
*Исправить:* сделать член обязательным (`bool HasPrivateApiCredentials { get; }`) и реализовать в шести классах через проверку key/secret/passphrase; поправить текст ошибки на 6930 («Binance futures» при выбираемой бирже); тест на все восемь классов.
*Трудоёмкость:* S

**29. DexKeeperStore: неатомарная конкурентная запись + молчаливая потеря армированных стоп-ордеров** — `CryptoAITerminal.TerminalUI/Services/DexKeeperStore.cs:42`, `ViewModels/DexTradingViewModel.cs:808-812`
*Проблема:* `File.WriteAllText` прямо в целевой файл под `catch {}`; `SaveKeeper` вызывается как `_ = Task.Run(...)` из четырёх мест (772, 805, 867, 1612) без сериализации. `Load()` (26-40) на битом JSON молча возвращает пустой список, и после перезапуска все stop-loss/trailing/DCA по DEX исчезают без единого сообщения. `AtomicJsonFile.Write` в проекте есть и здесь не используется. Тот же паттерн в `DexPerpSessionStore.cs:50-61` и `_watchlistStore.Save` (1148).
*Исправить:* `AtomicJsonFile.Write` с уникальным tmp + `SemaphoreSlim(1,1)`/очередь «сохранить последний снимок»; в `Load` — `BackupCorruptFile` + признак ошибки и явное сообщение пользователю.
*Трудоёмкость:* S

**30. При закрытии приложения останавливается только Rule Bot — лимитки грида остаются на бирже** — `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml.cs:94`
*Проблема:* В `Closing` гасится только `AIBotVM`; в `MainWindowViewModel.Dispose` `GridBotVM`/`DcaBotVM` не встречаются вовсе, хотя `GridBot.StopAsync` (`Services/GridBot.cs:288-297`) именно и вызывает `CancelAllOrdersAsync`. Состояние сетки (`_activeBuyOrders`/`_activeSellOrders`) живёт только в памяти, после рестарта бот выставит второй комплект поверх старого.
*Исправить:* собрать боты в `IReadOnlyList<IAsyncDisposable>` и гасить с общим таймаутом; персистить состояние грида рядом с `software-working-orders.json` и на старте предлагать «возобновить / отменить».
*Трудоёмкость:* L

**31. Telegram bot token и ключи AI-провайдеров лежат в Postgres открытым текстом** — `CryptoAITerminal.Server.Data/NotificationRepository.cs:23`, `ProviderKeyStore.cs:33`
*Проблема:* `INSERT INTO notification_channels (..., token, ...)` и `INSERT INTO provider_keys (provider, api_key, ...)` без шифрования, при том что в том же процессе зарегистрирован `IEnvelopeCipher` и `SecretsRepository` хранит только ciphertext+wrapped_dek. Любой дамп/реплика отдаёт рабочие Telegram-боты клиентов и серверные ключи Anthropic/OpenAI/Covalent.
*Исправить:* `token` → `token_ciphertext`/`token_wrapped_dek`, `api_key` → `api_key_ciphertext`/`api_key_wrapped_dek`; шифровать в Upsert/Set, расшифровывать точечно; миграция с перешифровкой и обнулением открытых колонок.
*Трудоёмкость:* M

---

## 🟠 Важно (неправильное поведение, падения, утечки)

**1. Нет ни одного глобального обработчика исключений; 380 из 407 ReactiveCommand не защищены** — `CryptoAITerminal.TerminalUI/Program.cs:10`, `ViewModels/MainWindowViewModel.cs:4121`
*Проблема:* Grep по решению: `AppDomain.CurrentDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, `RxApp.DefaultExceptionHandler` — ноль. `ThrownExceptions` подписан только в `MainWindowViewModel.cs:4138` (11 команд) и `SniperViewModel.cs:531` (16). Плюс 18 обработчиков `Tick += async (_, _) => ...` (async void), из которых, например, `DexTradingViewModel.cs:1660 → PollKeeperAsync` защищён try/catch лишь частично. Приложение исчезает молча при открытых позициях.
*Исправить:* в `Program.Main` до `StartWithClassicDesktopLifetime` — три перехватчика с записью в файловый лог; `SubscribeCommandErrors` сделать extension-методом и вешать при создании каждой команды; обернуть все `Tick += async` в `SafeTick(Func<Task>)` по образцу `GridBot.cs:95`.
*Трудоёмкость:* M

**2. Ошибка запуска Grid-бота роняет приложение, бот остаётся «running»** — `CryptoAITerminal.TerminalUI/ViewModels/GridBotViewModel.cs:267`
*Проблема:* `IsRunning = true` стоит до `await _bot.StartAsync()` (270), тело `StartAsync` (225-270) без try/catch, а `GridBot.StartAsync` делает `ConnectAsync`/`GetCurrentPriceAsync`/`PlaceInitialOrdersAsync` — все бросают при плохих ключах/бане/обрыве. Исключение уходит в `RxApp.DefaultExceptionHandler`, которого нет.
*Исправить:* try/catch с `IsRunning = false; _bot?.Dispose(); _bot = null; BotLog = $"Start failed: {ex.Message}"` + тост; `IsRunning = true` — после успешного await.
*Трудоёмкость:* M

**3. Старт без интернета: тикер-стрим не переподключается до перезапуска** — `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs:5037`
*Проблема:* `ConnectAsync` вызывается ровно один раз из `InitializeAsync`, ошибка гасится `AddLog`. Авто-реконнект CryptoExchange.Net работает только для уже установленной подписки. На весь сеанс мертвы ценовые алерты (`AlertService` подписан на `_gateway.MarketDataStream`, строка 331) и доска рынков; индикатора «поток не подключён» в UI нет.
*Исправить:* цикл с экспоненциальным бэкоффом по образцу `LiquidationStreamService.cs:376-419`; свойство `MarketStreamConnected` и бейдж LIVE/RECONNECTING/OFFLINE; блокировать арминг алертов и старт ботов без стрима.
*Трудоёмкость:* M

**4. Реестр локализации в MainWindow растёт бесконечно и держит мёртвые контролы** — `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml.cs:41`
*Проблема:* `record struct LocalizationKey(AvaloniaObject Target, string PropertyName)` — сильная ссылка; в `_sourceTexts`/`_observedProperties`/`_localizationSubscriptions` только добавляют, `Remove`/`Clear` нет (Dispose только в `Closing`). `_localizationScanTimer` каждые 2 с обходит видимое дерево и регистрирует новые контролы, а списки пересобираются полным Clear+Add (`MarketFeedDeskViewModel.cs:916` каждые 220 мс, `BotsDeskViewModel.cs:378`) в невиртуализированных ItemsControl. Таймер никогда не стабилизируется.
*Исправить:* `WeakReference<AvaloniaObject>` (или `ConditionalWeakTable`); хранить подписку вместе с ключом и в тике удалять записи, у которых `GetVisualRoot() is null`; не регистрировать поддеревья под `ItemsPresenter`.
*Трудоёмкость:* L

**5. Логотип и баннер токена (Bitmap) заменяются без Dispose** — `CryptoAITerminal.TerminalUI/ViewModels/DexTokenMetadataViewModel.cs:175`
*Проблема:* `Logo`/`Banner` — `Avalonia.Media.Imaging.Bitmap` (нативная Skia-поверхность). Старое значение не диспоузится ни в сеттере, ни в `Clear()` (226-235), ни в проигравшей гонке (`if (seq == _seq)` — свежескачанный Bitmap теряется). `ApplyAsync` вызывается на каждую смену токена (`DexTradingViewModel.cs:155 → 212`).
*Исправить:* диспоузить предыдущее значение в сеттере (`if (!ReferenceEquals(old, value)) old?.Dispose();`) и `else logo?.Dispose();` в отброшенной ветке гонки.
*Трудоёмкость:* S

**6. Чарт-контролы не отписываются от CollectionChanged при удалении из дерева** — `CryptoAITerminal.TerminalUI/Controls/CexPriceChart.cs:49`, `Controls/CexCandlestickChart.cs:554-582`
*Проблема:* Подписка/отписка живёт только в `OnPropertyChanged` по `PointsProperty`/`CandlesProperty`. `OnDetachedFromVisualTree` не переопределён нигде в проекте. При удалении виджета с дашборда или фильтрации `VisibleMarkets` долгоживущая коллекция VM держит делегат на мёртвый контрол, и на каждом тике вызывается `InvalidateVisual()` впустую.
*Исправить:* вынести тело подписки в метод и вызывать из `OnAttachedToVisualTree`, отписываться в `OnDetachedFromVisualTree` (для `_subscribedCollection` и `_subscribedWalls`).
*Трудоёмкость:* S

**7. GridBotViewModel обновляет реактивные свойства из потока таймера бота** — `CryptoAITerminal.TerminalUI/ViewModels/GridBotViewModel.cs:298`
*Проблема:* `AppendLog` делает `BotLog += ...` без диспетчера, а события приходят из колбэка `System.Threading.Timer` (`Services/GridBot.cs:97 → PollFillsAsync`, `OnLog?.Invoke` на 249-251). `+=` по строке — неатомарный read-modify-write, конкурирующий с записями из UI (264, 210): строки лога теряются. Рядом `DcaBotViewModel.cs:426-427` делает это правильно через `Dispatcher.UIThread.Post`.
*Исправить:* обернуть `AppendLog` и обработчик `OnStatsChanged` в `Dispatcher.UIThread.Post`.
*Трудоёмкость:* S

**8. CompositeRuleEngine: словари пишутся из потока сокета и читаются из UI-таймера** — `CryptoAITerminal.TerminalUI/Services/CompositeRuleEngine.cs:98`
*Проблема:* `FeedMarketData` вызывается напрямую из Rx-подписки (`MainWindowViewModel.cs:1107`) без `Dispatcher.UIThread.Post` (соседняя подписка на 1225 его имеет), а `EvaluateAll` (132) читает те же `Dictionary` из `DispatcherTimer`. Параллельные запись+чтение обычного словаря → `InvalidOperationException` или зависание в цепочке бакетов на UI-потоке. Плюс `_marketDataSubscription2` не диспоузится.
*Исправить:* `ConcurrentDictionary` либо общий `lock (_stateLock)`; минимально — маршалить подписку через `Dispatcher.UIThread.Post`; добавить `_marketDataSubscription2?.Dispose()` в `Dispose()`.
*Трудоёмкость:* M

**9. GridBot считает исчезнувший из open-orders ордер исполненным** — `CryptoAITerminal.TerminalUI/Services/GridBot.cs:222`
*Проблема:* Единственный критерий «исполнен» — отсутствие Id в `openIds`. Отмена руками, экспирация, ADL или пустой список из-за сбоя (`BybitGateway.GetOpenOrdersAsync` при `!result.Success` возвращает `[]`, не бросая) дают фантомный филл: выставляется sell без инвентаря (231), инкрементируются `CyclesCompleted` и `GridPnL` по ценам сетки (240-251), результат уходит в PnL-дашборд (`MainWindowViewModel.cs:984`).
*Исправить:* запрашивать реальный статус ордера и продолжать только при `Filled/PartiallyFilled`, беря фактические `FilledQuantity` и цену филла; при `openOrders.Count == 0` и >1 ордере в словарях считать тик сбойным.
*Трудоёмкость:* M

**10. MaxDrawdownPct считается от пика кумулятивного PnL** — `CryptoAITerminal.TerminalUI/Services/PnlDashboardService.cs:191`
*Проблема:* `equity` стартует с нуля, поэтому у стратегии, убыточной с первой сделки, `peak == 0` и дашборд показывает «Max drawdown 0%» при любом убытке; при малом пике процент, наоборот, раздувается. Плюс `TradeRecord.IsWin => PnlUsd > 0m` относит сделки с нулевым PnL к убыточным и занижает WinRate.
*Исправить:* вести `equity = start + cumulativePnl` с `peak = start` и настраиваемым стартовым капиталом; разделить win/loss/breakeven, нулевые исключать из знаменателя WinRate.
*Трудоёмкость:* S

**11. KuCoin (spot и futures): таймер тикеров без reentrancy-гарда** — `CryptoAITerminal.Gateway.KuCoin/KucoinGateway.cs:52`, `KucoinFuturesGateway.cs:72`
*Проблема:* Период 3 с, а `PollTickersAsync` идёт по 11 символам последовательно (`MainWindowViewModel.cs:35, 258-259`) — при 150-400 мс на round-trip проход занимает 2-4,5 с, тики накладываются и накапливаются: 429/бан и устаревшие котировки вперемешку в `MarketDataStream`. Рядом есть правильные примеры: `WebApiQueueProcessor.cs:76`, `BalanceRefresher.cs:61`.
*Исправить:* `if (Interlocked.Exchange(ref _polling, 1) == 1) return;` + `finally`; символы — через `Task.WhenAll` или пакетный `GetTickersAsync()`.
*Трудоёмкость:* S

**12. Миграции Postgres применяются только при первом создании БД** — `docker-compose.yml:18`
*Проблема:* `/docker-entrypoint-initdb.d` выполняется только на пустом каталоге данных, а том `pgdata` переживает пересборку. Раннера миграций в коде нет (grep на `schema_migrations|EnsureCreated|Migrate()` — ноль), в SETUP.md процедура не описана. Новая таблица → api стартует нормально → первый запрос падает `relation does not exist` и отдаёт голый 500 (`UseExceptionHandler` тоже нет).
*Исправить:* таблица `schema_migrations(filename, applied_at)` и прогон `./db/*.sql` в лексикографическом порядке на старте api (или Flyway/DbUp отдельным one-shot сервисом с `depends_on: db: service_healthy`); сделать .sql идемпотентными; проверку версии схемы в `/health`.
*Трудоёмкость:* L

**13. Нет UseForwardedHeaders за Caddy** — `CryptoAITerminal.Server.Api/Program.cs:159`
*Проблема:* `ClientIp(ctx) => ctx.Connection.RemoteIpAddress?.ToString()` за прокси всегда возвращает адрес контейнера caddy, поэтому `audit_log.ip` для `withdrawal_requested`/`secret_stored`/`2fa_enabled` (386, 398, 418, 440, 452) бесполезен. Плюс лимитер стоит до auth (100 vs 107), и весь анонимный трафик делит одно окно 120/мин.
*Исправить:* `app.UseForwardedHeaders(...)` с обязательными `KnownProxies`/`KnownNetworks`, в Caddyfile отдавать стандартный `X-Forwarded-For`.
*Трудоёмкость:* S

**14. Чекбоксы «I understand…» в диалогах подтверждения ни на что не влияют** — `CryptoAITerminal.TerminalUI/ViewModels/BotsDesk/BotsDeskViewModel.Modals.cs:588`
*Проблема:* `ConfirmReady` смотрит только на введённое слово; `ConfirmCheck.On` (`BotsDeskItems.cs:225`) присваивается в 607 и нигде не читается для гейтинга. У «Enable AUTO mode» (`Rail.cs:329-334`) `HasType` не задан вовсе — `ConfirmReady == true` изначально.
*Исправить:* `ConfirmReady => (!HasType || слово совпало) && ConfirmChecks.All(c => c.On);`, вызывать `RaiseConfirmBtn()` из команды чекбокса; проставить `HasType = true, TypeWord = "AUTO"` для copilotAuto.
*Трудоёмкость:* S

**15. Блок «How to create keys» в Settings всегда пустой** — `CryptoAITerminal.TerminalUI/Views/SettingsView.axaml:356`
*Проблема:* `SettingsDeskViewModel.cs:538` отдаёт `(string n, string text)[]`, у `ValueTuple` `Item1`/`Item2` — поля, а reflection-биндинг Avalonia работает только со свойствами. `x:CompileBindings="False"` (строка 5) прячет ошибку от компилятора. Новичок жмёт «?» и видит 4 пустые строки.
*Исправить:* `public sealed record HelpStep(string N, string Text);`, биндинги `{Binding N}`/`{Binding Text}` и `x:DataType="vm:HelpStep"` в DataTemplate.
*Трудоёмкость:* S

**16. Нет обработки 429 и бэкоффа в REST-поллерах** — `CryptoAITerminal.TerminalUI/Services/CustomMarketPoller.cs:45`
*Проблема:* Каждые 6 с последовательный опрос каждого добавленного рынка отдельным запросом; при 30 монетах — 300 запросов/мин к DexScreener (его лимит — 300/мин). Grep на `429|TooManyRequests|Retry-After` в CEX/DEX-гейтвеях — ноль. При бане `catch { }` глотает ошибку, цены замирают без индикации, а поллер продлевает бан.
*Исправить:* отличать 429/418, умножать интервал (6→12→24…до 5 мин) с уважением `Retry-After`; батчить запросы; выставлять во `CexMarketItemViewModel` флаг «данные устарели» вместо пустого catch.
*Трудоёмкость:* M

**17. Триал сбрасывается удалением файла, а ошибка чтения = «триал только начался»** — `CryptoAITerminal.TerminalUI/Services/LicenseService.cs:190`
*Проблема:* `%LocalAppData%\CryptoAITerminal\.trial` — простой текст с ISO-датой, без подписи и привязки к машине. Удаление файла рестартит 14 дней (201-203), дата из будущего даёт сколько угодно дней (`TrialDaysRemaining` клампит только снизу, 182-188), `catch { return DateTime.UtcNow; }` (205-209) играет в пользу обходчика. Гейт реальный: `MainWindowViewModel.cs:3860`.
*Исправить:* DPAPI с entropy + дубль в HKCU, брать максимум давности; `Math.Clamp(TrialDays - elapsed, 0, TrialDays)`, дату из будущего считать подделкой; `catch` → «истёк».
*Трудоёмкость:* M

**18. Автообновление молча проглатывает все ошибки** — `CryptoAITerminal.TerminalUI/Services/VelopackUpdateService.cs:44`
*Проблема:* `CheckAsync` (54-58) при любой ошибке возвращает «обновлений нет» — неотличимо от «вы на последней версии»; `DownloadAsync` — `false` без причины; `ApplyAndRestart` — `catch {}`. Конструктор при сбое оставляет `_manager = null`, и `IsSupported` навсегда false. Пользователь месяцами сидит на версии с известными критическими багами.
*Исправить:* `AppUpdateInfo.Failed(current, reason)` вместо ложного «актуально»; три состояния в UI; логировать каждую проверку.
*Трудоёмкость:* S

**19. В десктопе нет файлового лога вообще** — `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs:6564`
*Проблема:* Единственный лог — строка `LogMessages` в памяти + лента из 8 строк. Grep на `Serilog|Microsoft.Extensions.Logging|AppendAllText|StreamWriter|ILogger` — ноль; `.LogToTrace()` в опубликованном приложении никуда не пишет; три `Debug.WriteLine` вырезаются в Release. ~67 пустых catch-блоков в продакшн-коде тоже молчат. После падения разобрать инцидент нечем.
*Исправить:* один статический логгер в `%LocalAppData%\CryptoAITerminal\logs\app-{date}.log` с ротацией (Serilog.Sinks.File, `retainedFileCountLimit: 14`); `AddLog` дублирует в файл; туда же три глобальных обработчика исключений.
*Трудоёмкость:* M

---

## 🎨 Визуал и UX

**1. Основной цвет подписей `#2d4a5e` даёт контраст ~2,1:1 — 377 подписей нечитаемы** — `CryptoAITerminal.TerminalUI/Views/AiSignalsView.axaml:65`
*Проблема:* `Foreground="#2d4a5e"` встречается 377 раз в 12 файлах (PortfolioView 73, BotsView 66, SettingsView 59, AiSignalsView 43, NewsView 32, RulesView 31, BotsModals 23, AnalyticsView 22, TradingDeskView 19). L(#2d4a5e)=0.0627 против фона #050c14 (0.0034) = 2,11:1 при WCAG AA 4.5:1 для шрифта 8-9px. Следом `#3d5a72` (134 раза) — 2,7:1, `#4A6880` (51) — 3,3:1.
*Исправить:* ввести шкалу токенов `TextMuted #9FB4C8` / `TextSoft #7C97AF` / `TextDim #6A85A0` в `Styles/AppStyles.axaml` и массово заменить эти три цвета в атрибутах `Foreground=` на `{DynamicResource TextDim}`. Для `BorderBrush`/`Fill` разделителей #2d4a5e оставить.
*Трудоёмкость:* M

**2. Disabled-состояние главных торговых кнопок: текст сливается с фоном (1,05-1,54:1)** — `CryptoAITerminal.TerminalUI/Styles/TradingDeskStyles.axaml:221`, `Views/TradingDeskView.axaml:341-342`
*Проблема:* Disabled меняет только `Background` презентера (#123a33 / #2a1540), а у вложенных TextBlock жёстко стоят `Foreground="#051018"` и `Foreground="#0b3e37"` — локальные значения, которые ничем не перебиваются. Когда ордер заблокирован риск-менеджером (`CanPlacePrimaryOrder`), надпись исчезает.
*Исправить:* добавить в disabled-сеттеры `TextElement.Foreground` (#6FBFAE / #B79AD8) и убрать хардкод `Foreground` из разметки кнопки.
*Трудоёмкость:* S

**3. Hover-стили кнопок мертвы: селектор без `/template/ ContentPresenter`** — `CryptoAITerminal.TerminalUI/Styles/AppStyles.axaml:388` (также 320, 352, 431, 978, 983; `Views/MarketsView.axaml:35, 50, 78, 96, 109`)
*Проблема:* В FluentTheme `:pointerover` задан сеттером прямо на `PART_ContentPresenter`, и `Background` на самом Button (через TemplateBinding) проигрывает. При наведении на 28 кнопок сайдбара и на элементы MarketsView появляется дефолтный серый Fluent. У `Button.TdDanger` (`TradingDeskStyles.axaml:225` — «SELL TOKEN», «CLOSE POSITION») и `Button.TdSide` (156, переключатель BUY/SELL) состояний нет вовсе — под курсором пропадает индикация выбранной стороны. Правильная форма в проекте есть: `MainWindow.axaml:1371`, `TradingDeskStyles.axaml:88`.
*Исправить:* перевести все состояния на `Selector="... :pointerover /template/ ContentPresenter#PART_ContentPresenter"`; добавить `TdDanger:pointerover|:disabled` и `TdSide.active.buy|.sell:pointerover`.
*Трудоёмкость:* M

**4. Токены палитры не используются: 3873 хардкод-цвета во Views, 0 обращений к ресурсам** — `CryptoAITerminal.TerminalUI/Styles/AppStyles.axaml:36`, `Styles/TradingDeskStyles.axaml:19`
*Проблема:* 23 токена объявлены (AppBg, Surface0-3, PanelStroke*, Text*, Accent*, Positive/Negative/Warning), но во всех 57 .axaml во Views ноль `{DynamicResource}`; Positive/Negative/Warning/Surface0/2/3/TextDim не читаются нигде. Следствие — пять неразличимых почти-чёрных фонов (#060d14 183, #07111a 91, #07101a 83, #050f14 49, #050c14 38; #07101A и #07111A отличаются на 1 в зелёном) и шесть оттенков границ одной роли (#0d1b27 183, #152233 158, #0a1520 65, #111d29 31, #111c28 17, #152535 16), плюс третья палитра в `ScannerView.axaml:28, 35, 42`.
*Исправить:* свести к трём поверхностям и двум границам, заменить самые частотные литералы (`#8FA3B8` 266, `#E8F4FF` 124, `#21E6C1` 178) на `{DynamicResource}`; для строковых цветов из VM возвращать ключ токена и резолвить конвертером; CI-проверка на нерост `Foreground="#`.
*Трудоёмкость:* L

**5. Пять разных пар «рост/падение»** — `CryptoAITerminal.TerminalUI/ViewModels/AllPositionsViewModel.cs:78`, `PnlDashboardViewModel.cs:68`, `DexTradingViewModel.cs:3187`, `CexMarketItemViewModel.cs:259 и 437`, `DashboardViewModel.cs:151`
*Проблема:* #21E6C1/#FF6B6B, #3DDC84/#FF5D73, #21e6c1/#ff5c7c, #21E6C1/#FF857B, #3DDC84/#FF6B6B — причём в одном классе `CexMarketItemViewModel` монета красится по-разному в списке и в пилюле. #21E6C1 одновременно означает «бренд/акцент» и «рост».
*Исправить:* `Theme/SemanticColors.cs` с `Positive #3DDC84` / `Negative #FF6B6B` / `Neutral` и `Sgn(decimal)`, заменить все литералы в контексте знака; #21E6C1 оставить только брендовым акцентом.
*Трудоёмкость:* M

**6. Тёмная тема не зафиксирована** — `CryptoAITerminal.TerminalUI/App.axaml:33`
*Проблема:* `RequestedThemeVariant` нет нигде (0 совпадений), FluentTheme идёт за системной темой Windows. Приложение жёстко тёмное (`Window { Background #060D14 }`), но 44 `ToolTip.Tip` (включая `PrimaryOrderBlockedReason` на кнопке ордера, `TradingDeskView.axaml:339`), 39 ComboBox, ToggleSwitch, Slider, ScrollBar возьмут светлые ресурсы.
*Исправить:* `<Application ... RequestedThemeVariant="Dark">`.
*Трудоёмкость:* S

**7. ToggleSwitch и Slider не стилизованы — системный акцент Windows** — `CryptoAITerminal.TerminalUI/Views/ArbView.axaml:22`
*Проблема:* Стиля `Selector="ToggleSwitch"` нет нигде, `SystemAccentColor` не переопределён — включённый тумблер рисуется акцентом Windows (по умолчанию синим). А тумблеры управляют деньгами: `AutoExecute` (ArbView), `AutoReinvest` (FundingArbView:56), `IsAutoMode` (MainWindow:1492). `Slider.TradeSlider` (`AppStyles.axaml:616`) задаёт только MinHeight.
*Исправить:* переопределить `SystemAccentColor`/`Dark1`/`Light1` на бирюзовую шкалу в `Styles.Resources` + явные стили для `ToggleSwitch:checked` и трека Slider (`SliderTrackValueFill`, а не Foreground).
*Трудоёмкость:* S

**8. Приложение рендерится двумя UI-шрифтами; подключённый Inter не регистрируется** — `CryptoAITerminal.TerminalUI/Styles/AppStyles.axaml:51`
*Проблема:* `FontDisplay` (Space Grotesk) проставлен на корне 17 View, а MainWindow, DashboardView и все виджеты наследуют `Segoe UI Variable` из стиля Window — при переходе Trading Desk → Scanner меняется гарнитура всего интерфейса. Моноширинный расползся на три: `{StaticResource FontMono}` (DM Mono, 381 раз) vs `TextBlock.Mono` → «Cascadia Code, Consolas» (1317) vs `TextBox.LogBox` → «Consolas, Cascadia Code» (545). Пакет `Avalonia.Fonts.Inter` (.csproj:22) подключён, а `.WithInterFont()` в `Program.cs:24-30` не вызывается.
*Исправить:* задать `FontFamily="{StaticResource FontDisplay}"` в стиле Window и убрать из корней View; `TextBlock.Mono`/`TextBox.LogBox` перевести на `FontMono`; либо вызвать `.WithInterFont()`, либо убрать пакет и «Inter» из фолбэков.
*Трудоёмкость:* S

**9. ScrollViewer внутри вертикального StackPanel в 5 виджетах — прокрутка не работает никогда** — `Views/Dashboard/Widgets/TapeWidget.axaml:28` (также `WhalesWidget:39`, `FundingWidget:43`, `ScannerWidget:53`, `PortfolioWidget:39`)
*Проблема:* StackPanel меряет детей бесконечной высотой, viewport = высоте контента, скроллбар не появляется, а список вылезает за ячейку (`WidgetGridPanel.cs:51` арранжит жёстко) и перекрывает соседей — ClipToBounds в WidgetHost не задан. Правильно сделано в `OrderBookWidget.axaml:7` и `TrackedCoinsWidget.axaml:8`.
*Исправить:* корневой `Grid RowDefinitions="Auto,Auto,*"` со ScrollViewer в звёздочной строке.
*Трудоёмкость:* M

**10. CellHeight=84 меньше хрома WidgetHost: однострочный виджет получает ~17px** — `CryptoAITerminal.TerminalUI/Controls/WidgetGridPanel.cs:12`
*Проблема:* 84−10=74px, из них Panel Padding 14+14 и рамка 2 = −30, шапка WidgetTitle + Margin ≈ −27 → ~17px на контент. Дефолтный OVERVIEW (`DashboardLayoutMath.cs:16`, rowSpan=1) рисует `PriceStatsWidget` высотой ≈77px — контент в 4,5 раза выше отведённого и вылезает поверх следующей строки.
*Исправить:* `CellHeight = 128` (или вычитать хром на уровне хоста через MinHeight) и пересчитать дефолтные RowSpan в `DashboardLayoutMath.DefaultLayout`.
*Трудоёмкость:* M

**11. Фиксированные px-колонки в виджетах шире самого виджета** — `Views/Dashboard/Widgets/FundingWidget.axaml:34`, `TapeWidget.axaml:17, 33`
*Проблема:* На 1366×768 виджет `DefaultColSpan=4` (`WidgetCatalogEntry.cs:23`) даёт ~328px полезной ширины, а фикс — 310+24 spacing (Funding) и 328 (Tape, `"38,80,50,*,*,70,90"`). Звёздочные колонки SYMBOL/PRICE/QTY схлопываются в 0.
*Исправить:* пропорциональные колонки (`1.4*,0.9*,0.9*,0.8*,0.9*`) + `MinWidth` и `TextTrimming="CharacterEllipsis"`.
*Трудоёмкость:* M

**12. PortfolioView: таблицы 610-692px в центральной колонке ~446px** — `CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml:542` (также 435, 180)
*Проблема:* Тело жёстко резервирует `ColumnDefinitions="280,*,380"`; на 1366px центру остаётся ~446px, а ребалансировка требует 692px фикса (`32,90,100,80,110,110,90,*,80`) — колонка REBALANCE ORDER схлопывается в 0, правые колонки уезжают под правую панель. Таблица ASSETS (610px фикса) ломается так же.
*Исправить:* пропорции с MinWidth либо горизонтальный скролл, как уже сделано в `BotsView.axaml:299-300`; боковые колонки сделать адаптивными (сворачивать правую ниже 1200px).
*Трудоёмкость:* L

**13. PageScroll даёт бесконечную высоту viewport-страницам** — `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml:470` (и 483-486 для PortfolioView)
*Проблема:* `MarketsView` спроектирован как экран фиксированной высоты (`Grid RowDefinitions="52,*"`, липкая шапка, `RowDefinitions="48,*"` у таблицы, собственные ScrollViewer в панелях на 222 и 513). Обёртка в PageScroll убивает виртуализацию ListBox, отключает внутренние скроллы и уводит липкие шапки вверх. То же с PortfolioView. AiSignalsView/SniperView не обёрнуты и работают корректно. (Для TradingDeskView снятие обёртки не поможет — там корень StackPanel.)
*Исправить:* убрать `<ScrollViewer Classes="PageScroll">` вокруг `views:MarketsView` и `views:PortfolioView`; PageScroll оставить только для страниц-«простыней».
*Трудоёмкость:* S

**14. `MinHeight="760"` на пяти desk-страницах при доступных ~714px** — `Views/SettingsView.axaml:36` (также `NewsView:31`, `AnalyticsView:31`, `RulesView:37`, `BotsView:62`)
*Проблема:* Страница всегда даёт внешний вертикальный скролл поверх собственных скроллеров, а закреплённый снизу футер (`SettingsView.axaml:110`) уезжает за кромку. На логических 1280×720 дефицит ~94px.
*Исправить:* убрать `MinHeight="760"` и `Height="{Binding $parent[ScrollViewer].Bounds.Height}"`, не оборачивать эти страницы в PageScroll; при желании — `MinHeight="460"`.
*Трудоёмкость:* M

**15. LiquidationView: Canvas 1200×460 центрируется и обрезается** — `CryptoAITerminal.TerminalUI/Views/LiquidationView.axaml:119`
*Проблема:* `ClipToBounds="True"` при доступных ~970px срезает ~90px с каждой стороны. Бары кодируют величину длиной (`LiquidationHeatmapViewModel.cs:499`, `RW = 1200`) и прибиты к `Canvas.Left = 0`, поэтому у всех одинаково срезается начало: соотношение 2:1 превращается в 2,8:1.
*Исправить:* считать `RW` из фактической ширины контейнера (пробросить `Bounds.Width` в VM) либо обернуть в `ScrollViewer HorizontalScrollBarVisibility="Auto"` (Viewbox нежелателен — см. комментарий на 112-114 про баг stretch в Avalonia 12).
*Трудоёмкость:* M

**16. Панель EXECUTION GUARD в тикете — декорация** — `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml:308`
*Проблема:* Четыре индикатора (309-333) имеют жёстко зашитый `Fill="#3ddc84"` без единого биндинга и горят «ок» всегда, даже когда `GuardPassLabel` рядом показывает BLOCK.
*Исправить:* привязать к реальным проверкам (Liquidity → `SpreadPercent`, Slippage → `SlippageTolerancePercent`, Exposure → `CurrentOpenExposureUsdt` vs `GlobalMaxOpenExposureUsdt`, Risk Limits → `TryApproveUsdRisk`) с красным при провале, либо удалить блок.
*Трудоёмкость:* M

**17. Восемь виджетов дашборда без пустого состояния** — `Views/Dashboard/Widgets/WhalesWidget.axaml:39` (также Tape, Funding, Scanner, Gas, OrderBook, Portfolio, TrackedCoins, LiqHeatmap)
*Проблема:* Из 17 виджетов пустоту объясняют только три (`PositionsWidget:32-35`, `AnalyticsWidget:71`, `NewsWidget:39`). Остальные показывают пустой прямоугольник — не отличить «трекер не запущен» от «нет ключа API» от «сеть отвалилась».
*Исправить:* переиспользуемый стиль `TextBlock.WidgetEmpty` + `IsVisible="{Binding !Collection.Count}"` в каждом виджете.
*Трудоёмкость:* M

**18. WhaleTrackerView: `HorizontalAlignment="Right"` внутри горизонтального StackPanel (no-op) + нет пустого состояния** — `CryptoAITerminal.TerminalUI/Views/WhaleTrackerView.axaml:41` (StackPanel — 37, ItemsControl — 80)
*Проблема:* Кнопка Clear прилипает к длинному тексту про API-ключи в середине панели, при узком окне строка вылезает за панель. Список «Live Whale Alerts» (79-128) не имеет плейсхолдера — после Start Tracking пользователь видит пустоту.
*Исправить:* `Grid ColumnDefinitions="Auto,*,Auto,Auto"` вместо StackPanel; плейсхолдер с `IsVisible="{Binding !WhaleTrackerVM.RecentAlerts.Count}"`.
*Трудоёмкость:* S

**19. PortfolioView: числа в таблице ребалансировки выровнены влево** — `CryptoAITerminal.TerminalUI/Views/PortfolioView.axaml:562-564` (заголовки — 544-546)
*Проблема:* BALANCE/PRICE/VALUE без `TextAlignment="Right"` — разряды не выстраиваются в столбец. Соседняя таблица ASSETS (463-467) и весь остальной терминал (`MarketsView.axaml:464-486`, `TradingDeskView.axaml:554-560`) правят числа вправо.
*Исправить:* добавить `TextAlignment="Right"` ячейкам и заголовкам.
*Трудоёмкость:* S

**20. Смешение языков: русские строки в англоязычном интерфейсе** — `Views/MainWindow.axaml:1109` (также 142, 226, 1117, 1144, 1145, 1170, 1173), `Views/Dashboard/WidgetHost.axaml:19-28`, `ViewModels/CopyTradingViewModel.cs:201, 230`
*Проблема:* Оболочка англоязычная, но центр уведомлений, тултипы, баннер обновления и тултипы ресайза виджетов зашиты по-русски; в `CopyTradingViewModel` языки чередуются в одном классе (195 EN, 201 RU, 230 RU, 235 EN). Переключателем не лечится: `UiLocalizationService.Translate` (864) при English возвращает вход как есть, словарь односторонний.
*Исправить:* перевести все зашитые русские строки на английский, перевод оставить сервису; CI-проверка на кириллицу в *.axaml и строковых литералах ViewModels.
*Трудоёмкость:* M

**21. Результат ордера и ошибки закрытия позиции не показываются тостом** — `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs:4138`
*Проблема:* Все результаты ручной торговли идут только в `AddLog` (лента обрезается до 8 записей). Тостовая инфраструктура есть и используется другими десками (335, 668, 676, 683, 694, 702), но путь ордера ею не пользуется: ордер по хоткею с другой страницы не даёт вообще никакой обратной связи. Провал закрытия на экране позиций уходит в `AllPositionsViewModel.cs:331 → AddLog` ленты трейдинг-деска, а `StatusLabel` (виден в `PositionsView.axaml:48`) не обновляется.
*Исправить:* `ShowToast` в `ExecuteBuyMarket`/`ExecuteSellMarket`/`ExecuteClosePosition` и в `SubscribeCommandErrors`; в `ClosePartialAsync` обновлять локальный `StatusLabel`.
*Трудоёмкость:* S

**22. Ни одного `AutomationProperties.Name` во всём UI** — `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml:141`
*Проблема:* Grep по `AutomationProperties` — 0 совпадений; `TabIndex` — только два `SelectedIndex` у TabControl. Кнопки с одним глифом (колокольчик, сворачивание сайдбара 224-228, зум графика `TradingDeskView.axaml:399, 401`, `✕` модалок, `✓/✕` в `AgentActionTrayView.axaml:13-15`) безымянны для скринридера, подсказка живёт только в ToolTip.
*Исправить:* проставить `AutomationProperties.Name` на всех иконочных кнопках; задать явный `TabIndex` в тикете: сторона → тип → цена → размер → TP → SL → PLACE ORDER.
*Трудоёмкость:* M

---

## ⚡ Производительность

**1. Каждый тик цены перестраивает 4 UI-коллекции и поднимает ~120 PropertyChanged** — `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs:6875`
*Проблема:* Сеттер `CurrentMarketData` (1426-1438) на каждый тик зовёт `RaiseTradingStateChanged()` (76 нотификаций + `RaiseCexActionStateChanged` ещё 23) и в хвосте `RefreshPositionRows()` (`PositionRows.Clear()`, 9498), `RefreshSignalRows()` (9523), `RefreshAiSignalStudioContext()` (8754), `UpdateTradeIdea()`. Четыре события Reset в секунду на 11 символах → пересоздание контейнеров строк и мигание таблиц.
*Исправить:* обновлять строки in-place (образец — `CexMarketItemViewModel.UpdateLevels`); разделить `RaiseTradingStateChanged` на «ценовую» и «структурную» части; коалесить тики (400 мс, как `_marketExplorerRefreshTimer`).
*Трудоёмкость:* M

**2. `RebuildChart`: Clear()+Add на ObservableCollection при каждом тике каждого рынка** — `CryptoAITerminal.TerminalUI/ViewModels/CexMarketItemViewModel.cs:704`
*Проблема:* `UpdateMarketData` (597) безусловно вызывает `RebuildChart`, который делает `ChartPoints.Clear()` и до 720 `Add` (`MaxHistoryPoints = 720`). Подписчик `CexPriceChart.OnPointsCollectionChanged` (`Controls/CexPriceChart.cs:207-210`) на каждое событие зовёт `InvalidateVisual()`; коллекция привязана в `MarketsView.axaml:635`, `PriceChartWidget.axaml:41`, `TrackedCoinsWidget.axaml:51`. Вызывается для всех 11 рынков (`MainWindowViewModel.cs:1230`), а не только выбранного. Рядом `_priceHistory.RemoveAt(0)` (594) — сдвиг списка на 720 элементов.
*Исправить:* инкрементальное обновление (менять элемент по индексу, дописывать хвост, срезать лишнее); кольцевой буфер вместо `List` + `RemoveAt(0)`; `RebuildChart` только для видимых графиков.
*Трудоёмкость:* M

**3. Свечной график пересчитывает MA/Bollinger/RSI/HA по всем свечам на каждый кадр** — `CryptoAITerminal.TerminalUI/Controls/CexCandlestickChart.cs:309`
*Проблема:* `Render` зовёт `BuildSeries()` (920) без кэша, хотя соседний `EnsureCandlesRefreshedForRender()` (606) кэширован. `BuildSeries` не смотрит на `ShowMa20/ShowBollinger/ShowRsi` и считает всё: `allCloses`, два SMA, Bollinger(20) вложенным циклом (960-980), RSI Wilder, Heikin-Ashi — 11 массивов `double[total]` + 11 срезов. `OnPointerMoved` безусловно вызывает `InvalidateVisual()` (457).
*Исправить:* кэшировать `BuildSeries` по ключу `(Candles, _allCandles.Count, _visibleStartIndex, _visibleCount)`; считать только включённые серии; Bollinger — скользящими суммами; перекрестие вынести в отдельный лёгкий слой; инвалидировать только при реальном изменении состояния указателя.
*Трудоёмкость:* M

**4. `Color.Parse` и `new Pen/SolidColorBrush` внутри цикла отрисовки каждой свечи** — `CryptoAITerminal.TerminalUI/Controls/CexCandlestickChart.cs:759`
*Проблема:* На каждую свечу — разбор строки цвета и две аллокации; тот же паттерн в `DrawBackdrop` (688-691), `DrawGrid` (696), `DrawLineArea` (780), `DrawIndicatorOverlays` (820-831), `DrawRsiPanel` (870-915), `DrawCrosshair` (1180-1212). Все цвета статичны.
*Исправить:* `static readonly IBrush BullBrush = new SolidColorBrush(Color.Parse("#21E6C1"));` и `static readonly Pen BullPen = new(BullBrush, 1);` — выбирать одну из готовых пар.
*Трудоёмкость:* S

**5. `GetVisibleHistory()` — LINQ-аллокация в геттерах, вызываемых каскадом** — `CryptoAITerminal.TerminalUI/ViewModels/CexMarketItemViewModel.cs:248`
*Проблема:* `_priceHistory.Where(...).ToList()` (792-801) по буферу до 720 семплов на каждый вызов, без кэша. `RangeLabel` → 4 фильтрации, `RangePercent` → 3, `ActivityScore`/`ActivityScoreLabel` → по 2. `RaiseDerivedState()` (740) поднимает их все разом на каждом тике, а `GetVisibleMarkets()` (`MainWindowViewModel.cs:7605-7617`) ещё и сортирует по `ActivityScore`/`ChangePercent`/`SpreadPercent`.
*Исправить:* кэшировать срез (`_visibleCache ??= BuildVisibleHistory()`, инвалидация в `UpdateMarketData`/`ApplyTimeframe`); `SessionHigh/Low/ChangePercent` считать одним проходом в поля.
*Трудоёмкость:* M

**6. DexTradingViewModel: сортировка всей 8-дневной истории по каждому токену каждые 3 с в UI-потоке** — `CryptoAITerminal.TerminalUI/ViewModels/DexTradingViewModel.cs:2114`
*Проблема:* `_refreshTimer` (1626-1631) стартует в конструкторе и живёт до `Dispose` (1739) независимо от того, открыт ли DEX-раздел (гейтинг в `SelectMainTab` есть только для tape/arb/funding/liquidation). Цикл `RecordPriceSample` (1948-1951) идёт после `await loader()` без `ConfigureAwait(false)`, т.е. на UI-потоке, и для каждого из 100-300 токенов делает `RemoveAll` + полный `Sort` уже отсортированного списка; плюс дисковый снапшот каждые 20 сэмплов (2118-2124).
*Исправить:* убрать `Sort` (сэмплы монотонны); `RemoveRange(0, k)` или кольцевой буфер с лимитом точек; вынести цикл с UI-потока; останавливать таймер, когда раздел не виден.
*Трудоёмкость:* M

**7. Таймеры торгового деска опрашивают 4 биржи по REST всегда** — `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.TradingDesk.cs:107`
*Проблема:* `_venueQuoteTimer` (5 с, `Task.WhenAll` по Binance/Bybit/OKX/KuCoin, `GetOrderBookAsync(depth: 5)`), `_tapeTimer` (4 с) и `_orderBookTimer` (6 с, `depth: 100`, `MainWindowViewModel.cs:5077`) стартуют из конструктора и стопаются только в `Dispose` — ~62 запроса/мин даже когда пользователь в Settings. Рядом в `SelectMainTab` (5480-5491) уже есть гейтинг «heavy per-page timers» для других разделов.
*Исправить:* `TradingDeskActivate()/Deactivate()` рядом со строками 5488-5490; при возврате — один немедленный refresh; неактивные венью опрашивать раз в 30 с.
*Трудоёмкость:* S

**8. BotsDesk тикает каждую секунду и каждые 15 с синхронно читает pnl-history.json в UI-потоке** — `ViewModels/BotsDesk/BotsDeskViewModel.Live.cs:162`
*Проблема:* `_liveTimer` (1 с) стартует в `Attach()` (85-87) при старте приложения; проверка `IsBotsSectionVisible` есть только внутри `RefreshPositionsIfStale()` (133), остальные семь вызовов `Tick()` (107-124) идут всегда. `ReloadPnl` → `PnlDashboardService.Load()` → `AtomicJsonFile.Read` → синхронный `File.ReadAllText` + `Deserialize` в UI-потоке, затем `GetAll()` копирует список и `ComputeByBot` проходит по нему дважды.
*Исправить:* гейтить таймер по видимости; `Load()` в `Task.Run` с возвратом через `Dispatcher.UIThread.Post`; лучше — подписаться на `OnTradeRecorded` и пересчитывать только при записи сделки.
*Трудоёмкость:* M

**9. DashboardViewModel каждые 5 с пересоздаёт карточки и сортирует всю кривую эквити** — `CryptoAITerminal.TerminalUI/ViewModels/DashboardViewModel.cs:84`
*Проблема:* Таймер стартует в конструкторе, стопается только в `Dispose` (178). `RefreshBotCards()` начинается с `BotCards.Clear()` (116); `RefreshPnlSummary()` (145-167) зовёт `GetAll()`, `ComputeMetrics` и `ComputeEquityCurve`, который делает `OrderBy(...).ToList()` и строит новый список на N+1 элементов (`PnlDashboardService.cs:209-226`) — ради единственного `equityPoints[^1].Equity`.
*Исправить:* обновлять карточки in-place; заменить `ComputeEquityCurve` на `records.Sum(r => r.PnlUsd)`; гейтить таймер по видимости / подписаться на `OnTradeRecorded`.
*Трудоёмкость:* S

**10. MarketFeedDesk.Recompute инвалидирует ВСЕ привязки каждые 220 мс** — `ViewModels/MarketFeedDesk/MarketFeedDeskViewModel.cs:479`
*Проблема:* `this.RaisePropertyChanged(string.Empty)` = «изменилось всё» для сотен свойств VM; перед этим два `.ToList()` (461, 464) и десять Rebuild-методов (467-476), из которых `RebuildViewTabs` начинается с `ViewTabs.Clear()` (537). Триггер — `_tapeVm.Rows.CollectionChanged` (94). `ComputeTapeStats` (482-506) делает шесть отдельных LINQ-проходов по одному списку.
*Исправить:* убрать `RaisePropertyChanged(string.Empty)`; вызывать только Rebuild активной вкладки; `ComputeTapeStats` — одним `foreach`; ранний выход в `ScheduleRebuild()` при `IsNewsSectionVisible != true`.
*Трудоёмкость:* M

**11. CandleRepository пишет свечи по одной строке за round-trip** — `CryptoAITerminal.Server.Data/CandleRepository.cs:35`
*Проблема:* `foreach (var r in rows) await conn.ExecuteAsync(...)` внутри транзакции. `CandlePollingService` (37-39) берёт 60 свечей × 50 токенов последовательно каждые 5 с → 50 HTTP + 3000 INSERT + 100 доп. запросов за тик; очередь `tracked_tokens` отстаёт тем сильнее, чем больше пользователей.
*Исправить:* один batched INSERT через `unnest` (приём уже применён в `ApiReadRepository.cs:74-76`) или `NpgsqlBinaryImporter` + MERGE; `Parallel.ForEachAsync` с `MaxDegreeOfParallelism = 4..8` по токенам.
*Трудоёмкость:* M

**12. BalanceRefresher: 48 последовательных приватных REST-вызовов в минуту** — `CryptoAITerminal.TerminalUI/Services/BalanceRefresher.cs:65`
*Проблема:* 8 таргетов (`MainWindowViewModel.cs:921-931`) × 6 активов (20) строго по одному, каждые 60 с. Реализации тянут весь аккаунт ради одного актива (`BinanceFuturesGateway.cs:329-345`). Без ключей `EnsurePrivateApiConfigured()` бросает на каждом из 48 вызовов, и все они гасятся `catch { continue; }`.
*Исправить:* `GetBalancesAsync(IEnumerable<string> assets)` в `IExchangeGateway` — 8 запросов вместо 48; таргеты через `Task.WhenAll`; пропускать гейтвеи без `HasPrivateApiCredentials`.
*Трудоёмкость:* M

**13. `LogMessages` — неограниченная конкатенация строки, привязанная к TextBox** — `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs:6573`
*Проблема:* `RecentActivityFeed` обрезается до 8 (6576-6579), а строка не обрезается нигде (три вхождения во всём проекте: объявление 2029, конкатенация 6573, биндинг `MainWindow.axaml:854`). N записей → O(N²) аллокаций; только в этом классе 131 вызов `AddLog`, включая таймерные пути (6178, 6249) каждые 6 с при недоступной сети.
*Исправить:* `ObservableCollection<string>` с лимитом 500 и виртуализованный список, либо `StringBuilder` с обрезкой головы и обновлением свойства не чаще раза в секунду.
*Трудоёмкость:* S

**14. ScrollViewer вокруг ListBox отключает виртуализацию** — `Views/Dashboard/Widgets/TrackedCoinsWidget.axaml:29` (также `BacktestView.axaml:435-436`, `MainWindow.axaml:470-472` → `MarketsView.axaml:420`)
*Проблема:* `ScrollViewer.PageScroll` (`AppStyles.axaml:853`) меряет ребёнка бесконечной высотой, `VirtualizingStackPanel` реализует все элементы. В каждой строке — живой `CexPriceChart` с подпиской на `CollectionChanged`; список растёт вместе с добавленными пользователем парами.
*Исправить:* убрать внешний ScrollViewer там, где внутри есть собственный прокручиваемый список.
*Трудоёмкость:* M

**15. Стакан: 200 строк в невиртуализированном ItemsControl под MaxHeight=190** — `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml:771` (зеркально 803)
*Проблема:* `ItemsControl` по умолчанию использует `StackPanel`; `OrderBookDepth = 100` (`CexMarketItemViewModel.cs:627`) на обе стороны — ~1400 контролов при видимых ~12, которые ещё и обходит сканер локализации каждые 2 с.
*Исправить:* `<ItemsControl.ItemsPanel><ItemsPanelTemplate><VirtualizingStackPanel/></ItemsPanelTemplate></ItemsControl.ItemsPanel>` либо отдавать готовые `TopBids`/`TopAsks` (569-572).
*Трудоёмкость:* S

**16. Заметка в журнале переписывает весь JSON на каждое нажатие клавиши** — `CryptoAITerminal.TerminalUI/Views/JournalView.axaml:147`
*Проблема:* В Avalonia дефолт `UpdateSourceTrigger` — PropertyChanged; сеттер `Notes` (`TradeJournalViewModel.cs:32-40`) на каждый символ зовёт `_svc.Save()` → `AtomicJsonFile.Write` (полная сериализация + temp-файл + move, синхронно в UI-потоке). Заметка из 50 символов = 50 полных записей журнала. То же у сеттера `Tag` (43-52).
*Исправить:* `Text="{Binding Notes, UpdateSourceTrigger=LostFocus}"` и/или debounce 500-1000 мс вместо сохранения из сеттера.
*Трудоёмкость:* S

**17. Композитные эндпоинты сеют записи в кэш, минуя вытеснение** — `CryptoAITerminal.Server.Api/CompositeEndpoints.cs:115`
*Проблема:* Вытеснение живёт только в `SharedResponseCache.GetOrCreateAsync` (`MaybeEvict()`, 109); `Seed` (147-153) и `TryGetFresh` ничего не чистят и не инкрементируют `_requests`. `/api/dex/tokens?ids=` принимает произвольные пары chain:token, и для ненайденных сеется заглушка (114) — до 200 новых ключей за запрос.
*Исправить:* вызывать `MaybeEvict`/публичный `Sweep()` в конце `ServeComposedAsync` и внутри `Seed`, инкрементировать `_requests`; не сеять заглушки для неизвестных токенов.
*Трудоёмкость:* S

---

## 🧹 Код, архитектура, тесты, сборка

**1. MainWindowViewModel — класс-бог: 10 292 строки, конструктор на 1 226 строк** — `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs:175`
*Проблема:* Конструктор (175-1400) создаёт 106 объектов через `new`, лезет в `CredentialsService.Load()` (192), поднимает 8 гейтвеев (250-259) и запускает таймеры — класс невозможно создать в тесте. 962 строки с `public`, 480 приватных полей. Partial-разбиение косметическое: `.TradingDesk.cs` 493, `.AgentActions.cs` 206, `.PortfolioDesk.cs` 15.
*Исправить:* вынести по границам, уже размеченным комментариями: `.Credentials.cs` (191-226, 2187-2500), `.Gateways.cs` (переиспользовать `Executor/GatewayFactory`), `Services/SoftwareWorkingOrderEngine` (6257-6600 — самодостаточный и сразу тестируемый), `.RiskDesk.cs` (2994-3100); сетевые вызовы убрать из конструктора в `InitializeAsync`.
*Трудоёмкость:* L

**2. Нет DI-контейнера, при этом пакет подключён и не используется** — `CryptoAITerminal.TerminalUI/App.axaml.cs:36`, `CryptoAITerminal.TerminalUI.csproj:30`
*Проблема:* Grep по TerminalUI: `ServiceCollection`, `IServiceProvider`, `AddSingleton`, `Splat.Locator` — ноль. Граф собирается тремя способами: `new` в конструкторе, статические синглтоны (`GatewayHealthService.cs:15`, `UiLocalizationService.cs:845`) и полностью статический `CredentialsService.cs:18`. `Microsoft.Extensions.DependencyInjection 10.0.5` подключён вразрез с соседним комментарием о пиннинге на 8.0.x.
*Исправить:* один `ServiceCollection` в `Program.cs` с `ICredentialsStore`, `ICexGatewayFactory`, `IGatewayHealth`, `IUiLocalization`; резолвить `MainWindow` из провайдера; синглтоны оставить временным фасадом. Либо удалить неиспользуемый пакет.
*Трудоёмкость:* L

**3. Gateway.Base — пустая заглушка; мапа таймфреймов скопирована 5 раз** — `CryptoAITerminal.Gateway.Base/Class1.cs:1`
*Проблема:* Проект есть в `.slnx:13`, содержит только шаблонный `Class1.cs`, и ни один csproj на него не ссылается. Общий код разъехался: `BybitGateway.cs:225`, `OKXGateway.cs:201`, `KucoinGateway.cs:192`, `KucoinFuturesGateway.cs:287`, инлайн у Binance (`BinanceGateway.cs:138, 174`, `BinanceFuturesGateway.cs:128`) — с молчаливым дефолтом (`OneHour` у большинства, `OneMinute` у Binance). Ровно этот класс дефекта — БАГ-30. Плюс `ConcurrentDictionary<string,string> _orderSymbols` продублирован в 7 гейтвеях, и записи удаляются только при отмене — исполненные ордера копятся навсегда.
*Исправить:* `Gateway.Base/TimeframeParser.cs` с `TimeSpan? Parse(...)`, тонкий маппинг в SDK-энум у каждого гейтвея и **бросок** при неизвестном значении; `Gateway.Base/OrderSymbolCache.cs` с лимитом/TTL и `Forget(orderId)`; удалить `Class1.cs`; тест на все 14 строк и регистр.
*Трудоёмкость:* M

**4. `IsPositionSideMismatch` скопирован 5 раз в money-путях** — `Services/TradingBot.cs:390`, `Services/GridBot.cs:42`, `Services/FundingArbitrageService.cs:64`, `Services/AiTraderAgentService.cs:763`, `ViewModels/MainWindowViewModel.cs:4866`
*Проблема:* От функции зависят 7 точек `catch (Exception ex) when (IsPositionSideMismatch(ex))` (TradingBot 180/272, GridBot 155/199, FundingArb 81, AiTrader 637, MainWindowVM 4953) — весь fallback one-way ↔ hedge (БАГ-60, БАГ-61). Детект по подстрокам текста ошибки: смена формулировки или новый код (`-4061`) требует правки в пяти файлах, пропущенная копия = молчаливый отказ одного бота.
*Исправить:* `CryptoAITerminal.Core/Trading/ExchangeErrors.cs` с массивом маркеров и одним методом; заменить пять копий; вынести и повторяющийся флип `_isHedgeMode` + retry; тест с реальными текстами ошибок четырёх бирж.
*Трудоёмкость:* S

**5. 2 702 строки money-кода и все 8 классов гейтвеев без единого теста** — `CryptoAITerminal.TerminalUI/Services/TradingBot.cs:212`
*Проблема:* 768/768 тестов зелёные, но grep по `CryptoAITerminal.Core.Tests` не находит упоминаний `TradingBot` (405 строк, БАГ-01/04/05/06/62), `FundingArbitrageService` (484, БАГ-03/42/61), `StatArbService` (348), `PnlDashboardService` (545), `DcaBot` (237), `CredentialsService` (624), `MarketOrderRouter` (59). Ни один гейтвей не покрыт — а именно там жили БАГ-30/31/32/49-54.
*Исправить:* начать с чистых функций: конверсия `sz` (OKX `ctVal`) и multiplier (KuCoin futures) статическими методами + таблица значений; `MarketOrderRouter` тестируется прямо сейчас через фейк `IExchangeGateway`; `CredentialsService` параметризовать по `FilePath`; вынести из `TradingBot` расчёт P&L и решение о входе в `TradingBotCore`.
*Трудоёмкость:* L

**6. CI собирает только замыкание Core.Tests; решение .slnx не собирается пинованным SDK** — `.github/workflows/ci.yml:16`
*Проблема:* Единственный шаг — `dotnet test CryptoAITerminal.Core.Tests`. Вне замыкания: **Server.Api, CandleWorker, AdminCli, WebApi, Gateway.Base** — при этом compose собирает Server.Api (`docker-compose.yml:54`) и CandleWorker (95). `global.json` пинует 8.0.422, а формат `.slnx` требует SDK ≥ 9.0.200: `dotnet build CryptoAITerminal.slnx -c Release` → `error MSB4068`. `CryptoAITerminal.Backend` вообще отсутствует в `.slnx`.
*Исправить:* поднять `global.json` до 9.0.200 и вернуть сборку решения одной строкой; job `docker compose build server-api candle-worker executor`; добавить Backend в `.slnx`; прогонять `dotnet publish TerminalUI -c Release -r win-x64` — единственную реально отгружаемую конфигурацию.
*Трудоёмкость:* S

**7. Нет Directory.Build.props и TreatWarningsAsErrors; 67 предупреждений при пересборке** — `CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj:1`
*Проблема:* Во всех 22 csproj нет `TreatWarningsAsErrors`/`AnalysisLevel`; нет `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`. Замер: 57 × AVLN5001 (`TextBox.Watermark` устарел, 57 вхождений в 11 файлах), 5 × CS0108 (перекрытие `ReactiveObject.Changed` — `SettingsModels.cs:20, 110, 260`, `BotsDesk/BotsDeskItems.cs:267`, `AIEngine/AiFailure.cs:16`), 3 nullable (`DexTradingViewModel.cs:205, 212`, `Gateway.DEX/WalletCopyTradeMonitor.cs:107`), 1 × CS0219 (`Gateway.DEX/LiquidityPoolCalculator.cs:113`), 1 × CS1998. Три реальных nullable-предупреждения в DEX-путях тонут в шуме.
*Исправить:* массовая замена `Watermark=` → `PlaceholderText=`; починить nullable и CS0108 (переименовать в `OnValueChanged`); `Directory.Build.props` + `Directory.Packages.props` и после зачистки `TreatWarningsAsErrors`.
*Трудоёмкость:* M

**8. NEST + Elasticsearch.Net (6 МБ) уезжают в инсталлятор неиспользуемыми** — `CryptoAITerminal.Gateway.Binance/CryptoAITerminal.Gateway.Binance.csproj:12`
*Проблема:* Ни одного `using Nest`/`ElasticClient` в исходниках, при этом в `bin/Release/net8.0-windows/win-x64/` лежат `Nest.dll` (5 051 392 байт) и `Elasticsearch.Net.dll` (1 007 104) — вес каждого Velopack-обновления. Там же неиспользуемый `Microsoft.Extensions.Options 10.0.5` (строка 11) в net8.0-проекте.
*Исправить:* удалить NEST и Options из Gateway.Binance, DI-пакет из TerminalUI (или понизить до 8.0.x); заодно убрать мусорные каталоги `Users090AppDataLocalTempp0build*` в 8 проектах и добавить их в `.gitignore`.
*Трудоёмкость:* S

**9. Мёртвый код: 110-строчный бэктест в никуда, `_router`, недиспоуженная подписка** — `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs:7375`
*Проблема:* `_quickBacktestSnapshot` (153) присваивается один раз (7377) и не читается никогда, а `BuildQuickBacktestSnapshot()` (7392-7501) — симуляция MA(9/21) по всем свечам — крутится на UI-потоке при каждом обновлении графика (вызов из 6150). `BacktestStatusLabel` и др. (3098+) читают `BacktestVM`. Поле `_router` (62, 245) не используется нигде и зафиксировало Binance-гейтвей. `_marketDataSubscription2` (1107) не диспоузится в `Dispose()`.
*Исправить:* удалить метод, поле и присваивание (или привязать метки к снапшоту и увести расчёт в `Task.Run`); удалить `_router`; добавить `_marketDataSubscription2?.Dispose();`.
*Трудоёмкость:* S

**10. Десять View дублируют один ControlTemplate кнопки** — `Views/PortfolioView.axaml:12` (также SettingsView:13, BotsView:16, AnalyticsView:15, RulesView:15, NewsView:15, AnalyticsModals:7, BotsModals:10, MarketFeedModals:7, RulesModals:9)
*Проблема:* Копии разошлись: hover `Opacity 0.82` (PortfolioView:36) против `0.86` (остальные), disabled `0.45` против `0.4` (`AppStyles.axaml:401`). Подсветка приглушением на почти чёрном фоне читается как «отключено» и конфликтует с disabled-состоянием.
*Исправить:* один `Style Selector="Button.Flat"` в AppStyles с именованным `PART_ContentPresenter`, удалить 10 локальных копий; hover — осветление фона, а не Opacity.
*Трудоёмкость:* M

**11. 49 из 101 класса стилей мертвы, включая всю систему поверхностей Surface0-3** — `CryptoAITerminal.TerminalUI/Styles/AppStyles.axaml:58`
*Проблема:* Не используются Surface0-3, SurfaceChrome, весь AI-блок (AIPanelCard, AiStudioStage, AiPromptCard, AiFactCard, AiMessage*, AiThumbCard, AiPromptChip, ConfidenceRing), весь ордербук (OrderBookRowButton/BidRow/AskRow/SideCard), TopNavButton, StatusPill, PrimaryTradeButton, TradeSideButton, WarningCard, ToastCard, InfoBand, InnerCard, MicroCopy, SectionMetric. Комментарий на 58-65 описывает «5 семантических уровней», которых в приложении нет — новый разработчик получает ложную картину и продолжает писать хардкод.
*Исправить:* либо удалить 49 мёртвых классов вместе с комментарием, либо принять Surface0-3 как канон и перевести на них `Panel` (50) и `SurfaceCard` (44), удалив алиасы.
*Трудоёмкость:* M

**12. `GridBotViewModel.StopSync` — `ConfigureAwait(false)` без `await`** — `CryptoAITerminal.TerminalUI/ViewModels/GridBotViewModel.cs:301`
*Проблема:* `public void StopSync() => StopAsync().ConfigureAwait(false);` — задача уходит в фон, исключения не наблюдаются. Метод нигде не вызывается, то есть это ловушка: подключивший его к выходу получит грид с живой сеткой на бирже (`StopAsync` ждёт `GridBot.StopAsync` → `CancelAllOrdersAsync`). Паттерн БАГ-04 исправлен в `Services/TradingBot.cs`, здесь уцелел.
*Исправить:* удалить метод либо сделать честным (`StopAsync().Wait(TimeSpan.FromSeconds(5))` в try/catch, по образцу `MainWindow.axaml.cs:96`) и вызвать из `Dispose()`.
*Трудоёмкость:* S

**13. Backend/Program.cs пересылает тело клиента дословно в AI-провайдера** — `CryptoAITerminal.Backend/Program.cs:68`
*Проблема:* Тело читается без ограничения размера и уходит в `ForwardAnthropicAsync` с серверным ключом — клиент выбирает model, max_tokens и объём контекста, платит владелец. В Server.Api это закрыто `AiRequestPolicy` + `AiBudget`. Проект не входит в `.slnx`, не собирается в compose и не имеет Dockerfile — то есть код вытеснен, но остаётся в дереве.
*Исправить:* удалить проект (зафиксировав, что он вытеснен Server.Api) либо применить тот же гейт — `AiRequestPolicy.Apply(...)` + `CountUsage` в общий `AiBudget` (класс лежит в Server.Common, на который Backend уже ссылается).
*Трудоёмкость:* S

**14. AiRequestPolicy ловит только JsonException — неверный тип поля даёт 500** — `CryptoAITerminal.Server.Common/AiRequestPolicy.cs:100`
*Проблема:* `try/catch(JsonException)` охватывает только `JsonNode.Parse`, а `obj["model"]?.GetValue<string>()` (100) и `obj[tokenField]?.GetValue<int?>()` (118) бросают `InvalidOperationException`. Тело `{"model": 1}` вылетает из `Apply`; `ForwardAiAsync` (`Server.Api/Program.cs:273`) вызов не оборачивает, `UseExceptionHandler` в приложении нет — 500 вместо 400 на входном гейте.
*Исправить:* `obj["model"] is JsonValue mv && mv.TryGetValue<string>(out var model)` (аналогично для max_tokens) с внятным `AiPolicyResult(false, ...)`; расширить catch вокруг всего тела `Apply`; добавить `app.UseExceptionHandler`.
*Трудоёмкость:* S

---

## 🎨 Дополнение к визуалу (проверено отдельно, вне 10 ревьюеров)

**A1. Окно жёстко полноэкранное и без кнопок управления** — `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml:15-16`, `Views/MainWindow.axaml.cs:269`
*Проблема:* `WindowDecorations="None"` + `WindowState="FullScreen"`, плюс `ConfigureFullscreenWindow()` форсит `WindowState.FullScreen` в коде. Своих кнопок свернуть/развернуть/закрыть в topbar нет (в `MainWindow.axaml` только `CloseWhatsNewCommand` и закрытие командной палитры). Терминал нельзя положить на половину экрана рядом с TradingView, нельзя свернуть, нельзя вынести на второй монитор — для торгового софта, который держат открытым весь день рядом с другими окнами, это блокирующее ограничение.
*Исправить:* `WindowState="Maximized"` по умолчанию, оставить `ExtendClientAreaToDecorationsHint`, добавить свой chrome с тремя кнопками (`WindowState = Minimized / Maximized↔Normal / Close`) и `BeginMoveDrag` на topbar; полноэкранный режим — по F11, состояние сохранять в настройках.
*Трудоёмкость:* M

**A2. Непропускаемый сплэш 3,3 секунды на каждом запуске** — `CryptoAITerminal.TerminalUI/Views/MainWindow.axaml.cs:286, 299`
*Проблема:* `await Task.Delay(TimeSpan.FromMilliseconds(2600))` + `await Task.Delay(TimeSpan.FromMilliseconds(700))` — фиксированные задержки, не привязанные к реальной готовности данных. Ни клавиши, ни клика для пропуска нет. Каждый перезапуск во время отладки или после падения — минус 3,3 с.
*Исправить:* завершать сплэш по `Task.WhenAll(инициализация)` с `MinimumDisplay = 600 мс`, вешать `Escape`/клик на немедленное завершение, запоминать «показывать заставку» флагом в настройках.
*Трудоёмкость:* S

**A3. Нет типографической шкалы: доминирует 9px, 24 уникальных размера** — `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml:730`
*Проблема:* Распределение `FontSize` по Views: 9px — 620 раз, 11px — 564, 10px — 512, 12px — 217, 8px — 179, 7px — 7. То есть самый частый размер текста в приложении — 9px, и почти треть всего текста меньше 10px. Плюс дробные ступени 8.5 / 9.5 / 10.5 / 11.5 / 12.5 — шкалы нет, есть подгонка по месту. Худший случай складывается с проблемой контраста №1: `TradingDeskView.axaml:730` — `Text="ENTRY" Foreground="#3d5a72" FontSize="7"` (7px при 2,6:1), там же :736 «TP» и :742 «SL» — подписи уровней ордера.
*Исправить:* ввести шкалу из 6 ступеней (`FsCaption 11 / FsBody 13 / FsSubhead 15 / FsTitle 18 / FsH2 22 / FsH1 28`) как ресурсы в `AppStyles.axaml`, поднять нижнюю границу до 11px, выразить её через классы (`TdLabel`, `TdNum`, `Mono`) и убрать `FontSize` из разметки. Отдельно — прогнать проект на 150% DPI: при 9px и `LetterSpacing="0.8"` капслочные подписи начинают слипаться.
*Трудоёмкость:* L

**A4. Нет сетки отступов: 264 уникальных значения Margin/Padding** — `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml`
*Проблема:* Среди значений — `8,3`, `6,2`, `7,0`, `9,0`, `11,0`, `0,1`, `0,0,0,2`, `0,0,0,6`. Ни 4-, ни 8-пиксельной сетки: соседние карточки на одном экране отбиты по-разному, вертикальный ритм списков плавает. Это тот дефект, который не называют словами, но который читается как «неаккуратно».
*Исправить:* закрепить шаг 4px (4/8/12/16/24/32), вынести в `Thickness`-ресурсы (`PadCard`, `PadCompact`, `GapRow`), заменить механически — начать с 5 самых больших View, там сосредоточено ~60% значений.
*Трудоёмкость:* M

**A5. Цвета живут ещё и в C#: 1770 hex-литералов вне разметки** — `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs`, `Views/MainWindow.axaml.cs:371-374`, `ViewModels/SettingsDesk/SettingsModels.cs:258`
*Проблема:* Дополнение к пункту «Визуал №4» (там посчитаны 3873 литерала в AXAML): ещё 1770 лежат в `.cs` — `MainWindowViewModel` 127, `PortfolioDeskViewModel` 108, `PortfolioDeskRows` 79, `SettingsDeskViewModel` 62, `AiSignalDeskViewModel` 54, `CexCandlestickChart` 42. Механизм — VM отдаёт цвет строкой (`public string Bg` ×39, `Color` ×32, `SideBrush` ×14, `PnlColor` ×6…), View резолвит через `StringToBrush` (489 использований). Плюс прямой хардкод в code-behind: `MainWindow.axaml.cs:371-374`, `Brush.Parse("#21E6C1")` / `Brush.Parse("#3D5A72")` для кнопок языка, и в моделях — `SettingsModels.cs:258 KeyColor = "#8fa3b8"`. Итого ~5,6 тыс. точек, где живёт палитра: светлая тема или ребрендинг физически невозможны.
*Исправить:* VM должны возвращать **семантический ключ** (`"positive"`, `"negative"`, `"muted"`), а не hex; конвертер резолвит ключ в `{DynamicResource}` по текущей теме. Начать с `PortfolioDesk*` и `CexMarketItemViewModel` — там основная масса. Code-behind `RefreshLanguageButtons` перевести на псевдокласс `Classes.active` и стиль.
*Трудоёмкость:* L

**A6. `StringToBrushConverter.Parse` — строка без эффекта** — `CryptoAITerminal.TerminalUI/Converters/StringToBrushConverter.cs:42`
*Проблема:* `b.ToImmutable();` — результат отбрасывается, следующей строкой идёт `return b.ToImmutable();`. Метод возвращает вторую копию, первый вызов чистая аллокация. Не баг поведения, но след copy-paste в горячем конвертере.
*Исправить:* удалить строку 42.
*Трудоёмкость:* S

> Проверено и **не** является проблемой (чтобы не тратить время): `MinWidth="1040"` в `BotsView.axaml:300` и `MinWidth="860"` в `SniperView.axaml:556` находятся внутри `ScrollViewer HorizontalScrollBarVisibility="Auto"` — это правильный паттерн, а не превышение `MinWidth="960"` окна. `StringToBrushConverter` кэширует кисти (строки 21, 31) — источником аллокаций на тик он не является. Иконки векторные (45 `Path`/`PathIcon`), эмодзи-иконок нет.

---

## Порядок работ (топ-12 по отношению влияние/трудоёмкость)

| № | Что | Почему первым | Трудоёмкость |
|---|-----|---------------|--------------|
| 1 | WebApi fail-open: 503 без токена, вебхук off без секрета, биндинг на 127.0.0.1 (`WebApi/Program.cs:26`) | Открытый в интернет приём реальных рыночных ордеров; правка — десяток строк | S |
| 2 | Хоткеи B/S и Escape: гейт по разделу, модификаторы, смена дефолта CancelOrders (`MainWindow.axaml.cs:182, 187`) | Одиночная клавиша шлёт неподтверждённый маркет-ордер и снимает все TP/SL; исправляется в одном обработчике | S |
| 3 | `HasPrivateApiCredentials` — убрать default `true` (`Core/Interfaces/IExchangeGateway.cs:35`) | Pre-trade guard fail-open для 6 из 8 гейтвеев + заведомо ложный зелёный индикатор; компилятор сам покажет все точки | S |
| 4 | OKX/Bybit: `quantityAsset`/`marketUnit` = BaseAsset для market BUY (`OKXGateway.cs:127`, `BybitGateway.cs:157`) | Размер позиции завышается в разы на двух площадках; правка — один параметр в каждом вызове | S |
| 5 | Серверный trailing-бот: kill-switch + allowlist + risk (`Executor/BotExecutorService.cs:336`) | Kill-switch не останавливает ботов, закрывающих позиции; блок копируется из соседней grid-ветки | S |
| 6 | `/api/2fa/setup`: требовать текущий код при активной 2FA (`Server.Api/Program.cs:403`) | Один POST снимает единственный контроль над кастодиальным выводом | S |
| 7 | AI-трейдер: `Stop()` ставит `_killed`, страж paper для CEX-ветки (`AiTraderAgentService.cs:138`, `AiTraderViewModel.cs:298`) | Stop не останавливает уже начатые ордера, а PAPER ONLY не действует на агента; обе правки — по нескольку строк | S |
| 8 | CredentialsService: не затирать при сбое чтения + не писать plaintext молча (`CredentialsService.cs:548, 571`) | Безвозвратная потеря приватного ключа DEX и всех ключей бирж; плюс секреты открытым текстом | S |
| 9 | Кнопка «Live Allowed» → confirm-флоу (`RiskView.axaml:148`) | Один промах мышью снимает глобальный шлюз для Trading/DEX/Sniper/TRON; `AskConfirm` уже готов | S |
| 10 | Binance spot-гейтвей: реализовать или бросать `NotSupportedException` (`BinanceGateway.cs:284`) | Фейковый «Filled» на дефолтном spot-пути десктопа и сервера — корневая причина ложных PnL и «placed» в БД | M |
| 11 | Funding-арбитраж: инкрементальное накопление + гард реентерабельности (`FundingArbitrageService.cs:385`) | Бесконечно дублирующиеся реальные spot+perp ордера каждые 30 с с растущей базой | M |
| 12 | TpSlManager: `_closed` после подтверждения, откат трейлинга при сбое (`TpSlManager.cs:221, 290`) | Позиция остаётся без стопа именно в момент срабатывания триггера — худший сценарий для денег | M |