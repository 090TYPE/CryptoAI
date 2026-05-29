// Manual Snipe — lets the user paste any token contract address and instantly
// buy/sell/probe it through the active DEX gateway, regardless of whether the
// automated scanner has seen the pair. All operations share the same slippage
// setting and buy amount as the automated sniper.

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.DEX;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public partial class SniperViewModel
{
    // ── Timer helper ──────────────────────────────────────────────────────────

    private void QueueManualSnipeLookup()
    {
        _manualSnipeQuoteTimer.Stop();
        if (!string.IsNullOrWhiteSpace(_manualSnipeAddress))
        {
            _manualSnipeQuoteTimer.Start();
        }
    }

    // ── Lookup + quote ────────────────────────────────────────────────────────

    private async Task ManualSnipeLookupAndQuoteAsync()
    {
        var address = await RunOnUiAsync(() => ManualSnipeAddress);
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        await RunOnUiAsync(() =>
        {
            IsManualSnipeLoading = true;
            ManualSnipeInfoLabel = "Resolving token...";
            ManualSnipeInfoBrush = "#8FA3B8";
            ManualSnipeStatusMessage = string.Empty;
        });

        try
        {
            // Try to resolve the token from DexScreener
            var results = await _dexClient.SearchTokensAsync(address);
            var token = results.FirstOrDefault(t =>
                string.Equals(t.TokenAddress, address, StringComparison.OrdinalIgnoreCase));
            token ??= results.FirstOrDefault();

            if (token is null)
            {
                await RunOnUiAsync(() =>
                {
                    _resolvedManualSnipeToken = null;
                    ManualSnipeInfoLabel = "Token not found on DexScreener.";
                    ManualSnipeInfoBrush = "#FF8A65";
                    ManualSnipeQuoteLabel = string.Empty;
                    IsManualSnipeLoading = false;
                });
                return;
            }

            _resolvedManualSnipeToken = token;

            var gateway = _walletWorkspace.ActiveDexGateway;
            string quoteLabel;
            string quoteBrush;

            if (gateway is not null && BuyAmountBnb > 0m)
            {
                try
                {
                    var tokensOut = await gateway.GetTokenPriceInNativeAsync(token.TokenAddress, BuyAmountBnb, token.DexId);
                    quoteLabel = tokensOut > 0
                        ? $"≈ {tokensOut:0.########} {token.Symbol}"
                        : "Router quote unavailable.";
                    quoteBrush = tokensOut > 0 ? "#21E6C1" : "#8FA3B8";
                }
                catch
                {
                    quoteLabel = "Quote fetch failed.";
                    quoteBrush = "#FF8A65";
                }
            }
            else
            {
                var priceUsd = token.PriceUsd;
                quoteLabel = priceUsd > 0
                    ? $"≈ {BuyAmountBnb / (token.PriceNative > 0 ? token.PriceNative : priceUsd):0.########} {token.Symbol} (est.)"
                    : string.Empty;
                quoteBrush = "#F4B860";
            }

            var networkLabel = $"{token.ChainId.ToUpperInvariant()} / {token.DexId} / {(token.PriceUsd > 0 ? $"${token.PriceUsd:N6}" : "price n/a")}";
            var liqLabel = token.LiquidityUsd > 0 ? $"  Liq: ${token.LiquidityUsd / 1000:N0}K" : string.Empty;

            await RunOnUiAsync(() =>
            {
                ManualSnipeInfoLabel = $"{token.Name} ({token.Symbol}) — {networkLabel}{liqLabel}";
                ManualSnipeInfoBrush = "#21E6C1";
                ManualSnipeQuoteLabel = quoteLabel;
                ManualSnipeQuoteBrush = quoteBrush;
                IsManualSnipeLoading = false;
            });
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                _resolvedManualSnipeToken = null;
                ManualSnipeInfoLabel = $"Lookup error: {ex.Message}";
                ManualSnipeInfoBrush = "#FF8A65";
                ManualSnipeQuoteLabel = string.Empty;
                IsManualSnipeLoading = false;
            });
        }
    }

    // ── Buy ───────────────────────────────────────────────────────────────────

    private async Task ManualSnipeBuyAsync()
    {
        var address = await RunOnUiAsync(() => ManualSnipeAddress);
        if (string.IsNullOrWhiteSpace(address))
        {
            await RunOnUiAsync(() => ManualSnipeStatusMessage = "Enter a token contract address first.");
            return;
        }

        if (!_walletWorkspace.TryApproveLiveExecution("Manual snipe buy", out var executionReason))
        {
            await RunOnUiAsync(() => ManualSnipeStatusMessage = executionReason);
            return;
        }

        var gateway = _walletWorkspace.ActiveDexGateway;
        if (gateway is null)
        {
            await RunOnUiAsync(() => ManualSnipeStatusMessage = "Connect a trade-enabled wallet first.");
            return;
        }

        await RunOnUiAsync(() =>
        {
            IsManualSnipeLoading = true;
            ManualSnipeStatusMessage = "Sending buy...";
        });

        try
        {
            var buyAmount = await RunOnUiAsync(() => BuyAmountBnb);
            var slippage = await RunOnUiAsync(() => SlippagePercent);
            var token = _resolvedManualSnipeToken;
            var dexId = token?.DexId;

            var txHash = await gateway.BuyTokenAsync(address, buyAmount, slippage, dexId: dexId);
            await RunOnUiAsync(() =>
            {
                ManualSnipeStatusMessage = $"Buy sent: {txHash}";
                PushLog($"Manual snipe BUY {address[..Math.Min(10, address.Length)]}... for {buyAmount:0.######} — tx: {txHash}", true);
            });
            _ = ManualSnipeRefreshBalanceAsync();
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                ManualSnipeStatusMessage = $"Buy failed: {ex.Message}";
                PushLog($"Manual snipe BUY failed for {address[..Math.Min(10, address.Length)]}...: {ex.Message}", false);
            });
        }
        finally
        {
            await RunOnUiAsync(() => IsManualSnipeLoading = false);
        }
    }

    // ── Sell ──────────────────────────────────────────────────────────────────

    private async Task ManualSnipeSellAsync()
    {
        var address = await RunOnUiAsync(() => ManualSnipeAddress);
        var sellAmount = await RunOnUiAsync(() => ManualSnipeSellAmount);

        if (string.IsNullOrWhiteSpace(address))
        {
            await RunOnUiAsync(() => ManualSnipeStatusMessage = "Enter a token contract address first.");
            return;
        }

        if (sellAmount <= 0m)
        {
            await RunOnUiAsync(() => ManualSnipeStatusMessage = "Enter a sell amount above zero.");
            return;
        }

        if (!_walletWorkspace.TryApproveLiveExecution("Manual snipe sell", out var executionReason))
        {
            await RunOnUiAsync(() => ManualSnipeStatusMessage = executionReason);
            return;
        }

        var gateway = _walletWorkspace.ActiveDexGateway;
        if (gateway is null)
        {
            await RunOnUiAsync(() => ManualSnipeStatusMessage = "Connect a trade-enabled wallet first.");
            return;
        }

        await RunOnUiAsync(() =>
        {
            IsManualSnipeLoading = true;
            ManualSnipeStatusMessage = "Sending sell...";
        });

        try
        {
            var slippage = await RunOnUiAsync(() => SlippagePercent);
            var token = _resolvedManualSnipeToken;
            var dexId = token?.DexId;

            var txHash = await gateway.SellTokenAsync(address, sellAmount, slippage, dexId: dexId);
            await RunOnUiAsync(() =>
            {
                ManualSnipeStatusMessage = $"Sell sent: {txHash}";
                PushLog($"Manual snipe SELL {sellAmount:0.########} of {address[..Math.Min(10, address.Length)]}... — tx: {txHash}", true);
            });
            _ = ManualSnipeRefreshBalanceAsync();
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                ManualSnipeStatusMessage = $"Sell failed: {ex.Message}";
                PushLog($"Manual snipe SELL failed for {address[..Math.Min(10, address.Length)]}...: {ex.Message}", false);
            });
        }
        finally
        {
            await RunOnUiAsync(() => IsManualSnipeLoading = false);
        }
    }

    // ── Sellability probe ─────────────────────────────────────────────────────

    private async Task ManualSnipeProbeAsync()
    {
        var address = await RunOnUiAsync(() => ManualSnipeAddress);
        if (string.IsNullOrWhiteSpace(address))
        {
            await RunOnUiAsync(() => ManualSnipeStatusMessage = "Enter a token contract address to probe.");
            return;
        }

        var gateway = _walletWorkspace.ActiveDexGateway;
        if (gateway is null)
        {
            await RunOnUiAsync(() => ManualSnipeStatusMessage = "Connect a wallet to run the on-chain probe.");
            return;
        }

        await RunOnUiAsync(() =>
        {
            IsManualSnipeLoading = true;
            ManualSnipeStatusMessage = "Running on-chain sellability probe...";
        });

        try
        {
            var slippage = await RunOnUiAsync(() => SlippagePercent);
            var buyAmount = await RunOnUiAsync(() => BuyAmountBnb);
            var token = _resolvedManualSnipeToken;

            var probe = await gateway.ProbeSellabilityAsync(new DexSellabilityProbeRequest(
                address,
                slippage,
                DexId: token?.DexId,
                NativeAmountToProbe: buyAmount > 0m ? buyAmount : null,
                PrimeAllowance: true));

            var icon = probe.Passed ? "✅" : "❌";
            var summary = $"{icon} {probe.Narrative}";
            if (probe.RoundTripLossPercent.HasValue)
            {
                summary += $" | Round-trip loss: {probe.RoundTripLossPercent:0.##}%";
            }

            await RunOnUiAsync(() =>
            {
                ManualSnipeStatusMessage = summary;
                ManualSnipeInfoBrush = probe.Passed ? "#21E6C1" : "#FF8A65";
                PushLog($"Manual snipe probe for {address[..Math.Min(10, address.Length)]}...: {probe.Narrative}", probe.Passed);
            });
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                ManualSnipeStatusMessage = $"Probe failed: {ex.Message}";
                PushLog($"Manual snipe probe failed for {address[..Math.Min(10, address.Length)]}...: {ex.Message}", false);
            });
        }
        finally
        {
            await RunOnUiAsync(() => IsManualSnipeLoading = false);
        }
    }

    // ── Token balance refresh ─────────────────────────────────────────────────

    private async Task ManualSnipeRefreshBalanceAsync()
    {
        var address = await RunOnUiAsync(() => ManualSnipeAddress);
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        var gateway = _walletWorkspace.ActiveDexGateway;
        if (gateway is null || !_walletWorkspace.CanUseDexTradingOnSelectedNetwork)
        {
            await RunOnUiAsync(() =>
            {
                ManualSnipeTokenBalance = 0m;
                ManualSnipeTokenBalanceLabel = string.Empty;
            });
            return;
        }

        try
        {
            var balance = await gateway.GetTokenBalanceAsync(address);
            var symbol = _resolvedManualSnipeToken?.Symbol ?? "TOKEN";
            await RunOnUiAsync(() =>
            {
                ManualSnipeTokenBalance = balance;
                ManualSnipeTokenBalanceLabel = $"{balance:0.########} {symbol}";
            });
        }
        catch
        {
            await RunOnUiAsync(() =>
            {
                ManualSnipeTokenBalance = 0m;
                ManualSnipeTokenBalanceLabel = "Balance unavailable";
            });
        }
    }

    // ── Sell presets ──────────────────────────────────────────────────────────

    private void ApplyManualSnipeSellPreset(string? preset)
    {
        var ratio = preset?.Trim() switch
        {
            "25"  => 0.25m,
            "50"  => 0.50m,
            "75"  => 0.75m,
            "100" => 1.00m,
            _     => 0m
        };

        if (ratio <= 0m)
        {
            return;
        }

        if (ManualSnipeTokenBalance <= 0m)
        {
            ManualSnipeStatusMessage = "No token balance loaded. Click ↻ to refresh.";
            return;
        }

        ManualSnipeSellAmount = Math.Round(ManualSnipeTokenBalance * ratio, 8, MidpointRounding.AwayFromZero);
        ManualSnipeStatusMessage = $"Sell amount set to {preset}% of balance: {ManualSnipeSellAmount:0.########}.";
    }

    // ── Utility: run on UI thread ─────────────────────────────────────────────

    private async Task RunOnUiAsync(Action action)
    {
        await Dispatcher.UIThread.InvokeAsync(action);
    }

    private async Task<T> RunOnUiAsync<T>(Func<T> func)
    {
        return await Dispatcher.UIThread.InvokeAsync(func);
    }
}
