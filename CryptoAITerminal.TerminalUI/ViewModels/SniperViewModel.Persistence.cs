// Wave-2 split of SniperViewModel: settings/history/audit-trail load+save +
// open-position restore. All file I/O for the sniper lives here so that the
// main file stays focused on lifecycle and execution logic. Pure move — no
// behaviour change.

using System;
using System.IO;
using System.Linq;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public partial class SniperViewModel
{
    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return;
            }

            var settings = AtomicJsonFile.Read<SniperSettingsSnapshot>(_settingsFilePath);
            if (settings is null)
            {
                return;
            }

            _autoBuyEnabled = settings.AutoBuyEnabled;
            _buyAmountBnb = settings.BuyAmountBnb;
            _minLiquidityUsd = settings.MinLiquidityUsd;
            _maxLiquidityUsd = settings.MaxLiquidityUsd;
            _minVolume24hUsd = settings.MinVolume24hUsd;
            _minMomentum5m = settings.MinMomentum5m;
            _maxMarketCapUsd = settings.MaxMarketCapUsd;
            _maxRiskScore = settings.MaxRiskScore;
            _minPairAgeMinutes = Math.Max(0m, settings.MinPairAgeMinutes);
            _maxPairAgeMinutes = settings.MaxPairAgeMinutes <= 0m ? _maxPairAgeMinutes : Math.Max(0m, settings.MaxPairAgeMinutes);
            _launchMaxPairAgeMinutes = settings.LaunchMaxPairAgeMinutes <= 0m ? _launchMaxPairAgeMinutes : Math.Max(1m, settings.LaunchMaxPairAgeMinutes);
            _warmPairMinAgeMinutes = settings.WarmPairMinAgeMinutes <= 0m ? _warmPairMinAgeMinutes : Math.Max(0m, settings.WarmPairMinAgeMinutes);
            _selectedStrategyMode = string.IsNullOrWhiteSpace(settings.SelectedStrategyMode) ? _selectedStrategyMode : settings.SelectedStrategyMode;
            _maxVolumeToLiquidityRatio = settings.MaxVolumeToLiquidityRatio;
            _maxMarketCapToLiquidityRatio = settings.MaxMarketCapToLiquidityRatio;
            _enableExecutionGuard = settings.EnableExecutionGuard;
            _blockSuspectedHoneypots = settings.BlockSuspectedHoneypots;
            _paperTradingEnabled = settings.PaperTradingEnabled;
            _autoTakeProfitEnabled = settings.AutoTakeProfitEnabled;
            _takeProfitPercent = settings.TakeProfitPercent <= 0m ? _takeProfitPercent : settings.TakeProfitPercent;
            _autoStopLossEnabled = settings.AutoStopLossEnabled;
            _stopLossPercent = settings.StopLossPercent <= 0m ? _stopLossPercent : settings.StopLossPercent;
            _autoTrailingStopEnabled = settings.AutoTrailingStopEnabled;
            _trailingStopPercent = settings.TrailingStopPercent <= 0m ? _trailingStopPercent : settings.TrailingStopPercent;
            _partialTakeProfitEnabled = settings.PartialTakeProfitEnabled;
            _partialTakeProfitTriggerPercent = settings.PartialTakeProfitTriggerPercent <= 0m ? _partialTakeProfitTriggerPercent : settings.PartialTakeProfitTriggerPercent;
            _partialTakeProfitSellPercent = settings.PartialTakeProfitSellPercent <= 0m ? _partialTakeProfitSellPercent : settings.PartialTakeProfitSellPercent;
            _breakEvenEnabled = settings.BreakEvenEnabled;
            _breakEvenTriggerPercent = settings.BreakEvenTriggerPercent <= 0m ? _breakEvenTriggerPercent : settings.BreakEvenTriggerPercent;
            _maxSimulatedBuyTaxPercent = settings.MaxSimulatedBuyTaxPercent;
            _maxSimulatedSellTaxPercent = settings.MaxSimulatedSellTaxPercent;
            _cooldownSeconds = settings.CooldownSeconds;
            _maxSimultaneousPositions = settings.MaxSimultaneousPositions;
            _maxBuysPerSession = settings.MaxBuysPerSession;
            if (settings.MaxDailyLiveLossNative > 0m ||
                settings.MaxExposurePerChainNative > 0m ||
                settings.MaxExposurePerWalletNative > 0m ||
                settings.MaxConsecutiveLiveLosses > 0 ||
                settings.HardCapTotalLiveExposureNative > 0m)
            {
                _maxDailyLiveLossNative = Math.Max(0m, settings.MaxDailyLiveLossNative);
                _maxExposurePerChainNative = Math.Max(0m, settings.MaxExposurePerChainNative);
                _maxExposurePerWalletNative = Math.Max(0m, settings.MaxExposurePerWalletNative);
                _maxConsecutiveLiveLosses = Math.Max(0, settings.MaxConsecutiveLiveLosses);
                _hardCapTotalLiveExposureNative = Math.Max(0m, settings.HardCapTotalLiveExposureNative);
            }
            if (settings.TinyDryRunCapNative > 0m)
            {
                _tinyDryRunCapNative = Math.Max(0m, settings.TinyDryRunCapNative);
            }
            _selectedScanVenue = NormalizeVenue(settings.SelectedScanVenue);
            _selectedTradingProfile = NormalizeTradingProfile(settings.SelectedTradingProfile);
            _selectedScalpPreset = NormalizeScalpPreset(settings.SelectedScalpPreset);
            _futuresLeverage = settings.FuturesLeverage <= 0 ? _futuresLeverage : Math.Clamp(settings.FuturesLeverage, 1, 50);
            _selectedFuturesBias = NormalizeFuturesBias(settings.SelectedFuturesBias);
            if (!PaperTradingEnabled && IsCexVenue)
            {
                PaperTradingEnabled = true;
            }
            _requireBnbQuote = settings.RequireBnbQuote;
            _preferStableQuote = settings.PreferStableQuote;
            _enabledChainsText = string.IsNullOrWhiteSpace(settings.EnabledChainsText) ? _enabledChainsText : settings.EnabledChainsText;
            _whitelistText = settings.WhitelistText ?? string.Empty;
            _watchlistText = settings.WatchlistText ?? string.Empty;
            _blacklistText = string.IsNullOrWhiteSpace(settings.BlacklistText) ? _blacklistText : settings.BlacklistText;
            _selectedPresetName = string.IsNullOrWhiteSpace(settings.SelectedPresetName) ? _selectedPresetName : settings.SelectedPresetName;
            if (settings.SlippagePercent >= 0.1m && settings.SlippagePercent <= 50m)
            {
                _slippagePercent = settings.SlippagePercent;
            }

            RaiseSafetyProperties();
        }
        catch (Exception ex)
        {
            var backupPath = AtomicJsonFile.BackupCorruptFile(_settingsFilePath);
            var backupName = string.IsNullOrWhiteSpace(backupPath) ? "no backup" : Path.GetFileName(backupPath);
            PushLog($"Sniper settings load failed; corrupt file backup: {backupName}. {ex.Message}", false);
        }
    }

    private void PersistSettings()
    {
        try
        {
            var snapshot = new SniperSettingsSnapshot
            {
                AutoBuyEnabled = _autoBuyEnabled,
                BuyAmountBnb = _buyAmountBnb,
                MinLiquidityUsd = _minLiquidityUsd,
                MaxLiquidityUsd = _maxLiquidityUsd,
                MinVolume24hUsd = _minVolume24hUsd,
                MinMomentum5m = _minMomentum5m,
                MaxMarketCapUsd = _maxMarketCapUsd,
                MaxRiskScore = _maxRiskScore,
                MinPairAgeMinutes = _minPairAgeMinutes,
                MaxPairAgeMinutes = _maxPairAgeMinutes,
                LaunchMaxPairAgeMinutes = _launchMaxPairAgeMinutes,
                WarmPairMinAgeMinutes = _warmPairMinAgeMinutes,
                SelectedStrategyMode = _selectedStrategyMode,
                MaxVolumeToLiquidityRatio = _maxVolumeToLiquidityRatio,
                MaxMarketCapToLiquidityRatio = _maxMarketCapToLiquidityRatio,
                EnableExecutionGuard = _enableExecutionGuard,
                BlockSuspectedHoneypots = _blockSuspectedHoneypots,
                PaperTradingEnabled = _paperTradingEnabled,
                AutoTakeProfitEnabled = _autoTakeProfitEnabled,
                TakeProfitPercent = _takeProfitPercent,
                AutoStopLossEnabled = _autoStopLossEnabled,
                StopLossPercent = _stopLossPercent,
                AutoTrailingStopEnabled = _autoTrailingStopEnabled,
                TrailingStopPercent = _trailingStopPercent,
                PartialTakeProfitEnabled = _partialTakeProfitEnabled,
                PartialTakeProfitTriggerPercent = _partialTakeProfitTriggerPercent,
                PartialTakeProfitSellPercent = _partialTakeProfitSellPercent,
                BreakEvenEnabled = _breakEvenEnabled,
                BreakEvenTriggerPercent = _breakEvenTriggerPercent,
                MaxSimulatedBuyTaxPercent = _maxSimulatedBuyTaxPercent,
                MaxSimulatedSellTaxPercent = _maxSimulatedSellTaxPercent,
                CooldownSeconds = _cooldownSeconds,
                MaxSimultaneousPositions = _maxSimultaneousPositions,
                MaxBuysPerSession = _maxBuysPerSession,
                MaxDailyLiveLossNative = _maxDailyLiveLossNative,
                MaxExposurePerChainNative = _maxExposurePerChainNative,
                MaxExposurePerWalletNative = _maxExposurePerWalletNative,
                MaxConsecutiveLiveLosses = _maxConsecutiveLiveLosses,
                HardCapTotalLiveExposureNative = _hardCapTotalLiveExposureNative,
                TinyDryRunCapNative = _tinyDryRunCapNative,
                SelectedScanVenue = _selectedScanVenue,
                SelectedTradingProfile = _selectedTradingProfile,
                SelectedScalpPreset = _selectedScalpPreset,
                FuturesLeverage = _futuresLeverage,
                SelectedFuturesBias = _selectedFuturesBias,
                RequireBnbQuote = _requireBnbQuote,
                PreferStableQuote = _preferStableQuote,
                EnabledChainsText = _enabledChainsText,
                WhitelistText = _whitelistText,
                WatchlistText = _watchlistText,
                BlacklistText = _blacklistText,
                SelectedPresetName = _selectedPresetName,
                SlippagePercent = _slippagePercent
            };

            AtomicJsonFile.Write(_settingsFilePath, snapshot, StorageJsonOptions);
        }
        catch (Exception ex)
        {
            PushLog($"Sniper settings save failed: {ex.Message}", false);
        }
    }

    private void LoadPaperTradeHistory()
    {
        try
        {
            foreach (var record in _tradeHistoryService.Load(_paperHistoryFilePath).OrderByDescending(static record => record.ClosedAtLocal))
            {
                PaperTradeHistory.Add(record);
            }

            RaiseSafetyProperties();
        }
        catch (Exception ex)
        {
            var backupPath = AtomicJsonFile.BackupCorruptFile(_paperHistoryFilePath);
            PushLog($"Paper sniper history load failed; backup: {Path.GetFileName(backupPath)}. {ex.Message}", false);
        }
    }

    private void LoadLiveTradeHistory()
    {
        try
        {
            foreach (var record in _tradeHistoryService.Load(_liveHistoryFilePath).OrderByDescending(static record => record.ClosedAtLocal))
            {
                LiveTradeHistory.Add(record);
            }

            RaiseSafetyProperties();
        }
        catch (Exception ex)
        {
            PushLog($"Paper sniper history save failed: {ex.Message}", false);
        }
    }

    private void PersistPaperTradeHistory()
    {
        try
        {
            _tradeHistoryService.Save(_paperHistoryFilePath, PaperTradeHistory);
        }
        catch (Exception ex)
        {
            var backupPath = AtomicJsonFile.BackupCorruptFile(_liveHistoryFilePath);
            PushLog($"Live sniper history load failed; backup: {Path.GetFileName(backupPath)}. {ex.Message}", false);
        }
    }

    private void PersistLiveTradeHistory()
    {
        try
        {
            _tradeHistoryService.Save(_liveHistoryFilePath, LiveTradeHistory);
        }
        catch (Exception ex)
        {
            PushLog($"Live sniper history save failed: {ex.Message}", false);
        }
    }

    private void LoadExecutionAuditTrail()
    {
        try
        {
            foreach (var record in _auditService.Load(_auditTrailFilePath).OrderByDescending(static record => record.LoggedAtLocal))
            {
                ExecutionAuditTrail.Add(record);
            }
        }
        catch (Exception ex)
        {
            var backupPath = AtomicJsonFile.BackupCorruptFile(_auditTrailFilePath);
            PushLog($"Sniper audit trail load failed; backup: {Path.GetFileName(backupPath)}. {ex.Message}", false);
        }
    }

    private void PersistExecutionAuditTrail()
    {
        try
        {
            _auditService.Save(_auditTrailFilePath, ExecutionAuditTrail);
        }
        catch (Exception ex)
        {
            PushLog($"Sniper audit trail save failed: {ex.Message}", false);
        }
    }

    private void RestoreOpenLivePositions()
    {
        try
        {
            foreach (var position in _openPositionStateService.Load(_liveOpenPositionsFilePath))
            {
                OpenPositions.Add(position);
                _executedBuys.Add(position.TokenInfo.TokenAddress);
                AppendExecutionAuditRecord(_auditService.CreateRecoveryRecord(
                    position,
                    "position-restored",
                    "Application restored an open live position from local session state.",
                    rpcRecoveryObserved: false));
            }

            RunLoggedAsync(RefreshOpenPositionMarketDataAsync, "Sniper position restore refresh");
        }
        catch (Exception ex)
        {
            var backupPath = AtomicJsonFile.BackupCorruptFile(_liveOpenPositionsFilePath);
            PushLog($"Open sniper positions restore failed; backup: {Path.GetFileName(backupPath)}. {ex.Message}", false);
        }
    }

    private void PersistOpenLivePositions()
    {
        try
        {
            _openPositionStateService.Save(_liveOpenPositionsFilePath, OpenPositions);
        }
        catch (Exception ex)
        {
            PushLog($"Open sniper positions save failed: {ex.Message}", false);
        }
    }

    private void AppendExecutionAuditRecord(SniperExecutionAuditRecordViewModel record)
    {
        ExecutionAuditTrail.Insert(0, record);
        TrimCollection(ExecutionAuditTrail, 250);
        PersistExecutionAuditTrail();
        RebuildLiveReadinessStatuses();
        RaiseSafetyProperties();
    }

    private void RebuildLiveReadinessStatuses()
    {
        LiveReadyStatuses.Clear();
        foreach (var status in _liveReadinessService.BuildStatuses(ExecutionAuditTrail))
        {
            LiveReadyStatuses.Add(status);
        }
    }

    private void ArchivePaperTrade(SniperCandidateViewModel candidate)
    {
        var record = _tradeHistoryService.CreatePaperRecord(candidate);
        if (record is null)
        {
            return;
        }

        PaperTradeHistory.Insert(0, record);
        TrimCollection(PaperTradeHistory, 120);
        PersistPaperTradeHistory();
    }

    private void ArchiveLiveTrade(SniperCandidateViewModel candidate, string exitReason)
    {
        var record = _tradeHistoryService.CreateLiveRecord(candidate, exitReason);
        if (record is null)
        {
            return;
        }

        LiveTradeHistory.Insert(0, record);
        TrimCollection(LiveTradeHistory, 120);
        PersistLiveTradeHistory();
        ApplyEmergencyRiskStopIfNeeded(true);
    }
}
