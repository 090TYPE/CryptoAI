using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Plain-English review of a finished backtest. Claude when a key is configured,
/// otherwise a deterministic offline heuristic flagging the classic overfit tells
/// (too few trades, no edge over buy&hold, implausible Sharpe, deep drawdown).
/// </summary>
public sealed class BacktestReviewAiService
{
    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }

    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }

    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<BacktestReview> ReviewAsync(BacktestMetrics m, CancellationToken ct = default)
    {
        if (UsesLiveModel)
        {
            try
            {
                var provider = new BacktestReviewAiProvider(ApiKey, Model);
                var review = await provider.ReviewAsync(m, ct).ConfigureAwait(false);
                if (review is not null) return review;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* degrade to offline */ }
        }

        return BuildOffline(m);
    }

    private static BacktestReview BuildOffline(BacktestMetrics m)
    {
        var risks = new List<string>();
        var edge = m.NetReturnPct - m.BuyHoldReturnPct;

        if (m.Trades < 10) risks.Add($"Only {m.Trades} trades — sample too small to trust.");
        if (edge <= 0) risks.Add($"No edge over buy & hold ({edge:+0.0;-0.0}%).");
        if (m.MaxDrawdownPct >= 30m) risks.Add($"Deep drawdown ({m.MaxDrawdownPct:0.0}%).");
        if (m.Sharpe >= 3m && m.Trades < 30) risks.Add($"Sharpe {m.Sharpe:0.0} on {m.Trades} trades looks too good — likely curve-fit.");
        if (m.WinRatePct >= 80m && m.Trades < 20) risks.Add($"{m.WinRatePct:0}% win rate on a tiny sample is suspicious.");

        string verdict;
        string summary;
        if (m.Trades < 10 || (m.Sharpe >= 3m && m.Trades < 30) || (m.WinRatePct >= 85m && m.Trades < 20))
        {
            verdict = "OVERFIT";
            summary = $"Results rest on too few trades ({m.Trades}); the numbers are unlikely to hold out of sample.";
        }
        else if (edge <= 0 || m.NetReturnPct <= 0)
        {
            verdict = "WEAK";
            summary = $"The strategy returned {m.NetReturnPct:0.0}% vs buy&hold {m.BuyHoldReturnPct:0.0}% — no convincing edge.";
        }
        else if (m.Sharpe >= 1m && m.Trades >= 30 && m.MaxDrawdownPct < 30m)
        {
            verdict = "ROBUST";
            summary = $"Beats buy&hold by {edge:0.0}% across {m.Trades} trades with Sharpe {m.Sharpe:0.0} and a contained {m.MaxDrawdownPct:0.0}% drawdown.";
        }
        else
        {
            verdict = "PROMISING";
            summary = $"A positive {edge:0.0}% edge over buy&hold, but validate further (Sharpe {m.Sharpe:0.0}, {m.Trades} trades).";
        }

        return new BacktestReview(verdict, summary, risks.ToArray(), "Heuristic (offline)", true);
    }
}
