using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Bots;

/// <summary>
/// Два заслона AI-трейдера, оба пробивались насквозь.
///
/// ПЕРВЫЙ — потолок на один ордер. Размер приходил от модели двумя полями, и они согласовывались
/// только когда одно из них пустое:
///
///     if (usd &lt;= 0) usd = qty * mid;
///     if (qty &lt;= 0) qty = usd / mid;
///
/// Если модель прислала ОБА, не срабатывала ни одна строка: потолок сверялся с присланным usd, а
/// в биржу уходило присланное quantity. {"usd":40,"quantity":2} по биткоину — это 128 000 USD
/// сквозь лимит в 50 USD за ордер. Злого умысла для этого не нужно: моделям свойственно заполнять
/// оба поля, и достаточно одной арифметической ошибки в рассуждении.
///
/// ВТОРОЙ — портфельные потолки в бумажном режиме. Выход `if (!LiveEnabled) return PaperFill(...)`
/// стоит ВЫШЕ проверок числа позиций, экспозиции и денег, поэтому бумажный прогон набирал книгу,
/// которую живой режим с теми же настройками не собрал бы никогда. Кривая доходности получалась
/// от более концентрированной книги с уходящей в минус кассой — а под неё человек заводит
/// настоящие деньги.
/// </summary>
public class AgentOrderSizeAndPaperCapTests
{
    // ── Заглушки ──────────────────────────────────────────────────────────────

    private sealed class StubGateway : IExchangeGateway
    {
        private readonly decimal _px;
        public StubGateway(decimal px) => _px = px;
        public bool HasPrivateApiCredentials => true;
        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Order> PlaceOrderAsync(Order o) { o.Status = OrderStatus.Filled; return Task.FromResult(o); }
        public Task CancelOrderAsync(string id) => Task.CompletedTask;
        public Task<decimal> GetBalanceAsync(string a) => Task.FromResult(1_000_000m);
        public Task<OrderBook> GetOrderBookAsync(string s, int d = 10) => Task.FromResult(new OrderBook
        {
            Symbol = s,
            Bids = [new OrderBookLevel { Price = _px, Quantity = 1000m }],
            Asks = [new OrderBookLevel { Price = _px, Quantity = 1000m }],
        });
        public IObservable<MarketData> MarketDataStream => System.Reactive.Linq.Observable.Empty<MarketData>();
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public ScriptedHandler(IEnumerable<string> responses) => _responses = new Queue<string>(responses);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Count > 0 ? _responses.Dequeue() : EndTurn("done"))
            });
    }

    private static string ToolUse(string id, string name, string inputJson) =>
        $$"""
        {"stop_reason":"tool_use","content":[
          {"type":"text","text":"trading"},
          {"type":"tool_use","id":"{{id}}","name":"{{name}}","input":{{inputJson}}}
        ]}
        """;

    private static string EndTurn(string text) =>
        $$"""{"stop_reason":"end_turn","content":[{"type":"text","text":"{{text}}"}]}""";

    /// <summary>Прогоняет один круг агента и отдаёт все состоявшиеся исполнения.</summary>
    private static async Task<List<(string Sym, string Side, decimal Qty, decimal Usd)>> RunAsync(
        decimal price, AiTraderAgentService.Limits limits, params string[] orders)
    {
        var script = new List<string>();
        for (int i = 0; i < orders.Length; i++)
            script.Add(ToolUse("t" + i, "place_order", orders[i]));
        script.Add(EndTurn("done"));

        var svc = new AiTraderAgentService(new StubGateway(price), limits: limits,
            httpForTests: new HttpClient(new ScriptedHandler(script))) { LiveEnabled = false };

        var fills = new List<(string, string, decimal, decimal)>();
        svc.OnFill += (sym, side, qty, _, usd, _) => fills.Add((sym, side, qty, usd));

        await svc.RunOnceAsync("test-key", "claude-sonnet-4-6");
        return fills;
    }

    // ── Потолок на один ордер ─────────────────────────────────────────────────

    [Fact]
    public async Task A_quantity_that_contradicts_the_declared_usd_cannot_slip_past_the_cap()
    {
        // Ровно тот случай: модель называет 40 USD, а количество тянет на 128 000.
        var fills = await RunAsync(
            price: 64_000m,
            new AiTraderAgentService.Limits(MaxOrderUsd: 50m),
            """{"symbol":"BTCUSDT","side":"buy","usd":40,"quantity":2}""");

        Assert.Empty(fills);
    }

    [Fact]
    public async Task The_cap_is_measured_against_the_quantity_that_is_actually_placed()
    {
        // Те же два поля, но количество на этот раз укладывается в потолок — заявка должна пройти,
        // и пройти именно количеством, а не выдуманным usd.
        var fills = await RunAsync(
            price: 100m,
            new AiTraderAgentService.Limits(MaxOrderUsd: 50m),
            """{"symbol":"BTCUSDT","side":"buy","usd":5,"quantity":0.3}""");

        var fill = Assert.Single(fills);
        Assert.Equal(0.3m, fill.Qty);
        Assert.Equal(30m, fill.Usd);   // 0.3 × 100, а не заявленные 5
    }

    [Fact]
    public async Task Sizing_by_usd_alone_still_works()
    {
        var fills = await RunAsync(
            price: 100m,
            new AiTraderAgentService.Limits(MaxOrderUsd: 100m),
            """{"symbol":"BTCUSDT","side":"buy","usd":50}""");

        var fill = Assert.Single(fills);
        Assert.Equal(0.5m, fill.Qty);
    }

    [Fact]
    public async Task Sizing_by_quantity_alone_still_works()
    {
        var fills = await RunAsync(
            price: 100m,
            new AiTraderAgentService.Limits(MaxOrderUsd: 100m),
            """{"symbol":"BTCUSDT","side":"buy","quantity":0.4}""");

        var fill = Assert.Single(fills);
        Assert.Equal(0.4m, fill.Qty);
        Assert.Equal(40m, fill.Usd);
    }

    // ── Портфельные потолки в бумажном режиме ─────────────────────────────────

    [Fact]
    public async Task Paper_respects_the_open_position_cap()
    {
        var fills = await RunAsync(
            price: 100m,
            new AiTraderAgentService.Limits(MaxOrderUsd: 1000m, MaxOpenPositions: 2,
                MaxTotalExposureUsd: 100_000m, VirtualStartUsd: 100_000m),
            """{"symbol":"AAAUSDT","side":"buy","usd":100}""",
            """{"symbol":"BBBUSDT","side":"buy","usd":100}""",
            """{"symbol":"CCCUSDT","side":"buy","usd":100}""");

        Assert.Equal(2, fills.Count);
        Assert.DoesNotContain(fills, f => f.Sym == "CCCUSDT");
    }

    [Fact]
    public async Task Paper_respects_the_total_exposure_cap()
    {
        var fills = await RunAsync(
            price: 100m,
            new AiTraderAgentService.Limits(MaxOrderUsd: 1000m, MaxOpenPositions: 10,
                MaxTotalExposureUsd: 250m, VirtualStartUsd: 100_000m),
            """{"symbol":"AAAUSDT","side":"buy","usd":200}""",
            """{"symbol":"BBBUSDT","side":"buy","usd":200}""");

        // 200 проходит, 200 + 200 = 400 против потолка 250 — нет.
        var fill = Assert.Single(fills);
        Assert.Equal("AAAUSDT", fill.Sym);
    }

    [Fact]
    public async Task Paper_cash_can_no_longer_go_negative()
    {
        var fills = await RunAsync(
            price: 100m,
            new AiTraderAgentService.Limits(MaxOrderUsd: 10_000m, MaxOpenPositions: 10,
                MaxTotalExposureUsd: 100_000m, VirtualStartUsd: 150m),
            """{"symbol":"AAAUSDT","side":"buy","usd":100}""",
            """{"symbol":"AAAUSDT","side":"buy","usd":100}""");

        // Виртуальной кассы 150: первая покупка проходит, на вторую денег нет.
        var fill = Assert.Single(fills);
        Assert.Equal(100m, fill.Usd);
    }

    [Fact]
    public async Task Paper_still_lets_a_position_be_reduced_after_the_caps_tighten()
    {
        // Сокращение и закрытие обязаны проходить всегда — иначе бумажную книгу нельзя разгрузить.
        var fills = await RunAsync(
            price: 100m,
            new AiTraderAgentService.Limits(MaxOrderUsd: 1000m, MaxOpenPositions: 1,
                MaxTotalExposureUsd: 150m, VirtualStartUsd: 100_000m),
            """{"symbol":"AAAUSDT","side":"buy","usd":100}""",
            """{"symbol":"AAAUSDT","side":"sell","usd":100}""");

        Assert.Equal(2, fills.Count);
        Assert.Equal("sell", fills[1].Side);
    }
}
