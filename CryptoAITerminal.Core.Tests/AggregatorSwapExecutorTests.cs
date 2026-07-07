using CryptoAITerminal.Gateway.DEX;

namespace CryptoAITerminal.Core.Tests;

public class AggregatorSwapExecutorTests
{
    [Theory]
    [InlineData("Ethereum", "eth")]
    [InlineData("BSC", "bsc")]
    [InlineData("Base", "base")]
    [InlineData("Arbitrum", "arbitrum")]
    [InlineData("Polygon", "polygon")]
    [InlineData("Avalanche", "avax")]
    public void NetworkSlug_maps_supported_networks(string network, string slug)
    {
        Assert.Equal(slug, AggregatorSwapExecutor.NetworkSlug(network));
    }

    [Theory]
    [InlineData("Solana")]
    [InlineData("Tron")]
    [InlineData("")]
    public void NetworkSlug_returns_null_for_unsupported(string network)
    {
        Assert.Null(AggregatorSwapExecutor.NetworkSlug(network));
    }

    [Fact]
    public void BuildSwapUrl_uses_invariant_numbers_and_all_params()
    {
        var url = AggregatorSwapExecutor.BuildSwapUrl(
            "eth", AggregatorSwapExecutor.NativePlaceholder, "0xToken",
            amount: 1.5m, slippagePercent: 1.5m, account: "0xOwner", gasPriceGwei: 12m);

        Assert.Contains("/v3/eth/swap_quote", url);
        Assert.Contains("inTokenAddress=" + AggregatorSwapExecutor.NativePlaceholder, url);
        Assert.Contains("outTokenAddress=0xToken", url);
        Assert.Contains("amount=1.5", url);      // invariant decimal point, not comma
        Assert.Contains("slippage=1.5", url);
        Assert.Contains("gasPrice=12", url);
        Assert.Contains("account=0xOwner", url);
    }

    [Fact]
    public void ParseSwapTx_extracts_tx_and_min_out()
    {
        const string json = """
        {
          "code": 200,
          "data": {
            "to": "0xRouter",
            "data": "0xabcdef",
            "value": "1000000000000000000",
            "gasPrice": "5000000000",
            "estimatedGas": "250000",
            "minOutAmount": "1234500000",
            "outToken": { "symbol": "USDC", "decimals": 6 }
          }
        }
        """;

        var tx = AggregatorSwapExecutor.ParseSwapTx(json);

        Assert.NotNull(tx);
        Assert.Equal("0xRouter", tx!.To);
        Assert.Equal("0xabcdef", tx.Data);
        Assert.Equal(System.Numerics.BigInteger.Parse("1000000000000000000"), tx.Value);
        Assert.Equal(System.Numerics.BigInteger.Parse("5000000000"), tx.GasPrice);
        Assert.Equal(new System.Numerics.BigInteger(250000), tx.EstimatedGas);
        Assert.Equal("USDC", tx.OutSymbol);
        Assert.Equal(6, tx.OutDecimals);
        Assert.Equal(1234.5m, tx.MinOutTokens);
    }

    [Fact]
    public void ParseSwapTx_falls_back_to_gas_and_outAmount_fields()
    {
        const string json = """
        {
          "data": {
            "to": "0xRouter",
            "data": "0x01",
            "value": "0",
            "gas": "300000",
            "outAmount": "5000000000000000000",
            "outToken": { "symbol": "PEPE", "decimals": 18 }
          }
        }
        """;

        var tx = AggregatorSwapExecutor.ParseSwapTx(json);

        Assert.NotNull(tx);
        Assert.Equal(new System.Numerics.BigInteger(300000), tx!.EstimatedGas);
        Assert.Equal(5m, tx.MinOutTokens);
        Assert.Equal(System.Numerics.BigInteger.Zero, tx.Value);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":{\"to\":\"0xRouter\"}}")]
    [InlineData("not json")]
    public void ParseSwapTx_returns_null_when_no_executable_route(string json)
    {
        Assert.Null(AggregatorSwapExecutor.ParseSwapTx(json));
    }
}
