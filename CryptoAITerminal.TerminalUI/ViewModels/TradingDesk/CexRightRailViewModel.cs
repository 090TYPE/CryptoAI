using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.TradingDesk;

/// <summary>
/// The CEX desk's right rail: the venue tabs at the top, the exchange info strip, the live
/// position card with its REVERSE / CLOSE buttons, the order-book depth toggle and the risk-mode
/// badge.
///
/// Two kinds of member live here, and the difference matters when reading it.
///
/// Owned: the venue rows, which venue is selected, and the depth preference. Picking a venue also
/// re-points spot and futures routing and re-sources the tape — that is shell work, so this view
/// model writes its own rows and raises <see cref="VenueQuoteSelected"/> /
/// <see cref="TapeRefreshRequested"/> for <see cref="MainWindowViewModel"/> to finish, exactly as
/// <see cref="MarketFeedOwnerViewModel"/> does for the active symbol.
///
/// Read-through: the position card and the two guards. Position size, entry price, exchange PnL,
/// leverage and the close/reverse block reasons are all computed by the shell from account state
/// this view model has no business owning, so they are handed in as accessors and the shell calls
/// the matching <c>Raise…</c> method from the same places it re-raises them on itself. The two
/// commands are the shell's own instances, re-exposed rather than copied — the shell still owns
/// their error subscriptions.
/// </summary>
public sealed class CexRightRailViewModel : ReactiveObject
{
    private readonly Func<decimal> _positionQuantity;
    private readonly Func<int> _manualFuturesLeverage;
    private readonly Func<decimal> _averageEntryPrice;
    private readonly Func<decimal> _exchangeUnrealizedPnl;
    private readonly Func<string> _cexCloseBlockedReason;
    private readonly Func<string> _cexReverseBlockedReason;
    private readonly Func<string> _riskModeLabel;

    private string _selectedObDepth = "0.1";

    /// <param name="venueOrder">The venues shown as tabs, in display order.</param>
    /// <param name="baseVenue">The venue the others are priced against (the delta column's zero).</param>
    /// <param name="selectedVenue">The venue spot routing points at when the desk opens.</param>
    public CexRightRailViewModel(
        IReadOnlyList<string> venueOrder,
        string baseVenue,
        string selectedVenue,
        Func<decimal> positionQuantity,
        Func<int> manualFuturesLeverage,
        Func<decimal> averageEntryPrice,
        Func<decimal> exchangeUnrealizedPnl,
        Func<string> cexCloseBlockedReason,
        Func<string> cexReverseBlockedReason,
        Func<string> riskModeLabel)
    {
        _positionQuantity = positionQuantity;
        _manualFuturesLeverage = manualFuturesLeverage;
        _averageEntryPrice = averageEntryPrice;
        _exchangeUnrealizedPnl = exchangeUnrealizedPnl;
        _cexCloseBlockedReason = cexCloseBlockedReason;
        _cexReverseBlockedReason = cexReverseBlockedReason;
        _riskModeLabel = riskModeLabel;

        foreach (var name in venueOrder)
        {
            TradingVenues.Add(new TradingVenueQuoteViewModel(name)
            {
                IsBase = string.Equals(name, baseVenue, StringComparison.OrdinalIgnoreCase),
                IsSelected = string.Equals(name, selectedVenue, StringComparison.OrdinalIgnoreCase),
            });
        }

        SelectTradingVenueQuoteCommand = ReactiveCommand.Create<string>(SelectTradingVenueQuote, outputScheduler: App.UiScheduler);
        SelectObDepthCommand = ReactiveCommand.Create<string>(v => { if (!string.IsNullOrWhiteSpace(v)) SelectedObDepth = v; }, outputScheduler: App.UiScheduler);
    }

    // ═════════════════════════ shell wiring ═════════════════════════════════

    /// <summary>
    /// Raised when the user picks a venue tab, before the rows are restyled. The shell points both
    /// spot and futures order routing / display at it, so the header, order book and tape all
    /// re-source from there.
    /// </summary>
    public event Action<string>? VenueQuoteSelected;

    /// <summary>Raised after a venue switch so the shell can re-pull the trade tape.</summary>
    public event Action? TapeRefreshRequested;

    /// <summary>
    /// Second construction phase: CLOSE and REVERSE are the shell's own commands (it subscribes
    /// their thrown exceptions), and they do not exist yet when this view model is built.
    /// </summary>
    public void AttachCommands(
        ReactiveCommand<Unit, Unit> closePositionCommand,
        ReactiveCommand<Unit, Unit> reversePositionCommand)
    {
        ClosePositionCommand = closePositionCommand;
        ReversePositionCommand = reversePositionCommand;
    }

    /// <summary>Re-raises the position card. Called by the shell from RaisePositionStateChanged.</summary>
    public void RaisePositionCardChanged()
    {
        this.RaisePropertyChanged(nameof(PositionSideLabel));
        this.RaisePropertyChanged(nameof(PositionSideBrush));
        this.RaisePropertyChanged(nameof(PositionSideBackground));
        this.RaisePropertyChanged(nameof(EntryPriceLabel));
        this.RaisePropertyChanged(nameof(ExchangeUnrealizedPnlLabel));
    }

    /// <summary>Re-raises the leverage badge. Called by the shell from the leverage setter.</summary>
    public void RaiseLeverageChanged() => this.RaisePropertyChanged(nameof(ManualLeverageLabel));

    /// <summary>Re-raises CLOSE / REVERSE enablement. Called from RaiseCexActionStateChanged.</summary>
    public void RaiseActionGuardChanged()
    {
        this.RaisePropertyChanged(nameof(CanExecuteCexClose));
        this.RaisePropertyChanged(nameof(CanExecuteCexReverse));
        this.RaisePropertyChanged(nameof(CexCloseBlockedReason));
        this.RaisePropertyChanged(nameof(CexReverseBlockedReason));
    }

    /// <summary>Re-raises the risk badge. Called when the wallet's sizing percent changes.</summary>
    public void RaiseRiskModeChanged() => this.RaisePropertyChanged(nameof(RiskModeLabel));

    // ═════════════════════════ venue tabs ═══════════════════════════════════

    /// <summary>The four live venues shown as switchable tabs for the active symbol.</summary>
    public ObservableCollection<TradingVenueQuoteViewModel> TradingVenues { get; } = [];

    public ReactiveCommand<string, Unit> SelectTradingVenueQuoteCommand { get; }

    private void SelectTradingVenueQuote(string exchange)
    {
        if (string.IsNullOrWhiteSpace(exchange))
        {
            return;
        }

        VenueQuoteSelected?.Invoke(exchange);

        foreach (var venue in TradingVenues)
        {
            venue.IsSelected = string.Equals(venue.Exchange, exchange, StringComparison.OrdinalIgnoreCase);
        }

        TapeRefreshRequested?.Invoke();
    }

    // ═════════════════════════ exchange info strip ══════════════════════════

    /// <summary>Taker fee shown on the exchange info strip.</summary>
    public string ExchangeFeeLabel => "0.10%";

    // ═════════════════════════ position card ════════════════════════════════

    /// <summary>Live perp position side badge for the right-rail position card.</summary>
    public string PositionSideLabel => _positionQuantity() > 0m ? "LONG" : _positionQuantity() < 0m ? "SHORT" : "FLAT";
    public string PositionSideBrush => _positionQuantity() > 0m ? SemanticColor.Keys.Positive : _positionQuantity() < 0m ? SemanticColor.Keys.Negative : "#5a7a94";
    public string PositionSideBackground => _positionQuantity() > 0m ? "#07221a" : _positionQuantity() < 0m ? "#200a0a" : "#0a1622";
    public string ManualLeverageLabel => $"{_manualFuturesLeverage()}×";
    public string EntryPriceLabel => _averageEntryPrice() > 0 ? $"{_averageEntryPrice():N2}" : "--";
    public string ExchangeUnrealizedPnlLabel => $"{_exchangeUnrealizedPnl():+0.00;-0.00;0.00} USDT";

    /// <summary>The shell's own REVERSE command, re-exposed for the rail's button.</summary>
    public ReactiveCommand<Unit, Unit> ReversePositionCommand { get; private set; } = null!;

    /// <summary>The shell's own CLOSE command, re-exposed for the rail's button.</summary>
    public ReactiveCommand<Unit, Unit> ClosePositionCommand { get; private set; } = null!;

    public bool CanExecuteCexReverse => string.IsNullOrWhiteSpace(CexReverseBlockedReason);
    public bool CanExecuteCexClose => string.IsNullOrWhiteSpace(CexCloseBlockedReason);
    public string CexCloseBlockedReason => _cexCloseBlockedReason();
    public string CexReverseBlockedReason => _cexReverseBlockedReason();

    // ═════════════════════════ order book depth ═════════════════════════════

    public ReactiveCommand<string, Unit> SelectObDepthCommand { get; }

    /// <summary>Order-book price-grouping preference shown in the depth toggle (0.01 / 0.1 / 1).</summary>
    public string SelectedObDepth
    {
        get => _selectedObDepth;
        private set => this.RaiseAndSetIfChanged(ref _selectedObDepth, value);
    }

    // ═════════════════════════ risk badge ═══════════════════════════════════

    /// <summary>MODERATE / BALANCED / AGGRESSIVE, derived from the wallet's global sizing percent.</summary>
    public string RiskModeLabel => _riskModeLabel();
}
