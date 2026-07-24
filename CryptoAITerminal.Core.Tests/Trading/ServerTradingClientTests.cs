using System.Net;
using System.Text;
using System.Text.Json;
using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests.Trading;

/// <summary>
/// I1 — the desktop is the only client, so the server's (uid, ClientOrderId) idempotency is
/// unreachable unless the client itself reuses the id across a lost transport. A POST that times
/// out while the order goes live must never become a second real order.
/// </summary>
public class ServerTradingClientTests
{
    /// <summary>Plays a scripted sequence of outcomes and records what was actually sent.</summary>
    private sealed class StubHandler(params Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _next;
        public int Calls;
        public readonly List<string> Bodies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return script[Math.Min(_next++, script.Length - 1)](); // the func throws for a transport failure
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ServerTradingClient Make(StubHandler handler) =>
        // Zero backoff: the retry policy is what is under test, not the wall clock.
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") },
            "http://localhost/hub", "lic", [TimeSpan.Zero, TimeSpan.Zero]);

    private static PlaceMarketCommand Cmd(string cid = "cid-abc") =>
        new("binance", "BTCUSDT", OrderSide.Buy, 0.01m, false, 5, FuturesMarginMode.Isolated,
            FuturesPositionSide.Long, cid);

    private static string ClientOrderIdOf(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("clientOrderId").GetString()!;

    [Fact]
    public async Task A_transport_failure_is_retried_with_the_same_client_order_id()
    {
        var handler = new StubHandler(
            () => throw new HttpRequestException("connection reset"),
            () => Json(HttpStatusCode.OK, """{"accepted":true,"orderId":"EX-1","rejectReason":null}"""));

        var res = await Make(handler).PlaceMarketAsync(Cmd());

        Assert.True(res.Accepted);
        Assert.Equal("EX-1", res.OrderId);
        Assert.Equal(2, handler.Calls);
        // Reusing the id is the whole safety argument: the retry can only ever replay the first
        // attempt's outcome server-side. A fresh id per attempt would double the position.
        Assert.All(handler.Bodies, b => Assert.Equal("cid-abc", ClientOrderIdOf(b)));
    }

    [Fact]
    public async Task A_timeout_is_retried_and_the_same_client_order_id_is_reused()
    {
        var handler = new StubHandler(
            () => throw new TaskCanceledException("timed out"),
            () => Json(HttpStatusCode.OK, """{"accepted":true,"orderId":"EX-2","rejectReason":null}"""));

        var res = await Make(handler).PlaceMarketAsync(Cmd("cid-timeout"));

        Assert.True(res.Accepted);
        Assert.Equal(2, handler.Calls);
        Assert.All(handler.Bodies, b => Assert.Equal("cid-timeout", ClientOrderIdOf(b)));
    }

    [Fact]
    public async Task A_non_success_status_is_not_retried_and_carries_the_code_and_body()
    {
        var handler = new StubHandler(() => Json(HttpStatusCode.TooManyRequests, "rate limited"));

        var res = await Make(handler).PlaceMarketAsync(Cmd());

        Assert.Equal(1, handler.Calls); // a response arrived; the server has already decided
        Assert.False(res.Accepted);
        Assert.Null(res.OrderId);
        Assert.Contains("429", res.RejectReason!);
        Assert.Contains("rate limited", res.RejectReason!);
    }

    [Fact]
    public async Task A_non_json_server_error_body_is_reported_rather_than_thrown()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("<html>nginx</html>", Encoding.UTF8, "text/html")
        });

        var res = await Make(handler).PlaceMarketAsync(Cmd());

        Assert.Equal(1, handler.Calls);
        Assert.False(res.Accepted);
        Assert.Contains("500", res.RejectReason!);
    }

    [Fact]
    public async Task All_attempts_failing_at_transport_level_reports_an_ambiguous_outcome()
    {
        var handler = new StubHandler(() => throw new HttpRequestException("no route to host"));

        var res = await Make(handler).PlaceMarketAsync(Cmd());

        Assert.Equal(3, handler.Calls); // first attempt + 2 retries
        Assert.False(res.Accepted);
        // The order may well be live — claiming a clean failure would invite a re-click.
        Assert.Contains("may or may not", res.RejectReason!, StringComparison.OrdinalIgnoreCase);
    }
}
