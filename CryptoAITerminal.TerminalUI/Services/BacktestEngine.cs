using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services;

// Одна закрытая сделка
public readonly record struct BacktestTrade(
    DateTime OpenTime,
    DateTime CloseTime,
    decimal  EntryPrice,
    decimal  ExitPrice,
    decimal  ReturnPercent);

// Точка кривой эквити: время + значение счёта (начальный баланс = 100)
public readonly record struct EquityPoint(DateTime Time, decimal Value);

public readonly record struct BacktestResult(
    bool     IsReady,
    string   Message,
    IReadOnlyList<BacktestTrade>  Trades,
    IReadOnlyList<EquityPoint>    EquityCurve,
    decimal  WinRatePercent,
    decimal  NetReturnPercent,
    decimal  MaxDrawdownPercent,
    decimal  SharpeRatio,
    decimal  BestTradePercent,
    decimal  WorstTradePercent,
    string   LastSignal)
{
    public int TradeCount => Trades.Count;
    public int WinCount   => Trades.Count(t => t.ReturnPercent > 0);

    public static BacktestResult Empty(string message = "Нет данных") => new(
        false, message,
        Array.Empty<BacktestTrade>(),
        Array.Empty<EquityPoint>(),
        0, 0, 0, 0, 0, 0, "Hold");
}

public static class BacktestEngine
{
    /// <summary>
    /// Прогоняет стратегию на наборе свечей.
    /// commissionPercent — комиссия на одну сторону (0.1 = 0.1% за открытие + 0.1% за закрытие).
    /// </summary>
    public static BacktestResult Run(
        IStrategy strategy,
        IReadOnlyList<DexOhlcvPoint> candles,
        decimal commissionPercent = 0.1m)
    {
        strategy.Reset();

        if (candles.Count < 2)
            return BacktestResult.Empty("Нужно минимум 2 свечи");

        var trades      = new List<BacktestTrade>();
        var equityCurve = new List<EquityPoint>();
        decimal? entryPrice = null;
        DateTime entryTime  = default;
        var equity          = 100m;
        var peakEquity      = equity;
        var maxDrawdown     = 0m;
        var lastSignal      = "Hold";

        // Начальная точка кривой (БАГ-25: цикл начинается с индекса 1 чтобы не дублировать её)
        equityCurve.Add(new EquityPoint(candles[0].Timestamp, equity));

        foreach (var candle in candles.Skip(1))
        {
            var marketData = new MarketData
            {
                Symbol    = "BACKTEST",
                LastPrice = candle.Close,
                High24h   = candle.High,   // used by BreakoutStrategy
                Low24h    = candle.Low,    // used by BreakoutStrategy
                Timestamp = candle.Timestamp
            };

            var (signal, confidence) = strategy.Analyze(marketData);

            if (confidence >= 0.3m)
            {
                if (signal == "BUY" && entryPrice is null)
                {
                    entryPrice = candle.Close;
                    entryTime  = candle.Timestamp;
                    lastSignal = "Long";
                }
                else if (signal == "SELL" && entryPrice is not null)
                {
                    equity = ClosePosition(
                        trades, equityCurve,
                        entryPrice.Value, candle.Close,
                        entryTime, candle.Timestamp,
                        equity, ref peakEquity, ref maxDrawdown,
                        commissionPercent);
                    entryPrice = null;
                    lastSignal = "Flat";
                }
            }

            // Обновляем кривую каждую свечу (если позиция открыта — нереализованная прибыль)
            var currentEquity = entryPrice is null
                ? equity
                : equity * (1m + (candle.Close - entryPrice.Value) / entryPrice.Value);
            equityCurve.Add(new EquityPoint(candle.Timestamp, currentEquity));
        }

        // Закрываем открытую позицию по последней цене
        if (entryPrice is not null)
        {
            equity = ClosePosition(
                trades, equityCurve,
                entryPrice.Value, candles[^1].Close,
                entryTime, candles[^1].Timestamp,
                equity, ref peakEquity, ref maxDrawdown,
                commissionPercent);
            lastSignal = "Long (открыто)";
        }

        if (trades.Count == 0)
            return BacktestResult.Empty("Ни одной сделки — стратегия не дала полного пересечения на этих данных.");

        var wins      = trades.Count(t => t.ReturnPercent > 0);
        var winRate   = (decimal)wins / trades.Count * 100m;
        var netReturn = equity - 100m;
        var best      = trades.Max(t => t.ReturnPercent);
        var worst     = trades.Min(t => t.ReturnPercent);
        var sharpe    = ComputeSharpe(trades.Select(t => t.ReturnPercent).ToList());

        // Дедупликация точек кривой (оставляем по одной на свечу)
        var cleanCurve = equityCurve
            .GroupBy(p => p.Time)
            .Select(g => g.Last())
            .OrderBy(p => p.Time)
            .ToList();

        return new BacktestResult(
            true,
            $"{candles.Count} свечей, {trades.Count} сделок",
            trades,
            cleanCurve,
            winRate, netReturn, maxDrawdown, sharpe, best, worst, lastSignal);
    }

    private static decimal ClosePosition(
        List<BacktestTrade> trades,
        List<EquityPoint>   equityCurve,
        decimal entryPrice, decimal exitPrice,
        DateTime entryTime, DateTime exitTime,
        decimal equity,
        ref decimal peakEquity,
        ref decimal maxDrawdown,
        decimal commissionPercent)
    {
        var commission = commissionPercent / 100m * 2m; // туда + обратно
        var raw        = (exitPrice - entryPrice) / entryPrice * 100m;
        var ret        = raw - commission * 100m;

        trades.Add(new BacktestTrade(entryTime, exitTime, entryPrice, exitPrice, ret));
        equity     *= 1m + ret / 100m;
        peakEquity  = Math.Max(peakEquity, equity);

        if (peakEquity > 0m)
            maxDrawdown = Math.Max(maxDrawdown, (peakEquity - equity) / peakEquity * 100m);

        equityCurve.Add(new EquityPoint(exitTime, equity));
        return equity;
    }

    private static decimal ComputeSharpe(List<decimal> returns)
    {
        if (returns.Count < 2) return 0m;
        var avg      = returns.Average();
        var variance = returns.Sum(r => (r - avg) * (r - avg)) / (returns.Count - 1);
        var stdDev   = (decimal)Math.Sqrt((double)variance);
        // БАГ-20: аннуализированный Sharpe (√252 = дневные → годовые), rf=0
        return stdDev == 0m ? 0m : avg / stdDev * (decimal)Math.Sqrt(252);
    }
}
