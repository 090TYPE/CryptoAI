# Trading Spot-Exchange Selector — Design

**Date:** 2026-06-20
**Project:** CryptoAI Terminal (Avalonia .NET 8 desktop)
**Goal:** In the Trading desk, let the user choose which CEX the order book (bids/asks), price, and order placement use — not just Binance. Mirror the existing multi-exchange futures pattern for spot.

## Decisions (locked)

- **Exchanges:** Binance, Bybit, OKX, KuCoin (the four spot gateways already constructed in `MainWindowViewModel`).
- **Scope:** "everything" — switching the spot exchange routes the order book, the displayed price derived from it, and spot order placement to that exchange. (Futures already has its own exchange selector; this adds the spot equivalent.)
- **No-keys behavior:** the order book and price (public data) always work for any selected exchange. Buy/Sell are disabled with a hint when the selected spot exchange has no API credentials. The dropdown lists all four exchanges regardless of keys.

## Non-goals (YAGNI)

- DEX is unaffected (separate venue mode).
- No new exchanges beyond the four already wired.
- No per-exchange symbol normalization changes (symbols already flow through existing gateways).
- No change to the futures selector.

## Current state (what exists)

- `MainWindowViewModel` holds spot gateways: `_gateway` (Binance, type `BinanceGateway`), `_bybitSpotGateway`, `_okxSpotGateway`, `_kucoinSpotGateway` (all `IExchangeGateway`).
- Futures already multi-exchange: `_futuresGatewaysMap` (Dictionary keyed by exchange name), `_selectedFuturesExchange`, `SelectedFuturesExchange`, `ActiveFuturesGateway` (line ~1502), `IsFuturesPrivateApiReady`, `FuturesPrivateApiStatusLabel/Brush`.
- Order book: `RefreshOrderBookAsync(market)` (line ~5406) picks `gateway = useFutures ? ActiveFuturesGateway : _gateway` and calls `GetOrderBookAsync(symbol, 50)`, with a spot fallback to `_gateway`.
- `ActiveCexGateway` (line ~3544) = `IsManualFuturesMode ? ActiveFuturesGateway : _gateway`.
- Spot order placement path constructs `new MarketOrderRouter(_gateway)` (line ~4364).
- `IExchangeGateway.HasPrivateApiCredentials` reports whether private endpoints (order placement) are usable.

## Architecture

### 1. Spot venue model (mirror of the futures pattern)

Add to `MainWindowViewModel`:
- `_spotGatewaysMap : Dictionary<string, IExchangeGateway>` (StringComparer.OrdinalIgnoreCase) = `{ "Binance"=_gateway, "Bybit"=_bybitSpotGateway, "OKX"=_okxSpotGateway, "KuCoin"=_kucoinSpotGateway }`. Built in the constructor right after the spot gateways are assigned.
- `_selectedSpotExchange : string = "Binance"`.
- `public IReadOnlyList<string> SpotExchangeOptions { get; } = ["Binance","Bybit","OKX","KuCoin"];`
- `public string SelectedSpotExchange { get; set; }` — on set: `RaiseAndSetIfChanged`, then raise `IsSpotPrivateApiReady`, `SpotPrivateApiStatusLabel`, `SpotPrivateApiStatusBrush`, `CexMarketModeSummary`, `TradingTerminalSummary`, and trigger an immediate `RefreshSelectedOrderBookAsync()`.
- `private IExchangeGateway ActiveSpotGateway => _spotGatewaysMap is not null && _spotGatewaysMap.TryGetValue(_selectedSpotExchange, out var gw) ? gw : _gateway;`

The resolution helper is extracted as a pure static for testing:
- `internal static IExchangeGateway ResolveGateway(IReadOnlyDictionary<string, IExchangeGateway> map, string key, IExchangeGateway fallback)` — returns `map[key]` if present (case-insensitive), else `fallback`. Both `ActiveSpotGateway` and (optionally) `ActiveFuturesGateway` can use it; for this feature only `ActiveSpotGateway` must.

### 2. Routing changes

- `ActiveCexGateway` → `IsManualFuturesMode ? ActiveFuturesGateway : ActiveSpotGateway`.
- `RefreshOrderBookAsync`: spot branch uses `ActiveSpotGateway`; the spot fallbacks currently hard-coded to `_gateway` become `ActiveSpotGateway`.
- Spot order placement (`new MarketOrderRouter(_gateway)`) → `new MarketOrderRouter(ActiveSpotGateway)`.

### 3. Keys / order gating

- `public bool IsSpotPrivateApiReady => ActiveSpotGateway.HasPrivateApiCredentials;`
- `public string SpotPrivateApiStatusLabel` / `SpotPrivateApiStatusBrush` — mirror the futures status props (e.g. "Ключи {exchange}: готово" green / "Нет ключей {exchange}" amber).
- The spot Buy/Sell buttons' `IsEnabled` (or the existing can-execute path) gains a spot-mode condition: when in spot CEX mode, require `IsSpotPrivateApiReady`. When disabled, show a hint "Добавь API-ключи {SelectedSpotExchange} в Настройках".
- Order-placement code that runs for spot should guard: if not `IsSpotPrivateApiReady`, surface the same hint instead of calling the gateway (avoid a raw 401).

### 4. UI (TradingDeskView)

- Add a "Биржа (спот)" `ComboBox` bound to `SpotExchangeOptions` / `SelectedSpotExchange`, visible only in spot CEX mode (`IsCexTradingMode && !IsManualFuturesMode`), placed near the existing market-mode / futures-exchange controls.
- Add a small key-status label bound to `SpotPrivateApiStatusLabel` / `SpotPrivateApiStatusBrush` (same treatment as the futures status label).
- The order book panel header may show the active exchange (optional, low cost).

### 5. Switch behavior

Changing `SelectedSpotExchange`:
1. Re-fetches the order book immediately (`RefreshSelectedOrderBookAsync`).
2. Recomputes key-readiness props so Buy/Sell enable/disable and the status label update.
3. Updates the summary strings (`CexMarketModeSummary` → e.g. "{exchange} spot market").

## Error handling

- Order book fetch failure for a non-Binance spot exchange: log it (existing `AddLog`) and leave the previous book; never crash. (The order book refresh already wraps gateway calls in try/catch.)
- Placement without keys: blocked client-side with the hint; no gateway call.
- Unknown/missing map key: `ResolveGateway` falls back to `_gateway` (Binance).

## Testing

- **Unit (Core.Tests):** `ResolveGateway` returns the mapped gateway for a known key (case-insensitive), and the fallback for an unknown/empty key. Use simple `IExchangeGateway` stubs/mocks.
- **Manual smoke:** in Trading spot mode, switch the exchange dropdown → order book reloads from that exchange; with no keys for it, Buy/Sell are disabled and the hint shows; with Binance (keys present) Buy/Sell enabled. Futures mode and DEX mode unaffected.

## Files (anticipated)

- `CryptoAITerminal.TerminalUI/ViewModels/MainWindowViewModel.cs` — spot map, `SelectedSpotExchange`, `ActiveSpotGateway`, `ResolveGateway`, routing + gating changes, status props.
- `CryptoAITerminal.TerminalUI/Views/TradingDeskView.axaml` — spot exchange ComboBox + status label + Buy/Sell enable binding.
- `CryptoAITerminal.Core.Tests/SpotExchangeResolveTests.cs` — `ResolveGateway` unit tests.
