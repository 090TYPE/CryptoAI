using CryptoAITerminal.Gateway.DEX;

namespace CryptoAITerminal.Core.Tests;

public class HyperliquidPerpClientTests
{
    [Fact]
    public void ParseAccountState_reads_margin_and_positions()
    {
        const string json = """
        {
          "marginSummary": { "accountValue": "12500.50", "totalMarginUsed": "3000.0" },
          "withdrawable": "9500.50",
          "assetPositions": [
            {
              "type": "oneWay",
              "position": {
                "coin": "ETH",
                "szi": "1.5",
                "entryPx": "3000.0",
                "positionValue": "4650.0",
                "unrealizedPnl": "150.0",
                "liquidationPx": "2400.0",
                "leverage": { "type": "cross", "value": 5 },
                "marginUsed": "930.0"
              }
            },
            {
              "type": "oneWay",
              "position": {
                "coin": "BTC",
                "szi": "-0.1",
                "entryPx": "60000.0",
                "positionValue": "6000.0",
                "unrealizedPnl": "-40.0",
                "liquidationPx": "72000.0",
                "leverage": { "type": "isolated", "value": 3 },
                "marginUsed": "2000.0"
              }
            }
          ]
        }
        """;

        var state = HyperliquidPerpClient.ParseAccountState(json);

        Assert.Equal(12500.50m, state.AccountValueUsd);
        Assert.Equal(3000.0m, state.TotalMarginUsedUsd);
        Assert.Equal(9500.50m, state.WithdrawableUsd);
        Assert.Equal(2, state.Positions.Count);

        var eth = state.Positions[0];
        Assert.Equal("ETH", eth.Coin);
        Assert.True(eth.IsLong);
        Assert.Equal("LONG", eth.Side);
        Assert.Equal(1.5m, eth.Size);
        Assert.Equal(3000.0m, eth.EntryPrice);
        Assert.Equal(150.0m, eth.UnrealizedPnl);
        Assert.Equal(5, eth.Leverage);
        Assert.Equal("cross", eth.LeverageType);

        var btc = state.Positions[1];
        Assert.False(btc.IsLong);
        Assert.Equal("SHORT", btc.Side);
        Assert.Equal(-0.1m, btc.Size);
        Assert.Equal("isolated", btc.LeverageType);
    }

    [Fact]
    public void ParseAccountState_skips_flat_positions()
    {
        const string json = """
        {
          "marginSummary": { "accountValue": "100", "totalMarginUsed": "0" },
          "withdrawable": "100",
          "assetPositions": [
            { "position": { "coin": "SOL", "szi": "0", "entryPx": "0" } }
          ]
        }
        """;

        var state = HyperliquidPerpClient.ParseAccountState(json);
        Assert.Empty(state.Positions);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("garbage")]
    public void ParseAccountState_degrades_to_empty(string json)
    {
        var state = HyperliquidPerpClient.ParseAccountState(json);
        Assert.Equal(0m, state.AccountValueUsd);
        Assert.Equal(0m, state.TotalMarginUsedUsd);
        Assert.Empty(state.Positions);
    }

    [Fact]
    public void BuildOrderActionJson_produces_canonical_wire_format()
    {
        var json = HyperliquidPerpClient.BuildOrderActionJson(
            assetIndex: 4, isBuy: true, limitPrice: 3000.5m, size: 1.25m, reduceOnly: false, tif: "Gtc");

        Assert.Equal(
            "{\"type\":\"order\",\"orders\":[{\"a\":4,\"b\":true,\"p\":\"3000.5\",\"s\":\"1.25\",\"r\":false,\"t\":{\"limit\":{\"tif\":\"Gtc\"}}}],\"grouping\":\"na\"}",
            json);
    }

    [Fact]
    public void BuildOrderActionJson_marks_short_reduceonly_ioc()
    {
        var json = HyperliquidPerpClient.BuildOrderActionJson(
            assetIndex: 0, isBuy: false, limitPrice: 60000m, size: 0.1m, reduceOnly: true, tif: "Ioc");

        Assert.Contains("\"b\":false", json);
        Assert.Contains("\"r\":true", json);
        Assert.Contains("\"tif\":\"Ioc\"", json);
    }

    [Fact]
    public void PlaceOrderAsync_is_gated_until_testnet_validated()
    {
        var client = new HyperliquidPerpClient();
        Assert.ThrowsAsync<NotSupportedException>(() =>
            client.PlaceOrderAsync("0xkey", 0, true, 100m, 1m, false));
    }
}
