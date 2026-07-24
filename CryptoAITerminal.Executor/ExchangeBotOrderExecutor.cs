using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Server.Common;

namespace CryptoAITerminal.Executor;

/// <summary>Encrypted trade key material + its API permissions, fetched on the executor node only.</summary>
public sealed record CexKeyMaterial(byte[] Ciphertext, byte[] WrappedDek, string? Permissions);

/// <summary>Finds a user's CEX trade key for an exchange (executor-only, returns ciphertext).</summary>
public interface ICexKeyProvider
{
    Task<CexKeyMaterial?> FindAsync(Guid userId, string exchange, CancellationToken ct);
}

/// <summary>Current price for sizing paper fills / converting notional to quantity.</summary>
public interface IPriceSource
{
    Task<decimal> GetPriceAsync(string exchange, string symbol, CancellationToken ct);
}

/// <summary>Builds the right <see cref="IExchangeGateway"/> for an exchange/market from decrypted creds.</summary>
public interface IGatewayFactory
{
    IExchangeGateway Create(string exchange, string market, string credentialsJson);
}

/// <summary>
/// Real bot order executor: paper simulates by price; live decrypts the user's trade-only key and
/// places a market order through the gateway. Refuses keys that carry withdraw permission.
/// </summary>
public sealed class ExchangeBotOrderExecutor : IBotOrderExecutor
{
    private readonly ICexKeyProvider _keys;
    private readonly IEnvelopeCipher _cipher;
    private readonly IGatewayFactory _factory;
    private readonly IPriceSource _price;

    public ExchangeBotOrderExecutor(ICexKeyProvider keys, IEnvelopeCipher cipher, IGatewayFactory factory, IPriceSource price)
    {
        _keys = keys; _cipher = cipher; _factory = factory; _price = price;
    }

    public async Task<BotFill> PlaceAsync(BotIntent i, CancellationToken ct)
    {
        var symbol = i.Asset + i.Quote;
        var price = await _price.GetPriceAsync(i.Exchange, symbol, ct);
        var qty = i.Quantity ?? (price > 0 ? i.NotionalUsd.GetValueOrDefault() / price : 0m);

        // Paper: simulate at the current price, never touch private endpoints.
        if (!string.Equals(i.Mode, "live", StringComparison.OrdinalIgnoreCase))
            return new BotFill("paper", "paper-" + Guid.NewGuid().ToString("N")[..12], price, qty, null);

        // Live: require a trade-only key. Withdraw-scoped keys are refused (§7.1 variant B).
        var key = await _keys.FindAsync(i.UserId, i.Exchange, ct);
        if (key is null)
            return new BotFill("failed", null, null, null, $"no trade key for {i.Exchange}");

        var perms = key.Permissions ?? "";
        if (perms.Contains("withdraw", StringComparison.OrdinalIgnoreCase))
            return new BotFill("failed", null, null, null, "key has withdraw permission — refused (trade-only required)");
        if (!perms.Contains("trade", StringComparison.OrdinalIgnoreCase))
            return new BotFill("failed", null, null, null, "key lacks trade permission");

        var creds = await _cipher.DecryptAsync(key.Ciphertext, key.WrappedDek, ct);
        try
        {
            var gateway = _factory.Create(i.Exchange, i.Market, creds);
            var order = new Order
            {
                Symbol = symbol,
                Side = string.Equals(i.Side, "sell", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = qty,
                ClientOrderId = i.ClientOrderId ?? "",
                MarketType = string.Equals(i.Market, "futures", StringComparison.OrdinalIgnoreCase)
                    ? TradingMarketType.FuturesUsdM : TradingMarketType.Spot,
            };
            var placed = await gateway.PlaceOrderAsync(order);
            return new BotFill("placed", placed.Id, placed.Price, placed.FilledQuantity, null);
        }
        catch (Exception ex)
        {
            return new BotFill("failed", null, null, null, ex.Message);
        }
        finally
        {
            creds = null!; // drop the decrypted secret from our local reference
        }
    }
}
