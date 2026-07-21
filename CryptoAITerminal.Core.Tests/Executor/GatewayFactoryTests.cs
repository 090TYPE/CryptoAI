using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4.1 — maps exchange/market to a keyed gateway. CEX spot supported; Binance and
/// futures are explicitly not yet supported server-side (per-user keys / later phase).</summary>
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
    public void Futures_is_not_supported_yet()
    {
        Assert.Throws<NotSupportedException>(() => Factory.Create("bybit", "futures", Creds));
    }
}
