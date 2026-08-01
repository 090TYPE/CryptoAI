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
/// Живая торговля на СПОТЕ — режим AI-трейдера по умолчанию (AiTraderViewModel: Spot).
///
/// Портфельные потолки и выход из позиции опирались на <c>GetOpenPositionsAsync</c>. Позиций на
/// споте не существует, ни один спотовый шлюз этот метод не реализует, и работала заглушка
/// интерфейса — пустой список. Отсюда три следствия, и все три противоречили тому, что показано
/// на экране:
///   • лимит числа позиций не срабатывал ни разу — список всегда пуст;
///   • суммарная экспозиция всегда считалась нулевой, упирало только в остаток на балансе;
///   • close_position на каждый вызов отвечал «нет открытой позиции» — агент умел покупать и не
///     умел выйти.
///
/// Чинится не «взять баланс базового актива»: он общий со всем, что пользователь держит по своим
/// причинам, и продавать его целиком — распродажа чужих монет. Агент ведёт СВОЙ инвентарь и
/// распоряжается только им.
/// </summary>
public class AgentLiveSpotBookTests
{
    private sealed class SpotGateway : IExchangeGateway
    {
        private readonly decimal _px;
        public SpotGateway(decimal px) => _px = px;

        public List<Order> Placed { get; } = [];
        public bool HasPrivateApiCredentials => true;

        public Task<Order> PlaceOrderAsync(Order o) { Placed.Add(o); o.Status = OrderStatus.Filled; return Task.FromResult(o); }
        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task CancelOrderAsync(string id) => Task.CompletedTask;
        public Task<decimal> GetBalanceAsync(string a) => Task.FromResult(1_000_000m);
        public Task<OrderBook> GetOrderBookAsync(string s, int d = 10) => Task.FromResult(new OrderBook
        {
            Symbol = s,
            Bids = [new OrderBookLevel { Price = _px, Quantity = 1000m }],
            Asks = [new OrderBookLevel { Price = _px, Quantity = 1000m }],
        });
        public IObservable<MarketData> MarketDataStream => System.Reactive.Linq.Observable.Empty<MarketData>();
        // Спотовый шлюз позиций не отдаёт — ровно как настоящие: работает заглушка интерфейса.
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _r;
        public ScriptedHandler(IEnumerable<string> r) => _r = new Queue<string>(r);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage m, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(_r.Count > 0 ? _r.Dequeue() : End("done")) });
    }

    private static string Tool(string id, string name, string json) =>
        $$"""
        {"stop_reason":"tool_use","content":[
          {"type":"text","text":"trading"},
          {"type":"tool_use","id":"{{id}}","name":"{{name}}","input":{{json}}}
        ]}
        """;

    private static string End(string t) => $$"""{"stop_reason":"end_turn","content":[{"type":"text","text":"{{t}}"}]}""";

    private static async Task<(SpotGateway Gate, List<(string Sym, string Side, decimal Qty)> Fills)> RunAsync(
        decimal price, AiTraderAgentService.Limits limits, params (string Tool, string Json)[] calls)
    {
        var script = new List<string>();
        for (int i = 0; i < calls.Length; i++) script.Add(Tool("t" + i, calls[i].Tool, calls[i].Json));
        script.Add(End("done"));

        var gate = new SpotGateway(price);
        var svc = new AiTraderAgentService(gate, limits: limits,
            httpForTests: new HttpClient(new ScriptedHandler(script)),
            marketType: TradingMarketType.Spot) { LiveEnabled = true };

        var fills = new List<(string, string, decimal)>();
        svc.OnFill += (sym, side, qty, _, _, _) => fills.Add((sym, side, qty));

        await svc.RunOnceAsync("test-key", "claude-sonnet-4-6");
        return (gate, fills);
    }

    private static AiTraderAgentService.Limits Limits(int maxPositions = 10, decimal maxExposure = 1_000_000m) =>
        new(MaxOrderUsd: 100_000m, MaxOpenPositions: maxPositions, MaxTotalExposureUsd: maxExposure);

    // ── Лимит числа позиций ───────────────────────────────────────────────────

    [Fact]
    public async Task The_open_position_cap_now_binds_on_live_spot()
    {
        var (_, fills) = await RunAsync(100m, Limits(maxPositions: 2),
            ("place_order", """{"symbol":"AAAUSDT","side":"buy","usd":100}"""),
            ("place_order", """{"symbol":"BBBUSDT","side":"buy","usd":100}"""),
            ("place_order", """{"symbol":"CCCUSDT","side":"buy","usd":100}"""));

        Assert.Equal(2, fills.Count);
        Assert.DoesNotContain(fills, f => f.Sym == "CCCUSDT");
    }

    [Fact]
    public async Task Adding_to_a_symbol_already_held_is_not_a_new_position()
    {
        var (_, fills) = await RunAsync(100m, Limits(maxPositions: 1),
            ("place_order", """{"symbol":"AAAUSDT","side":"buy","usd":100}"""),
            ("place_order", """{"symbol":"AAAUSDT","side":"buy","usd":100}"""));

        Assert.Equal(2, fills.Count);
    }

    // ── Экспозиция ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cumulative_exposure_now_binds_on_live_spot()
    {
        // Раньше экспозиция всегда была нулевой, и агент покупал, пока не кончится баланс.
        var (_, fills) = await RunAsync(100m, Limits(maxExposure: 250m),
            ("place_order", """{"symbol":"AAAUSDT","side":"buy","usd":200}"""),
            ("place_order", """{"symbol":"BBBUSDT","side":"buy","usd":200}"""));

        var fill = Assert.Single(fills);
        Assert.Equal("AAAUSDT", fill.Sym);
    }

    // ── Выход из позиции ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_agent_can_now_close_what_it_bought()
    {
        var (gate, fills) = await RunAsync(100m, Limits(),
            ("place_order", """{"symbol":"AAAUSDT","side":"buy","usd":300}"""),
            ("close_position", """{"symbol":"AAAUSDT"}"""));

        Assert.Equal(2, fills.Count);
        Assert.Equal("close", fills[1].Side);
        Assert.Equal(3m, fills[1].Qty);                 // 300 / 100
        Assert.Equal(2, gate.Placed.Count);
        Assert.Equal(OrderSide.Sell, gate.Placed[1].Side);
    }

    [Fact]
    public async Task Closing_what_the_agent_never_bought_sells_nothing()
    {
        // Баланс монеты может быть каким угодно — это деньги пользователя, а не агента.
        var (gate, fills) = await RunAsync(100m, Limits(),
            ("close_position", """{"symbol":"AAAUSDT"}"""));

        Assert.Empty(fills);
        Assert.Empty(gate.Placed);
    }

    [Fact]
    public async Task Selling_more_than_the_agent_holds_is_trimmed_to_its_own_holding()
    {
        var (gate, fills) = await RunAsync(100m, Limits(),
            ("place_order", """{"symbol":"AAAUSDT","side":"buy","usd":100}"""),   // 1 монета
            ("place_order", """{"symbol":"AAAUSDT","side":"sell","quantity":50}"""));

        Assert.Equal(2, fills.Count);
        Assert.Equal(1m, fills[1].Qty);                 // обрезано до собственного остатка
        Assert.Equal(1m, gate.Placed[1].Quantity);
    }

    [Fact]
    public async Task A_short_sale_on_spot_is_refused_outright()
    {
        var (gate, fills) = await RunAsync(100m, Limits(),
            ("place_order", """{"symbol":"AAAUSDT","side":"sell","usd":100}"""));

        Assert.Empty(fills);
        Assert.Empty(gate.Placed);
    }

    [Fact]
    public async Task Selling_out_frees_the_position_slot_again()
    {
        var (_, fills) = await RunAsync(100m, Limits(maxPositions: 1),
            ("place_order", """{"symbol":"AAAUSDT","side":"buy","usd":100}"""),
            ("close_position", """{"symbol":"AAAUSDT"}"""),
            ("place_order", """{"symbol":"BBBUSDT","side":"buy","usd":100}"""));

        Assert.Equal(3, fills.Count);
        Assert.Equal("BBBUSDT", fills[2].Sym);
    }
}
