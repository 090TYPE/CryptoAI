using System;
using System.Reactive;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.TradingDesk;

/// <summary>
/// The CEX desk's order ticket: BUY/SELL side, order type, spot/futures market mode, the manual
/// trading profile, the four price and size fields, slippage tolerance, and the futures leverage +
/// margin-mode pair — plus everything derived from them: the segmented-button brushes, the PLACE
/// ORDER caption and hint, the ticket notional and the fee estimates.
///
/// This view model OWNS that state; it is not a mirror of the shell.
///
/// What it deliberately does not own is the execution path. <c>PlacePrimaryOrderAsync</c>, the nine
/// <c>GetCex*BlockedReason</c> gates (they read the gateways, the risk manager and the wallet), the
/// presets and the working-order evaluator all stay in <see cref="MainWindowViewModel"/>. The four
/// blocked reasons the primary button needs are handed in as accessors, exactly as
/// <see cref="CexRightRailViewModel"/> takes its close/reverse reasons, and
/// <see cref="PlacePrimaryOrderCommand"/> is the shell's own command instance re-exposed through
/// <see cref="AttachCommands"/> — the shell still owns its error subscription.
///
/// The orchestration split is the one <see cref="ChartPanelViewModel"/> uses: a setter normalises
/// and writes its own value — early-outs included, so the orchestration fires exactly as often as
/// it used to — and then raises an event, and the shell's handler does verbatim what that setter's
/// tail used to do next.
/// </summary>
public sealed class OrderTicketViewModel : ReactiveObject
{
    private readonly Func<decimal> _currentTradePrice;
    private readonly Func<string> _baseAssetSymbol;
    private readonly Func<string> _cexMarketBuyBlockedReason;
    private readonly Func<string> _cexMarketSellBlockedReason;
    private readonly Func<string> _cexBuyLimitBlockedReason;
    private readonly Func<string> _cexSellLimitBlockedReason;

    private string _selectedOrderSide = "BUY";
    private string _selectedOrderType = "Limit";
    private string _selectedCexMarketMode = "Spot";
    private string _selectedTradingProfile = "Swing";

    private decimal _tradeQuantity = 0.001m;
    private decimal _limitPrice;
    private decimal _takeProfitPrice;
    private decimal _stopLossPrice;
    private decimal _slippageTolerancePercent = 0.50m;

    private int _manualFuturesLeverage = 3;
    private string _manualFuturesMarginMode = "Cross";

    /// <param name="currentTradePrice">The desk's mark price — the ticket prices its notional off it.</param>
    /// <param name="baseAssetSymbol">Base asset of the active symbol, shown in the button hint.</param>
    public OrderTicketViewModel(
        Func<decimal> currentTradePrice,
        Func<string> baseAssetSymbol,
        Func<string> cexMarketBuyBlockedReason,
        Func<string> cexMarketSellBlockedReason,
        Func<string> cexBuyLimitBlockedReason,
        Func<string> cexSellLimitBlockedReason)
    {
        _currentTradePrice = currentTradePrice;
        _baseAssetSymbol = baseAssetSymbol;
        _cexMarketBuyBlockedReason = cexMarketBuyBlockedReason;
        _cexMarketSellBlockedReason = cexMarketSellBlockedReason;
        _cexBuyLimitBlockedReason = cexBuyLimitBlockedReason;
        _cexSellLimitBlockedReason = cexSellLimitBlockedReason;

        SelectOrderSideCommand = ReactiveCommand.Create<string>(SelectOrderSide, outputScheduler: App.UiScheduler);
        SelectOrderTypeCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) SelectedOrderType = v; }, outputScheduler: App.UiScheduler);
        SelectMarketModeCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) SelectedCexMarketMode = v; }, outputScheduler: App.UiScheduler);
        SelectTradingProfileCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) SelectedTradingProfile = v; }, outputScheduler: App.UiScheduler);
        SelectMarginModeCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) ManualFuturesMarginMode = v; }, outputScheduler: App.UiScheduler);
    }

    // ═════════════════════════ shell wiring ═════════════════════════════════

    /// <summary>
    /// Raised after side / type / slippage / limit / take-profit / stop-loss has been written. The
    /// shell re-raises the ticket fan-out (<c>RaiseOrderTicketStateChanged</c>), most of whose names
    /// — Strategy*, Ai*, the guard chain — still live on the shell.
    /// </summary>
    public event Action? TicketStateChanged;

    /// <summary>
    /// Raised after <see cref="TradeQuantity"/> has been written. Size feeds exposure, so the shell
    /// re-raises the whole trading fan-out rather than only the ticket one — that is what the old
    /// setter did.
    /// </summary>
    public event Action? TradeQuantityChanged;

    /// <summary>
    /// Raised after <see cref="SelectedCexMarketMode"/> has actually changed. The shell re-snapshots
    /// the market for display, re-raises the mode and trading fan-outs and schedules the passive
    /// manual-mode sync.
    /// </summary>
    public event Action? MarketModeChanged;

    /// <summary>
    /// Raised after <see cref="ManualFuturesLeverage"/> has actually changed. The shell re-raises its
    /// mode summary, refreshes the right rail's leverage badge and clears the per-symbol futures
    /// setup cache so the next order re-sends leverage to the exchange.
    /// </summary>
    public event Action? LeverageChanged;

    /// <summary>
    /// Raised after <see cref="ManualFuturesMarginMode"/> has actually changed. Same shell tail as
    /// leverage, plus the margin-mode label.
    /// </summary>
    public event Action? MarginModeChanged;

    /// <summary>
    /// Raised after <see cref="SelectedTradingProfile"/> has actually changed, carrying the
    /// normalised profile. The shell re-raises the profile labels and, for Scalp, re-applies the
    /// scalp preset — which is why the preset itself could not move here.
    /// </summary>
    public event Action<string>? TradingProfileChanged;

    /// <summary>
    /// Raised by <see cref="SelectOrderSideCommand"/> with the normalised side. The shell re-points
    /// its preferred order-book side and re-raises the trading fan-out.
    /// </summary>
    public event Action<string>? OrderSideSelected;

    /// <summary>
    /// Second construction phase: PLACE ORDER is the shell's own command (it owns the thrown-exception
    /// subscription and the whole execution path behind it), and it does not exist yet when this view
    /// model is built.
    /// </summary>
    public void AttachCommands(ReactiveCommand<Unit, Unit> placePrimaryOrderCommand)
    {
        PlacePrimaryOrderCommand = placePrimaryOrderCommand;
    }

    /// <summary>
    /// Writes the profile field without normalising it and without running the setter's tail. The
    /// shell's <c>ApplyScalpPreset</c> is exactly what that tail calls, so going through the setter
    /// from inside it would re-enter the preset. Verbatim the assignment the shell used to make on
    /// its own backing field.
    /// </summary>
    public void SetTradingProfileWithoutOrchestration(string value) =>
        this.RaiseAndSetIfChanged(ref _selectedTradingProfile, value);

    /// <summary>Re-raises the side / type styling. Called from the shell's ticket fan-out.</summary>
    public void RaiseSideAndTypeChanged()
    {
        this.RaisePropertyChanged(nameof(IsLimitOrderType));
        this.RaisePropertyChanged(nameof(BuySideBackground));
        this.RaisePropertyChanged(nameof(SellSideBackground));
        this.RaisePropertyChanged(nameof(BuySideForeground));
        this.RaisePropertyChanged(nameof(SellSideForeground));
    }

    /// <summary>
    /// Re-raises the PLACE ORDER caption / hint and the fee estimates. Called from the shell's ticket
    /// and trading fan-outs, where those names used to sit.
    /// </summary>
    public void RaisePrimaryOrderCostChanged()
    {
        this.RaisePropertyChanged(nameof(PrimaryOrderButtonText));
        this.RaisePropertyChanged(nameof(PrimaryOrderButtonHint));
        this.RaisePropertyChanged(nameof(EstimatedTradingFee));
        this.RaisePropertyChanged(nameof(EstimatedTradingFeeLabel));
        this.RaisePropertyChanged(nameof(EstimatedNetworkFeeUsdt));
        this.RaisePropertyChanged(nameof(EstimatedNetworkFeeLabel));
        this.RaisePropertyChanged(nameof(EstimatedTotalCostLabel));
    }

    /// <summary>Re-raises the ticket notional. It is priced off the shell's mark price.</summary>
    public void RaiseTradeNotionalChanged()
    {
        this.RaisePropertyChanged(nameof(TradeNotional));
        this.RaisePropertyChanged(nameof(TradeNotionalLabel));
    }

    /// <summary>Re-raises the spot/futures flag. Called from the shell's trading and mode fan-outs.</summary>
    public void RaiseMarketModeChanged() => this.RaisePropertyChanged(nameof(IsManualFuturesMode));

    /// <summary>
    /// Re-raises PLACE ORDER enablement. Called from the shell's <c>RaiseCexActionStateChanged</c>,
    /// since the reason itself is computed by the shell's guard chain.
    /// </summary>
    public void RaisePrimaryOrderGuardChanged()
    {
        this.RaisePropertyChanged(nameof(CanPlacePrimaryOrder));
        this.RaisePropertyChanged(nameof(PrimaryOrderBlockedReason));
    }

    // ═════════════════════════ commands ═════════════════════════════════════

    public ReactiveCommand<string, Unit> SelectOrderSideCommand { get; }
    public ReactiveCommand<string, Unit> SelectOrderTypeCommand { get; }
    public ReactiveCommand<string, Unit> SelectMarketModeCommand { get; }
    public ReactiveCommand<string, Unit> SelectTradingProfileCommand { get; }
    public ReactiveCommand<string, Unit> SelectMarginModeCommand { get; }

    /// <summary>The shell's own PLACE ORDER command, re-exposed for the ticket's button.</summary>
    public ReactiveCommand<Unit, Unit> PlacePrimaryOrderCommand { get; private set; } = null!;

    private void SelectOrderSide(string? side)
    {
        var normalized = string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase) ? "SELL" : "BUY";
        SelectedOrderSide = normalized;
        OrderSideSelected?.Invoke(normalized);
    }

    // ═════════════════════════ side / type / mode / profile ═════════════════

    public string SelectedOrderSide
    {
        get => _selectedOrderSide;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedOrderSide, value);
            TicketStateChanged?.Invoke();
        }
    }

    public string SelectedOrderType
    {
        get => _selectedOrderType;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedOrderType, value);
            TicketStateChanged?.Invoke();
        }
    }

    public string SelectedCexMarketMode
    {
        get => _selectedCexMarketMode;
        set
        {
            var normalized = string.Equals(value, "Futures", StringComparison.OrdinalIgnoreCase) ? "Futures" : "Spot";
            if (string.Equals(_selectedCexMarketMode, normalized, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedCexMarketMode, normalized);
            MarketModeChanged?.Invoke();
        }
    }

    public string SelectedTradingProfile
    {
        get => _selectedTradingProfile;
        set
        {
            var normalized = value?.Trim() switch
            {
                var v when string.Equals(v, "Scalp", StringComparison.OrdinalIgnoreCase) => "Scalp",
                var v when string.Equals(v, "Aggro", StringComparison.OrdinalIgnoreCase) => "Aggro",
                _ => "Swing",
            };
            if (string.Equals(_selectedTradingProfile, normalized, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedTradingProfile, normalized);
            TradingProfileChanged?.Invoke(normalized);
        }
    }

    // ═════════════════════════ futures leverage / margin ════════════════════

    public int ManualFuturesLeverage
    {
        get => _manualFuturesLeverage;
        set
        {
            var normalized = Math.Max(1, value);
            if (_manualFuturesLeverage == normalized)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _manualFuturesLeverage, normalized);
            LeverageChanged?.Invoke();
        }
    }

    public string ManualFuturesMarginMode
    {
        get => _manualFuturesMarginMode;
        set
        {
            var normalized = string.Equals(value, "Isolated", StringComparison.OrdinalIgnoreCase) ? "Isolated" : "Cross";
            if (string.Equals(_manualFuturesMarginMode, normalized, StringComparison.Ordinal))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _manualFuturesMarginMode, normalized);
            MarginModeChanged?.Invoke();
        }
    }

    // ═════════════════════════ price / size fields ══════════════════════════

    public decimal TradeQuantity
    {
        get => _tradeQuantity;
        set
        {
            this.RaiseAndSetIfChanged(ref _tradeQuantity, value);
            TradeQuantityChanged?.Invoke();
        }
    }

    public decimal LimitPrice
    {
        get => _limitPrice;
        set
        {
            this.RaiseAndSetIfChanged(ref _limitPrice, value);
            TicketStateChanged?.Invoke();
        }
    }

    public decimal TakeProfitPrice
    {
        get => _takeProfitPrice;
        set
        {
            this.RaiseAndSetIfChanged(ref _takeProfitPrice, value);
            TicketStateChanged?.Invoke();
        }
    }

    public decimal StopLossPrice
    {
        get => _stopLossPrice;
        set
        {
            this.RaiseAndSetIfChanged(ref _stopLossPrice, value);
            TicketStateChanged?.Invoke();
        }
    }

    public decimal SlippageTolerancePercent
    {
        get => _slippageTolerancePercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _slippageTolerancePercent, value);
            TicketStateChanged?.Invoke();
        }
    }

    // ═════════════════════════ derived ══════════════════════════════════════

    public bool IsLimitOrderType => string.Equals(SelectedOrderType, "Limit", StringComparison.OrdinalIgnoreCase);
    public bool IsManualFuturesMode => string.Equals(SelectedCexMarketMode, "Futures", StringComparison.OrdinalIgnoreCase);
    public string BuySideBackground => GetOrderSideBackground("BUY");
    public string SellSideBackground => GetOrderSideBackground("SELL");
    public string BuySideForeground => GetOrderSideForeground("BUY");
    public string SellSideForeground => GetOrderSideForeground("SELL");
    public string PrimaryOrderButtonText => SelectedOrderSide == "SELL"
        ? IsLimitOrderType ? "PLACE SELL ORDER" : "SELL MARKET"
        : IsLimitOrderType ? "PLACE BUY ORDER" : "BUY MARKET";
    public string PrimaryOrderButtonHint => TradeNotional <= 0
        ? "Set price and quantity to prepare the ticket."
        : $"{TradeQuantity:0.0000} {_baseAssetSymbol()} for {TradeNotional:N2} USDT";
    public decimal TradeNotional => TradeQuantity * _currentTradePrice();
    public string TradeNotionalLabel => TradeNotional <= 0 ? "--" : $"~ {TradeNotional:N2} USDT";
    public decimal EstimatedTradingFee => TradeNotional <= 0 ? 0m : TradeNotional * 0.001m;
    public string EstimatedTradingFeeLabel => $"{EstimatedTradingFee:N2} USDT";
    public decimal EstimatedNetworkFeeUsdt => TradeNotional <= 0 ? 0m : Math.Max(0.20m, TradeNotional * 0.00015m);
    public string EstimatedNetworkFeeLabel => $"{EstimatedNetworkFeeUsdt:N2} USDT";
    public string EstimatedTotalCostLabel => $"{TradeNotional + EstimatedTradingFee + EstimatedNetworkFeeUsdt:N2} USDT";
    public bool CanPlacePrimaryOrder => string.IsNullOrWhiteSpace(PrimaryOrderBlockedReason);
    public string PrimaryOrderBlockedReason => GetPrimaryOrderBlockedReason();

    private string GetPrimaryOrderBlockedReason()
    {
        var isSell = string.Equals(SelectedOrderSide, "SELL", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(SelectedOrderType, "Market", StringComparison.OrdinalIgnoreCase))
        {
            return isSell ? _cexMarketSellBlockedReason() : _cexMarketBuyBlockedReason();
        }

        return isSell ? _cexSellLimitBlockedReason() : _cexBuyLimitBlockedReason();
    }

    private string GetOrderSideBackground(string side) =>
        string.Equals(SelectedOrderSide, side, StringComparison.OrdinalIgnoreCase)
            ? side == "SELL" ? "#402125" : "#16372F"
            : "#101821";

    private string GetOrderSideForeground(string side) =>
        string.Equals(SelectedOrderSide, side, StringComparison.OrdinalIgnoreCase)
            ? side == "SELL" ? "#FF857B" : "#42F5B1"
            : "#8FA3B8";
}
