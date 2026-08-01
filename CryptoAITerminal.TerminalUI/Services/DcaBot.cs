using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Base;
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

    // Правила площадки по каждой паре — один запрос за сеанс. Цикл всегда идёт под _executeLock,
    // так что обычного словаря достаточно.
    private readonly Dictionary<string, SymbolFilters?> _filters = new(StringComparer.OrdinalIgnoreCase);

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

        // Не передаём async-лямбду в Timer — это эквивалент async void и
        // непойманное исключение крашит процесс (ср. БАГ-11 в GridBot).
        // Оборачиваем в SafeCheckAsync.
        _checkTimer = new Timer(_ => { _ = SafeCheckAsync(); }, null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        if (executeImmediately)
            await CheckAndExecuteAsync();
    }

    private async Task SafeCheckAsync()
    {
        try { await CheckAndExecuteAsync(); }
        catch (ObjectDisposedException) { /* бот остановлен, семафор/таймер уже освобождён */ }
        catch (Exception ex) { OnLog?.Invoke($"Cycle error: {ex.Message}"); }
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

        // Правила площадки по этой паре. GridBot спрашивает их на том же шлюзе с первого дня,
        // а DCA слал Math.Round(..., 6) вслепую: на паре с шагом лота грубее 1e-6 (у большинства
        // альтов шаг 1, 0.1 или 0.01) каждый ордер возвращался с -1013 LOT_SIZE, а закупка на
        // сумму ниже minNotional отбивалась вся целиком. Пользователь видел «Order failed» в логе
        // и пустую таблицу закупок — при том что расписание считалось отработавшим.
        var filters = await GetFiltersAsync(coin.Symbol);

        decimal quantity = SymbolFilterMath.NormalizeQuantity(allocationUsdt / currentPrice, filters);
        // Шага площадка не публикует (или шлюз не умеет его отдавать) — остаётся прежний потолок
        // точности, он безопаснее сырого деления.
        if (filters is not { StepSize: > 0m })
            quantity = Math.Round(quantity, 6, MidpointRounding.ToZero);

        if (quantity <= 0)
        {
            // NormalizeQuantity отдаёт 0 и когда объём меньше минимального лота — это не то же
            // самое, что «денег на один шаг не хватило», и человеку стоит видеть разницу.
            var why = filters is { MinQuantity: > 0m } mq
                ? $"Allocation {allocationUsdt:N2} USDT is below the {mq.MinQuantity} {BaseAssetOf(coin.Symbol)} minimum lot"
                : $"Allocation {allocationUsdt:N2} USDT too small";
            ReportSkipped(coin.Symbol, currentPrice, why);
            return;
        }

        if (!SymbolFilterMath.MeetsMinNotional(currentPrice, quantity, filters))
        {
            ReportSkipped(coin.Symbol, currentPrice,
                $"Order {quantity * currentPrice:N2} USDT is below the venue minimum of {filters!.MinNotional:N2}");
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

    /// <summary>
    /// Правила площадки по паре, один раз за сеанс. Ответ не меняется месяцами, а цикл DCA
    /// проходит по всему списку монет — спрашивать заново на каждом круге незачем. Отсутствие
    /// ответа кэшируем тоже: шлюз, который их не отдаёт (дефолт интерфейса — null), иначе
    /// опрашивался бы вечно.
    /// </summary>
    private async Task<SymbolFilters?> GetFiltersAsync(string symbol)
    {
        if (_filters.TryGetValue(symbol, out var cached)) return cached;

        SymbolFilters? filters = null;
        try
        {
            filters = await _gateway.GetSymbolFiltersAsync(symbol);
        }
        catch (Exception ex)
        {
            // Закупку из-за этого не отменяем: считать по прежним правилам хуже, чем точно, но
            // куда лучше, чем пропустить цикл из-за икнувшего справочного эндпоинта.
            OnLog?.Invoke($"[{symbol}] Could not read venue filters ({ex.Message}) — using inferred precision.");
        }

        _filters[symbol] = filters;
        return filters;
    }

    /// <summary>
    /// Непроведённая закупка попадает и в лог, и в таблицу исполнений — ровно как пропуск по MA.
    /// Иначе отказ площадки виден только в логе, а таблица молчит, будто цикла и не было.
    /// </summary>
    private void ReportSkipped(string symbol, decimal price, string reason)
    {
        OnLog?.Invoke($"[{symbol}] Skipped — {reason}");
        OnExecution?.Invoke(new DcaExecution
        {
            Symbol = symbol,
            ExecutedAt = DateTime.UtcNow,
            Price = price,
            Quantity = 0,
            TotalUsdt = 0,
            Executed = false,
            Reason = reason
        });
    }

    /// <summary>Базовый актив пары, для сообщений — BTCUSDT → BTC.</summary>
    private static string BaseAssetOf(string symbol)
    {
        foreach (var quote in new[] { "USDT", "USDC", "BUSD", "FDUSD", "USD" })
            if (symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
                return symbol[..^quote.Length];
        return symbol;
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
