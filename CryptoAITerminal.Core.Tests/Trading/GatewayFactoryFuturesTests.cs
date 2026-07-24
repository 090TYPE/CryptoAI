using CryptoAITerminal.Executor;
using CryptoAITerminal.Gateway.OKX;
using CryptoAITerminal.Gateway.Binance;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Trading;

public class GatewayFactoryFuturesTests
{
    private const string Creds = "{\"key\":\"k\",\"secret\":\"s\",\"passphrase\":\"p\"}";

    [Fact]
    public void Create_futures_okx_returns_okx_futures_gateway()
    {
        var gw = new GatewayFactory().Create("okx", "futures", Creds);
        Assert.IsType<OKXFuturesGateway>(gw);
    }

    [Fact]
    public void Create_futures_binance_returns_binance_futures_gateway()
    {
        var gw = new GatewayFactory().Create("binance", "futures", Creds);
        Assert.IsType<BinanceFuturesGateway>(gw);
    }
}
