using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.Gateway.DEX;

namespace CryptoAITerminal.Core.Tests;

public class GeckoTerminalClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StubHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json)
            });
    }

    [Fact]
    public async Task GetPoolOhlcvAsync_ParsesSnakeCaseOhlcvList()
    {
        // GeckoTerminal returns the candle array under the snake_case key "ohlcv_list".
        const string json =
            "{\"data\":{\"attributes\":{\"ohlcv_list\":[" +
            "[1781338500,0.0004945,0.0005276,0.0004887,0.0005216,3061.31]," +
            "[1781338800,0.0005216,0.0005400,0.0005100,0.0005300,1200.50]" +
            "]}}}";

        var http = new HttpClient(new StubHandler(json))
        {
            BaseAddress = new Uri("https://api.geckoterminal.com/api/v2/")
        };
        var client = new GeckoTerminalClient(http);

        var candles = await client.GetPoolOhlcvAsync("solana", "POOL", "minute", 5, 2);

        Assert.Equal(2, candles.Count);
        Assert.Equal(0.0004945m, candles[0].Open);
        Assert.Equal(0.0005276m, candles[0].High);
        Assert.Equal(0.0004887m, candles[0].Low);
        Assert.Equal(0.0005216m, candles[0].Close);
        Assert.Equal(0.0005300m, candles[1].Close);
    }
}
