// Wave-1 split of SniperViewModel (was 4933 lines in a single file).
// This partial keeps all preset-application helpers so the main file
// stays focused on lifecycle, properties, and the execution pipeline.
// Pure behaviour move — no logic changes.

namespace CryptoAITerminal.TerminalUI.ViewModels;

public partial class SniperViewModel
{
    private void ApplySafePreset()
    {
        _selectedTradingProfile = "Balanced";
        _selectedPresetName = "Safe";
        AutoBuyEnabled = false;
        PaperTradingEnabled = true;
        BuyAmountBnb = 0.005m;
        MinLiquidityUsd = 60000m;
        MaxLiquidityUsd = 350000m;
        MinVolume24hUsd = 180000m;
        MinMomentum5m = 4m;
        MaxMarketCapUsd = 3000000m;
        MaxRiskScore = 30;
        MaxVolumeToLiquidityRatio = 10m;
        MaxMarketCapToLiquidityRatio = 18m;
        MaxSimulatedBuyTaxPercent = 8m;
        MaxSimulatedSellTaxPercent = 10m;
        MaxDailyLiveLossNative = 0.02m;
        MaxExposurePerChainNative = 0.01m;
        MaxExposurePerWalletNative = 0.015m;
        MaxConsecutiveLiveLosses = 2;
        HardCapTotalLiveExposureNative = 0.02m;
        TinyDryRunCapNative = 0.01m;
        TakeProfitPercent = 18m;
        PartialTakeProfitEnabled = true;
        PartialTakeProfitTriggerPercent = 10m;
        PartialTakeProfitSellPercent = 40m;
        BreakEvenEnabled = true;
        BreakEvenTriggerPercent = 8m;
        AutoStopLossEnabled = true;
        StopLossPercent = 8m;
        AutoTrailingStopEnabled = true;
        TrailingStopPercent = 6m;
        PushLog("Applied SAFE sniper preset.", true);
        RaiseSafetyProperties();
    }

    private void ApplyBalancedPreset()
    {
        _selectedTradingProfile = "Balanced";
        _selectedPresetName = "Balanced";
        AutoBuyEnabled = false;
        PaperTradingEnabled = true;
        BuyAmountBnb = 0.01m;
        MinLiquidityUsd = 25000m;
        MaxLiquidityUsd = 500000m;
        MinVolume24hUsd = 100000m;
        MinMomentum5m = 3m;
        MaxMarketCapUsd = 5000000m;
        MaxRiskScore = 45;
        MaxVolumeToLiquidityRatio = 18m;
        MaxMarketCapToLiquidityRatio = 30m;
        MaxSimulatedBuyTaxPercent = 12m;
        MaxSimulatedSellTaxPercent = 15m;
        MaxDailyLiveLossNative = 0.05m;
        MaxExposurePerChainNative = 0.02m;
        MaxExposurePerWalletNative = 0.03m;
        MaxConsecutiveLiveLosses = 3;
        HardCapTotalLiveExposureNative = 0.04m;
        TinyDryRunCapNative = 0.02m;
        TakeProfitPercent = 20m;
        PartialTakeProfitEnabled = true;
        PartialTakeProfitTriggerPercent = 12m;
        PartialTakeProfitSellPercent = 35m;
        BreakEvenEnabled = true;
        BreakEvenTriggerPercent = 10m;
        AutoStopLossEnabled = true;
        StopLossPercent = 12m;
        AutoTrailingStopEnabled = true;
        TrailingStopPercent = 8m;
        PushLog("Applied BALANCED sniper preset.", true);
        RaiseSafetyProperties();
    }

    private void ApplyAggressivePreset()
    {
        _selectedTradingProfile = "Balanced";
        _selectedPresetName = "Aggressive";
        AutoBuyEnabled = true;
        PaperTradingEnabled = true;
        BuyAmountBnb = 0.015m;
        MinLiquidityUsd = 15000m;
        MaxLiquidityUsd = 800000m;
        MinVolume24hUsd = 60000m;
        MinMomentum5m = 2m;
        MaxMarketCapUsd = 9000000m;
        MaxRiskScore = 58;
        MaxVolumeToLiquidityRatio = 24m;
        MaxMarketCapToLiquidityRatio = 40m;
        MaxSimulatedBuyTaxPercent = 15m;
        MaxSimulatedSellTaxPercent = 18m;
        MaxDailyLiveLossNative = 0.10m;
        MaxExposurePerChainNative = 0.04m;
        MaxExposurePerWalletNative = 0.06m;
        MaxConsecutiveLiveLosses = 4;
        HardCapTotalLiveExposureNative = 0.08m;
        TinyDryRunCapNative = 0.03m;
        TakeProfitPercent = 28m;
        PartialTakeProfitEnabled = true;
        PartialTakeProfitTriggerPercent = 16m;
        PartialTakeProfitSellPercent = 30m;
        BreakEvenEnabled = true;
        BreakEvenTriggerPercent = 12m;
        AutoStopLossEnabled = true;
        StopLossPercent = 15m;
        AutoTrailingStopEnabled = true;
        TrailingStopPercent = 10m;
        PushLog("Applied AGGRESSIVE sniper preset.", true);
        RaiseSafetyProperties();
    }

    private void ApplyScalpPreset(string? preset)
    {
        SelectedTradingProfile = "Scalp";
        var normalized = NormalizeScalpPreset(preset);
        _selectedScalpPreset = normalized;

        switch (normalized)
        {
            case "Tight":
                _selectedPresetName = "Scalp Tight";
                AutoBuyEnabled = false;
                PaperTradingEnabled = true;
                BuyAmountBnb = 0.008m;
                MinLiquidityUsd = 80000m;
                MaxLiquidityUsd = 0m;
                MinVolume24hUsd = 180000m;
                MinMomentum5m = 0.8m;
                MaxRiskScore = 28;
                MinPairAgeMinutes = IsDexVenue ? 3m : 0m;
                MaxPairAgeMinutes = 90m;
                CooldownSeconds = 25;
                MaxSimultaneousPositions = 1;
                MaxBuysPerSession = 8;
                TakeProfitPercent = 2.4m;
                StopLossPercent = 1.2m;
                TrailingStopPercent = 0.8m;
                PartialTakeProfitEnabled = true;
                PartialTakeProfitTriggerPercent = 1.2m;
                PartialTakeProfitSellPercent = 60m;
                BreakEvenEnabled = true;
                BreakEvenTriggerPercent = 0.9m;
                FuturesLeverage = 4;
                break;
            case "Aggro":
                _selectedPresetName = "Scalp Aggro";
                AutoBuyEnabled = true;
                PaperTradingEnabled = true;
                BuyAmountBnb = 0.015m;
                MinLiquidityUsd = 30000m;
                MaxLiquidityUsd = 0m;
                MinVolume24hUsd = 100000m;
                MinMomentum5m = 1.8m;
                MaxRiskScore = 46;
                MinPairAgeMinutes = 0m;
                MaxPairAgeMinutes = 180m;
                CooldownSeconds = 15;
                MaxSimultaneousPositions = 2;
                MaxBuysPerSession = 18;
                TakeProfitPercent = 4.8m;
                StopLossPercent = 2.1m;
                TrailingStopPercent = 1.4m;
                PartialTakeProfitEnabled = true;
                PartialTakeProfitTriggerPercent = 2.2m;
                PartialTakeProfitSellPercent = 45m;
                BreakEvenEnabled = true;
                BreakEvenTriggerPercent = 1.4m;
                FuturesLeverage = 8;
                break;
            default:
                _selectedPresetName = "Scalp Standard";
                AutoBuyEnabled = false;
                PaperTradingEnabled = true;
                BuyAmountBnb = 0.01m;
                MinLiquidityUsd = 50000m;
                MaxLiquidityUsd = 0m;
                MinVolume24hUsd = 140000m;
                MinMomentum5m = 1.2m;
                MaxRiskScore = 36;
                MinPairAgeMinutes = IsDexVenue ? 1m : 0m;
                MaxPairAgeMinutes = 120m;
                CooldownSeconds = 20;
                MaxSimultaneousPositions = 1;
                MaxBuysPerSession = 12;
                TakeProfitPercent = 3.2m;
                StopLossPercent = 1.6m;
                TrailingStopPercent = 1.0m;
                PartialTakeProfitEnabled = true;
                PartialTakeProfitTriggerPercent = 1.6m;
                PartialTakeProfitSellPercent = 50m;
                BreakEvenEnabled = true;
                BreakEvenTriggerPercent = 1.1m;
                FuturesLeverage = 6;
                break;
        }

        PushLog($"Applied {normalized.ToUpperInvariant()} scalp sniper preset.", true);
        RaiseSafetyProperties();
    }

    private void ApplyAllChainsPreset()
    {
        EnabledChainsText = "bsc,ethereum,base,solana,tron";
        StatusMessage = "All-chain preset applied. BSC, Ethereum, Base, Solana, and Tron are in scope.";
        PushLog("Applied ALL-CHAINS sniper coverage preset.", true);
        RaiseSafetyProperties();
    }

    private void ApplyEvmChainsPreset()
    {
        EnabledChainsText = "bsc,ethereum,base";
        StatusMessage = "EVM-only preset applied. BSC, Ethereum, and Base remain in scope.";
        PushLog("Applied EVM-ONLY sniper coverage preset.", true);
        RaiseSafetyProperties();
    }

    private void ApplyBuyBalancePreset(string? preset)
    {
        if (decimal.TryParse(preset, out var globalPercent))
        {
            _walletWorkspace.GlobalPositionSizingPercent = globalPercent;
            _pendingGlobalSizingApply = true;
        }

        if (!PreferStableQuote)
        {
            StatusMessage = "Balance presets are tied to the shared stable quote mode. Switch the terminal quote away from native first.";
            return;
        }

        var ratio = preset?.Trim() switch
        {
            "25" => 0.25m,
            "50" => 0.50m,
            "75" => 0.75m,
            "100" => 1m,
            _ => 0m
        };

        if (ratio <= 0m)
        {
            return;
        }

        BuyAmountBnb = RoundSniperBuyAmount(StableQuoteBalance * ratio);
        StatusMessage = $"Sniper buy amount set to {preset}% of available {PreferredStableQuoteSymbol}: {BuyAmountBnb:0.######}.";
    }
}
