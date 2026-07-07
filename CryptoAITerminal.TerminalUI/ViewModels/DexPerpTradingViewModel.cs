using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.Core.Trading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Drives the paper PERP side of the DEX terminal. Owns a <see cref="DexPerpPaperEngine"/>
/// and a <see cref="DexPerpMarketSimulator"/>; a 1s timer advances the mark price, ticks
/// the engine (firing resting orders, marking the position, checking liquidation) and
/// republishes bindable state. The mark is anchored to the real spot price of the token
/// selected in the SWAP list. Everything here is paper — no value leaves the app.
/// </summary>
public sealed class DexPerpTradingViewModel : ReactiveObject, IDisposable
{
    private readonly Func<decimal> _anchorPrice;
    private readonly Func<string> _symbol;
    private readonly DexPerpPaperEngine _engine = new(startingBalance: 10_000m);
    private readonly DexPerpSessionStore _store = new();
    private DexPerpMarketSimulator _sim;
    private readonly DispatcherTimer _timer;

    private bool _isActive;
    private string _lastSymbol = string.Empty;
    private int _fundingCounter;
    private int _lastFillCount;
    private int _lastSavedFills = -1;
    private int _lastSavedOrders = -1;

    private string _side = "Long";
    private string _orderType = "Market";
    private string _marginMode = "Isolated";
    private int _leverage = 10;
    private decimal _sizeTokens = 1m;
    private decimal _triggerPrice;
    private decimal _takeProfit;
    private decimal _stopLoss;
    private int _dcaLegs = 3;
    private decimal _dcaStepPercent = 5m;
    private decimal _gridLower;
    private decimal _gridUpper;
    private int _gridLevels = 5;
    private decimal _trailingDistance;
    private string _statusMessage = "Paper PERP desk ready. Arm an order to simulate a fill.";

    public DexPerpTradingViewModel(Func<decimal> anchorPrice, Func<string> symbol)
    {
        _anchorPrice = anchorPrice;
        _symbol = symbol;
        _sim = new DexPerpMarketSimulator(ResolveAnchor());

        // Restore a prior paper session; anchor the mark to the restored position so it
        // isn't marked against a stale/empty spot price on the first tick.
        _engine.LoadSnapshot(_store.Load());
        if (_engine.Position is { MarkPrice: > 0m } restored)
        {
            _sim = new DexPerpMarketSimulator(restored.MarkPrice);
        }

        SelectSideCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) Side = v; }, outputScheduler: App.UiScheduler);
        SelectOrderTypeCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) OrderType = v; }, outputScheduler: App.UiScheduler);
        SelectMarginModeCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) MarginMode = v; }, outputScheduler: App.UiScheduler);
        PlaceOrderCommand = ReactiveCommand.Create(PlaceOrder, outputScheduler: App.UiScheduler);
        CloseCommand = ReactiveCommand.Create(() => { _engine.Close(); StatusMessage = "Position closed."; Republish(); }, outputScheduler: App.UiScheduler);
        ReverseCommand = ReactiveCommand.Create(() => { _engine.Reverse(); StatusMessage = "Position reversed."; Republish(); }, outputScheduler: App.UiScheduler);
        CancelOrderCommand = ReactiveCommand.Create<string>(id => { _engine.CancelOrder(id); Republish(); }, outputScheduler: App.UiScheduler);
        ClosePartialCommand = ReactiveCommand.Create<string>(ClosePartial, outputScheduler: App.UiScheduler);
        ArmTrailingCommand = ReactiveCommand.Create(ArmTrailing, outputScheduler: App.UiScheduler);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        RebuildBook();
        Republish();
    }

    // ── Activation (only work while the PERP zone is visible) ─────────────────
    public void SetActive(bool active)
    {
        _isActive = active;
        if (active)
        {
            ReanchorIfNeeded(force: true);
            Republish();
        }
    }

    private decimal ResolveAnchor()
    {
        var price = _anchorPrice();
        return price > 0m ? price : 100m;
    }

    private void ReanchorIfNeeded(bool force = false)
    {
        var symbol = _symbol();
        var anchor = ResolveAnchor();

        // Re-anchor when the token changes (or on first activation) but never mid-trade,
        // so an existing paper position keeps a coherent mark.
        var symbolChanged = !string.Equals(symbol, _lastSymbol, StringComparison.OrdinalIgnoreCase);
        if ((force || symbolChanged) && _engine.Position is null && _engine.WorkingOrders.Count == 0)
        {
            _sim = new DexPerpMarketSimulator(anchor, seed: symbol.GetHashCode());
            _lastSymbol = symbol;
            RebuildBook();
        }
    }

    private void OnTick()
    {
        if (!_isActive)
        {
            return;
        }

        ReanchorIfNeeded();

        var mark = _sim.NextMark();
        _engine.Tick(mark);

        // Funding settles every 8 ticks (paper cadence).
        if (++_fundingCounter >= 8)
        {
            _fundingCounter = 0;
            _engine.AccrueFunding(_sim.FundingRate);
        }

        RebuildBook();
        Republish();
    }

    // ── Ticket state ──────────────────────────────────────────────────────────
    public string Side { get => _side; set { this.RaiseAndSetIfChanged(ref _side, value); this.RaisePropertyChanged(nameof(IsLong)); this.RaisePropertyChanged(nameof(IsShort)); } }
    public bool IsLong => string.Equals(_side, "Long", StringComparison.OrdinalIgnoreCase);
    public bool IsShort => string.Equals(_side, "Short", StringComparison.OrdinalIgnoreCase);

    public string OrderType
    {
        get => _orderType;
        set
        {
            this.RaiseAndSetIfChanged(ref _orderType, value);
            this.RaisePropertyChanged(nameof(IsMarket));
            this.RaisePropertyChanged(nameof(IsLimit));
            this.RaisePropertyChanged(nameof(IsStop));
            this.RaisePropertyChanged(nameof(IsDca));
            this.RaisePropertyChanged(nameof(IsGrid));
            this.RaisePropertyChanged(nameof(NeedsTriggerPrice));
            this.RaisePropertyChanged(nameof(PlaceButtonText));
        }
    }
    public bool IsMarket => Is("Market");
    public bool IsLimit => Is("Limit");
    public bool IsStop => Is("Stop");
    public bool IsDca => Is("DCA");
    public bool IsGrid => Is("Grid");
    public bool NeedsTriggerPrice => IsLimit || IsStop || IsDca;
    private bool Is(string v) => string.Equals(_orderType, v, StringComparison.OrdinalIgnoreCase);

    public string MarginMode { get => _marginMode; set { this.RaiseAndSetIfChanged(ref _marginMode, value); this.RaisePropertyChanged(nameof(IsCross)); this.RaisePropertyChanged(nameof(IsIsolated)); } }
    public bool IsCross => string.Equals(_marginMode, "Cross", StringComparison.OrdinalIgnoreCase);
    public bool IsIsolated => string.Equals(_marginMode, "Isolated", StringComparison.OrdinalIgnoreCase);

    public int Leverage { get => _leverage; set { this.RaiseAndSetIfChanged(ref _leverage, Math.Clamp(value, 1, 125)); this.RaisePropertyChanged(nameof(LeverageLabel)); this.RaisePropertyChanged(nameof(EstMarginLabel)); } }
    public string LeverageLabel => $"{_leverage}×";

    public decimal SizeTokens { get => _sizeTokens; set { this.RaiseAndSetIfChanged(ref _sizeTokens, Math.Max(0m, value)); this.RaisePropertyChanged(nameof(EstMarginLabel)); this.RaisePropertyChanged(nameof(NotionalLabel)); } }
    public decimal TriggerPrice { get => _triggerPrice; set => this.RaiseAndSetIfChanged(ref _triggerPrice, value); }
    public decimal TakeProfit { get => _takeProfit; set => this.RaiseAndSetIfChanged(ref _takeProfit, value); }
    public decimal StopLoss { get => _stopLoss; set => this.RaiseAndSetIfChanged(ref _stopLoss, value); }
    public int DcaLegs { get => _dcaLegs; set => this.RaiseAndSetIfChanged(ref _dcaLegs, Math.Clamp(value, 2, 20)); }
    public decimal DcaStepPercent { get => _dcaStepPercent; set => this.RaiseAndSetIfChanged(ref _dcaStepPercent, Math.Max(0.1m, value)); }
    public decimal GridLower { get => _gridLower; set => this.RaiseAndSetIfChanged(ref _gridLower, value); }
    public decimal GridUpper { get => _gridUpper; set => this.RaiseAndSetIfChanged(ref _gridUpper, value); }
    public int GridLevels { get => _gridLevels; set => this.RaiseAndSetIfChanged(ref _gridLevels, Math.Clamp(value, 2, 50)); }
    public decimal TrailingDistance { get => _trailingDistance; set => this.RaiseAndSetIfChanged(ref _trailingDistance, Math.Max(0m, value)); }

    public string StatusMessage { get => _statusMessage; private set => this.RaiseAndSetIfChanged(ref _statusMessage, value); }

    public string PlaceButtonText => IsMarket ? $"OPEN {Side.ToUpperInvariant()} (MARKET)"
        : IsGrid ? "DEPLOY GRID"
        : IsDca ? "ARM DCA LADDER"
        : $"ARM {OrderType.ToUpperInvariant()} {Side.ToUpperInvariant()}";

    public decimal EstMargin => _leverage <= 0 ? 0m : _sizeTokens * _sim.Mark / _leverage;
    public string EstMarginLabel => $"{EstMargin:N2} USDT";
    public string NotionalLabel => $"{_sizeTokens * _sim.Mark:N2} USDT";

    // ── Commands ──────────────────────────────────────────────────────────────
    public ReactiveCommand<string, Unit> SelectSideCommand { get; }
    public ReactiveCommand<string, Unit> SelectOrderTypeCommand { get; }
    public ReactiveCommand<string, Unit> SelectMarginModeCommand { get; }
    public ReactiveCommand<Unit, Unit> PlaceOrderCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> ReverseCommand { get; }
    public ReactiveCommand<string, Unit> CancelOrderCommand { get; }
    public ReactiveCommand<string, Unit> ClosePartialCommand { get; }
    public ReactiveCommand<Unit, Unit> ArmTrailingCommand { get; }

    private void ClosePartial(string? pct)
    {
        var fraction = pct switch { "25" => 0.25m, "50" => 0.5m, "75" => 0.75m, _ => 0m };
        if (fraction <= 0m)
        {
            return;
        }

        _engine.ClosePartial(fraction);
        StatusMessage = $"Closed {pct}% of the position.";
        Republish();
    }

    private void ArmTrailing()
    {
        if (_engine.Position is null)
        {
            StatusMessage = "Open a position before arming a trailing stop.";
            return;
        }
        if (_trailingDistance <= 0m)
        {
            StatusMessage = "Set a trailing distance above zero.";
            return;
        }

        _engine.ArmTrailingStop(_trailingDistance);
        StatusMessage = $"Trailing stop armed · {Format(_trailingDistance)} distance.";
        Republish();
        SaveSession();
    }

    private void PlaceOrder()
    {
        if (_sizeTokens <= 0m && !IsGrid)
        {
            StatusMessage = "Enter a position size above zero.";
            return;
        }

        var side = IsLong ? PerpSide.Long : PerpSide.Short;
        var margin = IsCross ? PerpMarginMode.Cross : PerpMarginMode.Isolated;
        var mark = _sim.Mark;
        var trigger = _triggerPrice > 0m ? _triggerPrice : mark;

        switch (_orderType.ToUpperInvariant())
        {
            case "MARKET":
                _engine.PlaceMarket(side, _sizeTokens, _leverage, margin, mark);
                ArmTpSl(side, margin);
                StatusMessage = $"Market {side} filled @ {Format(mark)}.";
                break;
            case "LIMIT":
                _engine.PlaceLimit(side, trigger, _sizeTokens, _leverage, margin);
                StatusMessage = $"Limit {side} armed @ {Format(trigger)}.";
                break;
            case "STOP":
                _engine.PlaceStop(side, trigger, _sizeTokens, _leverage, margin);
                StatusMessage = $"Stop {side} armed @ {Format(trigger)}.";
                break;
            case "DCA":
                var legs = _engine.CreateDcaPlan(side, _sizeTokens, _dcaLegs, trigger, _dcaStepPercent, _leverage, margin);
                StatusMessage = $"DCA ladder armed: {legs.Count} legs from {Format(trigger)}, {_dcaStepPercent:0.##}% step.";
                break;
            case "GRID":
                var lower = _gridLower > 0m ? _gridLower : mark * 0.9m;
                var upper = _gridUpper > 0m ? _gridUpper : mark * 1.1m;
                var perLevel = _sizeTokens > 0m ? _sizeTokens : 1m;
                var rungs = _engine.CreateGridPlan(lower, upper, _gridLevels, perLevel, mark, _leverage, margin);
                StatusMessage = $"Grid deployed: {rungs.Count} rungs across {Format(lower)}–{Format(upper)}.";
                break;
        }

        Republish();
    }

    private void ArmTpSl(PerpSide side, PerpMarginMode margin)
    {
        var exit = side == PerpSide.Long ? PerpSide.Short : PerpSide.Long;
        if (_takeProfit > 0m)
        {
            _engine.PlaceLimit(exit, _takeProfit, _sizeTokens, _leverage, margin);
        }
        if (_stopLoss > 0m)
        {
            _engine.PlaceStop(exit, _stopLoss, _sizeTokens, _leverage, margin);
        }
    }

    // ── Live telemetry ────────────────────────────────────────────────────────
    public ObservableCollection<DexPerpBookRowViewModel> AskLevels { get; } = new();
    public ObservableCollection<DexPerpBookRowViewModel> BidLevels { get; } = new();
    public ObservableCollection<DexPerpPositionRowViewModel> Positions { get; } = new();
    public ObservableCollection<DexPerpOrderRowViewModel> Orders { get; } = new();
    public ObservableCollection<DexPerpFillRowViewModel> FillsRows { get; } = new();

    /// <summary>Current simulated mark price (for the AI assistant / external readers).</summary>
    public decimal MarkPrice => _sim.Mark;
    /// <summary>Current paper account equity.</summary>
    public decimal Equity => _engine.AccountEquity;

    public bool ShowPositionsPlaceholder => Positions.Count == 0;
    public bool ShowOrdersPlaceholder => Orders.Count == 0;
    public bool ShowFillsPlaceholder => FillsRows.Count == 0;
    public bool HasPosition => _engine.Position is not null;

    public string MarkPriceLabel => Format(_sim.Mark);
    public string MidPriceLabel => Format(_sim.Mark);
    public string FundingRateLabel => $"{_sim.FundingRate * 100m:+0.####;-0.####;0}%";
    public string FundingBrush => _sim.FundingRate >= 0m ? "#f4b860" : "#3ddc84";
    public string SettlementLabel => "every 8h (paper)";
    public string FundingCountdownLabel => $"{Math.Max(0, 8 - _fundingCounter)}s";
    public string FundingPaidLabel => $"{_engine.FundingPaid:+0.00;-0.00;0.00}";
    public string TrailingStopLabel => _engine.Position is { TrailingDistance: > 0m } p ? Format(p.TrailingStopPrice) : "off";
    public string EquityCurvePoints => BuildEquityCurve();
    public string EquityLabel => $"{_engine.AccountEquity:N2} USDT";
    public string RealizedPnlLabel => $"{_engine.RealizedPnl:+0.00;-0.00;0.00} USDT";
    public string RealizedPnlBrush => _engine.RealizedPnl >= 0m ? "#3ddc84" : "#ff6b6b";

    public string PositionSideLabel => _engine.Position?.Side.ToString().ToUpperInvariant() ?? "FLAT";
    public string PositionSideBrush => _engine.Position is null ? "#5a7a94" : _engine.Position.Side == PerpSide.Long ? "#3ddc84" : "#ff6b6b";
    public string PositionSizeLabel => _engine.Position is null ? "--" : $"{_engine.Position.Size:0.####}";
    public string EntryLabel => _engine.Position is null ? "--" : Format(_engine.Position.EntryPrice);
    public string LiquidationLabel => _engine.Position is null ? "--" : Format(_engine.Position.LiquidationPrice);
    public string MarginLabel => _engine.Position is null ? "--" : $"{_engine.Position.Margin:N2}";
    public string UpnlLabel => _engine.Position is null ? "--" : $"{_engine.Position.UnrealizedPnl:+0.00;-0.00;0.00}";
    public string UpnlBrush => (_engine.Position?.UnrealizedPnl ?? 0m) >= 0m ? "#3ddc84" : "#ff6b6b";
    public string RoeLabel => _engine.Position is null ? "--" : $"{_engine.Position.Roe * 100m:+0.0;-0.0;0.0}%";

    private void RebuildBook()
    {
        var (asks, bids) = _sim.BuildOrderBook(depth: 12);
        var maxCum = Math.Max(
            asks.Count > 0 ? asks[^1].Cumulative : 0m,
            bids.Count > 0 ? bids[^1].Cumulative : 0m);

        SyncBook(AskLevels, asks, maxCum, isAsk: true);
        SyncBook(BidLevels, bids, maxCum, isAsk: false);
    }

    private static void SyncBook(ObservableCollection<DexPerpBookRowViewModel> target, System.Collections.Generic.IReadOnlyList<DexPerpBookLevel> src, decimal maxCum, bool isAsk)
    {
        // In-place update to avoid book flicker.
        while (target.Count > src.Count) target.RemoveAt(target.Count - 1);
        for (var i = 0; i < src.Count; i++)
        {
            var width = maxCum <= 0m ? 0d : (double)(src[i].Cumulative / maxCum) * 120d;
            var row = new DexPerpBookRowViewModel(src[i].Price, src[i].Size, src[i].Cumulative, width, isAsk);
            if (i < target.Count) target[i] = row; else target.Add(row);
        }
    }

    private void Republish()
    {
        // Positions
        Positions.Clear();
        if (_engine.Position is { } pos)
        {
            Positions.Add(new DexPerpPositionRowViewModel(pos, _symbol()));
        }

        // Working orders
        Orders.Clear();
        foreach (var order in _engine.WorkingOrders)
        {
            Orders.Add(new DexPerpOrderRowViewModel(order));
        }

        // Fills (rebuild only when count changed)
        if (_engine.Fills.Count != _lastFillCount)
        {
            _lastFillCount = _engine.Fills.Count;
            FillsRows.Clear();
            foreach (var fill in _engine.Fills.AsEnumerable().Reverse().Take(100))
            {
                FillsRows.Add(new DexPerpFillRowViewModel(fill));
            }
        }

        this.RaisePropertyChanged(nameof(ShowPositionsPlaceholder));
        this.RaisePropertyChanged(nameof(ShowOrdersPlaceholder));
        this.RaisePropertyChanged(nameof(ShowFillsPlaceholder));
        this.RaisePropertyChanged(nameof(HasPosition));
        this.RaisePropertyChanged(nameof(MarkPriceLabel));
        this.RaisePropertyChanged(nameof(MidPriceLabel));
        this.RaisePropertyChanged(nameof(FundingRateLabel));
        this.RaisePropertyChanged(nameof(FundingBrush));
        this.RaisePropertyChanged(nameof(EquityLabel));
        this.RaisePropertyChanged(nameof(RealizedPnlLabel));
        this.RaisePropertyChanged(nameof(RealizedPnlBrush));
        this.RaisePropertyChanged(nameof(PositionSideLabel));
        this.RaisePropertyChanged(nameof(PositionSideBrush));
        this.RaisePropertyChanged(nameof(PositionSizeLabel));
        this.RaisePropertyChanged(nameof(EntryLabel));
        this.RaisePropertyChanged(nameof(LiquidationLabel));
        this.RaisePropertyChanged(nameof(MarginLabel));
        this.RaisePropertyChanged(nameof(UpnlLabel));
        this.RaisePropertyChanged(nameof(UpnlBrush));
        this.RaisePropertyChanged(nameof(RoeLabel));
        this.RaisePropertyChanged(nameof(EstMarginLabel));
        this.RaisePropertyChanged(nameof(NotionalLabel));
        this.RaisePropertyChanged(nameof(FundingCountdownLabel));
        this.RaisePropertyChanged(nameof(FundingPaidLabel));
        this.RaisePropertyChanged(nameof(TrailingStopLabel));
        this.RaisePropertyChanged(nameof(EquityCurvePoints));

        // Persist on any structural change (a new fill or working-order set).
        if (_engine.Fills.Count != _lastSavedFills || _engine.WorkingOrders.Count != _lastSavedOrders)
        {
            _lastSavedFills = _engine.Fills.Count;
            _lastSavedOrders = _engine.WorkingOrders.Count;
            SaveSession();
        }
    }

    private void SaveSession()
    {
        var snapshot = _engine.Export();
        _ = Task.Run(() => _store.Save(snapshot));
    }

    private string BuildEquityCurve()
    {
        var pts = _engine.EquityCurve;
        if (pts.Count < 2)
        {
            return string.Empty;
        }

        var min = pts.Min();
        var max = pts.Max();
        var range = Math.Max(max - min, 0.0001m);
        const double w = 300d, h = 34d;
        return string.Join(' ', pts.Select((v, i) =>
        {
            var x = w * i / (pts.Count - 1);
            var y = h - (double)((v - min) / range) * h;
            return $"{x.ToString("0.#", CultureInfo.InvariantCulture)},{y.ToString("0.#", CultureInfo.InvariantCulture)}";
        }));
    }

    private string Format(decimal price) => DexPerpFormat.Price(price);

    public void Dispose()
    {
        _timer.Stop();
    }
}

/// <summary>Number formatting shared by the paper-PERP row view models.</summary>
internal static class DexPerpFormat
{
    public static string Price(decimal price) => price switch
    {
        >= 1000m => $"{price:N2}",
        >= 1m => $"{price:N4}",
        >= 0.0001m => $"{price:N6}",
        _ => price.ToString("N8", CultureInfo.InvariantCulture)
    };
}

public sealed class DexPerpBookRowViewModel : ReactiveObject
{
    public DexPerpBookRowViewModel(decimal price, decimal size, decimal cumulative, double depthWidth, bool isAsk)
    {
        PriceLabel = DexPerpFormat.Price(price);
        SizeLabel = size.ToString("0.###", CultureInfo.InvariantCulture);
        CumulativeLabel = cumulative.ToString("0.#", CultureInfo.InvariantCulture);
        DepthWidth = depthWidth;
        PriceBrush = isAsk ? "#ff7070" : "#3ddc84";
        DepthBrush = isAsk ? "#12ff6b6b" : "#123ddc84";
    }

    public string PriceLabel { get; }
    public string SizeLabel { get; }
    public string CumulativeLabel { get; }
    public double DepthWidth { get; }
    public string PriceBrush { get; }
    public string DepthBrush { get; }
}

public sealed class DexPerpPositionRowViewModel : ReactiveObject
{
    public DexPerpPositionRowViewModel(DexPerpPosition p, string symbol)
    {
        Symbol = symbol;
        SideLabel = p.Side.ToString().ToUpperInvariant();
        SideBrush = p.Side == PerpSide.Long ? "#3ddc84" : "#ff6b6b";
        SizeLabel = p.Size.ToString("0.####", CultureInfo.InvariantCulture);
        EntryLabel = DexPerpFormat.Price(p.EntryPrice);
        MarkLabel = DexPerpFormat.Price(p.MarkPrice);
        LiqLabel = DexPerpFormat.Price(p.LiquidationPrice);
        MarginLabel = p.Margin.ToString("N2", CultureInfo.InvariantCulture);
        UpnlLabel = p.UnrealizedPnl.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        UpnlBrush = p.UnrealizedPnl >= 0m ? "#3ddc84" : "#ff6b6b";
        RoeLabel = (p.Roe * 100m).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%";
        LeverageLabel = $"{p.Leverage}× {p.MarginMode.ToString().ToUpperInvariant()}";
    }

    public string Symbol { get; }
    public string SideLabel { get; }
    public string SideBrush { get; }
    public string SizeLabel { get; }
    public string EntryLabel { get; }
    public string MarkLabel { get; }
    public string LiqLabel { get; }
    public string MarginLabel { get; }
    public string UpnlLabel { get; }
    public string UpnlBrush { get; }
    public string RoeLabel { get; }
    public string LeverageLabel { get; }
}

public sealed class DexPerpOrderRowViewModel : ReactiveObject
{
    public DexPerpOrderRowViewModel(DexPerpWorkingOrder o)
    {
        Id = o.Id;
        SideLabel = o.Side.ToString().ToUpperInvariant();
        SideBrush = o.Side == PerpSide.Long ? "#3ddc84" : "#ff6b6b";
        TypeLabel = o.PlanId is null ? o.Kind.ToString().ToUpperInvariant()
            : o.PlanId.StartsWith("GRID", StringComparison.OrdinalIgnoreCase) ? "GRID"
            : o.PlanId.StartsWith("DCA", StringComparison.OrdinalIgnoreCase) ? "DCA"
            : o.Kind.ToString().ToUpperInvariant();
        TriggerLabel = DexPerpFormat.Price(o.TriggerPrice);
        SizeLabel = o.SizeTokens.ToString("0.####", CultureInfo.InvariantCulture);
        StatusLabel = o.Status.ToString().ToUpperInvariant();
    }

    public string Id { get; }
    public string SideLabel { get; }
    public string SideBrush { get; }
    public string TypeLabel { get; }
    public string TriggerLabel { get; }
    public string SizeLabel { get; }
    public string StatusLabel { get; }
}

public sealed class DexPerpFillRowViewModel : ReactiveObject
{
    public DexPerpFillRowViewModel(DexPerpFill f)
    {
        SideLabel = f.Side.ToString().ToUpperInvariant();
        SideBrush = f.Side == PerpSide.Long ? "#3ddc84" : "#ff6b6b";
        KindLabel = f.Kind;
        KindBrush = f.Kind switch { "LIQ" => "#ff6b6b", "CLOSE" => "#f4b860", _ => "#8fa3b8" };
        PriceLabel = DexPerpFormat.Price(f.Price);
        SizeLabel = f.SizeTokens.ToString("0.####", CultureInfo.InvariantCulture);
        TimeLabel = f.TimeUtc.ToLocalTime().ToString("HH:mm:ss");
    }

    public string SideLabel { get; }
    public string SideBrush { get; }
    public string KindLabel { get; }
    public string KindBrush { get; }
    public string PriceLabel { get; }
    public string SizeLabel { get; }
    public string TimeLabel { get; }
}
