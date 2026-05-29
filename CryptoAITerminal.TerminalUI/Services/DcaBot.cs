using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Dollar-Cost Averaging bot. Executes periodic market buys for a coin portfolio
/// according to configured weights. Optionally skips a coin if price is above its MA.
/// </summary>
public sealed class DcaBot : IDisposable
{
    private readonly IExchangeGateway _gateway;
    private readonly DcaBotConfig _cfg;

    private Timer? _checkTimer;
    private DateTime _nextExecutionUtc;
    private volatile bool _isStopped;
    private readonly SemaphoreSlim _executeLock = new(1, 1);

    public DateTime NextExecutionUtc => _nextExecutionUtc;
    public TimeSpan TimeUntilNext => _nextExecutionUtc > DateTime.UtcNow
        ? _nextExecutionUtc - DateTime.UtcNow
        : TimeSpan.Zero;

    public event Action<string>? OnLog;
    public event Action<DcaExecution>? OnExecution;

    public DcaBot(IExchangeGateway gateway, DcaBotConfig cfg)
    {
        _gateway = gateway;
        _cfg = cfg;
    }

    public async Task StartAsync(bool executeImmediately = false)
    {
        _isStopped = false;
        _nextExecutionUtc = executeImmediately ? DateTime.UtcNow : DateTime.UtcNow + GetInterval();
        OnLog?.Invoke($"DCA started. Next cycle: {_nextExecutionUtc:dd.MM HH:mm} UTC");

        _checkTimer = new Timer(async _ => await CheckAndExecuteAsync(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        if (executeImmediately)
            await CheckAndExecuteAsync();
    }

    public async Task ForceExecuteNowAsync()
    {
        _nextExecutionUtc = DateTime.UtcNow;
        await CheckAndExecuteAsync();
    }

    private async Task CheckAndExecuteAsync()
    {
        if (_isStopped || DateTime.UtcNow < _nextExecutionUtc) return;
        if (!await _executeLock.WaitAsync(0)) return;

        try
        {
            await ExecuteCycleAsync();
            _nextExecutionUtc = DateTime.UtcNow + GetInterval();
            OnLog?.Invoke($"Next cycle: {_nextExecutionUtc:dd.MM HH:mm} UTC");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Cycle error: {ex.Message}");
        }
        finally
        {
            _executeLock.Release();
        }
    }

    private async Task ExecuteCycleAsync()
    {
        OnLog?.Invoke($"=== DCA Cycle {DateTime.UtcNow:dd.MM.yyyy HH:mm} UTC ===");

        int totalWeight = _cfg.Coins.Sum(c => c.WeightPercent);
        if (totalWeight <= 0)
        {
            OnLog?.Invoke("No coins configured.");
            return;
        }

        foreach (var coin in _cfg.Coins)
        {
            if (_isStopped) break;
            decimal allocation = _cfg.TotalBudgetPerCycleUsdt * coin.WeightPercent / totalWeight;
            await ExecuteCoinAsync(coin, allocation);
        }
    }

    private async Task ExecuteCoinAsync(DcaCoinEntry coin, decimal allocationUsdt)
    {
        decimal currentPrice = await GetCurrentPriceAsync(coin.Symbol);

        if (coin.ConditionalBuyEnabled)
        {
            decimal? ma = await CalculateMaAsync(coin.Symbol, coin.MaPeriod);
            if (ma.HasValue && currentPrice > ma.Value)
            {
                var skipped = new DcaExecution
                {
                    Symbol = coin.Symbol,
                    ExecutedAt = DateTime.UtcNow,
                    Price = currentPrice,
                    Quantity = 0,
                    TotalUsdt = 0,
                    Executed = false,
                    Reason = $"Price {currentPrice:N2} > {coin.MaPeriod}MA {ma.Value:N2}"
                };
                OnLog?.Invoke($"[{coin.Symbol}] Skipped — {skipped.Reason}");
                OnExecution?.Invoke(skipped);
                return;
            }
        }

        if (currentPrice <= 0)
        {
            OnLog?.Invoke($"[{coin.Symbol}] Could not get price, skipping");
            return;
        }

        decimal quantity = Math.Round(allocationUsdt / currentPrice, 6, MidpointRounding.ToZero);
        if (quantity <= 0)
        {
            OnLog?.Invoke($"[{coin.Symbol}] Allocation {allocationUsdt:N2} USDT too small");
            return;
        }

        try
        {
            var order = new Order
            {
                Symbol = coin.Symbol,
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = quantity,
                MarketType = TradingMarketType.Spot
            };
            await _gateway.PlaceOrderAsync(order);

            var execution = new DcaExecution
            {
                Symbol = coin.Symbol,
                ExecutedAt = DateTime.UtcNow,
                Price = currentPrice,
                Quantity = quantity,
                TotalUsdt = quantity * currentPrice,
                Executed = true,
                Reason = "Scheduled"
            };
            OnLog?.Invoke($"[{coin.Symbol}] Bought {quantity:N6} @ {currentPrice:N4} = {execution.TotalUsdt:N2} USDT");
            OnExecution?.Invoke(execution);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[{coin.Symbol}] Order failed: {ex.Message}");
        }
    }

    private async Task<decimal?> CalculateMaAsync(string symbol, int period)
    {
        try
        {
            var candles = await _gateway.GetCandlesAsync(symbol, "1D", Math.Max(period, 50));
            if (candles.Count == 0) return null;
            var slice = candles.TakeLast(period).ToList();
            return slice.Average(c => c.Close);
        }
        catch
        {
            return null;
        }
    }

    private async Task<decimal> GetCurrentPriceAsync(string symbol)
    {
        try
        {
            var book = await _gateway.GetOrderBookAsync(symbol, 1);
            var bid = book.Bids.FirstOrDefault()?.Price ?? 0m;
            var ask = book.Asks.FirstOrDefault()?.Price ?? 0m;
            if (bid > 0 && ask > 0) return (bid + ask) / 2m;
            return bid > 0 ? bid : ask;
        }
        catch
        {
            return 0m;
        }
    }

    private TimeSpan GetInterval() => _cfg.IntervalType switch
    {
        DcaIntervalType.Hours => TimeSpan.FromHours(_cfg.IntervalValue),
        DcaIntervalType.Weeks => TimeSpan.FromDays(_cfg.IntervalValue * 7.0),
        _ => TimeSpan.FromDays(_cfg.IntervalValue)
    };

    public Task StopAsync()
    {
        _isStopped = true;
        _checkTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        OnLog?.Invoke("DCA bot stopped.");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _checkTimer?.Dispose();
        _executeLock.Dispose();
    }
}

public class DcaBotConfig
{
    public decimal TotalBudgetPerCycleUsdt { get; set; } = 100m;
    public DcaIntervalType IntervalType { get; set; } = DcaIntervalType.Days;
    public int IntervalValue { get; set; } = 1;
    public List<DcaCoinEntry> Coins { get; set; } = new();
}
