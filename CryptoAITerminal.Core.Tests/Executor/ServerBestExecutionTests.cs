using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Executor;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4.4 — pure best-execution selection: compare each connected exchange's top quote on
/// an effective (fee-adjusted) basis and pick the cheapest to buy / richest to sell. Ported from the
/// desktop BestExecutionRouterService's single-venue path.</summary>
public class ServerBestExecutionTests
{
    private static ServerBestExecution.ExchangeQuote Q(string ex, decimal price, decimal fee = 0.1m)
        => new(ex, price, fee);

    [Fact]
    public void Buy_picks_lowest_effective_price()
    {
        var best = ServerBestExecution.PickBest(OrderSide.Buy, new[]
        {
            Q("binance", 100.0m), Q("bybit", 99.9m), Q("okx", 100.2m),
        });
        Assert.Equal("bybit", best!.Value.Best.Exchange);
    }

    [Fact]
    public void Sell_picks_highest_effective_price()
    {
        var best = ServerBestExecution.PickBest(OrderSide.Sell, new[]
        {
            Q("binance", 100.0m), Q("bybit", 100.1m), Q("okx", 99.5m),
        });
        Assert.Equal("bybit", best!.Value.Best.Exchange);
    }

    [Fact]
    public void Fee_flips_the_winner_when_raw_prices_are_close()
    {
        // okx raw is lower but its fee makes it dearer to buy than binance.
        var best = ServerBestExecution.PickBest(OrderSide.Buy, new[]
        {
            Q("binance", 100.00m, fee: 0.05m),  // eff 100.05
            Q("okx",     100.00m, fee: 0.20m),  // eff 100.20
        });
        Assert.Equal("binance", best!.Value.Best.Exchange);
    }

    [Fact]
    public void Savings_measured_against_the_worst_venue()
    {
        var best = ServerBestExecution.PickBest(OrderSide.Buy, new[] { Q("a", 100m, 0m), Q("b", 110m, 0m) });
        Assert.Equal("a", best!.Value.Best.Exchange);
        Assert.Equal(10m, best.Value.SavingsPerUnit); // 110 (worst) - 100 (best)
    }

    [Fact]
    public void No_quotes_returns_null()
    {
        Assert.Null(ServerBestExecution.PickBest(OrderSide.Buy, Array.Empty<ServerBestExecution.ExchangeQuote>()));
    }
}
