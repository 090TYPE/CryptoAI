using System.Collections.ObjectModel;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.TradingDesk;

/// <summary>
/// The trading desk's read-only result surfaces: the public trade tape above the blotter and the
/// four blotter tabs (orders, positions, fills, signals) plus which tab is open.
///
/// It owns the rows and nothing else. Every one of these collections is filled by
/// <see cref="MainWindowViewModel"/> — order placement, exchange sync, fill accounting and the
/// trade-idea engine all live there and keep living there. What moved is only the storage and the
/// "is it empty" flags the placeholders bind to, so the blotter markup no longer has to compile
/// against the shell.
///
/// Because the shell mutates the collections directly, it has to say when a placeholder should be
/// re-evaluated: that is what the <c>Raise…Changed</c> methods are for. They are the exact
/// <c>RaisePropertyChanged</c> calls the shell used to make on itself.
/// </summary>
public sealed class TradeBlotterViewModel : ReactiveObject
{
    private int _selectedTradingBottomTabIndex;

    /// <summary>Public trade tape (most-recent first) for the active symbol on the active venue.</summary>
    public ObservableCollection<TapeTradeViewModel> TapeTrades { get; } = [];

    public bool ShowTapePlaceholder => TapeTrades.Count == 0;

    public ObservableCollection<WorkingOrderViewModel> WorkingOrders { get; } = [];
    public ObservableCollection<TradeFillViewModel> RecentFills { get; } = [];
    public ObservableCollection<PositionRowViewModel> PositionRows { get; } = [];
    public ObservableCollection<SignalRowViewModel> SignalRows { get; } = [];

    public bool ShowWorkingOrdersPlaceholder => WorkingOrders.Count == 0;
    public bool ShowRecentFillsPlaceholder => RecentFills.Count == 0;
    public bool ShowPositionRowsPlaceholder => PositionRows.Count == 0;
    public bool ShowSignalRowsPlaceholder => SignalRows.Count == 0;

    /// <summary>Which blotter tab is open: 0 Orders, 1 Positions, 2 Fills, 3 Signals.</summary>
    public int SelectedTradingBottomTabIndex
    {
        get => _selectedTradingBottomTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTradingBottomTabIndex, value);
    }

    /// <summary>Called by the shell after it has rewritten <see cref="TapeTrades"/>.</summary>
    public void RaiseTapeChanged() => this.RaisePropertyChanged(nameof(ShowTapePlaceholder));

    /// <summary>Called by the shell after it has added to or removed from <see cref="WorkingOrders"/>.</summary>
    public void RaiseWorkingOrdersChanged() => this.RaisePropertyChanged(nameof(ShowWorkingOrdersPlaceholder));

    /// <summary>Called by the shell after it has added to or removed from <see cref="RecentFills"/>.</summary>
    public void RaiseRecentFillsChanged() => this.RaisePropertyChanged(nameof(ShowRecentFillsPlaceholder));

    /// <summary>Called by the shell after it has rebuilt <see cref="PositionRows"/>.</summary>
    public void RaisePositionRowsChanged() => this.RaisePropertyChanged(nameof(ShowPositionRowsPlaceholder));

    /// <summary>Called by the shell after it has rebuilt <see cref="SignalRows"/>.</summary>
    public void RaiseSignalRowsChanged() => this.RaisePropertyChanged(nameof(ShowSignalRowsPlaceholder));
}
