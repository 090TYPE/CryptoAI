using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// View-state that drives the 3-column Sniper terminal layout: the centre tab
/// switcher, the currently selected candidate for the right detail panel, and
/// the derived colours/labels for the ARM pill. No trading logic lives here —
/// every command and collection it exposes is already implemented elsewhere in
/// <see cref="SniperViewModel"/>.
/// </summary>
public partial class SniperViewModel
{
    private string _activeSniperTab = "trending";
    private SniperCandidateViewModel? _selectedSniperCandidate;

    public ReactiveCommand<string, Unit> SelectSniperTabCommand { get; private set; } = default!;
    public ReactiveCommand<SniperCandidateViewModel, Unit> SelectSniperCandidateCommand { get; private set; } = default!;
    public ReactiveCommand<Unit, Unit> ToggleArmCommand { get; private set; } = default!;

    /// <summary>Wired from the constructor. Creates the terminal-only commands and keeps the
    /// detail panel's fallback selection fresh as candidate collections change.</summary>
    private void AttachTerminalState()
    {
        SelectSniperTabCommand = ReactiveCommand.Create<string>(SetActiveSniperTab);
        SelectSniperCandidateCommand = ReactiveCommand.Create<SniperCandidateViewModel>(SelectSniperCandidate);
        ToggleArmCommand = ReactiveCommand.CreateFromObservable(
            () => IsArmed ? StopCommand.Execute() : ArmCommand.Execute());

        SubscribeCommandErrors(SelectSniperTabCommand, "Select sniper tab");
        SubscribeCommandErrors(SelectSniperCandidateCommand, "Select sniper candidate");
        SubscribeCommandErrors(ToggleArmCommand, "Toggle sniper arm");

        void Watch(INotifyCollectionChanged c) => c.CollectionChanged += (_, _) => RaiseSelectionProperties();
        Watch(FreshPairs);
        Watch(AcceptedPairs);
        Watch(OpenPositions);
        Watch(PaperPositions);
    }

    public string ActiveSniperTab
    {
        get => _activeSniperTab;
        private set
        {
            this.RaiseAndSetIfChanged(ref _activeSniperTab, value);
            this.RaisePropertyChanged(nameof(IsTrendingTab));
            this.RaisePropertyChanged(nameof(IsFreshTab));
            this.RaisePropertyChanged(nameof(IsAcceptedTab));
            this.RaisePropertyChanged(nameof(IsPositionsTab));
            this.RaisePropertyChanged(nameof(IsPaperTab));
            this.RaisePropertyChanged(nameof(IsAnalyticsTab));
            this.RaisePropertyChanged(nameof(IsCandidateTab));
            this.RaisePropertyChanged(nameof(CurrentCandidates));
            RaiseSelectionProperties();
        }
    }

    private void SetActiveSniperTab(string tab)
    {
        ActiveSniperTab = string.IsNullOrWhiteSpace(tab) ? "trending" : tab.Trim().ToLowerInvariant();

        // Auto-focus the first candidate of the newly selected tab so the detail panel follows.
        var list = CurrentCandidates;
        if (list is { Count: > 0 })
        {
            SelectSniperCandidate(list[0]);
        }
    }

    public bool IsTrendingTab  => _activeSniperTab == "trending";
    public bool IsFreshTab     => _activeSniperTab == "fresh";
    public bool IsAcceptedTab  => _activeSniperTab == "accepted";
    public bool IsPositionsTab => _activeSniperTab == "positions";
    public bool IsPaperTab     => _activeSniperTab == "paper";
    public bool IsAnalyticsTab => _activeSniperTab == "analytics";
    public bool IsCandidateTab => IsFreshTab || IsAcceptedTab || IsPositionsTab || IsPaperTab;

    /// <summary>The candidate collection backing the active centre tab, or null for trending/analytics.</summary>
    public ObservableCollection<SniperCandidateViewModel>? CurrentCandidates => _activeSniperTab switch
    {
        "fresh"     => FreshPairs,
        "accepted"  => AcceptedPairs,
        "positions" => OpenPositions,
        "paper"     => PaperPositions,
        _           => null
    };

    public void SelectSniperCandidate(SniperCandidateViewModel? candidate)
    {
        _selectedSniperCandidate = candidate;
        RaiseSelectionProperties();
    }

    /// <summary>The candidate shown in the right detail panel. Falls back to the first live
    /// position / fresh pair so the panel is populated as soon as any data exists.</summary>
    public SniperCandidateViewModel? SelectedSniperCandidate =>
        _selectedSniperCandidate
        ?? OpenPositions.FirstOrDefault()
        ?? FreshPairs.FirstOrDefault()
        ?? AcceptedPairs.FirstOrDefault()
        ?? PaperPositions.FirstOrDefault();

    public bool HasSelectedCandidate => SelectedSniperCandidate is not null;

    private void RaiseSelectionProperties()
    {
        this.RaisePropertyChanged(nameof(SelectedSniperCandidate));
        this.RaisePropertyChanged(nameof(HasSelectedCandidate));
    }

    // ── ARM pill / toggle button (derived from IsArmed) ─────────────────────────
    public string ArmStatusLabel => IsArmed ? "ARMED · SCANNING" : "IDLE";
    public string ArmToggleLabel => IsArmed ? "DISARM SNIPER" : "ARM SNIPER";
    public string ArmDotHex      => IsArmed ? "#3DDC84" : "#5A7A94";
    public string ArmBorderHex   => IsArmed ? "#21E6C1" : "#22384F";
    public string ArmFillHex     => IsArmed ? "#0E2A2A" : "#07111A";
    public string ArmTextHex     => IsArmed ? "#21E6C1" : "#C8DCEF";

    private void RaiseArmPillProperties()
    {
        this.RaisePropertyChanged(nameof(ArmStatusLabel));
        this.RaisePropertyChanged(nameof(ArmToggleLabel));
        this.RaisePropertyChanged(nameof(ArmDotHex));
        this.RaisePropertyChanged(nameof(ArmBorderHex));
        this.RaisePropertyChanged(nameof(ArmFillHex));
        this.RaisePropertyChanged(nameof(ArmTextHex));
    }

    // ── Paper / live execution pill ─────────────────────────────────────────────
    public string PaperPillLabel => PaperTradingEnabled ? "PAPER MODE" : "LIVE MODE";
    public string PaperPillHex   => PaperTradingEnabled ? "#F4B860" : "#3DDC84";

    private void RaisePaperPillProperties()
    {
        this.RaisePropertyChanged(nameof(PaperPillLabel));
        this.RaisePropertyChanged(nameof(PaperPillHex));
    }
}
