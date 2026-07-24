using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Executor;
using CryptoAITerminal.Gateway.Bybit;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Maps exchange/market to a keyed gateway. CEX spot and futures are both supported
/// server-side, per-user keys included.</summary>
public class GatewayFactoryTests
{
    private const string Creds = "{\"key\":\"k\",\"secret\":\"s\",\"passphrase\":\"p\"}";
    private static readonly GatewayFactory Factory = new();

    [Theory]
    [InlineData("binance")]
    [InlineData("bybit")]
    [InlineData("okx")]
    [InlineData("kucoin")]
    public void Creates_keyed_spot_gateway_for_supported_cex(string exchange)
    {
        var gw = Factory.Create(exchange, "spot", Creds);

        Assert.NotNull(gw);
        Assert.IsAssignableFrom<IExchangeGateway>(gw);
    }

    [Fact]
    public void Unknown_exchange_throws()
    {
        Assert.Throws<NotSupportedException>(() => Factory.Create("ftx", "spot", Creds));
    }

    [Fact]
    public void Creates_keyed_futures_gateway_for_supported_cex()
    {
        var gw = Factory.Create("bybit", "futures", Creds);

        Assert.IsType<BybitFuturesGateway>(gw);
    }

    [Fact]
    public void Unknown_market_throws()
    {
        Assert.Throws<NotSupportedException>(() => Factory.Create("bybit", "margin", Creds));
    }
}
