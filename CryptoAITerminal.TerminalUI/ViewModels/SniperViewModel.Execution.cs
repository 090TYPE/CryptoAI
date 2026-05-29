using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public partial class SniperViewModel
{
    private async Task BuyCandidateAsync(SniperCandidateViewModel? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        if (candidate.IsExecutionBlocked)
        {
            candidate.Status = "Execution blocked";
            PushLog($"Buy blocked for {candidate.DisplayName}: {candidate.ExecutionBlockReason}", false);
            UpdateLatestExecution(candidate);
            return;
        }

        var chainProfile = GetChainProfile(candidate.TokenInfo.ChainId);
        bool isCexLiveMode = !PaperTradingEnabled && _cexLiveExecutionEnabled && _cexLiveAcknowledged && IsCexVenue;

        if (!PaperTradingEnabled && !isCexLiveMode && !chainProfile.SupportsLiveExecution)
        {
            candidate.Status = "Live execution unavailable";
            candidate.ExecutionVerdict = $"Live execution unavailable - {candidate.DisplayName}";
            candidate.ExecutionBlockReason = $"{chainProfile.DisplayName} is enabled for scanning and paper trading, but live execution is not wired to a router connector in this build.";
            PushLog($"Live buy blocked for {candidate.DisplayName}: {candidate.ExecutionBlockReason}", false);
            UpdateLatestExecution(candidate);
            return;
        }

        if (!PaperTradingEnabled && !isCexLiveMode && !_walletWorkspace.CanTradeChainId(candidate.TokenInfo.ChainId))
        {
            candidate.Status = "Wallet chain mismatch";
            candidate.ExecutionVerdict = $"Wallet network mismatch - {candidate.DisplayName}";
            candidate.ExecutionBlockReason = $"The active wallet session is armed for {_walletWorkspace.SelectedNetwork}, but this pair is on {chainProfile.DisplayName}. Switch the wallet network before live execution.";
            PushLog($"Live buy blocked for {candidate.DisplayName}: {candidate.ExecutionBlockReason}", false);
            UpdateLatestExecution(candidate);
            return;
        }

        // Optional external security scan (GoPlus + Honeypot.is / RugCheck)
        if (EnableExternalSecurityScan && !candidate.SecurityScanComplete)
        {
            candidate.Status = "Scanning security…";
            try
            {
                var scan = await _tokenSecurityService.ScanAsync(
                    candidate.TokenInfo.TokenAddress,
                    candidate.TokenInfo.ChainId);
                candidate.SecurityScanResult = scan;
                candidate.SecurityScanComplete = true;

                if (scan.IsHoneypot && BlockSuspectedHoneypots)
                {
                    candidate.IsSuspectedHoneypot = true;
                    candidate.IsExecutionBlocked = true;
                    candidate.ExecutionBlockReason = $"External scanner ({scan.Source}): {scan.Verdict}";
                    candidate.Status = "Blocked by external scan";
                    PushLog($"Security scan blocked {candidate.DisplayName}: {scan.Verdict} flags: {string.Join(", ", scan.Flags)}", false);
                    UpdateLatestExecution(candidate);
                    return;
                }
            }
            catch (Exception ex)
            {
                PushLog($"Security scan failed for {candidate.DisplayName}: {ex.Message}", false);
                // Non-fatal — continue with buy
            }
        }

        if (PaperTradingEnabled)
        {
            if (_paperExecutedBuys.Contains(candidate.TokenInfo.TokenAddress))
            {
                candidate.Status = "Already paper-bought";
                return;
            }

            var (paperCanEnter, paperReason) = EvaluateEntrySafety(candidate, false);
            if (!paperCanEnter)
            {
                candidate.Status = "Blocked by safety";
                PushLog($"Paper buy blocked for {candidate.DisplayName}: {paperReason}", false);
                RaiseSafetyProperties();
                return;
            }

            candidate.Status = "Paper filled";
            candidate.WasBought = true;
            candidate.IsOpenPosition = true;
            candidate.EntryAmountBnb = BuyAmountBnb;
            candidate.EntryPriceUsd = candidate.TokenInfo.PriceUsd;
            candidate.OpenedAtLocal = DateTime.Now;
            candidate.AutoTakeProfitEnabled = AutoTakeProfitEnabled;
            candidate.TakeProfitPercent = TakeProfitPercent;
            candidate.AutoStopLossEnabled = AutoStopLossEnabled;
            candidate.StopLossPercent = StopLossPercent;
            candidate.AutoTrailingStopEnabled = AutoTrailingStopEnabled;
            candidate.TrailingStopPercent = TrailingStopPercent;
            candidate.PartialTakeProfitEnabled = PartialTakeProfitEnabled;
            candidate.PartialTakeProfitTriggerPercent = PartialTakeProfitTriggerPercent;
            candidate.PartialTakeProfitSellPercent = PartialTakeProfitSellPercent;
            candidate.BreakEvenEnabled = BreakEvenEnabled;
            candidate.BreakEvenTriggerPercent = BreakEvenTriggerPercent;
            candidate.PositionSizePercent = 100m;
            candidate.PeakPriceUsd = candidate.TokenInfo.PriceUsd;
            candidate.TakeProfitTriggered = false;
            candidate.StopLossTriggered = false;
            candidate.TrailingStopTriggered = false;
            candidate.BreakEvenArmed = false;
            candidate.BreakEvenTriggered = false;
            candidate.PartialTakeProfitExecuted = false;
            _paperExecutedBuys.Add(candidate.TokenInfo.TokenAddress);
            _sessionBuyCount++;
            _lastBuyUtc = DateTime.UtcNow;
            PaperPositions.Insert(0, candidate);
            TrimCollection(PaperPositions, 25);
            PushLog($"Paper buy executed for {candidate.DisplayName} at {BuyAmountBnb:0.####} {(DetermineExecutionQuoteSymbol(candidate, null) ?? chainProfile.AllowedQuoteSymbols.FirstOrDefault() ?? "native")}.", true);
            RunLoggedAsync(RefreshOpenPositionMarketDataAsync, "Sniper position refresh");
            RaiseSafetyProperties();
            return;
        }

        if (_executedBuys.Contains(candidate.TokenInfo.TokenAddress))
        {
            candidate.Status = "Already bought";
            return;
        }

        if (!_walletWorkspace.TryApproveLiveExecution("Sniper live buy", out var executionReason))
        {
            candidate.Status = "Global paper guard";
            candidate.ExecutionVerdict = $"Global paper guard - {candidate.DisplayName}";
            candidate.ExecutionBlockReason = executionReason;
            PushLog($"Live buy blocked for {candidate.DisplayName}: {executionReason}", false);
            UpdateLatestExecution(candidate);
            RaiseSafetyProperties();
            return;
        }

        var (canEnter, safetyReason) = EvaluateEntrySafety(candidate, true);
        if (!canEnter)
        {
            candidate.Status = "Blocked by safety";
            PushLog($"Buy blocked for {candidate.DisplayName}: {safetyReason}", false);
            RaiseSafetyProperties();
            return;
        }

        // CEX live execution — routes to exchange gateway instead of DEX wallet.
        if (isCexLiveMode)
        {
            await ExecuteCexBuyAsync(candidate);
            return;
        }

        var gateway = _walletWorkspace.ActiveDexGateway;
        if (gateway is null)
        {
            candidate.Status = "Wallet not armed";
            PushLog($"Buy skipped for {candidate.DisplayName}: wallet is not trade-enabled for live execution on {chainProfile.DisplayName}.", false);
            return;
        }

        if (!await EnsureSufficientExecutionQuoteBalanceAsync(candidate, gateway))
        {
            candidate.Status = "Insufficient quote balance";
            RaiseSafetyProperties();
            return;
        }

        if (!gateway.SupportsDex(candidate.TokenInfo.DexId))
        {
            candidate.Status = "DEX connector unavailable";
            candidate.ExecutionVerdict = $"DEX connector unavailable - {candidate.DisplayName}";
            candidate.ExecutionBlockReason = $"{chainProfile.DisplayName} live execution is armed, but dex '{candidate.TokenInfo.DexId}' is not wired in the current connector set. Supported: {gateway.SupportedDexesLabel}.";
            PushLog($"Live buy blocked for {candidate.DisplayName}: {candidate.ExecutionBlockReason}", false);
            UpdateLatestExecution(candidate);
            return;
        }

        if (!await RunOnChainPreflightAsync(candidate, gateway))
        {
            RaiseSafetyProperties();
            return;
        }

        try
        {
            candidate.Status = "Buying...";
            var executionQuoteSymbol = DetermineExecutionQuoteSymbol(candidate, gateway);
            var buyResult = await gateway.ExecuteConfirmedBuyAsync(new DexBuyExecutionRequest(
                candidate.TokenInfo.TokenAddress,
                BuyAmountBnb,
                SlippagePercent,
                DexId: candidate.TokenInfo.DexId,
                SpendAssetSymbol: executionQuoteSymbol));
            candidate.Status = buyResult.BalanceVerified
                ? $"Bought: {buyResult.TransactionHash[..Math.Min(10, buyResult.TransactionHash.Length)]}..."
                : "Bought, balance sync pending";
            candidate.WasBought = true;
            candidate.IsOpenPosition = true;
            candidate.OpenedAtLocal = DateTime.Now;
            candidate.AutoTakeProfitEnabled = AutoTakeProfitEnabled;
            candidate.TakeProfitPercent = TakeProfitPercent;
            candidate.AutoStopLossEnabled = AutoStopLossEnabled;
            candidate.StopLossPercent = StopLossPercent;
            candidate.AutoTrailingStopEnabled = AutoTrailingStopEnabled;
            candidate.TrailingStopPercent = TrailingStopPercent;
            candidate.PartialTakeProfitEnabled = PartialTakeProfitEnabled;
            candidate.PartialTakeProfitTriggerPercent = PartialTakeProfitTriggerPercent;
            candidate.PartialTakeProfitSellPercent = PartialTakeProfitSellPercent;
            candidate.BreakEvenEnabled = BreakEvenEnabled;
            candidate.BreakEvenTriggerPercent = BreakEvenTriggerPercent;
            candidate.PositionSizePercent = 100m;
            candidate.PeakPriceUsd = candidate.TokenInfo.PriceUsd;
            candidate.TakeProfitTriggered = false;
            candidate.StopLossTriggered = false;
            candidate.TrailingStopTriggered = false;
            candidate.BreakEvenArmed = false;
            candidate.BreakEvenTriggered = false;
            candidate.PartialTakeProfitExecuted = false;
            candidate.TrackedTokenAmount = buyResult.ActualTokenAmountReceived;
            ApplyLiveEntryAccounting(candidate, buyResult);
            ApplyLiveBuyVerification(candidate, gateway, buyResult);
            _executedBuys.Add(candidate.TokenInfo.TokenAddress);
            _sessionBuyCount++;
            _lastBuyUtc = DateTime.UtcNow;
            OpenPositions.Insert(0, candidate);
            TrimCollection(OpenPositions, 20);
            PersistOpenLivePositions();
            AppendExecutionAuditRecord(_auditService.CreateEntryRecord(candidate, buyResult, candidate.Reason, TinyDryRunCapNative));
            PushLog(
                $"Bought {candidate.DisplayName} for {buyResult.SpendAssetAmount:0.########} {buyResult.SpendAssetSymbol ?? gateway.NativeSymbol}. Fill: {buyResult.ActualTokenAmountReceived:0.########} tokens. {buyResult.Narrative}",
                buyResult.Confirmed);
            await RunPostFillSellProbeAsync(candidate, gateway);
            RunLoggedAsync(RefreshOpenPositionMarketDataAsync, "Sniper position refresh");
        }
        catch (Exception ex)
        {
            candidate.Status = "Buy failed";
            PushLog($"Buy failed for {candidate.DisplayName}: {ex.Message}", false);
        }
        finally
        {
            RaiseSafetyProperties();
        }
    }

    private (bool CanEnter, string Reason) EvaluateEntrySafety(SniperCandidateViewModel? candidate, bool isLiveMode)
    {
        var decision = _riskPolicyService.EvaluateEntry(
            isLiveMode,
            candidate?.TokenInfo.ChainId,
            isLiveMode ? BuyAmountBnb : 0m,
            OpenPositions,
            PaperPositions,
            LiveTradeHistory,
            _sessionBuyCount,
            _lastBuyUtc,
            DateTime.Now,
            DateTime.UtcNow,
            BuildRiskLimits());
        return (decision.CanEnter, decision.Reason);
    }

    private static bool IsStableQuoteSymbol(string? symbol) =>
        string.Equals(symbol, "USDT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(symbol, "USDC", StringComparison.OrdinalIgnoreCase);

    private bool MatchesPreferredStableQuote(string? chainId, string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        return GetSupportedStableQuoteSymbolsForChain(chainId)
            .Contains(symbol.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private string? DetermineExecutionQuoteSymbol(SniperCandidateViewModel candidate, IDexTradeGateway? gateway)
    {
        if (IsCexToken(candidate.TokenInfo))
        {
            return "USDT";
        }

        if (!PreferStableQuote)
        {
            return null;
        }

        var requested = candidate.TokenInfo.QuoteSymbol?.Trim().ToUpperInvariant();
        var supportedStableRoutes = GetSupportedStableQuoteSymbolsForChain(candidate.TokenInfo.ChainId);
        if (supportedStableRoutes.Count == 0)
        {
            throw new InvalidOperationException($"Stable quote routing is enabled, but {candidate.DisplayName} has no configured stable routes on {candidate.TokenInfo.ChainId}.");
        }

        var preferred = GetPreferredStableQuoteSymbolForChain(candidate.TokenInfo.ChainId);
        if (gateway is null)
        {
            if (!string.IsNullOrWhiteSpace(requested) &&
                supportedStableRoutes.Contains(requested, StringComparer.OrdinalIgnoreCase))
            {
                return requested;
            }

            return preferred;
        }

        var supported = gateway.SupportedQuoteAssets
            .Select(static asset => asset.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(requested) &&
            supportedStableRoutes.Contains(requested, StringComparer.OrdinalIgnoreCase) &&
            supported.Contains(requested))
        {
            return requested;
        }

        if (!string.IsNullOrWhiteSpace(preferred) &&
            supported.Contains(preferred))
        {
            return preferred;
        }

        var fallback = supportedStableRoutes.FirstOrDefault(supported.Contains);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        throw new InvalidOperationException($"{gateway.NetworkName} live connector does not expose a supported stable route for {candidate.DisplayName}. Supported connector routes: {string.Join(", ", supported)}.");
    }

    private async Task<bool> EnsureSufficientExecutionQuoteBalanceAsync(SniperCandidateViewModel candidate, IDexTradeGateway gateway)
    {
        var quoteSymbol = DetermineExecutionQuoteSymbol(candidate, gateway);
        if (string.IsNullOrWhiteSpace(quoteSymbol))
        {
            return true;
        }

        if (!_walletWorkspace.TryApproveUsdRisk(BuyAmountBnb, 0m, 0m, out var riskReason))
        {
            candidate.ExecutionVerdict = $"Global risk cap blocked {candidate.DisplayName}";
            candidate.ExecutionBlockReason = riskReason;
            candidate.IsExecutionBlocked = true;
            UpdateLatestExecution(candidate);
            PushLog($"Live buy blocked for {candidate.DisplayName}: {candidate.ExecutionBlockReason}", false);
            return false;
        }

        var quoteAsset = DexQuoteAssetCatalog.Find(_walletWorkspace.SelectedNetwork, quoteSymbol);
        if (quoteAsset?.ContractAddress is null)
        {
            candidate.ExecutionVerdict = $"Quote route unavailable - {candidate.DisplayName}";
            candidate.ExecutionBlockReason = $"{quoteSymbol} route is not configured for {_walletWorkspace.SelectedNetwork}.";
            candidate.IsExecutionBlocked = true;
            UpdateLatestExecution(candidate);
            PushLog($"Live buy blocked for {candidate.DisplayName}: {candidate.ExecutionBlockReason}", false);
            return false;
        }

        var liveBalance = await gateway.GetTokenBalanceAsync(quoteAsset.ContractAddress);
        StableQuoteBalance = liveBalance;
        if (BuyAmountBnb <= liveBalance)
        {
            return true;
        }

        candidate.ExecutionVerdict = $"Insufficient {quoteSymbol} - {candidate.DisplayName}";
        candidate.ExecutionBlockReason = $"Need {BuyAmountBnb:0.######} {quoteSymbol}, available {liveBalance:0.######}.";
        candidate.IsExecutionBlocked = true;
        UpdateLatestExecution(candidate);
        PushLog($"Live buy blocked for {candidate.DisplayName}: {candidate.ExecutionBlockReason}", false);
        return false;
    }

    private void ApplyLiveBuyVerification(
        SniperCandidateViewModel candidate,
        IDexTradeGateway gateway,
        DexBuyExecutionResult buyResult)
    {
        if (buyResult.Confirmed && !buyResult.SuspectedPartialFill)
        {
            candidate.ExecutionVerdict = $"Fill confirmed - {candidate.DisplayName}";
            candidate.ExecutionBlockReason = buyResult.Narrative;
            candidate.IsExecutionBlocked = false;
            UpdateLatestExecution(candidate);
            return;
        }

        candidate.ExecutionVerdict = $"Fill verification warning - {candidate.DisplayName}";
        candidate.ExecutionBlockReason = buyResult.Narrative;
        candidate.IsExecutionBlocked = true;
        candidate.Status = buyResult.BalanceVerified
            ? "Bought, fill verification warning"
            : "Bought, size unresolved";
        UpdateLatestExecution(candidate);
        PushLog(
            $"Fill verification warning for {candidate.DisplayName}: {buyResult.Narrative} Token decimals: {buyResult.TokenDecimals}, expected {buyResult.ExpectedTokenAmount:0.########}, minimum {buyResult.MinimumTokenAmount:0.########}, actual {buyResult.ActualTokenAmountReceived:0.########}.",
            false);

        if (buyResult.ActualTokenAmountReceived <= SniperLiveExecutionService.TokenDustThreshold)
        {
            PushLog(
                $"Auto-exit is blocked for {candidate.DisplayName} until the wallet reports a positive token balance. Recheck the position on-chain before trusting automation on {gateway.NetworkName}.",
                false);
        }
    }

    private async Task<bool> RunOnChainPreflightAsync(SniperCandidateViewModel candidate, IDexTradeGateway gateway)
    {
        var quoteSymbol = DetermineExecutionQuoteSymbol(candidate, gateway);
        var probe = await gateway.ProbeSellabilityAsync(new DexSellabilityProbeRequest(
            candidate.TokenInfo.TokenAddress,
            SlippagePercent,
            DexId: candidate.TokenInfo.DexId,
            NativeAmountToProbe: quoteSymbol is null ? BuyAmountBnb : null,
            QuoteAmountToProbe: quoteSymbol is null ? null : BuyAmountBnb,
            QuoteAssetSymbol: quoteSymbol));

        candidate.ExecutionVerdict = probe.Passed
            ? $"On-chain preflight clear - {candidate.DisplayName}"
            : $"On-chain preflight failed - {candidate.DisplayName}";
        candidate.ExecutionBlockReason = probe.Narrative;
        candidate.IsExecutionBlocked = !probe.Passed;
        candidate.Status = probe.Passed ? "Preflight clear" : "Blocked by on-chain preflight";
        UpdateLatestExecution(candidate);

        if (probe.Passed)
        {
            PushLog($"On-chain preflight passed for {candidate.DisplayName}: {probe.Narrative}", true);
            return true;
        }

        PushLog($"On-chain preflight blocked {candidate.DisplayName}: {probe.Narrative}", false);
        return false;
    }

    private async Task RunPostFillSellProbeAsync(SniperCandidateViewModel candidate, IDexTradeGateway gateway)
    {
        var existingExecutionWarning = candidate.ExecutionVerdict.StartsWith("Fill verification warning", StringComparison.OrdinalIgnoreCase)
            ? candidate.ExecutionBlockReason
            : null;
        var quoteSymbol = DetermineExecutionQuoteSymbol(candidate, gateway);

        if (candidate.TrackedTokenAmount <= 0m)
        {
            candidate.ExecutionVerdict = $"Sell path uncertain - {candidate.DisplayName}";
            candidate.ExecutionBlockReason = string.IsNullOrWhiteSpace(existingExecutionWarning)
                ? "Buy filled, but the wallet did not report a positive token balance delta. A real sell simulation could not be armed automatically."
                : $"{existingExecutionWarning} Sell-path note: buy filled, but the wallet did not report a positive token balance delta. A real sell simulation could not be armed automatically.";
            candidate.IsExecutionBlocked = true;
            candidate.Status = "Bought, sell path pending";
            UpdateLatestExecution(candidate);
            PushLog($"Post-fill sell probe could not arm {candidate.DisplayName}: {candidate.ExecutionBlockReason}", false);
            return;
        }

        var probe = await gateway.ProbeSellabilityAsync(new DexSellabilityProbeRequest(
            candidate.TokenInfo.TokenAddress,
            DexId: candidate.TokenInfo.DexId,
            TokenAmountToSell: candidate.TrackedTokenAmount,
            PrimeAllowance: true,
            QuoteAssetSymbol: quoteSymbol));

        candidate.ExecutionVerdict = probe.Passed
            ? existingExecutionWarning is null
                ? $"Sell path armed - {candidate.DisplayName}"
                : $"Sell path armed with fill warning - {candidate.DisplayName}"
            : $"Sell path failed - {candidate.DisplayName}";
        candidate.ExecutionBlockReason = string.IsNullOrWhiteSpace(existingExecutionWarning)
            ? probe.Narrative
            : $"{existingExecutionWarning} Sell-path note: {probe.Narrative}";
        candidate.IsExecutionBlocked = !probe.Passed || existingExecutionWarning is not null;
        if (!probe.Passed)
        {
            candidate.Status = "Bought, sell path at risk";
        }

        UpdateLatestExecution(candidate);
        PushLog(
            probe.Passed
                ? $"Post-fill sell probe passed for {candidate.DisplayName}: {probe.Narrative}"
                : $"Post-fill sell probe failed for {candidate.DisplayName}: {probe.Narrative}",
            probe.Passed);
    }

    private void ApplyLiveEntryAccounting(SniperCandidateViewModel candidate, DexBuyExecutionResult buyResult)
    {
        _liveExecutionService.ApplyConfirmedEntry(candidate, buyResult);
    }

    private Task<SniperLiveExitExecutionResult> ExecuteLiveSellWithRetriesAsync(
        SniperCandidateViewModel position,
        IDexTradeGateway gateway,
        decimal sellFraction,
        string exitLabel)
    {
        return _liveExecutionService.ExecuteSellWithRetriesAsync(position, gateway, sellFraction, exitLabel, SlippagePercent);
    }

    private Task<decimal?> TryReadTokenBalanceAsync(IDexTradeGateway gateway, string tokenAddress)
    {
        return _liveExecutionService.TryReadTokenBalanceAsync(gateway, tokenAddress);
    }

    private void RecalculateLivePositionSize(SniperCandidateViewModel position)
    {
        _liveExecutionService.RecalculateLivePositionSize(position);
    }

    private void MarkManualCloseRequired(SniperCandidateViewModel position, string reason)
    {
        position.Status = "Emergency manual-close required";
        position.ExecutionVerdict = $"Emergency manual close - {position.DisplayName}";
        position.ExecutionBlockReason = reason;
        position.IsExecutionBlocked = true;
        UpdateLatestExecution(position);
        PushLog($"Emergency manual close required for {position.DisplayName}: {reason}", false);
        AppendExecutionAuditRecord(_auditService.CreateRecoveryRecord(position, "rpc-recovery", reason, rpcRecoveryObserved: true));
        RaiseSafetyProperties();
    }

    private void ClearPosition(SniperCandidateViewModel? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        if (OpenPositions.Contains(candidate))
        {
            MarkManualCloseRequired(
                candidate,
                "Live position is still open. Use EMERGENCY CLOSE so the app can retry sells and reconcile the on-chain balance before removing it.");
            return;
        }

        if (PaperPositions.Remove(candidate))
        {
            ArchivePaperTrade(candidate);
            candidate.IsOpenPosition = false;
            candidate.Status = "Paper position cleared";
            ReleaseExecutedBuyKey(candidate, paper: true);
            PushLog($"Paper position cleared for {candidate.DisplayName}.", true);
            RaiseSafetyProperties();
        }
    }

    // Снимает блокировку повторного входа после закрытия позиции.
    // Без этого _executedBuys/_paperExecutedBuys накапливаются бесконечно
    // и через несколько часов снайпер не сможет повторно войти ни в один токен.
    private void ReleaseExecutedBuyKey(SniperCandidateViewModel? candidate, bool paper)
    {
        var key = candidate?.TokenInfo?.TokenAddress;
        if (string.IsNullOrWhiteSpace(key)) return;
        if (paper) _paperExecutedBuys.Remove(key);
        else       _executedBuys.Remove(key);
    }

    private async Task EmergencyClosePositionAsync(SniperCandidateViewModel? candidate)
    {
        if (candidate is null || !OpenPositions.Contains(candidate))
        {
            return;
        }

        if (!_walletWorkspace.TryApproveLiveExecution("Sniper emergency close", out var executionReason))
        {
            MarkManualCloseRequired(candidate, executionReason);
            return;
        }

        await ExecuteAutoExitAsync(candidate, "manual-close", false, isManualIntervention: true);
    }

    private void ResetSafetyState()
    {
        _sessionBuyCount = 0;
        _lastBuyUtc = null;
        PushLog("Safety state reset. Cooldown and session counters were cleared. Open paper and live positions were preserved.", true);
        RaiseSafetyProperties();
    }

    // Preset-application helpers live in SniperViewModel.Presets.cs

    private void UseMaxBuyAmount()
    {
        if (!PreferStableQuote)
        {
            StatusMessage = "MAX uses the shared stable quote balance. Switch the terminal quote away from native first.";
            return;
        }

        BuyAmountBnb = RoundSniperBuyAmount(StableQuoteBalance);
        StatusMessage = $"Sniper buy amount filled to MAX: {BuyAmountBnb:0.######} {PreferredStableQuoteSymbol}.";
    }

    private static decimal RoundSniperBuyAmount(decimal amount)
    {
        var rounded = Math.Round(Math.Max(0m, amount), 6, MidpointRounding.AwayFromZero);
        const decimal factor = 1_000_000m;
        return Math.Floor(rounded * factor) / factor;
    }

    private void PushLog(string message, bool positive)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => PushLog(message, positive), DispatcherPriority.Background);
            return;
        }

        DecisionLog.Insert(0, new SniperLogEntryViewModel
        {
            LocalTime = DateTime.Now,
            Message = message,
            IsPositive = positive
        });

        TrimCollection(DecisionLog, 80);
    }

    private static void TrimCollection<T>(ObservableCollection<T> collection, int maxCount)
    {
        while (collection.Count > maxCount)
        {
            collection.RemoveAt(collection.Count - 1);
        }
    }

    private void RaiseSafetyProperties()
    {
        UpdatePositionPulseTimerState();
        this.RaisePropertyChanged(nameof(AutoModeLabel));
        this.RaisePropertyChanged(nameof(ExecutionModeLabel));
        this.RaisePropertyChanged(nameof(SelectedScanVenue));
        this.RaisePropertyChanged(nameof(VenueSummary));
        this.RaisePropertyChanged(nameof(SelectedTradingProfile));
        this.RaisePropertyChanged(nameof(TradingProfileSummary));
        this.RaisePropertyChanged(nameof(ScalpPresetSummary));
        this.RaisePropertyChanged(nameof(FuturesExecutionSummary));
        this.RaisePropertyChanged(nameof(IsDexVenue));
        this.RaisePropertyChanged(nameof(IsCexSpotVenue));
        this.RaisePropertyChanged(nameof(IsFuturesVenue));
        this.RaisePropertyChanged(nameof(IsCexVenue));
        this.RaisePropertyChanged(nameof(IsScalpProfile));
        this.RaisePropertyChanged(nameof(SupportsSelectedVenueLiveExecution));
        this.RaisePropertyChanged(nameof(TakeProfitSummary));
        this.RaisePropertyChanged(nameof(ExitEngineSummary));
        this.RaisePropertyChanged(nameof(BuyAmountLabel));
        this.RaisePropertyChanged(nameof(PreferredStableQuoteSymbol));
        this.RaisePropertyChanged(nameof(StableQuoteModeLabel));
        this.RaisePropertyChanged(nameof(StableQuoteModeBrush));
        this.RaisePropertyChanged(nameof(StableQuoteBalanceLabel));
        this.RaisePropertyChanged(nameof(StableQuoteSpendableLabel));
        this.RaisePropertyChanged(nameof(HasEnoughStableQuoteBalanceForConfiguredBuy));
        this.RaisePropertyChanged(nameof(StableQuoteAvailabilityMessage));
        this.RaisePropertyChanged(nameof(StableQuoteAvailabilityBrush));
        this.RaisePropertyChanged(nameof(PresetSummary));
        this.RaisePropertyChanged(nameof(EnabledChainsSummary));
        this.RaisePropertyChanged(nameof(StrategyModeSummary));
        this.RaisePropertyChanged(nameof(QuoteRoutingSummary));
        this.RaisePropertyChanged(nameof(SupportedQuoteRoutesSummary));
        this.RaisePropertyChanged(nameof(ChainCoverageSummary));
        this.RaisePropertyChanged(nameof(TokenFilterSummary));
        this.RaisePropertyChanged(nameof(SignalFeedSummary));
        this.RaisePropertyChanged(nameof(FeedTelemetrySummary));
        this.RaisePropertyChanged(nameof(SnapshotTelemetrySummary));
        this.RaisePropertyChanged(nameof(QueueTelemetrySummary));
        this.RaisePropertyChanged(nameof(SignalNarrativeSummary));
        this.RaisePropertyChanged(nameof(WalletStatus));
        this.RaisePropertyChanged(nameof(WalletExecutionSummary));
        this.RaisePropertyChanged(nameof(SniperGuardStatusLabel));
        this.RaisePropertyChanged(nameof(SniperGuardStatusBrush));
        this.RaisePropertyChanged(nameof(SniperGuardSummary));
        this.RaisePropertyChanged(nameof(SniperGuardDetail));
        this.RaisePropertyChanged(nameof(OpenPositionPulseSummary));
        this.RaisePropertyChanged(nameof(OpenPositionTrackerStatus));
        this.RaisePropertyChanged(nameof(CanExecuteAutoBuy));
        this.RaisePropertyChanged(nameof(CanEmergencyCloseLivePositions));
        this.RaisePropertyChanged(nameof(EmergencyCloseBlockedReason));
        this.RaisePropertyChanged(nameof(OpenPositionCount));
        this.RaisePropertyChanged(nameof(FreshPairsSummary));
        this.RaisePropertyChanged(nameof(AcceptedQueueSummary));
        this.RaisePropertyChanged(nameof(OpenPositionsSummary));
        this.RaisePropertyChanged(nameof(LatestAuditSummary));
        this.RaisePropertyChanged(nameof(LatestDecisionSummary));
        this.RaisePropertyChanged(nameof(RemainingPositionSlots));
        this.RaisePropertyChanged(nameof(SessionBuyCount));
        this.RaisePropertyChanged(nameof(RemainingSessionBuys));
        this.RaisePropertyChanged(nameof(CooldownRemainingSeconds));
        this.RaisePropertyChanged(nameof(IsCooldownActive));
        this.RaisePropertyChanged(nameof(CooldownStatus));
        this.RaisePropertyChanged(nameof(SafetySummary));
        this.RaisePropertyChanged(nameof(DailyLiveLossNative));
        this.RaisePropertyChanged(nameof(TotalLiveExposureNative));
        this.RaisePropertyChanged(nameof(ConsecutiveLiveLossCount));
        this.RaisePropertyChanged(nameof(IsEmergencyRiskStopActive));
        this.RaisePropertyChanged(nameof(LiveRiskSummary));
        this.RaisePropertyChanged(nameof(LiveReadySummary));
        this.RaisePropertyChanged(nameof(PaperTradeCount));
        this.RaisePropertyChanged(nameof(LiveTradeCount));
        this.RaisePropertyChanged(nameof(WinningPaperTradeCount));
        this.RaisePropertyChanged(nameof(WinningLiveTradeCount));
        this.RaisePropertyChanged(nameof(WinRatePercent));
        this.RaisePropertyChanged(nameof(LiveWinRatePercent));
        this.RaisePropertyChanged(nameof(AveragePaperPnlPercent));
        this.RaisePropertyChanged(nameof(AverageLivePnlPercent));
        this.RaisePropertyChanged(nameof(WinRateLabel));
        this.RaisePropertyChanged(nameof(LiveWinRateLabel));
        this.RaisePropertyChanged(nameof(AveragePaperPnlLabel));
        this.RaisePropertyChanged(nameof(AverageLivePnlLabel));
        this.RaisePropertyChanged(nameof(BestPaperTradeLabel));
        this.RaisePropertyChanged(nameof(WorstPaperTradeLabel));
        this.RaisePropertyChanged(nameof(BestLiveTradeLabel));
        this.RaisePropertyChanged(nameof(WorstLiveTradeLabel));
        this.RaisePropertyChanged(nameof(CombinedTradeCount));
        this.RaisePropertyChanged(nameof(CombinedWinningTradeCount));
        this.RaisePropertyChanged(nameof(CombinedWinRatePercent));
        this.RaisePropertyChanged(nameof(CombinedWinRateLabel));
        this.RaisePropertyChanged(nameof(NetClosedPnlPercent));
        this.RaisePropertyChanged(nameof(NetClosedPnlLabel));
        this.RaisePropertyChanged(nameof(CombinedAveragePnlPercent));
        this.RaisePropertyChanged(nameof(CombinedAveragePnlLabel));
        this.RaisePropertyChanged(nameof(TradeMixLabel));
        this.RaisePropertyChanged(nameof(IsPerformancePositive));
        this.RaisePropertyChanged(nameof(OpenPaperPnlAveragePercent));
        this.RaisePropertyChanged(nameof(OpenPaperPnlLabel));
        this.RaisePropertyChanged(nameof(OpenRunnerCount));
        RebuildPerformanceCurve();
    }

    private async Task<int> RefreshTrackedPositionsAsync(IReadOnlyList<DexTokenInfo> pairs)
    {
        var byAddress = pairs.ToDictionary(token => token.TokenAddress, StringComparer.OrdinalIgnoreCase);
        var updatedCount = 0;
        foreach (var position in OpenPositions.Concat(PaperPositions).ToList())
        {
            if (byAddress.TryGetValue(position.TokenInfo.TokenAddress, out var token))
            {
                position.UpdateFromToken(token);
                await TryExecuteExitAsync(position);
                updatedCount++;
            }
        }

        return updatedCount;
    }

    private void RebuildPerformanceCurve()
    {
        PerformanceCurvePoints.Clear();

        var trades = PaperTradeHistory
            .Concat(LiveTradeHistory)
            .OrderBy(static trade => trade.ClosedAtLocal)
            .ToList();

        if (trades.Count == 0)
        {
            return;
        }

        var equity = new List<decimal>(trades.Count);
        decimal running = 0m;
        foreach (var trade in trades)
        {
            running += trade.PnlPercent;
            equity.Add(running);
        }

        var min = equity.Min();
        var max = equity.Max();
        var range = Math.Max((double)(max - min), 0.0001d);

        for (var index = 0; index < equity.Count; index++)
        {
            var normalized = (double)(equity[index] - min) / range;
            var invertedY = 100d - (normalized * 100d);
            PerformanceCurvePoints.Add(new Point(index + 1, invertedY));
        }
    }

    // Persistence + audit-trail helpers live in SniperViewModel.Persistence.cs

    private async Task TryExecuteExitAsync(SniperCandidateViewModel position)
    {
        if (!position.IsOpenPosition || position.EntryPriceUsd <= 0m)
        {
            return;
        }

        if (position.PartialTakeProfitEnabled &&
            !position.PartialTakeProfitExecuted &&
            position.PaperPnlPercent >= position.PartialTakeProfitTriggerPercent)
        {
            await ExecutePartialTakeProfitAsync(position);
        }

        if (position.BreakEvenEnabled &&
            !position.BreakEvenArmed &&
            position.PaperPnlPercent >= position.BreakEvenTriggerPercent)
        {
            position.BreakEvenArmed = true;
            position.Status = "Break-even armed";
        }

        if (position.AutoTakeProfitEnabled && position.CurrentPriceUsd >= position.TakeProfitTargetPriceUsd)
        {
            position.TakeProfitTriggered = true;
            await ExecuteAutoExitAsync(position, "take-profit", true);
            return;
        }

        if (position.AutoStopLossEnabled && position.CurrentPriceUsd <= position.StopLossTargetPriceUsd)
        {
            position.StopLossTriggered = true;
            await ExecuteAutoExitAsync(position, "stop-loss", false);
            return;
        }

        if (position.BreakEvenEnabled &&
            position.BreakEvenArmed &&
            position.CurrentPriceUsd <= position.EntryPriceUsd)
        {
            position.BreakEvenTriggered = true;
            await ExecuteAutoExitAsync(position, "break-even", true);
            return;
        }

        if (position.AutoTrailingStopEnabled &&
            position.PeakPriceUsd > position.EntryPriceUsd &&
            position.CurrentPriceUsd <= position.TrailingStopFloorPriceUsd)
        {
            position.TrailingStopTriggered = true;
            await ExecuteAutoExitAsync(position, "trailing-stop", true);
        }
    }

    private async Task ExecutePartialTakeProfitAsync(SniperCandidateViewModel position)
    {
        var sellFraction = Math.Clamp(position.PartialTakeProfitSellPercent / 100m, 0.01m, 0.99m);

        if (PaperPositions.Contains(position))
        {
            position.PartialTakeProfitExecuted = true;
            position.PositionSizePercent = Math.Max(1m, position.PositionSizePercent * (1m - sellFraction));
            position.BreakEvenArmed = position.BreakEvenEnabled || position.BreakEvenArmed;
            position.Status = $"Partial TP executed, runner {position.PositionSizePercent:0.#}% left";
            PushLog($"Paper partial take-profit executed for {position.DisplayName}: sold {position.PartialTakeProfitSellPercent:0.#}% at {position.PaperPnlPercent:+0.##;-0.##;0}%.", true);
            RaiseSafetyProperties();
            return;
        }

        if (!OpenPositions.Contains(position))
        {
            return;
        }

        var gateway = _walletWorkspace.ActiveDexGateway;
        if (gateway is null)
        {
            MarkManualCloseRequired(position, "Partial take-profit hit, but the trade-enabled wallet session is not available.");
            return;
        }

        if (!_walletWorkspace.TryApproveLiveExecution("Sniper partial take-profit", out var executionReason))
        {
            MarkManualCloseRequired(position, executionReason);
            return;
        }

        try
        {
            var result = await ExecuteLiveSellWithRetriesAsync(position, gateway, sellFraction, "partial take-profit");
            if (!result.Success)
            {
                MarkManualCloseRequired(position, result.FailureReason ?? "Partial take-profit retries exhausted.");
                return;
            }

            position.PartialTakeProfitExecuted = true;
            position.BreakEvenArmed = position.BreakEvenEnabled || position.BreakEvenArmed;
            position.Status = $"Partial TP executed, runner {position.PositionSizePercent:0.#}% left";
            AppendExecutionAuditRecord(_auditService.CreateExitRecord(position, result, "partial take-profit", TinyDryRunCapNative));
            PersistOpenLivePositions();
            PushLog(
                $"Live partial take-profit executed for {position.DisplayName}: sold {result.SoldTokenAmount:0.########} tokens, realized {result.RealizedNativeDelta:0.########} {gateway.NativeSymbol}.",
                true);
            RaiseSafetyProperties();
        }
        catch (Exception ex)
        {
            position.Status = "Partial TP failed";
            PushLog($"Partial take-profit failed for {position.DisplayName}: {ex.Message}", false);
        }
    }

    // ── CEX execution helpers ─────────────────────────────────────────────────

    // Keyed by TokenAddress; tracks the OrderSide used to enter so we can reverse correctly on close.
    private readonly Dictionary<string, OrderSide> _cexEntrySides
        = new(StringComparer.OrdinalIgnoreCase);

    // Keyed by TokenAddress; TP/SL managers for CEX futures positions (software simulation).
    private readonly Dictionary<string, TpSlManager> _cexTpSlManagers
        = new(StringComparer.OrdinalIgnoreCase);

    private async Task ExecuteCexBuyAsync(SniperCandidateViewModel candidate)
    {
        candidate.Status = "CEX Buying…";
        try
        {
            var symbol = (candidate.TokenInfo.Symbol ?? candidate.TokenInfo.TokenAddress).ToUpperInvariant();

            if (IsFuturesVenue && _futuresGateway is not null)
            {
                try { await _futuresGateway.SetLeverageAsync(symbol, FuturesLeverage); }
                catch { /* leverage setting is best-effort — gateway may reject if already set or unsupported */ }

                var side = SelectedFuturesBias == "Short Only" ? OrderSide.Sell : OrderSide.Buy;
                var posSide = side == OrderSide.Buy ? FuturesPositionSide.Long : FuturesPositionSide.Short;

                var order = await _futuresGateway.PlaceOrderAsync(new Order
                {
                    Symbol = symbol,
                    Side = side,
                    Type = OrderType.Market,
                    Quantity = BuyAmountBnb,
                    MarketType = TradingMarketType.FuturesUsdM,
                    PositionSide = posSide,
                    Leverage = FuturesLeverage
                });

                _cexEntrySides[candidate.TokenInfo.TokenAddress] = side;
                ApplyCexBuyToCandidate(candidate, order.Id, BuyAmountBnb, candidate.TokenInfo.PriceUsd);
                PushLog($"CEX Futures buy: {symbol} {side} x{FuturesLeverage} qty={BuyAmountBnb:0.####} USDT", true);

                // Attach software TP/SL manager for the futures position.
                if (AutoTakeProfitEnabled || AutoStopLossEnabled || AutoTrailingStopEnabled)
                {
                    var tpSlCfg = new CryptoAITerminal.Core.Models.TpSlConfig
                    {
                        TpEnabled       = AutoTakeProfitEnabled,
                        TpPercent       = TakeProfitPercent,
                        SlEnabled       = AutoStopLossEnabled,
                        SlPercent       = StopLossPercent,
                        TrailingStop    = AutoTrailingStopEnabled,
                        PartialTp       = PartialTakeProfitEnabled,
                        PartialTpClosePercent = PartialTakeProfitSellPercent,
                        PartialTp2Percent     = TakeProfitPercent * 2m,
                    };
                    var mgr = new TpSlManager(tpSlCfg);
                    mgr.OnEvent += msg => PushLog($"[TP/SL] {symbol}: {msg}", true);
                    _ = mgr.AttachAsync(
                        symbol, side, candidate.TokenInfo.PriceUsd, BuyAmountBnb,
                        posSide, TradingMarketType.FuturesUsdM,
                        _futuresGateway, _futuresGateway.MarketDataStream);

                    if (_cexTpSlManagers.TryGetValue(candidate.TokenInfo.TokenAddress, out var old))
                    {
                        _ = old.DetachAsync();
                        old.Dispose();
                    }
                    _cexTpSlManagers[candidate.TokenInfo.TokenAddress] = mgr;
                }
            }
            else if (IsCexSpotVenue && _spotGateway is not null)
            {
                var order = await _spotGateway.PlaceOrderAsync(new Order
                {
                    Symbol = symbol,
                    Side = OrderSide.Buy,
                    Type = OrderType.Market,
                    Quantity = BuyAmountBnb,
                    MarketType = TradingMarketType.Spot
                });

                _cexEntrySides[candidate.TokenInfo.TokenAddress] = OrderSide.Buy;
                ApplyCexBuyToCandidate(candidate, order.Id, BuyAmountBnb, candidate.TokenInfo.PriceUsd);
                PushLog($"CEX Spot buy: {symbol} qty={BuyAmountBnb:0.####} USDT", true);
            }
            else
            {
                candidate.Status = "CEX gateway unavailable";
                PushLog($"CEX buy failed for {candidate.DisplayName}: no configured gateway for {SelectedScanVenue}", false);
                return;
            }

            _executedBuys.Add(candidate.TokenInfo.TokenAddress);
            _sessionBuyCount++;
            _lastBuyUtc = DateTime.UtcNow;
            OpenPositions.Insert(0, candidate);
            TrimCollection(OpenPositions, 20);
            PersistOpenLivePositions();
        }
        catch (Exception ex)
        {
            candidate.Status = "CEX buy failed";
            PushLog($"CEX buy failed for {candidate.DisplayName}: {ex.Message}", false);
        }
        finally
        {
            RaiseSafetyProperties();
        }
    }

    private async Task ExecuteCexCloseAsync(SniperCandidateViewModel position, string exitLabel, bool positive, bool isManualIntervention = false)
    {
        Core.Interfaces.IExchangeGateway? gw = IsFuturesVenue
            ? (Core.Interfaces.IExchangeGateway?)_futuresGateway
            : _spotGateway;

        if (gw is null)
        {
            MarkManualCloseRequired(position, $"CEX {exitLabel}: gateway not available");
            return;
        }

        try
        {
            var symbol = (position.TokenInfo.Symbol ?? position.TokenInfo.TokenAddress).ToUpperInvariant();
            var qty = position.TrackedTokenAmount > 0 ? position.TrackedTokenAmount : position.EntryAmountBnb;

            // Determine close side: reverse of the entry side.
            _cexEntrySides.TryGetValue(position.TokenInfo.TokenAddress, out var entrySide);
            var closeSide = entrySide == OrderSide.Sell ? OrderSide.Buy : OrderSide.Sell;

            if (IsFuturesVenue)
            {
                await gw.PlaceOrderAsync(new Order
                {
                    Symbol = symbol,
                    Side = closeSide,
                    Type = OrderType.Market,
                    Quantity = qty,
                    MarketType = TradingMarketType.FuturesUsdM,
                    ReduceOnly = true
                });
            }
            else
            {
                await gw.PlaceOrderAsync(new Order
                {
                    Symbol = symbol,
                    Side = OrderSide.Sell,
                    Type = OrderType.Market,
                    Quantity = qty,
                    MarketType = TradingMarketType.Spot
                });
            }

            // Detach TP/SL manager before closing (prevents spurious close orders on already-closed position).
            if (_cexTpSlManagers.TryGetValue(position.TokenInfo.TokenAddress, out var tpSlMgr))
            {
                _ = tpSlMgr.DetachAsync();
                tpSlMgr.Dispose();
                _cexTpSlManagers.Remove(position.TokenInfo.TokenAddress);
            }

            OpenPositions.Remove(position);
            position.IsOpenPosition = false;
            position.Status = $"{(isManualIntervention ? "Manual" : "Auto")} CEX {exitLabel}";
            _cexEntrySides.Remove(position.TokenInfo.TokenAddress);
            ReleaseExecutedBuyKey(position, paper: false);
            ArchiveLiveTrade(position, exitLabel);
            PersistOpenLivePositions();
            PushLog($"CEX {exitLabel} for {position.DisplayName}.", positive);
            RaiseSafetyProperties();
        }
        catch (Exception ex)
        {
            MarkManualCloseRequired(position, $"CEX {exitLabel} failed: {ex.Message}");
        }
    }

    private void ApplyCexBuyToCandidate(SniperCandidateViewModel candidate, string orderId, decimal amount, decimal entryPrice)
    {
        candidate.WasBought = true;
        candidate.IsOpenPosition = true;
        candidate.OpenedAtLocal = DateTime.Now;
        candidate.EntryAmountBnb = amount;
        candidate.EntryPriceUsd = entryPrice;
        candidate.AutoTakeProfitEnabled = AutoTakeProfitEnabled;
        candidate.TakeProfitPercent = TakeProfitPercent;
        candidate.AutoStopLossEnabled = AutoStopLossEnabled;
        candidate.StopLossPercent = StopLossPercent;
        candidate.AutoTrailingStopEnabled = AutoTrailingStopEnabled;
        candidate.TrailingStopPercent = TrailingStopPercent;
        candidate.PartialTakeProfitEnabled = PartialTakeProfitEnabled;
        candidate.PartialTakeProfitTriggerPercent = PartialTakeProfitTriggerPercent;
        candidate.PartialTakeProfitSellPercent = PartialTakeProfitSellPercent;
        candidate.BreakEvenEnabled = BreakEvenEnabled;
        candidate.BreakEvenTriggerPercent = BreakEvenTriggerPercent;
        candidate.PositionSizePercent = 100m;
        candidate.PeakPriceUsd = entryPrice;
        candidate.TakeProfitTriggered = false;
        candidate.StopLossTriggered = false;
        candidate.TrailingStopTriggered = false;
        candidate.BreakEvenArmed = false;
        candidate.BreakEvenTriggered = false;
        candidate.PartialTakeProfitExecuted = false;
        candidate.EntryTxHash = orderId;
        candidate.Status = $"CEX bought: {orderId[..Math.Min(8, orderId.Length)]}";
    }

    private async Task ExecuteAutoExitAsync(SniperCandidateViewModel position, string exitLabel, bool positive, bool isManualIntervention = false)
    {
        if (PaperPositions.Contains(position))
        {
            PaperPositions.Remove(position);
            position.IsOpenPosition = false;
            position.Status = $"Auto-sold on {exitLabel} at {position.PaperPnlPercent:+0.##;-0.##;0}%";
            ArchivePaperTrade(position);
            ReleaseExecutedBuyKey(position, paper: true);
            PushLog($"Paper {exitLabel} triggered for {position.DisplayName} at {position.PaperPnlPercent:+0.##;-0.##;0}%.", positive);
            RaiseSafetyProperties();
            return;
        }

        if (!OpenPositions.Contains(position))
        {
            return;
        }

        // CEX live positions use the exchange gateway, not the DEX wallet.
        if (IsCexToken(position.TokenInfo))
        {
            if (!_walletWorkspace.TryApproveLiveExecution($"Sniper CEX {exitLabel}", out var cexExecReason))
            {
                MarkManualCloseRequired(position, cexExecReason);
                return;
            }
            await ExecuteCexCloseAsync(position, exitLabel, positive, isManualIntervention);
            return;
        }

        var gateway = _walletWorkspace.ActiveDexGateway;
        if (gateway is null)
        {
            MarkManualCloseRequired(position, $"{exitLabel} hit, but the trade-enabled wallet session is not available.");
            return;
        }

        if (!_walletWorkspace.TryApproveLiveExecution($"Sniper {exitLabel}", out var executionReason))
        {
            MarkManualCloseRequired(position, executionReason);
            return;
        }

        try
        {
            var result = await ExecuteLiveSellWithRetriesAsync(position, gateway, 1m, exitLabel);
            if (!result.Success)
            {
                MarkManualCloseRequired(position, result.FailureReason ?? $"{exitLabel} retries exhausted.");
                return;
            }

            if (position.TrackedTokenAmount > SniperLiveExecutionService.TokenDustThreshold)
            {
                MarkManualCloseRequired(
                    position,
                    $"{exitLabel} sell executed, but {position.TrackedTokenAmount:0.########} tokens still remain on-chain. Use EMERGENCY CLOSE again after balances settle.");
                return;
            }

            OpenPositions.Remove(position);
            position.IsOpenPosition = false;
            position.Status = $"{(isManualIntervention ? "Manual" : "Auto")} sold: {result.TransactionHash[..Math.Min(10, result.TransactionHash.Length)]}...";
            ReleaseExecutedBuyKey(position, paper: false);
            AppendExecutionAuditRecord(_auditService.CreateExitRecord(position, result, exitLabel, TinyDryRunCapNative));
            ArchiveLiveTrade(position, exitLabel);
            PersistOpenLivePositions();
            PushLog(
                $"{exitLabel} sell executed for {position.DisplayName} at {position.ClosedLivePnlPercent:+0.##;-0.##;0}% live PnL ({result.RealizedNativeDelta:0.########} {gateway.NativeSymbol} realized).",
                positive);
            RaiseSafetyProperties();
        }
        catch (Exception ex)
        {
            MarkManualCloseRequired(position, $"{exitLabel} sell failed for {position.DisplayName}: {ex.Message}");
        }
    }
}
