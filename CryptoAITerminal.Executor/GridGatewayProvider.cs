using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Server.Common;

namespace CryptoAITerminal.Executor;

/// <summary>Resolves the <see cref="IExchangeGateway"/> a grid bot should trade through this tick.</summary>
public interface IGridGatewayProvider
{
    /// <param name="open">Currently-tracked open orders (used to seed the paper simulator).</param>
    Task<IExchangeGateway> CreateAsync(
        Guid userId, string exchange, string market, string mode, decimal currentPrice,
        IReadOnlyList<TrackedGridOrder> open, CancellationToken ct);
}

/// <summary>
/// Paper mode → a <see cref="PaperExchangeGateway"/> seeded from the tracked orders + current price
/// (no venue, no money). Live mode → the user's decrypted trade-only key wired into a keyed gateway,
/// with the same withdraw-key refusal as <see cref="ExchangeBotOrderExecutor"/> (§7.1 variant B).
/// </summary>
public sealed class GridGatewayProvider : IGridGatewayProvider
{
    private readonly ICexKeyProvider _keys;
    private readonly IEnvelopeCipher _cipher;
    private readonly IGatewayFactory _factory;

    public GridGatewayProvider(ICexKeyProvider keys, IEnvelopeCipher cipher, IGatewayFactory factory)
    {
        _keys = keys; _cipher = cipher; _factory = factory;
    }

    public async Task<IExchangeGateway> CreateAsync(
        Guid userId, string exchange, string market, string mode, decimal currentPrice,
        IReadOnlyList<TrackedGridOrder> open, CancellationToken ct)
    {
        if (!string.Equals(mode, "live", StringComparison.OrdinalIgnoreCase))
        {
            var resting = open.Select(o => new Order
            {
                Id = o.ExtRef ?? o.Id.ToString(),
                Side = string.Equals(o.Side, "sell", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy,
                Type = OrderType.Limit,
                Price = o.Price,
                Quantity = o.Qty,
            });
            return new PaperExchangeGateway(currentPrice, resting);
        }

        var key = await _keys.FindAsync(userId, exchange, ct)
            ?? throw new InvalidOperationException($"no trade key for {exchange}");
        var perms = key.Permissions ?? "";
        if (perms.Contains("withdraw", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("key has withdraw permission — refused (trade-only required)");
        if (!perms.Contains("trade", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("key lacks trade permission");

        var creds = await _cipher.DecryptAsync(key.Ciphertext, key.WrappedDek, ct);
        return _factory.Create(exchange, market, creds);
    }
}
