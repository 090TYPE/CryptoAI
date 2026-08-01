using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Bots;

/// <summary>
/// DCA считал объём как <c>Math.Round(allocation / price, 6)</c> и отправлял его на биржу как есть —
/// при том что GridBot на том же шлюзе с первого дня спрашивает LOT_SIZE/NOTIONAL и округляет по
/// ним. Шесть знаков «на глазок» совпадают с правилами площадки только у пар вроде BTCUSDT: у DOGE
/// шаг лота 1, у большинства альтов 0.1 или 0.01, и туда уезжал объём с шестью знаками — биржа
/// отвечала -1013 LOT_SIZE. Закупка на сумму ниже minNotional отбивалась целиком.
///
/// Наружу это выходило одной строкой «Order failed» в логе: расписание считалось отработавшим,
/// следующий цикл назначался, таблица закупок оставалась пустой. Человек, поставивший DCA на
/// неделю, узнал бы об этом по отсутствию монет на балансе.
/// </summary>
public class DcaFilterTests
{
    // ── Заглушка биржи ────────────────────────────────────────────────────────

    private class FakeGateway : IExchangeGateway
    {
        private readonly decimal _price;
        private readonly SymbolFilters? _filters;

        public FakeGateway(decimal price, SymbolFilters? filters = null)
        {
            _price = price;
            _filters = filters;
        }

        public List<Order> Placed { get; } = [];
        public int FilterCalls { get; private set; }

        public Task<Order> PlaceOrderAsync(Order order)
        {
            Placed.Add(order);
            return Task.FromResult(order);
        }

        public virtual Task<SymbolFilters?> GetSymbolFiltersAsync(string symbol, CancellationToken ct = default)
        {
            FilterCalls++;
            return Task.FromResult(_filters);
        }

        public Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10) =>
            Task.FromResult(new OrderBook
            {
                Symbol = symbol,
                Bids = [new OrderBookLevel { Price = _price, Quantity = 10m }],
                Asks = [new OrderBookLevel { Price = _price, Quantity = 10m }],
            });

        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task CancelOrderAsync(string orderId) => Task.CompletedTask;
        public Task<decimal> GetBalanceAsync(string asset) => Task.FromResult(1_000_000m);
        public IObservable<MarketData> MarketDataStream => System.Reactive.Linq.Observable.Empty<MarketData>();
        public bool HasPrivateApiCredentials => true;
    }

    private sealed class ThrowingFilterGateway(decimal price) : FakeGateway(price)
    {
        public override Task<SymbolFilters?> GetSymbolFiltersAsync(string symbol, CancellationToken ct = default) =>
            throw new TimeoutException("exchangeInfo timed out");
    }

    private static DcaBotConfig OneCoin(decimal budget) => new()
    {
        TotalBudgetPerCycleUsdt = budget,
        Coins = [new DcaCoinEntry { Symbol = "DOGEUSDT", WeightPercent = 100 }],
    };

    /// <summary>Прогоняет один цикл и отдаёт всё, что бот сделал и сказал.</summary>
    private static async Task<(FakeGateway Gate, List<DcaExecution> Executions, List<string> Log)> RunCycleAsync(
        decimal price, SymbolFilters? filters, decimal budget)
    {
        var gate = new FakeGateway(price, filters);
        var executions = new List<DcaExecution>();
        var log = new List<string>();

        using var bot = new DcaBot(gate, OneCoin(budget));
        bot.OnExecution += executions.Add;
        bot.OnLog += log.Add;

        await bot.ForceExecuteNowAsync();
        await bot.StopAsync();

        return (gate, executions, log);
    }

    // ── Шаг лота ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_quantity_is_snapped_to_the_venue_lot_step()
    {
        // DOGE: шаг 1 монета. 100 USDT по 0.37 = 270.27… — на биржу должно уйти 270, а не 270.27027.
        var filters = new SymbolFilters("DOGEUSDT", TickSize: 0.00001m, StepSize: 1m,
                                        MinQuantity: 1m, MinNotional: 5m);

        var (gate, executions, _) = await RunCycleAsync(price: 0.37m, filters, budget: 100m);

        var order = Assert.Single(gate.Placed);
        Assert.Equal(270m, order.Quantity);
        Assert.Equal(0m, order.Quantity % filters.StepSize);
        Assert.True(executions.Single().Executed);
    }

    [Fact]
    public async Task Without_published_rules_the_old_six_decimal_cap_still_applies()
    {
        // Шлюз, который правил не отдаёт (заглушка интерфейса, DEX, бумажная торговля), должен
        // работать ровно как раньше — иначе правка ломает то, что не чинила.
        var (gate, _, _) = await RunCycleAsync(price: 0.37m, filters: null, budget: 100m);

        var order = Assert.Single(gate.Placed);
        Assert.Equal(270.27027m, order.Quantity);
    }

    // ── Минимальный лот ───────────────────────────────────────────────────────

    [Fact]
    public async Task An_allocation_below_the_minimum_lot_is_reported_not_shrunk()
    {
        // 3 USDT по 0.37 = 8.1 монеты при минимуме в 100 — ордера быть не должно, но человек
        // обязан увидеть ПОЧЕМУ, и увидеть это в таблице закупок, а не только в логе.
        var filters = new SymbolFilters("DOGEUSDT", 0.00001m, StepSize: 1m,
                                        MinQuantity: 100m, MinNotional: 0m);

        var (gate, executions, log) = await RunCycleAsync(price: 0.37m, filters, budget: 3m);

        Assert.Empty(gate.Placed);
        var skipped = Assert.Single(executions);
        Assert.False(skipped.Executed);
        Assert.Contains("minimum lot", skipped.Reason);
        Assert.Contains("DOGE ", skipped.Reason);   // базовый актив, а не пара
        Assert.Contains(log, l => l.Contains("Skipped"));
    }

    // ── Минимальная сумма ─────────────────────────────────────────────────────

    [Fact]
    public async Task An_order_below_the_venue_minimum_notional_never_reaches_the_exchange()
    {
        // 6 USDT проходят и по шагу, и по минимальному лоту, но не дотягивают до 10 USDT —
        // биржа ответила бы отказом. Отказ дешевле не получать.
        var filters = new SymbolFilters("DOGEUSDT", 0.00001m, StepSize: 1m,
                                        MinQuantity: 1m, MinNotional: 10m);

        var (gate, executions, _) = await RunCycleAsync(price: 0.37m, filters, budget: 6m);

        Assert.Empty(gate.Placed);
        var skipped = Assert.Single(executions);
        Assert.False(skipped.Executed);
        Assert.Contains("below the venue minimum", skipped.Reason);
    }

    [Fact]
    public async Task An_order_that_clears_every_rule_goes_through_untouched()
    {
        var filters = new SymbolFilters("DOGEUSDT", 0.00001m, StepSize: 1m,
                                        MinQuantity: 1m, MinNotional: 10m);

        var (gate, executions, _) = await RunCycleAsync(price: 0.37m, filters, budget: 50m);

        var order = Assert.Single(gate.Placed);
        Assert.Equal(135m, order.Quantity);                 // 50 / 0.37 = 135.13… → 135
        Assert.True(order.Quantity * 0.37m >= filters.MinNotional);
        Assert.True(executions.Single().Executed);
    }

    // ── Кэш и устойчивость ────────────────────────────────────────────────────

    [Fact]
    public async Task The_rules_are_fetched_once_per_symbol_not_once_per_cycle()
    {
        // Правила меняются раз в месяцы, а DCA проходит по списку монет на каждом круге.
        var gate = new FakeGateway(0.37m, new SymbolFilters("DOGEUSDT", 0.00001m, 1m, 1m, 10m));

        using var bot = new DcaBot(gate, OneCoin(50m));
        await bot.ForceExecuteNowAsync();
        await bot.ForceExecuteNowAsync();
        await bot.ForceExecuteNowAsync();
        await bot.StopAsync();

        Assert.Equal(3, gate.Placed.Count);
        Assert.Equal(1, gate.FilterCalls);
    }

    [Fact]
    public async Task A_gateway_that_throws_on_the_lookup_does_not_cancel_the_purchase()
    {
        // Справочный эндпоинт икнул — это не повод пропустить закупку по расписанию.
        var gate = new ThrowingFilterGateway(0.37m);
        var log = new List<string>();

        using var bot = new DcaBot(gate, OneCoin(100m));
        bot.OnLog += log.Add;
        await bot.ForceExecuteNowAsync();
        await bot.StopAsync();

        Assert.Single(gate.Placed);
        Assert.Contains(log, l => l.Contains("Could not read venue filters"));
    }
}
