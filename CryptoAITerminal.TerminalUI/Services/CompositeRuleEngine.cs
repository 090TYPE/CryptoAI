using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Evaluates a list of <see cref="CompositeRule"/> objects on a periodic timer
/// and dispatches actions when conditions are met.
///
/// Feed market data via <see cref="FeedMarketData"/> and candle closes via
/// <see cref="FeedCandle"/>.  The caller wires the On* callbacks before calling
/// <see cref="Start"/>.
/// </summary>
public sealed class CompositeRuleEngine : IDisposable
{
    // ─── constants ────────────────────────────────────────────────────────────
    private const int MaxHistory = 210;   // enough for MA(200)
    private const int EvalIntervalSec = 10;

    // ─── shared rule list (owned by caller) ──────────────────────────────────
    private readonly List<CompositeRule> _rules;

    // ─── market data state ────────────────────────────────────────────────────
    private readonly Dictionary<string, List<decimal>> _closes  = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<decimal>> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal>       _lastPrice   = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal>       _prevPrice   = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal>       _price24hAgo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal>       _fundingRate = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Symbol, decimal Entry, decimal PnlPct)> _openPositions = [];

    // ─── timer ───────────────────────────────────────────────────────────────
    private readonly DispatcherTimer _timer;

    // ─── action callbacks ────────────────────────────────────────────────────
    /// <summary>Called when "DCA Buy" fires. Args: symbol, usd-amount.</summary>
    public Action<string, decimal>? OnStartDcaBuy;
    /// <summary>Called when "Move Stop to Breakeven" fires. Arg: symbol.</summary>
    public Action<string>? OnMoveStopToBreakeven;
    /// <summary>Called when "Start Funding Arb" fires. Arg: symbol.</summary>
    public Action<string>? OnStartFundingArb;
    /// <summary>Called when "Pause Grid Bot" fires. Arg: symbol (ignored for now).</summary>
    public Action<string>? OnPauseGrid;
    /// <summary>Called when "Resume Grid Bot" fires. Arg: symbol.</summary>
    public Action<string>? OnResumeGrid;
    /// <summary>Called when "Close All Positions" fires. Arg: symbol or "ALL".</summary>
    public Action<string>? OnCloseAllPositions;
    /// <summary>Called when "Notify" fires. Arg: message.</summary>
    public Action<string>? OnNotify;
    /// <summary>Called when PlaceMarketBuy/Sell fires. Args: symbol, side("Buy"/"Sell"), qty, exchange, market.</summary>
    public Action<string, string, decimal, string, string>? OnPlaceMarketOrder;
    /// <summary>Called when PlaceLimitBuy/Sell fires. Args: symbol, side, qty, limitPrice, exchange, market.</summary>
    public Action<string, string, decimal, decimal, string, string>? OnPlaceLimitOrder;

    // ─── observable status ────────────────────────────────────────────────────
    /// <summary>Fired on UI thread when a rule fires. Args: name, short summary.</summary>
    public event Action<string, string>? OnRuleTriggered;
    /// <summary>Fired on UI thread when engine start/stop status changes.</summary>
    public event Action<string>? OnStatusChanged;

    public bool IsRunning { get; private set; }

    // ─── ctor ─────────────────────────────────────────────────────────────────

    public CompositeRuleEngine(List<CompositeRule> sharedRules)
    {
        _rules = sharedRules;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(EvalIntervalSec)
        };
        _timer.Tick += (_, _) => EvaluateAll();
    }

    // ─── lifecycle ────────────────────────────────────────────────────────────

    public void Start()
    {
        _timer.Start();
        IsRunning = true;
        OnStatusChanged?.Invoke("Running");
    }

    public void Stop()
    {
        _timer.Stop();
        IsRunning = false;
        OnStatusChanged?.Invoke("Stopped");
    }

    public void Dispose() => _timer.Stop();

    // ─── data feed ────────────────────────────────────────────────────────────

    /// <summary>Feed current price + optional funding rate for a symbol.</summary>
    public void FeedMarketData(string symbol, decimal lastPrice, decimal fundingRate = 0m,
        decimal price24hAgo = 0m)
    {
        if (_lastPrice.TryGetValue(symbol, out var prev))
            _prevPrice[symbol] = prev;
        _lastPrice[symbol] = lastPrice;
        if (fundingRate != 0m) _fundingRate[symbol] = fundingRate;
        if (price24hAgo > 0m)  _price24hAgo[symbol] = price24hAgo;
    }

    /// <summary>Feed a closed candle (close price + volume) for RSI/MA/VolSMA computation.</summary>
    public void FeedCandle(string symbol, decimal close, decimal volume)
    {
        Push(_closes,  symbol, close,  MaxHistory);
        Push(_volumes, symbol, volume, MaxHistory);
        _lastPrice[symbol] = close;
    }

    /// <summary>Update the open-position snapshot used by P&amp;L conditions.</summary>
    public void UpdatePositions(IEnumerable<(string Symbol, decimal Entry, decimal PnlPct)> positions)
    {
        _openPositions.Clear();
        _openPositions.AddRange(positions);
    }

    /// <summary>Push fresh funding-rate data (e.g. from FundingArbitrageViewModel).</summary>
    public void UpdateFundingRates(IEnumerable<(string Symbol, decimal Rate)> rates)
    {
        foreach (var (sym, rate) in rates)
            _fundingRate[sym] = rate;
    }

    // ─── public evaluation (for "Test Now" button) ────────────────────────────

    public void EvaluateAll()
    {
        foreach (var rule in _rules.ToList())
        {
            if (rule.IsEnabled)
                TryFireRule(rule);
        }
    }

    // ─── evaluation internals ─────────────────────────────────────────────────

    private void TryFireRule(CompositeRule rule)
    {
        // Cooldown guard
        if (rule.LastTriggeredAt is not null)
        {
            if (rule.Cooldown == RuleCooldown.Once) return;
            if (rule.Cooldown != RuleCooldown.Unlimited)
            {
                var elapsed  = DateTime.UtcNow - rule.LastTriggeredAt.Value;
                var required = CooldownToSpan(rule.Cooldown);
                if (elapsed < required) return;
            }
        }

        // Condition evaluation
        var met = rule.Logic == ConditionLogic.And
            ? rule.Conditions.Count > 0 && rule.Conditions.All(EvaluateCondition)
            : rule.Conditions.Any(EvaluateCondition);

        if (!met) return;

        // Fire all actions
        rule.LastTriggeredAt = DateTime.UtcNow;
        rule.TriggerCount++;

        foreach (var action in rule.Actions)
            ExecuteAction(action);

        var summary = $"{rule.Name} fired #{rule.TriggerCount} at {DateTime.Now:HH:mm:ss}";
        OnRuleTriggered?.Invoke(rule.Name, summary);
    }

    private bool EvaluateCondition(RuleCondition c)
    {
        return c.Type switch
        {
            ConditionType.RsiBelow =>
                ComputeRsi(c.Symbol, (int)c.Param1) is { } rsi && rsi < c.Param2,

            ConditionType.RsiAbove =>
                ComputeRsi(c.Symbol, (int)c.Param1) is { } rsi && rsi > c.Param2,

            ConditionType.PriceAboveMa =>
                ComputeMa(c.Symbol, (int)c.Param1) is { } ma
                && _lastPrice.TryGetValue(c.Symbol, out var px1)
                && px1 > ma * c.Param2,

            ConditionType.PriceBelowMa =>
                ComputeMa(c.Symbol, (int)c.Param1) is { } ma
                && _lastPrice.TryGetValue(c.Symbol, out var px2)
                && px2 < ma * c.Param2,

            ConditionType.VolumeAboveSma =>
                ComputeVolSma(c.Symbol, (int)c.Param1) is { } vsma
                && _volumes.TryGetValue(c.Symbol, out var vols)
                && vols.Count > 0
                && vols[^1] > vsma * c.Param2,

            ConditionType.OpenPositionPnlAbove =>
                _openPositions.Any(p =>
                    IsMatchingSymbol(c.Symbol, p.Symbol) && p.PnlPct > c.Param1),

            ConditionType.OpenPositionPnlBelow =>
                _openPositions.Any(p =>
                    IsMatchingSymbol(c.Symbol, p.Symbol) && p.PnlPct < c.Param1),

            ConditionType.FundingRateAbove =>
                _fundingRate.TryGetValue(c.Symbol, out var fr) && fr > c.Param1,

            ConditionType.Price24hChangeAbove =>
                _lastPrice.TryGetValue(c.Symbol, out var px24a)
                && _price24hAgo.TryGetValue(c.Symbol, out var p24a)
                && p24a > 0m
                && (px24a - p24a) / p24a * 100m > c.Param1,

            ConditionType.Price24hChangeBelow =>
                _lastPrice.TryGetValue(c.Symbol, out var px24b)
                && _price24hAgo.TryGetValue(c.Symbol, out var p24b)
                && p24b > 0m
                && (px24b - p24b) / p24b * 100m < c.Param1,

            // Direct price level comparisons
            ConditionType.PriceAbove =>
                _lastPrice.TryGetValue(c.Symbol, out var pxA) && pxA > c.Param1,

            ConditionType.PriceBelow =>
                _lastPrice.TryGetValue(c.Symbol, out var pxB) && pxB < c.Param1,

            ConditionType.PriceCrossAbove =>
                _lastPrice.TryGetValue(c.Symbol, out var pxNow)
                && pxNow > c.Param1
                && _prevPrice.TryGetValue(c.Symbol, out var pxPrev)
                && pxPrev <= c.Param1,

            ConditionType.PriceCrossBelow =>
                _lastPrice.TryGetValue(c.Symbol, out var pxNow2)
                && pxNow2 < c.Param1
                && _prevPrice.TryGetValue(c.Symbol, out var pxPrev2)
                && pxPrev2 >= c.Param1,

            _ => false
        };
    }

    private void ExecuteAction(RuleAction a)
    {
        switch (a.Type)
        {
            case ActionType.StartDcaBuy:
                OnStartDcaBuy?.Invoke(a.Symbol, a.Amount);
                break;
            case ActionType.MoveStopToBreakeven:
                OnMoveStopToBreakeven?.Invoke(a.Symbol);
                break;
            case ActionType.StartFundingArb:
                OnStartFundingArb?.Invoke(a.Symbol);
                break;
            case ActionType.PauseGridBot:
                OnPauseGrid?.Invoke(a.Symbol);
                break;
            case ActionType.ResumeGridBot:
                OnResumeGrid?.Invoke(a.Symbol);
                break;
            case ActionType.CloseAllPositions:
                OnCloseAllPositions?.Invoke(a.Symbol);
                break;
            case ActionType.Notify:
                var msg = string.IsNullOrWhiteSpace(a.Message)
                    ? $"Composite rule triggered: {a.Symbol}"
                    : a.Message;
                OnNotify?.Invoke(msg);
                break;

            case ActionType.PlaceMarketBuy:
                OnPlaceMarketOrder?.Invoke(a.Symbol, "Buy", a.Amount, "Binance", "Spot");
                break;

            case ActionType.PlaceMarketSell:
                OnPlaceMarketOrder?.Invoke(a.Symbol, "Sell", a.Amount, "Binance", "Spot");
                break;

            case ActionType.PlaceLimitBuy:
                // Amount = qty, Message = limit price (string)
                if (decimal.TryParse(a.Message, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var limitBuy))
                    OnPlaceLimitOrder?.Invoke(a.Symbol, "Buy", a.Amount, limitBuy, "Binance", "Spot");
                break;

            case ActionType.PlaceLimitSell:
                if (decimal.TryParse(a.Message, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var limitSell))
                    OnPlaceLimitOrder?.Invoke(a.Symbol, "Sell", a.Amount, limitSell, "Binance", "Spot");
                break;
        }
    }

    // ─── indicator computation ────────────────────────────────────────────────

    private decimal? ComputeRsi(string symbol, int period)
    {
        if (!_closes.TryGetValue(symbol, out var closes) || closes.Count < period + 1)
            return null;

        var slice = closes.TakeLast(period + 1).ToArray();
        var gains = 0m; var losses = 0m;
        for (int i = 1; i < slice.Length; i++)
        {
            var diff = slice[i] - slice[i - 1];
            if (diff > 0) gains += diff; else losses -= diff;
        }
        var avgGain = gains  / period;
        var avgLoss = losses / period;
        if (avgLoss == 0m) return 100m;
        var rs = avgGain / avgLoss;
        return 100m - 100m / (1m + rs);
    }

    private decimal? ComputeMa(string symbol, int period)
    {
        if (!_closes.TryGetValue(symbol, out var closes) || closes.Count < period)
            return null;
        return closes.TakeLast(period).Average();
    }

    private decimal? ComputeVolSma(string symbol, int period)
    {
        if (!_volumes.TryGetValue(symbol, out var vols) || vols.Count < period)
            return null;
        return vols.TakeLast(period).Average();
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static bool IsMatchingSymbol(string conditionSymbol, string positionSymbol) =>
        string.Equals(conditionSymbol, "ANY", StringComparison.OrdinalIgnoreCase)
        || string.Equals(conditionSymbol, positionSymbol, StringComparison.OrdinalIgnoreCase);

    private static void Push(Dictionary<string, List<decimal>> dict, string key, decimal value, int maxLen)
    {
        if (!dict.TryGetValue(key, out var list))
        {
            list = new List<decimal>(maxLen + 1);
            dict[key] = list;
        }
        list.Add(value);
        if (list.Count > maxLen) list.RemoveAt(0);
    }

    private static TimeSpan CooldownToSpan(RuleCooldown cd) => cd switch
    {
        RuleCooldown.Seconds30 => TimeSpan.FromSeconds(30),
        RuleCooldown.Minutes1  => TimeSpan.FromMinutes(1),
        RuleCooldown.Minutes5  => TimeSpan.FromMinutes(5),
        RuleCooldown.Minutes15 => TimeSpan.FromMinutes(15),
        RuleCooldown.Hours1    => TimeSpan.FromHours(1),
        RuleCooldown.Hours4    => TimeSpan.FromHours(4),
        _                      => TimeSpan.Zero
    };
}
