using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// The client-side favorites sync: pushes the watchlist to /api/favorites with the license
/// header, and stays a silent no-op when the server URL or license isn't configured.
/// </summary>
public class FavoritesSyncServiceTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task PushAsync_puts_watchlist_with_license_header()
    {
        var handler = new CapturingHandler();
        var svc = new FavoritesSyncService(
            baseUrlResolver: () => "http://localhost:5080/",
            tokenProvider: () => "tok-123",
            http: new HttpClient(handler));

        var ok = await svc.PushAsync(new[] { new DexWatchEntry("eth", "0xabc", "WETH") });

        Assert.True(ok);
        Assert.Equal(HttpMethod.Put, handler.Request!.Method);
        Assert.Equal("http://localhost:5080/api/favorites", handler.Request.RequestUri!.ToString());
        Assert.Equal("tok-123", Assert.Single(handler.Request.Headers.GetValues("X-License")));
        Assert.Contains("\"chain\":\"eth\"", handler.Body);
        Assert.Contains("\"tokenAddress\":\"0xabc\"", handler.Body);
        Assert.Contains("\"symbol\":\"WETH\"", handler.Body);
    }

    [Fact]
    public async Task PushAsync_noops_when_url_missing()
    {
        var handler = new CapturingHandler();
        var svc = new FavoritesSyncService(() => null, () => "tok", new HttpClient(handler));

        Assert.False(await svc.PushAsync(new[] { new DexWatchEntry("eth", "0xabc", "W") }));
        Assert.Null(handler.Request); // nothing was sent
    }

    [Fact]
    public async Task PushAsync_noops_when_token_missing()
    {
        var handler = new CapturingHandler();
        var svc = new FavoritesSyncService(() => "http://x", () => null, new HttpClient(handler));

        Assert.False(await svc.PushAsync(new[] { new DexWatchEntry("eth", "0xabc", "W") }));
        Assert.Null(handler.Request);
    }
}
