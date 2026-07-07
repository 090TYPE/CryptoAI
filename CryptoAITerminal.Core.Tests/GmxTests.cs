using System.Numerics;
using CryptoAITerminal.Gateway.DEX;
using Nethereum.Util;

namespace CryptoAITerminal.Core.Tests;

public class GmxTests
{
    private const string DummyKey = "0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";

    private static string Selector(string signature) =>
        new Sha3Keccack().CalculateHash(signature)[..8].ToLowerInvariant();

    private static string CallSelector(byte[] calldata) =>
        Convert.ToHexString(calldata[..4]).ToLowerInvariant();

    [Fact]
    public void ParseMarkets_reads_gm_markets()
    {
        const string json = """
        { "markets": [
            { "marketToken": "0xMkt1", "indexToken": "0xEth", "longToken": "0xEth", "shortToken": "0xUsdc" },
            { "marketToken": "0xMkt2", "indexToken": "0xBtc", "longToken": "0xBtc", "shortToken": "0xUsdc" }
        ] }
        """;

        var markets = GmxPerpClient.ParseMarkets(json);

        Assert.Equal(2, markets.Count);
        Assert.Equal("0xMkt1", markets[0].MarketToken);
        Assert.Equal("0xEth", markets[0].IndexToken);
        Assert.Equal("0xUsdc", markets[0].ShortToken);
    }

    [Fact]
    public void ParseTickers_reads_min_max_and_mid()
    {
        const string json = """
        [
          { "tokenAddress": "0xEth", "minPrice": "3000", "maxPrice": "3002" },
          { "tokenAddress": "0xBtc", "minPrice": "60000", "maxPrice": "60010" }
        ]
        """;

        var tickers = GmxPerpClient.ParseTickers(json);

        Assert.Equal(2, tickers.Count);
        Assert.Equal(3000m, tickers[0].MinPrice);
        Assert.Equal(3002m, tickers[0].MaxPrice);
        Assert.Equal(3001m, tickers[0].MidPrice);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("garbage")]
    public void Parsers_degrade_to_empty(string json)
    {
        Assert.Empty(GmxPerpClient.ParseMarkets(json));
        Assert.Empty(GmxPerpClient.ParseTickers(json));
    }

    [Fact]
    public void OrderType_market_increase_is_two()
    {
        Assert.Equal(2, (int)GmxOrderType.MarketIncrease);
        Assert.Equal(4, (int)GmxOrderType.MarketDecrease);
    }

    [Fact]
    public void BuildIncreaseCalldata_encodes_three_calls_with_correct_selectors()
    {
        var client = new GmxOrderClient(DummyKey, "http://localhost:8545", enableLiveOrders: false);
        var data = client.BuildIncreaseCalldata(
            market: "0x70d95587d40A2caf56bd97485aB3Eec10Bee6336",
            isLong: true,
            sizeDeltaUsd: BigInteger.Parse("1000000000000000000000000000000000"), // 1000 * 1e30
            collateralAmountRaw: new BigInteger(50_000_000),                       // 50 USDC (6dp)
            acceptablePrice: BigInteger.Parse("3100000000000000"),
            executionFeeWei: BigInteger.Parse("700000000000000"));                 // 0.0007 ETH

        Assert.Equal(3, data.Count);

        // sendWnt/sendTokens have simple, stable signatures — assert their exact selectors.
        Assert.Equal(Selector("sendWnt(address,uint256)"), CallSelector(data[0]));
        Assert.Equal(Selector("sendTokens(address,address,uint256)"), CallSelector(data[1]));

        // createOrder carries the big nested tuple; assert it encodes deterministically, is distinct,
        // and is the largest payload (the struct). Its exact selector must be verified against the
        // live GMX deployment, not hard-coded here.
        Assert.True(data[2].Length > data[0].Length);
        Assert.NotEqual(CallSelector(data[0]), CallSelector(data[2]));
        Assert.NotEqual(CallSelector(data[1]), CallSelector(data[2]));

        var again = client.BuildIncreaseCalldata(
            "0x70d95587d40A2caf56bd97485aB3Eec10Bee6336", true,
            BigInteger.Parse("1000000000000000000000000000000000"), new BigInteger(50_000_000),
            BigInteger.Parse("3100000000000000"), BigInteger.Parse("700000000000000"));
        Assert.Equal(CallSelector(data[2]), CallSelector(again[2])); // deterministic
    }

    [Fact]
    public async Task PlaceMarketIncreaseAsync_is_gated_off_by_default()
    {
        var client = new GmxOrderClient(DummyKey, "http://localhost:8545", enableLiveOrders: false);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.PlaceMarketIncreaseAsync("0xMkt", true, 1, 1, 1, 1));
    }

    [Fact]
    public void GmxArbitrum_addresses_are_populated()
    {
        Assert.StartsWith("0x", GmxArbitrum.ExchangeRouter);
        Assert.StartsWith("0x", GmxArbitrum.OrderVault);
        Assert.StartsWith("0x", GmxArbitrum.Usdc);
        Assert.Equal(42, GmxArbitrum.ExchangeRouter.Length);
    }
}
