using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine.Agent;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Autonomous AI trader: Claude drives a tool-use loop ("the AI trades by itself").
/// The model inspects the market and the (paper or live) account through tools and
/// places its own orders. This host owns every money-sensitive decision — the
/// engine (<see cref="ClaudeAgentRunner"/>) only relays tool calls.
///
/// Safety model:
///  • Paper mode (default): orders are fully simulated against a virtual USDT book
///    that fills at real public prices. Private exchange endpoints are never touched.
///  • Live mode (opt-in): orders route through the gateway, but only after passing
///    <see cref="RiskManager.RiskManager"/> plus hard caps (max order USD, max open
///    positions, daily loss) enforced here. A kill-switch halts everything instantly.
/// </summary>
public sealed class AiTraderAgentService
{
    public sealed record Limits(
        decimal MaxOrderUsd = 100m,
        decimal MaxTotalExposureUsd = 500m,
        int MaxOpenPositions = 3,
        decimal MaxDailyLossUsd = 100m,
        decimal VirtualStartUsd = 1000m);

    /// <summary>Where the agent trades.</summary>
    public enum Venue { Cex, Dex }

    /// <summary>On-chain (DEX) configuration — required when <see cref="Venue.Dex"/> is selected.</summary>
    public sealed record DexConfig(
        Gateway.DEX.IDexTradeGateway Gateway,
        decimal SlippagePercent = 3m,
        decimal MaxNativePerOrder = 0.1m,
        decimal VirtualNativeStart = 1m,
        string? DexId = null);

    private readonly IExchangeGateway _gateway;
    private readonly RiskManager.RiskManager _risk;
    private readonly Limits _limits;
    private readonly HttpClient? _httpForTests;

    // ── Venue ──
    private readonly Venue _venue;
    private readonly Gateway.DEX.IDexTradeGateway? _dex;
    private readonly DexConfig? _dexCfg;
    private bool IsDex => _venue == Venue.Dex;
    private string NativeSym => _dex?.NativeSymbol ?? "NATIVE";

    // DEX paper book — tokenAddress → (tokenQty, avgPriceNativePerToken). Cash is native.
    private readonly Dictionary<string, (decimal Qty, decimal AvgNative)> _dexPaper = new(StringComparer.OrdinalIgnoreCase);
    private decimal _dexNativeCash;
    private decimal _dexRealizedNative;

    // Market mode. Spot = long-only book; Futures = leverage + shorts + reduce-only closes.
    private readonly TradingMarketType _marketType;
    private readonly int _leverage;
    private readonly FuturesMarginMode _marginMode;
    private bool IsFutures => _marketType == TradingMarketType.FuturesUsdM;

    // Futures account state (mirrors TradingBot): one-way (Both) by default; flip to
    // hedge (Long/Short) once if the exchange rejects with a position-side mismatch.
    private bool _isHedgeMode;
    private readonly HashSet<string> _leverageApplied = new(StringComparer.OrdinalIgnoreCase);

    // Virtual paper book — symbol → (qty, avgEntryUsd). Cash is the remaining USDT.
    private readonly Dictionary<string, (decimal Qty, decimal Avg)> _paperPositions = new(StringComparer.OrdinalIgnoreCase);
    private decimal _paperCash;
    private decimal _paperRealizedPnl;
    private readonly object _bookLock = new();

    private volatile bool _killed;
    private CancellationTokenSource? _loopCts;

    /// <summary>True = simulate only. False = route to the exchange under hard limits.</summary>
    public bool LiveEnabled { get; set; }

    public bool IsRunning => _loopCts is { IsCancellationRequested: false };

    /// <summary>Streams every agent step (thinking, tool call, result) for the UI log.</summary>
    public event Action<AgentEvent>? OnEvent;

    /// <summary>Fired when a paper/live order actually fills. (symbol, side, qty, price, usd, mode)</summary>
    public event Action<string, string, decimal, decimal, decimal, string>? OnFill;

    public AiTraderAgentService(
        IExchangeGateway gateway,
        Limits? limits = null,
        HttpClient? httpForTests = null,
        TradingMarketType marketType = TradingMarketType.Spot,
        int leverage = 1,
        FuturesMarginMode marginMode = FuturesMarginMode.Cross,
        Venue venue = Venue.Cex,
        DexConfig? dex = null)
    {
        _venue = venue;
        if (venue == Venue.Dex)
        {
            _dexCfg = dex ?? throw new ArgumentNullException(nameof(dex), "DEX venue requires a DexConfig.");
            _dex = _dexCfg.Gateway;
            _gateway = _dex; // IDexTradeGateway is an IExchangeGateway (used only for ConnectAsync here).
            _dexNativeCash = _dexCfg.VirtualNativeStart;
        }
        else
        {
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        }
        _limits = limits ?? new Limits();
        _httpForTests = httpForTests;
        _marketType = marketType;
        _leverage = Math.Max(1, leverage);
        _marginMode = marginMode;
        _paperCash = _limits.VirtualStartUsd;
        _risk = new RiskManager.RiskManager(
            maxPositionSizeUsd: _limits.MaxTotalExposureUsd,
            maxDailyLossUsd: _limits.MaxDailyLossUsd);
    }

    /// <summary>Instantly stop trading; pending orders are not placed and the loop ends.</summary>
    public void Kill()
    {
        _killed = true;
        _loopCts?.Cancel();
        OnEvent?.Invoke(new AgentEvent(AgentEventKind.Error, "kill-switch", "Trading halted by kill-switch."));
    }

    public void Stop() => _loopCts?.Cancel();

    /// <summary>
    /// Runs the agent on a fixed cadence until stopped. Each tick is one full
    /// tool-use turn where the model reviews the account and may trade.
    /// </summary>
    public async Task StartLoopAsync(string apiKey, string model, TimeSpan interval, CancellationToken ct = default)
    {
        if (IsRunning) return;
        _killed = false;
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _loopCts.Token;

        await _gateway.ConnectAsync().ConfigureAwait(false);

        while (!token.IsCancellationRequested && !_killed)
        {
            try { await RunOnceAsync(apiKey, model, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { OnEvent?.Invoke(new AgentEvent(AgentEventKind.Error, "tick", ex.Message)); }

            try { await Task.Delay(interval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        OnEvent?.Invoke(new AgentEvent(AgentEventKind.Done, "loop", "Agent loop stopped."));
    }

    /// <summary>Single agent turn — exposed for the "Run once" button and tests.</summary>
    public async Task<AgentRunResult> RunOnceAsync(string apiKey, string model, CancellationToken ct = default)
    {
        if (_killed)
            return new AgentRunResult("Kill-switch active.", 0, 0, "killed");

        var runner = new ClaudeAgentRunner(apiKey, model, http: _httpForTests);
        var tools = BuildTools();
        return await runner.RunAsync(SystemPrompt(), UserInstruction(), tools, OnEvent, ct).ConfigureAwait(false);
    }

    // ── Prompts ──────────────────────────────────────────────────────────────

    private string SystemPrompt()
    {
        var mode = LiveEnabled ? "LIVE (real orders)" : "PAPER (simulated)";
        var tail = "When done, briefly explain your portfolio view and what you changed (or why you held).";

        if (IsDex)
        {
            return "You are an autonomous on-chain (DEX) trading agent on the " +
                $"{_dex!.NetworkName} network, trading via {_dex.SupportedDexesLabel}. " +
                "You trade tokens by CONTRACT ADDRESS, spending the native currency " +
                $"({NativeSym}). Tokens here are high-risk: ALWAYS call dex_check_token before buying a " +
                "new token to screen for honeypots/un-sellable tokens, and avoid anything that fails or " +
                "shows a large round-trip loss. Use dex_get_quote to learn the rate before sizing. " +
                $"Mode: {mode}. Hard limits: max {_dexCfg!.MaxNativePerOrder} {NativeSym} per trade, " +
                $"max {_limits.MaxOpenPositions} token positions, slippage {_dexCfg.SlippagePercent}%. " +
                "Be conservative; holding is always acceptable. " + tail;
        }

        var marketLine = IsFutures
            ? $"Market: USD-M PERPETUAL FUTURES at {_leverage}x leverage ({_marginMode} margin). " +
              "You may go LONG (buy) or SHORT (sell to open). Use close_position to flatten. " +
              "Leverage amplifies both gains and liquidation risk — size positions accordingly."
            : "Market: SPOT (long-only). You can only sell what you hold; no shorting.";

        return "You are an autonomous crypto trading agent managing a single account. " +
            "Use the tools to inspect prices, your balance and open positions, then decide whether to " +
            "open, add to, close, or hold positions. Trade only when you have a clear edge; holding is " +
            "always acceptable. Size every position conservatively and respect the stated risk limits. " +
            marketLine + " " +
            $"Mode: {mode}. " +
            $"Hard limits: max {_limits.MaxOrderUsd:0} USD per order, max {_limits.MaxOpenPositions} open positions, " +
            $"max {_limits.MaxTotalExposureUsd:0} USD total exposure. " +
            tail;
    }

    private static string UserInstruction() =>
        "Review the account and current market, then take any trades you judge favorable. " +
        "Inspect before acting. Finish with a one-paragraph summary.";

    // ── Tools ────────────────────────────────────────────────────────────────

    private IReadOnlyList<AgentTool> BuildTools() => IsDex ? BuildDexTools() : BuildCexTools();

    private IReadOnlyList<AgentTool> BuildCexTools() =>
    [
        new AgentTool(
            "get_price",
            "Get the current best bid/ask and mid price for a symbol (e.g. BTCUSDT). Works for any liquid pair.",
            new { type = "object", properties = new { symbol = new { type = "string", description = "e.g. BTCUSDT" } }, required = new[] { "symbol" } },
            GetPriceTool),

        new AgentTool(
            "get_balance",
            "Get available USDT balance for the account (virtual balance in paper mode).",
            new { type = "object", properties = new { } },
            GetBalanceTool),

        new AgentTool(
            "get_positions",
            "List all currently open positions with quantity, average entry and unrealized P&L.",
            new { type = "object", properties = new { } },
            GetPositionsTool),

        new AgentTool(
            "place_order",
            "Open or add to a position with a MARKET order. side is 'buy' or 'sell'. " +
            (IsFutures
                ? "Futures: 'buy' opens/adds a LONG, 'sell' opens/adds a SHORT (or reduces an opposite position). "
                : "Spot: 'buy' to acquire, 'sell' to reduce/exit a holding (no shorting). ") +
            "Provide either quantity (base asset) or usd (notional); usd is preferred. " +
            "Subject to risk limits — the result tells you if it was rejected.",
            new
            {
                type = "object",
                properties = new
                {
                    symbol = new { type = "string", description = "e.g. ETHUSDT" },
                    side = new { type = "string", @enum = new[] { "buy", "sell" } },
                    usd = new { type = "number", description = "Notional value in USD (preferred)." },
                    quantity = new { type = "number", description = "Base-asset quantity (alternative to usd)." }
                },
                required = new[] { "symbol", "side" }
            },
            PlaceOrderTool),

        new AgentTool(
            "close_position",
            "Fully close the open position for a symbol at market.",
            new { type = "object", properties = new { symbol = new { type = "string" } }, required = new[] { "symbol" } },
            ClosePositionTool),
    ];

    // ── DEX (on-chain) tool set ────────────────────────────────────────────────

    private IReadOnlyList<AgentTool> BuildDexTools() =>
    [
        new AgentTool(
            "dex_get_native_balance",
            $"Get the wallet's native {NativeSym} balance (virtual in paper mode). This is your spendable budget.",
            new { type = "object", properties = new { } },
            DexNativeBalanceTool),

        new AgentTool(
            "dex_get_token_balance",
            "Get the wallet's balance of a specific token by contract address.",
            new { type = "object", properties = new { token_address = new { type = "string" } }, required = new[] { "token_address" } },
            DexTokenBalanceTool),

        new AgentTool(
            "dex_get_quote",
            $"Quote how many tokens you receive for spending a given amount of native {NativeSym} on a token. Use it to learn the rate before buying.",
            new
            {
                type = "object",
                properties = new
                {
                    token_address = new { type = "string" },
                    native_amount = new { type = "number", description = $"Amount of {NativeSym} to spend in the quote." }
                },
                required = new[] { "token_address", "native_amount" }
            },
            DexQuoteTool),

        new AgentTool(
            "dex_check_token",
            "Safety screen a token before buying: simulates a round-trip to detect honeypots / un-sellable tokens and estimates round-trip loss. ALWAYS call this before buying a new token.",
            new { type = "object", properties = new { token_address = new { type = "string" } }, required = new[] { "token_address" } },
            DexCheckTokenTool),

        new AgentTool(
            "dex_get_positions",
            "List token positions held in the paper wallet (token address, qty, avg native cost).",
            new { type = "object", properties = new { } },
            DexPositionsTool),

        new AgentTool(
            "dex_buy",
            $"Buy a token by spending native {NativeSym}. Subject to the max-native-per-trade limit. Screen with dex_check_token first.",
            new
            {
                type = "object",
                properties = new
                {
                    token_address = new { type = "string" },
                    native_amount = new { type = "number", description = $"Amount of {NativeSym} to spend." }
                },
                required = new[] { "token_address", "native_amount" }
            },
            DexBuyTool),

        new AgentTool(
            "dex_sell",
            "Sell a token amount back to native currency.",
            new
            {
                type = "object",
                properties = new
                {
                    token_address = new { type = "string" },
                    token_amount = new { type = "number", description = "Amount of the token to sell. Omit to sell the full balance." }
                },
                required = new[] { "token_address" }
            },
            DexSellTool),
    ];

    private Task<string> DexNativeBalanceTool(JsonElement input, CancellationToken ct)
    {
        if (!LiveEnabled)
        {
            lock (_bookLock)
                return Task.FromResult(Json(new { mode = "paper", native_symbol = NativeSym, balance = Round(_dexNativeCash), realized_native = Round(_dexRealizedNative) }));
        }
        return DexLiveNativeBalanceAsync();
    }

    private async Task<string> DexLiveNativeBalanceAsync()
    {
        try { return Json(new { mode = "live", native_symbol = NativeSym, balance = Round(await _dex!.GetBalanceAsync(NativeSym).ConfigureAwait(false)) }); }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private async Task<string> DexTokenBalanceTool(JsonElement input, CancellationToken ct)
    {
        var addr = Str(input, "token_address");
        if (string.IsNullOrWhiteSpace(addr)) return Err("token_address required");
        if (!LiveEnabled)
        {
            lock (_bookLock)
            {
                _dexPaper.TryGetValue(addr, out var p);
                return Json(new { mode = "paper", token_address = addr, balance = Round(p.Qty) });
            }
        }
        try { return Json(new { mode = "live", token_address = addr, balance = Round(await _dex!.GetTokenBalanceAsync(addr).ConfigureAwait(false)) }); }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private async Task<string> DexQuoteTool(JsonElement input, CancellationToken ct)
    {
        var addr = Str(input, "token_address");
        var nativeAmount = Num(input, "native_amount");
        if (string.IsNullOrWhiteSpace(addr)) return Err("token_address required");
        if (nativeAmount <= 0) return Err("native_amount must be > 0");
        try
        {
            var tokensOut = await _dex!.GetTokenPriceInNativeAsync(addr, nativeAmount, _dexCfg!.DexId).ConfigureAwait(false);
            if (tokensOut <= 0) return Err("router quote unavailable for this token");
            return Json(new { token_address = addr, native_in = Round(nativeAmount), tokens_out = Round(tokensOut), native_per_token = Round(nativeAmount / tokensOut) });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private async Task<string> DexCheckTokenTool(JsonElement input, CancellationToken ct)
    {
        var addr = Str(input, "token_address");
        if (string.IsNullOrWhiteSpace(addr)) return Err("token_address required");
        try
        {
            var probe = await _dex!.ProbeSellabilityAsync(new Gateway.DEX.DexSellabilityProbeRequest(
                addr, _dexCfg!.SlippagePercent, _dexCfg.DexId,
                NativeAmountToProbe: _dexCfg.MaxNativePerOrder)).ConfigureAwait(false);
            return Json(new
            {
                token_address = addr,
                passed = probe.Passed,
                on_chain_simulation = probe.IsOnChainSimulation,
                round_trip_loss_pct = probe.RoundTripLossPercent.HasValue ? Round(probe.RoundTripLossPercent.Value) : (decimal?)null,
                narrative = probe.Narrative
            });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private Task<string> DexPositionsTool(JsonElement input, CancellationToken ct)
    {
        if (LiveEnabled)
            return Task.FromResult(Json(new { mode = "live", note = "Use dex_get_token_balance per token; live wallet holdings aren't enumerable here." }));
        lock (_bookLock)
        {
            var rows = _dexPaper.Where(p => p.Value.Qty != 0)
                .Select(p => new { token_address = p.Key, qty = Round(p.Value.Qty), avg_native_per_token = Round(p.Value.AvgNative) });
            return Task.FromResult(Json(new { mode = "paper", native_symbol = NativeSym, positions = rows }));
        }
    }

    private async Task<string> DexBuyTool(JsonElement input, CancellationToken ct)
    {
        if (_killed) return Err("kill-switch active");
        var addr = Str(input, "token_address");
        var nativeAmount = Num(input, "native_amount");
        if (string.IsNullOrWhiteSpace(addr)) return Err("token_address required");
        if (nativeAmount <= 0) return Err("native_amount must be > 0");
        if (nativeAmount > _dexCfg!.MaxNativePerOrder)
            return Err($"trade {nativeAmount} {NativeSym} exceeds max {_dexCfg.MaxNativePerOrder} {NativeSym} per trade");

        try
        {
            var tokensOut = await _dex!.GetTokenPriceInNativeAsync(addr, nativeAmount, _dexCfg.DexId).ConfigureAwait(false);
            if (tokensOut <= 0) return Err("router quote unavailable — not buying");

            if (!LiveEnabled)
            {
                lock (_bookLock)
                {
                    if (nativeAmount > _dexNativeCash) return Err($"insufficient paper {NativeSym}: have {Round(_dexNativeCash)}");
                    var isNewToken = !(_dexPaper.TryGetValue(addr, out var existing0) && existing0.Qty != 0);
                    if (isNewToken && _dexPaper.Count(kv => kv.Value.Qty != 0) >= _limits.MaxOpenPositions)
                        return Err($"max {_limits.MaxOpenPositions} token positions reached");
                    _dexPaper.TryGetValue(addr, out var p);
                    var pricePerToken = nativeAmount / tokensOut;
                    var newQty = p.Qty + tokensOut;
                    var newAvg = p.Qty <= 0 ? pricePerToken : ((p.AvgNative * p.Qty) + nativeAmount) / newQty;
                    _dexPaper[addr] = (newQty, newAvg);
                    _dexNativeCash -= nativeAmount;
                    OnFill?.Invoke(addr, "buy", tokensOut, pricePerToken, nativeAmount, "paper");
                    OnEvent?.Invoke(new AgentEvent(AgentEventKind.ToolResult, "DEX BUY (paper)", $"{Round(tokensOut)} tok for {nativeAmount} {NativeSym}"));
                    return Json(new { ok = true, mode = "paper", token_address = addr, tokens_out = Round(tokensOut), spent_native = Round(nativeAmount), native_left = Round(_dexNativeCash) });
                }
            }

            var tx = await _dex.BuyTokenAsync(addr, nativeAmount, _dexCfg.SlippagePercent, _dexCfg.DexId).ConfigureAwait(false);
            OnFill?.Invoke(addr, "buy", tokensOut, nativeAmount / tokensOut, nativeAmount, "live");
            OnEvent?.Invoke(new AgentEvent(AgentEventKind.ToolResult, "DEX BUY (live)", $"{nativeAmount} {NativeSym} → {addr}  tx={tx}"));
            return Json(new { ok = true, mode = "live", token_address = addr, spent_native = Round(nativeAmount), tx_hash = tx });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private async Task<string> DexSellTool(JsonElement input, CancellationToken ct)
    {
        if (_killed) return Err("kill-switch active");
        var addr = Str(input, "token_address");
        var tokenAmount = Num(input, "token_amount");
        if (string.IsNullOrWhiteSpace(addr)) return Err("token_address required");

        try
        {
            if (!LiveEnabled)
            {
                // Value the sale at the current round-trip rate (sell the requested qty → native).
                decimal heldQty;
                lock (_bookLock) { _dexPaper.TryGetValue(addr, out var pp); heldQty = pp.Qty; }
                if (heldQty <= 0) return Err($"no paper position for {addr}");
                if (tokenAmount <= 0 || tokenAmount > heldQty) tokenAmount = heldQty;

                // Probe: spend 1 unit of MaxNativePerOrder to get rate, then invert.
                var probeNative = _dexCfg!.MaxNativePerOrder;
                var tokensForProbe = await _dex!.GetTokenPriceInNativeAsync(addr, probeNative, _dexCfg.DexId).ConfigureAwait(false);
                var nativePerToken = tokensForProbe > 0 ? probeNative / tokensForProbe : 0m;
                var nativeOut = nativePerToken * tokenAmount;

                lock (_bookLock)
                {
                    _dexPaper.TryGetValue(addr, out var p);
                    var realized = (nativePerToken - p.AvgNative) * tokenAmount;
                    _dexRealizedNative += realized;
                    var newQty = p.Qty - tokenAmount;
                    if (newQty <= 0) _dexPaper.Remove(addr); else _dexPaper[addr] = (newQty, p.AvgNative);
                    _dexNativeCash += nativeOut;
                    OnFill?.Invoke(addr, "sell", tokenAmount, nativePerToken, nativeOut, "paper");
                    OnEvent?.Invoke(new AgentEvent(AgentEventKind.ToolResult, "DEX SELL (paper)", $"{Round(tokenAmount)} tok → {Round(nativeOut)} {NativeSym} (pnl {realized:+0.######;-0.######})"));
                    return Json(new { ok = true, mode = "paper", token_address = addr, sold = Round(tokenAmount), native_out = Round(nativeOut), realized_native = Round(realized) });
                }
            }

            var sellQty = tokenAmount > 0 ? tokenAmount : await _dex!.GetTokenBalanceAsync(addr).ConfigureAwait(false);
            if (sellQty <= 0) return Err($"no live balance for {addr}");
            var tx = await _dex!.SellTokenAsync(addr, sellQty, _dexCfg!.SlippagePercent, _dexCfg.DexId).ConfigureAwait(false);
            OnFill?.Invoke(addr, "sell", sellQty, 0m, 0m, "live");
            OnEvent?.Invoke(new AgentEvent(AgentEventKind.ToolResult, "DEX SELL (live)", $"{Round(sellQty)} {addr}  tx={tx}"));
            return Json(new { ok = true, mode = "live", token_address = addr, sold = Round(sellQty), tx_hash = tx });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private async Task<string> GetPriceTool(JsonElement input, CancellationToken ct)
    {
        var symbol = Str(input, "symbol");
        if (string.IsNullOrWhiteSpace(symbol)) return Err("symbol required");
        try
        {
            var (bid, ask, mid) = await GetQuoteAsync(symbol, ct).ConfigureAwait(false);
            if (mid <= 0) return Err($"no price for {symbol}");
            return Json(new { symbol, bid, ask, mid });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private Task<string> GetBalanceTool(JsonElement input, CancellationToken ct)
    {
        if (!LiveEnabled)
        {
            lock (_bookLock)
                return Task.FromResult(Json(new { mode = "paper", usdt = Round(_paperCash), realized_pnl = Round(_paperRealizedPnl) }));
        }
        return GetLiveBalanceAsync(ct);
    }

    private async Task<string> GetLiveBalanceAsync(CancellationToken ct)
    {
        try
        {
            var usdt = await _gateway.GetBalanceAsync("USDT").ConfigureAwait(false);
            return Json(new { mode = "live", usdt = Round(usdt) });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private async Task<string> GetPositionsTool(JsonElement input, CancellationToken ct)
    {
        try
        {
            if (!LiveEnabled)
            {
                List<object> rows = new();
                List<(string Symbol, decimal Qty, decimal Avg)> snapshot;
                lock (_bookLock)
                    snapshot = _paperPositions.Where(p => p.Value.Qty != 0)
                        .Select(p => (p.Key, p.Value.Qty, p.Value.Avg)).ToList();

                foreach (var p in snapshot)
                {
                    var (_, _, mid) = await GetQuoteAsync(p.Symbol, ct).ConfigureAwait(false);
                    var upnl = mid > 0 ? (mid - p.Avg) * p.Qty : 0m;
                    rows.Add(new { symbol = p.Symbol, qty = Round(p.Qty), avg_entry = Round(p.Avg), mark = Round(mid), unrealized_pnl = Round(upnl) });
                }
                return Json(new { mode = "paper", positions = rows });
            }

            var positions = await _gateway.GetOpenPositionsAsync().ConfigureAwait(false);
            var live = positions.Where(p => p.Quantity != 0).Select(p => new
            {
                symbol = p.Symbol,
                qty = Round(p.Quantity),
                avg_entry = Round(p.EntryPrice),
                unrealized_pnl = Round(p.UnrealizedPnl)
            });
            return Json(new { mode = "live", positions = live });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private async Task<string> PlaceOrderTool(JsonElement input, CancellationToken ct)
    {
        if (_killed) return Err("kill-switch active");

        var symbol = Str(input, "symbol");
        var sideRaw = Str(input, "side").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(symbol)) return Err("symbol required");
        if (sideRaw is not ("buy" or "sell")) return Err("side must be 'buy' or 'sell'");

        var (_, _, mid) = await GetQuoteAsync(symbol, ct).ConfigureAwait(false);
        if (mid <= 0) return Err($"no price for {symbol}");

        var usd = Num(input, "usd");
        var qty = Num(input, "quantity");
        if (usd <= 0 && qty <= 0) return Err("provide usd or quantity");
        if (usd <= 0) usd = qty * mid;
        if (qty <= 0) qty = usd / mid;

        // ── Hard caps (apply in both paper and live) ──
        if (usd > _limits.MaxOrderUsd)
            return Err($"order {usd:0} USD exceeds max {_limits.MaxOrderUsd:0} USD per order");

        var side = sideRaw == "buy" ? OrderSide.Buy : OrderSide.Sell;

        if (!LiveEnabled)
            return PaperFill(symbol, side, qty, mid);

        // ── Live path: RiskManager + exposure + open-position caps ──
        try
        {
            var balance = await _gateway.GetBalanceAsync("USDT").ConfigureAwait(false);
            var positions = await _gateway.GetOpenPositionsAsync().ConfigureAwait(false);
            var open = positions.Where(p => p.Quantity != 0).ToList();
            var exposure = open.Sum(p => Math.Abs(p.Quantity) * mid);
            var isNewSymbol = !open.Any(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            if (isNewSymbol && open.Count >= _limits.MaxOpenPositions)
                return Err($"max {_limits.MaxOpenPositions} open positions reached");

            var order = IsFutures
                ? new Order
                {
                    Symbol = symbol, Side = side, Type = OrderType.Market, Quantity = qty,
                    MarketType = TradingMarketType.FuturesUsdM, Leverage = _leverage,
                    MarginMode = _marginMode, PositionSide = EntryPositionSide(side == OrderSide.Buy)
                }
                : new Order { Symbol = symbol, Side = side, Type = OrderType.Market, Quantity = qty, MarketType = TradingMarketType.Spot };

            if (!_risk.CanPlaceOrder(order, mid, balance, exposure))
                return Err("rejected by risk manager (exposure / balance / daily-loss limit)");

            if (IsFutures) await EnsureFuturesLeverageAsync(symbol).ConfigureAwait(false);

            try
            {
                await _gateway.PlaceOrderAsync(order).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFutures && IsPositionSideMismatch(ex))
            {
                _isHedgeMode = !_isHedgeMode;
                order.PositionSide = EntryPositionSide(side == OrderSide.Buy);
                OnEvent?.Invoke(new AgentEvent(AgentEventKind.Error, "position-side",
                    $"mismatch — switched to {(_isHedgeMode ? "hedge" : "one-way")} and retrying"));
                await _gateway.PlaceOrderAsync(order).ConfigureAwait(false);
            }
            OnFill?.Invoke(symbol, sideRaw, qty, mid, usd, "live");
            OnEvent?.Invoke(new AgentEvent(AgentEventKind.ToolResult, "FILL (live)", $"{sideRaw} {qty:0.######} {symbol} @ {mid:0.####} (~{usd:0} USD)"));
            return Json(new { ok = true, mode = "live", market = IsFutures ? "futures" : "spot", symbol, side = sideRaw, qty = Round(qty), price = Round(mid), usd = Round(usd), leverage = IsFutures ? _leverage : 1 });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private async Task<string> ClosePositionTool(JsonElement input, CancellationToken ct)
    {
        if (_killed) return Err("kill-switch active");
        var symbol = Str(input, "symbol");
        if (string.IsNullOrWhiteSpace(symbol)) return Err("symbol required");

        var (_, _, mid) = await GetQuoteAsync(symbol, ct).ConfigureAwait(false);

        if (!LiveEnabled)
        {
            lock (_bookLock)
            {
                if (!_paperPositions.TryGetValue(symbol, out var pos) || pos.Qty == 0)
                    return Err($"no open paper position for {symbol}");
                var pnl = (mid - pos.Avg) * pos.Qty;
                _paperCash += pos.Qty * mid;
                _paperRealizedPnl += pnl;
                _paperPositions.Remove(symbol);
                OnFill?.Invoke(symbol, "close", pos.Qty, mid, pos.Qty * mid, "paper");
                OnEvent?.Invoke(new AgentEvent(AgentEventKind.ToolResult, "CLOSE (paper)", $"{symbol} pnl {pnl:+0.00;-0.00} USD"));
                if (pnl < 0) _risk.RecordLoss(Math.Abs(pnl));
                return Json(new { ok = true, mode = "paper", symbol, realized_pnl = Round(pnl) });
            }
        }

        try
        {
            var positions = await _gateway.GetOpenPositionsAsync().ConfigureAwait(false);
            var pos = positions.FirstOrDefault(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && p.Quantity != 0);
            if (pos is null) return Err($"no open live position for {symbol}");
            var side = pos.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy;
            var closeOrder = new Order
            {
                Symbol = symbol, Side = side, Type = OrderType.Market,
                Quantity = Math.Abs(pos.Quantity), ReduceOnly = true,
                MarketType = IsFutures ? TradingMarketType.FuturesUsdM : TradingMarketType.Spot,
                MarginMode = _marginMode,
                Leverage = IsFutures ? _leverage : null,
                // Close against the actual open side reported by the exchange.
                PositionSide = IsFutures ? pos.PositionSide : FuturesPositionSide.Both
            };
            await _gateway.PlaceOrderAsync(closeOrder).ConfigureAwait(false);
            OnFill?.Invoke(symbol, "close", Math.Abs(pos.Quantity), mid, Math.Abs(pos.Quantity) * mid, "live");
            return Json(new { ok = true, mode = "live", market = IsFutures ? "futures" : "spot", symbol });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    private string PaperFill(string symbol, OrderSide side, decimal qty, decimal price)
    {
        lock (_bookLock)
        {
            var signed = side == OrderSide.Buy ? qty : -qty;
            _paperPositions.TryGetValue(symbol, out var pos);

            // Spot is long-only: a sell can only reduce an existing holding, never go net-short.
            if (!IsFutures && side == OrderSide.Sell)
            {
                if (pos.Qty <= 0) return Err($"spot: no {symbol} holding to sell");
                if (qty > pos.Qty) { qty = pos.Qty; signed = -qty; }
            }

            var newQty = pos.Qty + signed;

            decimal realized = 0m;
            if (pos.Qty != 0 && Math.Sign(signed) != Math.Sign(pos.Qty))
            {
                // Reducing/closing — realize P&L on the overlap.
                var closedQty = Math.Min(Math.Abs(signed), Math.Abs(pos.Qty));
                realized = (price - pos.Avg) * closedQty * Math.Sign(pos.Qty);
                _paperRealizedPnl += realized;
                if (realized < 0) _risk.RecordLoss(Math.Abs(realized));
            }

            // Average entry only changes when increasing exposure in the same direction.
            decimal newAvg = pos.Avg;
            if (pos.Qty == 0 || Math.Sign(signed) == Math.Sign(pos.Qty))
                newAvg = pos.Qty == 0 ? price : ((pos.Avg * Math.Abs(pos.Qty)) + (price * qty)) / (Math.Abs(pos.Qty) + qty);
            else if (newQty == 0)
                newAvg = 0m;

            _paperCash -= signed * price; // buying spends cash, selling adds
            if (newQty == 0) _paperPositions.Remove(symbol);
            else _paperPositions[symbol] = (newQty, newAvg);

            OnFill?.Invoke(symbol, side == OrderSide.Buy ? "buy" : "sell", qty, price, qty * price, "paper");
            OnEvent?.Invoke(new AgentEvent(AgentEventKind.ToolResult, "FILL (paper)",
                $"{(side == OrderSide.Buy ? "buy" : "sell")} {qty:0.######} {symbol} @ {price:0.####}"));
            return Json(new { ok = true, mode = "paper", symbol, side = side == OrderSide.Buy ? "buy" : "sell", qty = Round(qty), price = Round(price), realized_pnl = Round(realized), cash = Round(_paperCash) });
        }
    }

    // ── Futures helpers (mirror TradingBot) ────────────────────────────────────

    /// <summary>PositionSide for a new entry. One-way → Both; hedge → Long/Short.</summary>
    private FuturesPositionSide EntryPositionSide(bool isBuy) =>
        _isHedgeMode
            ? (isBuy ? FuturesPositionSide.Long : FuturesPositionSide.Short)
            : FuturesPositionSide.Both;

    /// <summary>Apply leverage + margin mode once per symbol; biggest failures are non-fatal.</summary>
    private async Task EnsureFuturesLeverageAsync(string symbol)
    {
        if (!_leverageApplied.Add(symbol)) return;
        try { await _gateway.SetLeverageAsync(symbol, _leverage).ConfigureAwait(false); }
        catch (Exception ex) { OnEvent?.Invoke(new AgentEvent(AgentEventKind.Error, "leverage", ex.Message)); }
        try { await _gateway.SetMarginModeAsync(symbol, _marginMode).ConfigureAwait(false); }
        catch (Exception ex) { OnEvent?.Invoke(new AgentEvent(AgentEventKind.Error, "margin", ex.Message)); }
    }

    /// <summary>Binance/Bybit/OKX emit different hedge-vs-one-way mismatch messages.</summary>
    private static bool IsPositionSideMismatch(Exception ex)
    {
        var msg = ex.Message?.ToLowerInvariant() ?? string.Empty;
        return msg.Contains("position side") || msg.Contains("position mode")
            || msg.Contains("position idx") || msg.Contains("51124");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Public market data — best bid/ask via the order book; no API keys needed.</summary>
    private async Task<(decimal Bid, decimal Ask, decimal Mid)> GetQuoteAsync(string symbol, CancellationToken ct)
    {
        var book = await _gateway.GetOrderBookAsync(symbol, 5).ConfigureAwait(false);
        var bid = book.Bids.Count > 0 ? book.Bids[0].Price : 0m;
        var ask = book.Asks.Count > 0 ? book.Asks[0].Price : 0m;
        var mid = bid > 0 && ask > 0 ? (bid + ask) / 2m : Math.Max(bid, ask);
        return (bid, ask, mid);
    }

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static decimal Num(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDecimal() : 0m;

    private static decimal Round(decimal v) => Math.Round(v, 8, MidpointRounding.AwayFromZero);
    private static string Json(object o) => JsonSerializer.Serialize(o);
    private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg });
}
