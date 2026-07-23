using System.Text.Json;
using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Core.Enums;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Trading;

public class TradeContractsTests
{
    [Fact]
    public void PlaceMarketCommand_round_trips_through_json()
    {
        var cmd = new PlaceMarketCommand("okx", "BTCUSDT", OrderSide.Buy, 0.5m, false, 10,
            FuturesMarginMode.Cross, FuturesPositionSide.Long, "cid-1");
        var json = JsonSerializer.Serialize(cmd);
        var back = JsonSerializer.Deserialize<PlaceMarketCommand>(json)!;
        Assert.Equal(cmd, back);
    }
}
