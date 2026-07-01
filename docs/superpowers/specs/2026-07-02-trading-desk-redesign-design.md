# Trading Desk Redesign — Design Spec

Date: 2026-07-02
Owner: 090TYPE
Reference mockup: `Trading Standalone.html` (bundled React design)

## Goal

Reverse the `TradingDeskView` screen to match the reference mockup: a dense pro
trading terminal that is fully **live** (all data connected), lets the user
**switch exchanges for the same token**, and lists **every symbol from Markets**
(including user-added ones) as selectable chips.

## Scope decisions (confirmed with user)

- Exchange venue tabs show the **4 live venues**: Binance / Bybit / OKX / KuCoin.
  Kraken/Coinbase from the mockup are dropped (replaced by KuCoin).
- **Full re-layout** of `TradingDeskView.axaml` to the mockup structure,
  reusing existing VM commands/properties and App.axaml style classes.

## Layout (replaces TradingDeskView.axaml)

### Top status bar (full width)
Coin logo + `SelectedMarket.DisplaySymbol` + PERP badge + `{venue}·CEX` subtitle;
stat cells LAST PRICE (`LastPrice`+`ChangePercent`), 24H HIGH (`SessionHigh`),
24H LOW (`SessionLow`), 24H VOL (`Volume24hLabel`), FUNDING, OI; right side
CEX/DEX toggle, EQUITY (`AccountEquityLabel`), SESSION PNL (`SessionPnlLabel`),
GUARD status. FUNDING/OI show live value where available else "--".

### Left — Order Entry (~300px)
BUY/LONG · SELL/SHORT toggle (`SelectOrderSideCommand`); **symbol chips bound to
`Markets`** (new); Order Type; Market Mode (Futures/Spot); Leverage + slider +
Cross/Isolated; Size% + presets; TP/SL; Trading Profile (Swing/Scalp/Aggro);
Slippage + fee breakdown; Futures Telemetry; Execution Guard; big ORDER button
(`PlacePrimaryOrderCommand`). All existing VM members.

### Center
Chart metric row (Mark/Spread/High/Low/Vol) → timeframe toolbar + chart type
(Candles/Line/Area/HA) + indicator toggles (MA20/MA50/BB/RSI/Walls) + drawing
tools → `CexCandlestickChart` → **public trade tape** (new) → Orders/Positions/
Fills/Signals tabs (existing).

### Right column (~356px)
**Exchange venue tabs** with live same-token mid price + delta vs base (new);
venue header (symbol, 24h vol, fee); Position card (Reverse/Close); AI Signal;
Order Book ladder with depth bars (existing `TopAsks`/`TopBids`/`DepthWidth`);
Risk bar + Max DD; Wallets list (`WalletVM`).

## New code

1. **`TradingVenueQuoteViewModel`** + `ObservableCollection<TradingVenueQuoteViewModel>
   TradingVenues` on MainWindowViewModel. Each item: exchange name, live mid price
   (from `gateway.GetOrderBookAsync(symbol)`), delta vs Binance base, selected
   flag, brushes. A `DispatcherTimer` polls all 4 gateways for the current
   `SelectedTradingSymbol` and updates prices/deltas. Selecting a venue sets
   `SelectedSpotExchange` (reuses existing order-book/price re-sourcing) and marks
   the tab active. Re-polls on symbol change.
2. **Public trade tape**: `ObservableCollection<TapeTradeViewModel> TapeTrades`,
   filled from `ActiveSpotGateway.GetRecentTradesAsync(symbol)` on a timer /
   symbol change (works without private API keys).
3. **Chart type + indicators**: add `ChartType` (Candles/Line/Area/HeikinAshi),
   `ShowMa20`, `ShowMa50`, `ShowBollinger`, `ShowRsi`, `ShowWalls` state on VM +
   commands; render them in `CexCandlestickChart` (MA overlays, Bollinger bands,
   Heikin-Ashi/line/area transforms, RSI sub-panel). Walls already rendered.
4. **Symbol chips**: `ItemsControl` over `Markets`, each chip a button running
   `FocusMarketCommand`/selection, highlighted when active.

## Non-goals
- No Kraken/Coinbase gateways.
- No new order-execution logic; reuse existing commands.
- No changes to Markets tab.

## Testing / verification
- Build (`dotnet build`) green.
- Launch app, open Trading: verify layout matches mockup, symbol chips list all
  markets incl. custom, venue tabs show live prices + switch source, trade tape
  streams, chart toolbar toggles work.
