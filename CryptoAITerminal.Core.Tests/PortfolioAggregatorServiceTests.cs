using CryptoAITerminal.Gateway.DEX;

namespace CryptoAITerminal.Core.Tests;

public class PortfolioAggregatorServiceTests
{
    [Fact]
    public void ParseBalances_values_holdings_and_scales_decimals()
    {
        const string json = """
        {
          "data": {
            "items": [
              { "contract_ticker_symbol": "USDC", "contract_decimals": 6, "balance": "1500000000", "quote": 1500.0, "quote_rate": 1.0 },
              { "contract_ticker_symbol": "WETH", "contract_decimals": 18, "balance": "2000000000000000000", "quote": 6000.0, "quote_rate": 3000.0 }
            ]
          }
        }
        """;

        var holdings = PortfolioAggregatorService.ParseBalances(json, "Ethereum");

        Assert.Equal(2, holdings.Count);

        var usdc = holdings[0];
        Assert.Equal("Ethereum", usdc.Chain);
        Assert.Equal("USDC", usdc.Symbol);
        Assert.Equal(1500m, usdc.Balance);
        Assert.Equal(1500.0m, usdc.ValueUsd);
        Assert.Equal(1.0m, usdc.PriceUsd);

        var weth = holdings[1];
        Assert.Equal(2m, weth.Balance);
        Assert.Equal(6000.0m, weth.ValueUsd);
        Assert.Equal(3000.0m, weth.PriceUsd);
    }

    [Fact]
    public void ParseBalances_skips_zero_and_untitled()
    {
        const string json = """
        {
          "data": {
            "items": [
              { "contract_ticker_symbol": "ZERO", "contract_decimals": 18, "balance": "0", "quote": 0 },
              { "contract_ticker_symbol": "", "contract_decimals": 18, "balance": "1000000000000000000", "quote": 5 }
            ]
          }
        }
        """;

        var holdings = PortfolioAggregatorService.ParseBalances(json, "Base");
        Assert.Empty(holdings);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("garbage")]
    public void ParseBalances_degrades_to_empty(string json)
    {
        Assert.Empty(PortfolioAggregatorService.ParseBalances(json, "Ethereum"));
    }

    [Fact]
    public void Chains_cover_the_major_evm_networks()
    {
        var ids = PortfolioAggregatorService.Chains.Select(c => c.ChainId).ToHashSet();
        Assert.Contains(1, ids);      // Ethereum
        Assert.Contains(56, ids);     // BSC
        Assert.Contains(8453, ids);   // Base
        Assert.Contains(42161, ids);  // Arbitrum
    }

    [Fact]
    public async Task GetAllHoldingsAsync_returns_empty_without_key()
    {
        var svc = new PortfolioAggregatorService();
        var holdings = await svc.GetAllHoldingsAsync("", "0xabc");
        Assert.Empty(holdings);
    }
}
