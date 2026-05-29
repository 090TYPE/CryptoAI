using System;
using CryptoAITerminal.Core.Models;
using ReactiveUI;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class SniperCandidateViewModel : ReactiveObject
{
    private bool _wasBought;
    private bool _isOpenPosition;
    private string _status = string.Empty;
    private DateTime? _openedAtLocal;
    private decimal _entryAmountBnb;
    private decimal _entryPriceUsd;
    private int _riskScore;
    private string _riskBand = "Unknown";
    private string _riskSummary = string.Empty;
    private string _riskFlags = string.Empty;
    private string _strategyLabel = "Mixed";
    private string _strategySummary = string.Empty;
    private string _dexQualityLabel = string.Empty;
    private string _ownershipSignalLabel = string.Empty;
    private string _watchlistPriorityLabel = string.Empty;
    private string _pairAgeLabel = string.Empty;
    private string _signalSourceLabel = string.Empty;
    private string _signalConfirmationLabel = string.Empty;
    private int _signalPriorityScore;
    private bool _isExecutionBlocked;
    private bool _isSuspectedHoneypot;
    private decimal _simulatedBuyTaxPercent;
    private decimal _simulatedSellTaxPercent;
    private string _executionVerdict = "Not evaluated";
    private string _executionBlockReason = string.Empty;
    private bool _autoTakeProfitEnabled;
    private decimal _takeProfitPercent;
    private bool _autoStopLossEnabled;
    private decimal _stopLossPercent;
    private bool _autoTrailingStopEnabled;
    private decimal _trailingStopPercent;
    private bool _partialTakeProfitEnabled;
    private decimal _partialTakeProfitTriggerPercent;
    private decimal _partialTakeProfitSellPercent;
    private bool _partialTakeProfitExecuted;
    private bool _breakEvenEnabled;
    private decimal _breakEvenTriggerPercent;
    private bool _breakEvenArmed;
    private bool _breakEvenTriggered;
    private decimal _positionSizePercent = 100m;
    private decimal _peakPriceUsd;
    private decimal _trackedTokenAmount;
    private bool _usesLiveAccounting;
    private decimal _liveEntryCostNative;
    private decimal _liveRealizedProceedsNative;
    private decimal _liveEntryTokenAmount;
    private string _entryTxHash = string.Empty;
    private string _lastExitTxHash = string.Empty;
    private string _entryDexId = string.Empty;
    private bool _takeProfitTriggered;
    private bool _stopLossTriggered;
    private bool _trailingStopTriggered;
    private TokenSecurityResult? _securityScanResult;
    private bool _securityScanComplete;
    private int _rankScore;
    private string _rankScoreLabel = string.Empty;
    private string _rankScoreBand = string.Empty;

    public SniperCandidateViewModel(DexTokenInfo tokenInfo, string reason, bool passedFilters)
    {
        TokenInfo = tokenInfo;
        Reason = reason;
        PassedFilters = passedFilters;
    }

    public DexTokenInfo TokenInfo { get; }
    public string Reason { get; }
    public bool PassedFilters { get; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(TokenInfo.Symbol)
            ? TokenInfo.Name
            : $"{TokenInfo.Name} ({TokenInfo.Symbol})";

    public string PairLabel => $"{TokenInfo.ChainId.ToUpperInvariant()} / {TokenInfo.DexId} / {TokenInfo.QuoteSymbol}";

    public bool WasBought
    {
        get => _wasBought;
        set => this.RaiseAndSetIfChanged(ref _wasBought, value);
    }

    public bool IsOpenPosition
    {
        get => _isOpenPosition;
        set => this.RaiseAndSetIfChanged(ref _isOpenPosition, value);
    }

    public DateTime? OpenedAtLocal
    {
        get => _openedAtLocal;
        set
        {
            this.RaiseAndSetIfChanged(ref _openedAtLocal, value);
            this.RaisePropertyChanged(nameof(PositionMeta));
        }
    }

    public decimal EntryAmountBnb
    {
        get => _entryAmountBnb;
        set
        {
            this.RaiseAndSetIfChanged(ref _entryAmountBnb, value);
            this.RaisePropertyChanged(nameof(PositionMeta));
        }
    }

    public decimal EntryPriceUsd
    {
        get => _entryPriceUsd;
        set
        {
            this.RaiseAndSetIfChanged(ref _entryPriceUsd, value);
            this.RaisePropertyChanged(nameof(TakeProfitTargetPriceUsd));
            this.RaisePropertyChanged(nameof(TakeProfitSummary));
            this.RaisePropertyChanged(nameof(TakeProfitStatus));
            this.RaisePropertyChanged(nameof(PaperPnlPercent));
            this.RaisePropertyChanged(nameof(PaperPnlLabel));
            this.RaisePropertyChanged(nameof(PnlAccentHex));
        }
    }

    public int RiskScore
    {
        get => _riskScore;
        set => this.RaiseAndSetIfChanged(ref _riskScore, value);
    }

    public int RankScore
    {
        get => _rankScore;
        set => this.RaiseAndSetIfChanged(ref _rankScore, value);
    }

    public string RankScoreLabel
    {
        get => _rankScoreLabel;
        set => this.RaiseAndSetIfChanged(ref _rankScoreLabel, value);
    }

    public string RankScoreBand
    {
        get => _rankScoreBand;
        set => this.RaiseAndSetIfChanged(ref _rankScoreBand, value);
    }

    public string RiskBand
    {
        get => _riskBand;
        set => this.RaiseAndSetIfChanged(ref _riskBand, value);
    }

    public string RiskSummary
    {
        get => _riskSummary;
        set => this.RaiseAndSetIfChanged(ref _riskSummary, value);
    }

    public string RiskFlags
    {
        get => _riskFlags;
        set => this.RaiseAndSetIfChanged(ref _riskFlags, value);
    }

    public string StrategyLabel
    {
        get => _strategyLabel;
        set => this.RaiseAndSetIfChanged(ref _strategyLabel, value);
    }

    public string StrategySummary
    {
        get => _strategySummary;
        set => this.RaiseAndSetIfChanged(ref _strategySummary, value);
    }

    public string DexQualityLabel
    {
        get => _dexQualityLabel;
        set => this.RaiseAndSetIfChanged(ref _dexQualityLabel, value);
    }

    public string OwnershipSignalLabel
    {
        get => _ownershipSignalLabel;
        set => this.RaiseAndSetIfChanged(ref _ownershipSignalLabel, value);
    }

    public string WatchlistPriorityLabel
    {
        get => _watchlistPriorityLabel;
        set => this.RaiseAndSetIfChanged(ref _watchlistPriorityLabel, value);
    }

    public string PairAgeLabel
    {
        get => _pairAgeLabel;
        set => this.RaiseAndSetIfChanged(ref _pairAgeLabel, value);
    }

    public string SignalSourceLabel
    {
        get => _signalSourceLabel;
        set => this.RaiseAndSetIfChanged(ref _signalSourceLabel, value);
    }

    public string SignalConfirmationLabel
    {
        get => _signalConfirmationLabel;
        set => this.RaiseAndSetIfChanged(ref _signalConfirmationLabel, value);
    }

    public int SignalPriorityScore
    {
        get => _signalPriorityScore;
        set => this.RaiseAndSetIfChanged(ref _signalPriorityScore, value);
    }

    public bool IsExecutionBlocked
    {
        get => _isExecutionBlocked;
        set => this.RaiseAndSetIfChanged(ref _isExecutionBlocked, value);
    }

    public bool IsSuspectedHoneypot
    {
        get => _isSuspectedHoneypot;
        set => this.RaiseAndSetIfChanged(ref _isSuspectedHoneypot, value);
    }

    public decimal SimulatedBuyTaxPercent
    {
        get => _simulatedBuyTaxPercent;
        set => this.RaiseAndSetIfChanged(ref _simulatedBuyTaxPercent, value);
    }

    public decimal SimulatedSellTaxPercent
    {
        get => _simulatedSellTaxPercent;
        set => this.RaiseAndSetIfChanged(ref _simulatedSellTaxPercent, value);
    }

    public string ExecutionVerdict
    {
        get => _executionVerdict;
        set => this.RaiseAndSetIfChanged(ref _executionVerdict, value);
    }

    public string ExecutionBlockReason
    {
        get => _executionBlockReason;
        set => this.RaiseAndSetIfChanged(ref _executionBlockReason, value);
    }

    public bool AutoTakeProfitEnabled
    {
        get => _autoTakeProfitEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoTakeProfitEnabled, value);
            this.RaisePropertyChanged(nameof(TakeProfitSummary));
            this.RaisePropertyChanged(nameof(TakeProfitStatus));
            this.RaisePropertyChanged(nameof(TakeProfitAccentHex));
        }
    }

    public decimal TakeProfitPercent
    {
        get => _takeProfitPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _takeProfitPercent, value);
            this.RaisePropertyChanged(nameof(TakeProfitTargetPriceUsd));
            this.RaisePropertyChanged(nameof(TakeProfitSummary));
            this.RaisePropertyChanged(nameof(TakeProfitStatus));
            this.RaisePropertyChanged(nameof(TakeProfitAccentHex));
        }
    }

    public decimal TrackedTokenAmount
    {
        get => _trackedTokenAmount;
        set
        {
            this.RaiseAndSetIfChanged(ref _trackedTokenAmount, value);
            this.RaisePropertyChanged(nameof(TrackedTokenAmountLabel));
            RaiseLivePnlProperties();
        }
    }

    public bool UsesLiveAccounting
    {
        get => _usesLiveAccounting;
        set
        {
            this.RaiseAndSetIfChanged(ref _usesLiveAccounting, value);
            RaiseLivePnlProperties();
            this.RaisePropertyChanged(nameof(PositionMeta));
        }
    }

    public decimal LiveEntryCostNative
    {
        get => _liveEntryCostNative;
        set
        {
            this.RaiseAndSetIfChanged(ref _liveEntryCostNative, value);
            RaiseLivePnlProperties();
            this.RaisePropertyChanged(nameof(PositionMeta));
        }
    }

    public decimal LiveRealizedProceedsNative
    {
        get => _liveRealizedProceedsNative;
        set
        {
            this.RaiseAndSetIfChanged(ref _liveRealizedProceedsNative, value);
            RaiseLivePnlProperties();
            this.RaisePropertyChanged(nameof(PositionMeta));
        }
    }

    public decimal LiveEntryTokenAmount
    {
        get => _liveEntryTokenAmount;
        set
        {
            this.RaiseAndSetIfChanged(ref _liveEntryTokenAmount, value);
            RaiseLivePnlProperties();
        }
    }

    public string EntryTxHash
    {
        get => _entryTxHash;
        set => this.RaiseAndSetIfChanged(ref _entryTxHash, value);
    }

    public string LastExitTxHash
    {
        get => _lastExitTxHash;
        set => this.RaiseAndSetIfChanged(ref _lastExitTxHash, value);
    }

    public string EntryDexId
    {
        get => _entryDexId;
        set => this.RaiseAndSetIfChanged(ref _entryDexId, value);
    }

    public bool AutoStopLossEnabled
    {
        get => _autoStopLossEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoStopLossEnabled, value);
            this.RaisePropertyChanged(nameof(StopLossSummary));
            this.RaisePropertyChanged(nameof(ExitRuleSummary));
            this.RaisePropertyChanged(nameof(ExitStatus));
            this.RaisePropertyChanged(nameof(ExitAccentHex));
        }
    }

    public decimal StopLossPercent
    {
        get => _stopLossPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _stopLossPercent, value);
            this.RaisePropertyChanged(nameof(StopLossTargetPriceUsd));
            this.RaisePropertyChanged(nameof(StopLossSummary));
            this.RaisePropertyChanged(nameof(ExitRuleSummary));
            this.RaisePropertyChanged(nameof(ExitStatus));
            this.RaisePropertyChanged(nameof(ExitAccentHex));
        }
    }

    public bool AutoTrailingStopEnabled
    {
        get => _autoTrailingStopEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoTrailingStopEnabled, value);
            this.RaisePropertyChanged(nameof(TrailingStopSummary));
            this.RaisePropertyChanged(nameof(ExitRuleSummary));
            this.RaisePropertyChanged(nameof(ExitStatus));
            this.RaisePropertyChanged(nameof(ExitAccentHex));
        }
    }

    public decimal TrailingStopPercent
    {
        get => _trailingStopPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _trailingStopPercent, value);
            this.RaisePropertyChanged(nameof(TrailingStopFloorPriceUsd));
            this.RaisePropertyChanged(nameof(TrailingStopSummary));
            this.RaisePropertyChanged(nameof(ExitRuleSummary));
            this.RaisePropertyChanged(nameof(ExitStatus));
            this.RaisePropertyChanged(nameof(ExitAccentHex));
        }
    }

    public decimal PeakPriceUsd
    {
        get => _peakPriceUsd;
        set
        {
            this.RaiseAndSetIfChanged(ref _peakPriceUsd, value);
            this.RaisePropertyChanged(nameof(PeakPriceLabel));
            this.RaisePropertyChanged(nameof(TrailingStopFloorPriceUsd));
            this.RaisePropertyChanged(nameof(TrailingStopSummary));
            this.RaisePropertyChanged(nameof(ExitRuleSummary));
            this.RaisePropertyChanged(nameof(ExitStatus));
        }
    }

    public bool PartialTakeProfitEnabled
    {
        get => _partialTakeProfitEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _partialTakeProfitEnabled, value);
            this.RaisePropertyChanged(nameof(PartialTakeProfitSummary));
            this.RaisePropertyChanged(nameof(ExitRuleSummary));
            this.RaisePropertyChanged(nameof(ExitStatus));
        }
    }

    public decimal PartialTakeProfitTriggerPercent
    {
        get => _partialTakeProfitTriggerPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _partialTakeProfitTriggerPercent, value);
            this.RaisePropertyChanged(nameof(PartialTakeProfitSummary));
            this.RaisePropertyChanged(nameof(ExitRuleSummary));
        }
    }

    public decimal PartialTakeProfitSellPercent
    {
        get => _partialTakeProfitSellPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _partialTakeProfitSellPercent, value);
            this.RaisePropertyChanged(nameof(PartialTakeProfitSummary));
            this.RaisePropertyChanged(nameof(ExitRuleSummary));
        }
    }

    public bool PartialTakeProfitExecuted
    {
        get => _partialTakeProfitExecuted;
        set
        {
            this.RaiseAndSetIfChanged(ref _partialTakeProfitExecuted, value);
            this.RaisePropertyChanged(nameof(PartialTakeProfitSummary));
            this.RaisePropertyChanged(nameof(ExitStatus));
            this.RaisePropertyChanged(nameof(ExitAccentHex));
        }
    }

    public bool BreakEvenEnabled
    {
        get => _breakEvenEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _breakEvenEnabled, value);
            this.RaisePropertyChanged(nameof(BreakEvenSummary));
            this.RaisePropertyChanged(nameof(ExitRuleSummary));
            this.RaisePropertyChanged(nameof(ExitStatus));
        }
    }

    public decimal BreakEvenTriggerPercent
    {
        get => _breakEvenTriggerPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _breakEvenTriggerPercent, value);
            this.RaisePropertyChanged(nameof(BreakEvenSummary));
            this.RaisePropertyChanged(nameof(ExitRuleSummary));
        }
    }

    public bool BreakEvenArmed
    {
        get => _breakEvenArmed;
        set
        {
            this.RaiseAndSetIfChanged(ref _breakEvenArmed, value);
            this.RaisePropertyChanged(nameof(BreakEvenSummary));
            this.RaisePropertyChanged(nameof(ExitStatus));
        }
    }

    public bool BreakEvenTriggered
    {
        get => _breakEvenTriggered;
        set
        {
            this.RaiseAndSetIfChanged(ref _breakEvenTriggered, value);
            this.RaisePropertyChanged(nameof(ExitStatus));
            this.RaisePropertyChanged(nameof(ExitAccentHex));
        }
    }

    public decimal PositionSizePercent
    {
        get => _positionSizePercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _positionSizePercent, value);
            this.RaisePropertyChanged(nameof(SizeStatusLabel));
            this.RaisePropertyChanged(nameof(PositionMeta));
        }
    }

    public bool TakeProfitTriggered
    {
        get => _takeProfitTriggered;
        set
        {
            this.RaiseAndSetIfChanged(ref _takeProfitTriggered, value);
            this.RaisePropertyChanged(nameof(TakeProfitStatus));
            this.RaisePropertyChanged(nameof(TakeProfitAccentHex));
        }
    }

    public bool StopLossTriggered
    {
        get => _stopLossTriggered;
        set
        {
            this.RaiseAndSetIfChanged(ref _stopLossTriggered, value);
            this.RaisePropertyChanged(nameof(ExitStatus));
            this.RaisePropertyChanged(nameof(ExitAccentHex));
        }
    }

    public bool TrailingStopTriggered
    {
        get => _trailingStopTriggered;
        set
        {
            this.RaiseAndSetIfChanged(ref _trailingStopTriggered, value);
            this.RaisePropertyChanged(nameof(ExitStatus));
            this.RaisePropertyChanged(nameof(ExitAccentHex));
        }
    }

    public string RiskAccentHex => RiskBand switch
    {
        "Low" => "#3DDC84",
        "Guarded" => "#F5C451",
        "High" => "#FF8A4C",
        "Extreme" => "#FF5D73",
        _ => "#8FA3B8"
    };

    public string RiskBadgeText => $"Risk {RiskScore}/100 - {RiskBand}";
    public string ExecutionAccentHex => IsExecutionBlocked ? "#FF5D73" : "#3DDC84";
    public string TaxSummary => $"Buy tax {SimulatedBuyTaxPercent:0.#}% | Sell tax {SimulatedSellTaxPercent:0.#}%";
    public string HoneypotLabel => IsSuspectedHoneypot ? "Suspected honeypot" : "No honeypot signal";

    public TokenSecurityResult? SecurityScanResult
    {
        get => _securityScanResult;
        set
        {
            _securityScanResult = value;
            this.RaisePropertyChanged(nameof(SecurityScanResult));
            this.RaisePropertyChanged(nameof(SecurityVerdict));
            this.RaisePropertyChanged(nameof(SecurityVerdictColor));
            this.RaisePropertyChanged(nameof(SecurityFlagsSummary));
            this.RaisePropertyChanged(nameof(SecurityScoreLabel));
            this.RaisePropertyChanged(nameof(SecuritySourceLabel));
            this.RaisePropertyChanged(nameof(SecurityChecksSummary));
        }
    }

    public bool SecurityScanComplete
    {
        get => _securityScanComplete;
        set => this.RaiseAndSetIfChanged(ref _securityScanComplete, value);
    }

    public string SecurityVerdict => _securityScanComplete
        ? (_securityScanResult?.Verdict ?? "Unknown")
        : "Not scanned";

    public string SecurityVerdictColor => _securityScanResult?.Verdict switch
    {
        "Dangerous" => "#FF5D73",
        "High Risk" => "#FF8A4C",
        "Moderate Risk" => "#F4B860",
        "Likely Safe" => "#3DDC84",
        _ => "#8FA3B8"
    };

    public string SecurityFlagsSummary => _securityScanResult?.Flags is { Length: > 0 }
        ? string.Join(" · ", _securityScanResult.Flags.Take(4))
        : (_securityScanComplete ? "No flags" : string.Empty);

    public string SecurityScoreLabel => _securityScanResult is not null
        ? $"Score {_securityScanResult.Score}/100"
        : string.Empty;

    public string SecuritySourceLabel => _securityScanResult?.Source ?? string.Empty;

    public string SecurityChecksSummary
    {
        get
        {
            if (_securityScanResult is null) return string.Empty;
            var r = _securityScanResult;
            var parts = new System.Collections.Generic.List<string>(8);
            parts.Add(r.IsHoneypot         ? "🛑 Honeypot"       : "✓ Sellable");
            parts.Add(r.IsMintable         ? "⚠ Mintable"        : "✓ No mint");
            parts.Add(r.OwnershipRenounced ? "✓ Renounced"       : "⚠ Owner active");
            parts.Add(r.HasBlacklist       ? "⚠ Blacklist"       : "✓ No blacklist");
            if (r.TopHolderPercent > 0m)
                parts.Add(r.TopHolderConcentrated
                    ? $"⚠ Top holder {r.TopHolderPercent:N0}%"
                    : $"✓ Top holder {r.TopHolderPercent:N0}%");
            if (r.BuyTaxPercent > 0m || r.SellTaxPercent > 0m)
                parts.Add($"Tax {r.BuyTaxPercent:N0}%/{r.SellTaxPercent:N0}%");
            return string.Join("  ", parts);
        }
    }

    // ── Deployer analysis (populated asynchronously after SecurityScanResult) ──

    public bool HasDeployerAnalysis => _securityScanResult?.HasDeployerAnalysis == true;

    public string DeployerRiskLabel => _securityScanResult?.DeployerRiskLabel
        ?? (SecurityScanComplete ? "Deployer not analysed" : string.Empty);

    public string DeployerRiskBrush => _securityScanResult?.DeployerRiskBrush ?? "#8FA3B8";

    public string DeployerSummary
    {
        get
        {
            var r = _securityScanResult;
            if (r is null || !r.HasDeployerAnalysis) return string.Empty;
            return $"Deployer {r.DeployerAddress?[..6]}...{r.DeployerAddress?[^4..]} | "
                 + $"{r.DeployerTokenCount} tokens | "
                 + $"{r.DeployerRugpullCount} rugpulls | "
                 + $"{r.DeployerWalletAgeMonths}mo old";
        }
    }

    public void NotifyDeployerAnalysisUpdated()
    {
        this.RaisePropertyChanged(nameof(HasDeployerAnalysis));
        this.RaisePropertyChanged(nameof(DeployerRiskLabel));
        this.RaisePropertyChanged(nameof(DeployerRiskBrush));
        this.RaisePropertyChanged(nameof(DeployerSummary));
        this.RaisePropertyChanged(nameof(SecurityChecksSummary));
    }

    public string SignalProfileLabel => $"{StrategyLabel} | {PairAgeLabel} | {DexQualityLabel} | {SignalConfirmationLabel}";
    public decimal CurrentPriceUsd => TokenInfo.PriceUsd;
    public decimal TakeProfitTargetPriceUsd =>
        EntryPriceUsd <= 0m
            ? 0m
            : EntryPriceUsd * (1m + (TakeProfitPercent / 100m));

    public decimal StopLossTargetPriceUsd =>
        EntryPriceUsd <= 0m
            ? 0m
            : EntryPriceUsd * (1m - (StopLossPercent / 100m));

    public decimal TrailingStopFloorPriceUsd =>
        PeakPriceUsd <= 0m
            ? 0m
            : PeakPriceUsd * (1m - (TrailingStopPercent / 100m));

    public decimal PaperPnlPercent =>
        UsesLiveAccounting
            ? LivePnlPercent
            : EntryPriceUsd <= 0m
                ? 0m
                : ((CurrentPriceUsd - EntryPriceUsd) / EntryPriceUsd) * 100m;

    public decimal CurrentNativeValue =>
        UsesLiveAccounting
            ? Math.Max(0m, TrackedTokenAmount * Math.Max(0m, TokenInfo.PriceNative))
            : 0m;

    public decimal LivePnlPercent =>
        !UsesLiveAccounting || LiveEntryCostNative <= 0m
            ? 0m
            : ((LiveRealizedProceedsNative + CurrentNativeValue - LiveEntryCostNative) / LiveEntryCostNative) * 100m;

    public decimal ClosedLivePnlPercent =>
        !UsesLiveAccounting || LiveEntryCostNative <= 0m
            ? 0m
            : ((LiveRealizedProceedsNative - LiveEntryCostNative) / LiveEntryCostNative) * 100m;

    public decimal LiveNetPnlNative =>
        !UsesLiveAccounting
            ? 0m
            : LiveRealizedProceedsNative + CurrentNativeValue - LiveEntryCostNative;

    public string PaperPnlLabel =>
        EntryPriceUsd <= 0m
            ? "PnL n/a"
            : $"PnL {PaperPnlPercent:+0.##;-0.##;0}%";

    public string CurrentPriceLabel =>
        CurrentPriceUsd > 0m
            ? $"Current: $ {CurrentPriceUsd:N8}"
            : "Current: waiting for quote";

    public string MarketTickLabel =>
        TokenInfo.LastUpdatedUtc == DateTime.MinValue
            ? "Market tick pending"
            : $"Market tick: {TokenInfo.LastUpdatedUtc.ToLocalTime():HH:mm:ss}";

    public string PnlAccentHex => PaperPnlPercent switch
    {
        > 0m => "#3DDC84",
        < 0m => "#FF5D73",
        _ => "#8FA3B8"
    };

    public string TakeProfitSummary =>
        !AutoTakeProfitEnabled
            ? "Take-profit off"
            : EntryPriceUsd <= 0m
                ? $"Take-profit armed at +{TakeProfitPercent:0.##}%"
                : $"Take-profit at $ {TakeProfitTargetPriceUsd:N8} (+{TakeProfitPercent:0.##}%)";

    public string StopLossSummary =>
        !AutoStopLossEnabled
            ? "Stop-loss off"
            : EntryPriceUsd <= 0m
                ? $"Stop-loss armed at -{StopLossPercent:0.##}%"
                : $"Stop-loss at $ {StopLossTargetPriceUsd:N8} (-{StopLossPercent:0.##}%)";

    public string TrailingStopSummary =>
        !AutoTrailingStopEnabled
            ? "Trailing stop off"
            : PeakPriceUsd <= 0m
                ? $"Trailing stop armed at {TrailingStopPercent:0.##}%"
                : $"Trailing floor $ {TrailingStopFloorPriceUsd:N8} from peak $ {PeakPriceUsd:N8}";

    public string PartialTakeProfitSummary =>
        !PartialTakeProfitEnabled
            ? "Partial take-profit off"
            : PartialTakeProfitExecuted
                ? $"Partial executed: sold {PartialTakeProfitSellPercent:0.##}% at +{PartialTakeProfitTriggerPercent:0.##}%"
                : $"Partial sell {PartialTakeProfitSellPercent:0.##}% at +{PartialTakeProfitTriggerPercent:0.##}%";

    public string BreakEvenSummary =>
        !BreakEvenEnabled
            ? "Break-even off"
            : BreakEvenTriggered
                ? "Break-even triggered"
                : BreakEvenArmed
                    ? $"Break-even armed from +{BreakEvenTriggerPercent:0.##}%"
                    : $"Arm break-even at +{BreakEvenTriggerPercent:0.##}%";

    public string TakeProfitStatus =>
        !AutoTakeProfitEnabled
            ? "Manual exit only"
            : TakeProfitTriggered || (EntryPriceUsd > 0m && CurrentPriceUsd >= TakeProfitTargetPriceUsd)
                ? "Target reached"
                : EntryPriceUsd <= 0m
                    ? "Waiting for entry price"
                    : $"Live PnL {PaperPnlPercent:+0.##;-0.##;0}%";

    public string TakeProfitAccentHex =>
        TakeProfitTriggered || (EntryPriceUsd > 0m && CurrentPriceUsd >= TakeProfitTargetPriceUsd)
            ? "#3DDC84"
            : "#8FA3B8";

    public string ExitRuleSummary =>
        $"{TakeProfitSummary} | {StopLossSummary} | {TrailingStopSummary}";

    public string ExitStatus
    {
        get
        {
            if (TakeProfitTriggered)
            {
                return "Take-profit triggered";
            }

            if (BreakEvenTriggered)
            {
                return "Break-even triggered";
            }

            if (StopLossTriggered)
            {
                return "Stop-loss triggered";
            }

            if (TrailingStopTriggered)
            {
                return "Trailing stop triggered";
            }

            if (AutoTrailingStopEnabled && PeakPriceUsd > EntryPriceUsd)
            {
                return $"Trailing guard active from peak {PeakPriceUsd:N8}";
            }

            if (PartialTakeProfitExecuted)
            {
                return $"Runner active with {PositionSizePercent:0.#}% size left";
            }

            if (BreakEvenArmed)
            {
                return "Break-even guard armed";
            }

            if (AutoStopLossEnabled)
            {
                return $"Loss guard at {PaperPnlPercent:+0.##;-0.##;0}% live PnL";
            }

            return TakeProfitStatus;
        }
    }

    public string ExitAccentHex
    {
        get
        {
            if (TakeProfitTriggered)
            {
                return "#3DDC84";
            }

            if (PartialTakeProfitExecuted)
            {
                return "#21E6C1";
            }

            if (BreakEvenTriggered || StopLossTriggered || TrailingStopTriggered)
            {
                return "#FF8A4C";
            }

            return "#8FA3B8";
        }
    }

    public string TrackedTokenAmountLabel =>
        TrackedTokenAmount > 0m
            ? $"Tracked size: {TrackedTokenAmount:0.########}"
            : "Tracked size pending";

    public string PeakPriceLabel =>
        PeakPriceUsd > 0m
            ? $"Peak: $ {PeakPriceUsd:N8}"
            : "Peak pending";

    public string SizeStatusLabel => $"Position size: {PositionSizePercent:0.#}%";

    public string Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public string PositionMeta =>
        OpenedAtLocal is null
            ? "Waiting"
            : UsesLiveAccounting
                ? $"Opened {OpenedAtLocal:HH:mm:ss} | Cost {LiveEntryCostNative:0.####} {TokenInfo.QuoteSymbol} | Realized {LiveRealizedProceedsNative:0.####} {TokenInfo.QuoteSymbol} | Size {PositionSizePercent:0.#}%"
                : $"Opened {OpenedAtLocal:HH:mm:ss} | {EntryAmountBnb:0.####} BNB | Size {PositionSizePercent:0.#}%";

    public void UpdateFromToken(DexTokenInfo token)
    {
        TokenInfo.PriceUsd = token.PriceUsd;
        TokenInfo.PriceNative = token.PriceNative;
        TokenInfo.PriceChange5m = token.PriceChange5m;
        TokenInfo.PriceChange1h = token.PriceChange1h;
        TokenInfo.PriceChange24h = token.PriceChange24h;
        TokenInfo.Volume24h = token.Volume24h;
        TokenInfo.LiquidityUsd = token.LiquidityUsd;
        TokenInfo.MarketCap = token.MarketCap;
        TokenInfo.LastUpdatedUtc = token.LastUpdatedUtc;
        TokenInfo.ObservedFirstSeenUtc = token.ObservedFirstSeenUtc;
        TokenInfo.DexQualityScore = token.DexQualityScore;
        TokenInfo.DexQualityLabel = token.DexQualityLabel;
        TokenInfo.SignalSourceKind = token.SignalSourceKind;
        TokenInfo.SignalSourceLabel = token.SignalSourceLabel;
        TokenInfo.SignalConfirmationLabel = token.SignalConfirmationLabel;
        TokenInfo.SignalSourceCount = token.SignalSourceCount;
        TokenInfo.WatchlistMatched = token.WatchlistMatched;
        TokenInfo.WatchlistMatchText = token.WatchlistMatchText;
        TokenInfo.OwnershipSignalStatus = token.OwnershipSignalStatus;

        if (token.PriceUsd > PeakPriceUsd)
        {
            PeakPriceUsd = token.PriceUsd;
        }

        SignalSourceLabel = token.SignalSourceLabel;
        SignalConfirmationLabel = token.SignalConfirmationLabel;

        this.RaisePropertyChanged(nameof(CurrentPriceUsd));
        this.RaisePropertyChanged(nameof(CurrentPriceLabel));
        this.RaisePropertyChanged(nameof(MarketTickLabel));
        this.RaisePropertyChanged(nameof(SignalProfileLabel));
        this.RaisePropertyChanged(nameof(TakeProfitTargetPriceUsd));
        this.RaisePropertyChanged(nameof(StopLossTargetPriceUsd));
        this.RaisePropertyChanged(nameof(TrailingStopFloorPriceUsd));
        this.RaisePropertyChanged(nameof(TakeProfitSummary));
        this.RaisePropertyChanged(nameof(StopLossSummary));
        this.RaisePropertyChanged(nameof(TrailingStopSummary));
        this.RaisePropertyChanged(nameof(PartialTakeProfitSummary));
        this.RaisePropertyChanged(nameof(BreakEvenSummary));
        this.RaisePropertyChanged(nameof(ExitRuleSummary));
        this.RaisePropertyChanged(nameof(TakeProfitStatus));
        this.RaisePropertyChanged(nameof(TakeProfitAccentHex));
        this.RaisePropertyChanged(nameof(ExitStatus));
        this.RaisePropertyChanged(nameof(ExitAccentHex));
        this.RaisePropertyChanged(nameof(SizeStatusLabel));
        this.RaisePropertyChanged(nameof(PaperPnlPercent));
        this.RaisePropertyChanged(nameof(PaperPnlLabel));
        this.RaisePropertyChanged(nameof(PnlAccentHex));
        RaiseLivePnlProperties();
    }

    private void RaiseLivePnlProperties()
    {
        this.RaisePropertyChanged(nameof(CurrentNativeValue));
        this.RaisePropertyChanged(nameof(LivePnlPercent));
        this.RaisePropertyChanged(nameof(ClosedLivePnlPercent));
        this.RaisePropertyChanged(nameof(LiveNetPnlNative));
        this.RaisePropertyChanged(nameof(PaperPnlPercent));
        this.RaisePropertyChanged(nameof(PaperPnlLabel));
        this.RaisePropertyChanged(nameof(PnlAccentHex));
        this.RaisePropertyChanged(nameof(TakeProfitStatus));
        this.RaisePropertyChanged(nameof(ExitStatus));
    }
}
