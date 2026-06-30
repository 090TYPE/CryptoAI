using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class ReleaseNotesServiceTests
{
    /// <summary>Returns a scripted response (or throws) for any request.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _factory;
        public StubHandler(Func<HttpResponseMessage> factory) => _factory = factory;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_factory());
    }

    private static ReleaseNotesService Make(Func<HttpResponseMessage> factory)
        => new("090TYPE/CryptoAI", new HttpClient(new StubHandler(factory)));

    [Fact]
    public async Task Returns_body_on_success()
    {
        var svc = Make(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"body\":\"## Changes\\n- faster\"}")
        });
        var notes = await svc.GetNotesAsync("1.6.1");
        Assert.Equal("## Changes\n- faster", notes);
    }

    [Fact]
    public async Task Returns_null_on_404()
    {
        var svc = Make(() => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}")
        });
        Assert.Null(await svc.GetNotesAsync("9.9.9"));
    }

    [Fact]
    public async Task Returns_null_on_empty_body()
    {
        var svc = Make(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"body\":\"\"}")
        });
        Assert.Null(await svc.GetNotesAsync("1.6.1"));
    }

    [Fact]
    public async Task Returns_null_when_request_throws()
    {
        var svc = Make(() => throw new HttpRequestException("network down"));
        Assert.Null(await svc.GetNotesAsync("1.6.1"));
    }

    [Fact]
    public async Task Propagates_cancellation_from_timeout()
    {
        // An HttpClient timeout surfaces as TaskCanceledException (an OperationCanceledException);
        // the service re-throws it rather than masking it as a normal failure.
        var svc = Make(() => throw new TaskCanceledException("timeout"));
        await Assert.ThrowsAsync<TaskCanceledException>(() => svc.GetNotesAsync("1.6.1"));
    }
}
