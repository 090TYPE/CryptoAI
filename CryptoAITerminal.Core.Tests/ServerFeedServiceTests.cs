using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

/// <summary>The terminal reading what the server produced: shared AI digests + this user's inbox.</summary>
public class ServerFeedServiceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        private readonly string _body;
        public StubHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
        }
    }

    private static ServerFeedService Svc(StubHandler h) =>
        new(() => "http://localhost:5080", () => "tok", new HttpClient(h));

    [Fact]
    public async Task GetDigestsAsync_reads_shared_ai_streams()
    {
        var h = new StubHandler(
            "[{\"kind\":\"daily\",\"title\":\"Market cools off\",\"body\":\"# Daily\\nRisk-off.\",\"createdUtc\":\"2026-07-16T08:00:00Z\"}]");
        var list = await Svc(h).GetDigestsAsync("daily", 5);

        Assert.Equal("http://localhost:5080/api/digests?limit=5&kind=daily", h.Request!.RequestUri!.ToString());
        Assert.Equal("tok", Assert.Single(h.Request.Headers.GetValues("X-License")));
        var d = Assert.Single(list);
        Assert.Equal("daily", d.Kind);
        Assert.Equal("Market cools off", d.Title);
    }

    [Fact]
    public async Task GetInboxAsync_reads_personal_events()
    {
        var h = new StubHandler(
            "[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"kind\":\"alert\",\"title\":\"Price alert\"," +
            "\"message\":\"WETH above 1\",\"createdUtc\":\"2026-07-16T08:00:00Z\",\"readUtc\":null}]");
        var list = await Svc(h).GetInboxAsync(unreadOnly: true, limit: 10);

        Assert.Equal("http://localhost:5080/api/inbox?unread=true&limit=10", h.Request!.RequestUri!.ToString());
        var m = Assert.Single(list);
        Assert.Equal("alert", m.Kind);
        Assert.Null(m.ReadUtc);
    }

    [Fact]
    public async Task GetUnreadCountAsync_parses_count()
        => Assert.Equal(3, await Svc(new StubHandler("{\"unread\":3}")).GetUnreadCountAsync());

    [Fact]
    public async Task Stays_silent_when_server_not_configured()
    {
        var h = new StubHandler("[]");
        var svc = new ServerFeedService(() => null, () => "tok", new HttpClient(h));

        Assert.Empty(await svc.GetDigestsAsync());
        Assert.Empty(await svc.GetInboxAsync());
        Assert.Equal(0, await svc.GetUnreadCountAsync());
        Assert.Null(h.Request); // nothing was sent
    }
}
