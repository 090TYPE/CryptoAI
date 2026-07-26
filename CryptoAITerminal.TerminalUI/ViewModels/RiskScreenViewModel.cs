using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Host view model for <c>RiskView</c>.
///
/// Two thirds of the page already belong to desks that exist: open positions, realized PnL, the
/// sniper guard and the wallet execution mode. What is left is the shell's own risk envelope —
/// the RiskManager caps, the blocked-order audit log and the daily-loss sparkline — which is owned
/// by <see cref="MainWindowViewModel"/> and stays there. Those members are relayed through
/// delegates rather than copied, so the view can compile its bindings against this small type
/// while the shell keeps both the behaviour and the single source of truth.
/// </summary>
public sealed class RiskScreenViewModel : ReactiveObject
{
    /// <summary>
    /// Shell members re-published here under the same name. The shell raises the originals, so
    /// without this relay a compiled binding on <c>RiskScreenViewModel</c> would read the delegate
    /// once and never refresh.
    /// </summary>
    private static readonly HashSet<string> RelayedShellProperties = new(StringComparer.Ordinal)
    {
        nameof(RiskRuntimeStatusBrush),
        nameof(RiskRuntimeSummary),
        nameof(RiskCexExposureSummary),
        nameof(RiskSniperGuardSummary),
        nameof(RiskLimitPositionInput),
        nameof(RiskLimitDailyLossInput),
        nameof(RiskBudgetPositionCapLabel),
        nameof(RiskBudgetDailyLossLabel),
        nameof(RiskBudgetRemainingLabel),
        nameof(RiskBudgetWarningLabel),
        nameof(RiskBudgetWarningBrush),
        nameof(RiskBudgetBlockReason),
        nameof(RiskBudgetBlockBrush),
        nameof(RiskBlockHistoryLines),
        nameof(HasRiskBlockHistory),
        nameof(RiskDailyLossSparkline),
        nameof(HasRiskDailyLossSparkline),
        nameof(FuturesPrivateApiStatusLabel),
        nameof(FuturesPrivateApiStatusBrush),
    };

    private readonly Func<string> _riskRuntimeStatusBrush;
    private readonly Func<string> _riskRuntimeSummary;
    private readonly Func<string> _riskCexExposureSummary;
    private readonly Func<string> _riskSniperGuardSummary;
    private readonly Func<decimal> _riskLimitPositionInput;
    private readonly Action<decimal> _setRiskLimitPositionInput;
    private readonly Func<decimal> _riskLimitDailyLossInput;
    private readonly Action<decimal> _setRiskLimitDailyLossInput;
    private readonly Func<string> _riskBudgetPositionCapLabel;
    private readonly Func<string> _riskBudgetDailyLossLabel;
    private readonly Func<string> _riskBudgetRemainingLabel;
    private readonly Func<string> _riskBudgetWarningLabel;
    private readonly Func<string> _riskBudgetWarningBrush;
    private readonly Func<string> _riskBudgetBlockReason;
    private readonly Func<string> _riskBudgetBlockBrush;
    private readonly Func<IReadOnlyList<string>> _riskBlockHistoryLines;
    private readonly Func<bool> _hasRiskBlockHistory;
    private readonly Func<List<Avalonia.Point>> _riskDailyLossSparkline;
    private readonly Func<bool> _hasRiskDailyLossSparkline;
    private readonly Func<string> _futuresPrivateApiStatusLabel;
    private readonly Func<string> _futuresPrivateApiStatusBrush;

    /// <param name="shellNotifier">
    /// The shell. It already raises every risk-envelope property below, so this view model relays
    /// those notifications instead of re-deriving them from the RiskManager.
    /// </param>
    /// <param name="riskBlockHistoryEmptyLabel">
    /// A constant on the shell — passed by value because it never changes and needs no relay.
    /// </param>
    public RiskScreenViewModel(
        AllPositionsViewModel positions,
        PnlDashboardViewModel pnl,
        SniperViewModel sniper,
        WalletWorkspaceViewModel wallet,
        INotifyPropertyChanged shellNotifier,
        Func<string> riskRuntimeStatusBrush,
        Func<string> riskRuntimeSummary,
        Func<string> riskCexExposureSummary,
        Func<string> riskSniperGuardSummary,
        Func<decimal> riskLimitPositionInput,
        Action<decimal> setRiskLimitPositionInput,
        Func<decimal> riskLimitDailyLossInput,
        Action<decimal> setRiskLimitDailyLossInput,
        ReactiveCommand<Unit, Unit> applyRiskLimitsCommand,
        ReactiveCommand<Unit, Unit> resetDailyRiskCommand,
        Func<string> riskBudgetPositionCapLabel,
        Func<string> riskBudgetDailyLossLabel,
        Func<string> riskBudgetRemainingLabel,
        Func<string> riskBudgetWarningLabel,
        Func<string> riskBudgetWarningBrush,
        Func<string> riskBudgetBlockReason,
        Func<string> riskBudgetBlockBrush,
        string riskBlockHistoryEmptyLabel,
        Func<IReadOnlyList<string>> riskBlockHistoryLines,
        Func<bool> hasRiskBlockHistory,
        Func<List<Avalonia.Point>> riskDailyLossSparkline,
        Func<bool> hasRiskDailyLossSparkline,
        Func<string> futuresPrivateApiStatusLabel,
        Func<string> futuresPrivateApiStatusBrush,
        ReactiveCommand<string, Unit> selectMainTabCommand)
    {
        Positions = positions;
        Pnl       = pnl;
        Sniper    = sniper;
        Wallet    = wallet;

        _riskRuntimeStatusBrush      = riskRuntimeStatusBrush;
        _riskRuntimeSummary          = riskRuntimeSummary;
        _riskCexExposureSummary      = riskCexExposureSummary;
        _riskSniperGuardSummary      = riskSniperGuardSummary;
        _riskLimitPositionInput      = riskLimitPositionInput;
        _setRiskLimitPositionInput   = setRiskLimitPositionInput;
        _riskLimitDailyLossInput     = riskLimitDailyLossInput;
        _setRiskLimitDailyLossInput  = setRiskLimitDailyLossInput;
        _riskBudgetPositionCapLabel  = riskBudgetPositionCapLabel;
        _riskBudgetDailyLossLabel    = riskBudgetDailyLossLabel;
        _riskBudgetRemainingLabel    = riskBudgetRemainingLabel;
        _riskBudgetWarningLabel      = riskBudgetWarningLabel;
        _riskBudgetWarningBrush      = riskBudgetWarningBrush;
        _riskBudgetBlockReason       = riskBudgetBlockReason;
        _riskBudgetBlockBrush        = riskBudgetBlockBrush;
        _riskBlockHistoryLines       = riskBlockHistoryLines;
        _hasRiskBlockHistory         = hasRiskBlockHistory;
        _riskDailyLossSparkline      = riskDailyLossSparkline;
        _hasRiskDailyLossSparkline   = hasRiskDailyLossSparkline;
        _futuresPrivateApiStatusLabel  = futuresPrivateApiStatusLabel;
        _futuresPrivateApiStatusBrush  = futuresPrivateApiStatusBrush;

        ApplyRiskLimitsCommand    = applyRiskLimitsCommand;
        ResetDailyRiskCommand     = resetDailyRiskCommand;
        SelectMainTabCommand      = selectMainTabCommand;
        RiskBlockHistoryEmptyLabel = riskBlockHistoryEmptyLabel;

        // No unsubscribe: the notifier is the shell, which owns this instance, so the handler and
        // both objects die together.
        shellNotifier.PropertyChanged += OnShellPropertyChanged;
    }

    // ── Sub-view-models already owned elsewhere ──

    /// <summary>Open futures positions: unrealized PnL, position count, total size.</summary>
    public AllPositionsViewModel Positions { get; }

    /// <summary>Realized PnL, win rate, profit factor and max drawdown.</summary>
    public PnlDashboardViewModel Pnl { get; }

    /// <summary>Backs the sniper safety line in the header cards.</summary>
    public SniperViewModel Sniper { get; }

    /// <summary>Global execution guard: paper/live mode, quick controls, readiness notes.</summary>
    public WalletWorkspaceViewModel Wallet { get; }

    // ── Risk envelope relayed from the shell ──

    public string RiskRuntimeStatusBrush => _riskRuntimeStatusBrush();

    public string RiskRuntimeSummary => _riskRuntimeSummary();

    public string RiskCexExposureSummary => _riskCexExposureSummary();

    public string RiskSniperGuardSummary => _riskSniperGuardSummary();

    /// <summary>
    /// Two-way against the shell's own field. Nothing is cached here — the shell raises the change
    /// and <see cref="OnShellPropertyChanged"/> forwards it, so the editor reads the accepted value
    /// back instead of a local copy that could drift.
    /// </summary>
    public decimal RiskLimitPositionInput
    {
        get => _riskLimitPositionInput();
        set => _setRiskLimitPositionInput(value);
    }

    /// <inheritdoc cref="RiskLimitPositionInput"/>
    public decimal RiskLimitDailyLossInput
    {
        get => _riskLimitDailyLossInput();
        set => _setRiskLimitDailyLossInput(value);
    }

    public ReactiveCommand<Unit, Unit> ApplyRiskLimitsCommand { get; }

    public ReactiveCommand<Unit, Unit> ResetDailyRiskCommand { get; }

    public string RiskBudgetPositionCapLabel => _riskBudgetPositionCapLabel();

    public string RiskBudgetDailyLossLabel => _riskBudgetDailyLossLabel();

    public string RiskBudgetRemainingLabel => _riskBudgetRemainingLabel();

    public string RiskBudgetWarningLabel => _riskBudgetWarningLabel();

    public string RiskBudgetWarningBrush => _riskBudgetWarningBrush();

    public string RiskBudgetBlockReason => _riskBudgetBlockReason();

    public string RiskBudgetBlockBrush => _riskBudgetBlockBrush();

    public string RiskBlockHistoryEmptyLabel { get; }

    public IReadOnlyList<string> RiskBlockHistoryLines => _riskBlockHistoryLines();

    public bool HasRiskBlockHistory => _hasRiskBlockHistory();

    public List<Avalonia.Point> RiskDailyLossSparkline => _riskDailyLossSparkline();

    public bool HasRiskDailyLossSparkline => _hasRiskDailyLossSparkline();

    public string FuturesPrivateApiStatusLabel => _futuresPrivateApiStatusLabel();

    public string FuturesPrivateApiStatusBrush => _futuresPrivateApiStatusBrush();

    /// <summary>Shell navigation, used by the "Open Logs" quick control.</summary>
    public ReactiveCommand<string, Unit> SelectMainTabCommand { get; }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null)
        {
            foreach (var name in RelayedShellProperties)
            {
                this.RaisePropertyChanged(name);
            }

            return;
        }

        if (RelayedShellProperties.Contains(e.PropertyName))
        {
            this.RaisePropertyChanged(e.PropertyName);
        }
    }
}
