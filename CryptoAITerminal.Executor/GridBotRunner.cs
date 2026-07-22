using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Executor;

/// <summary>Grid bot settings for a run.</summary>
public readonly record struct GridConfig(
    Guid BotId, Guid UserId, string Symbol,
    decimal Lower, decimal Upper, int GridLevels, decimal QtyPerGrid, bool Futures);

/// <summary>A grid limit order we're tracking, persisted so the bot is restart-safe.</summary>
public sealed record TrackedGridOrder(
    Guid Id, Guid BotId, int Level, string Side, decimal Price, decimal Qty, string? ExtRef, string Status);

/// <summary>Persistence for active grid orders (backed by the grid_orders table in production).</summary>
public interface IGridOrderStore
{
    Task AddOpenAsync(Guid botId, Guid userId, int level, string side, decimal price, decimal qty, string? extRef, CancellationToken ct);
    Task<IReadOnlyList<TrackedGridOrder>> GetOpenAsync(Guid botId, CancellationToken ct);
    Task MarkFilledAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// Stateful grid runner: places the initial ladder on start, and on each poll detects fills and
/// places the opposite order. Pure decisions come from <see cref="ServerGridStrategy"/>; this class
/// only wires them to the gateway and the order store.
/// </summary>
public sealed class GridBotRunner
{
    private readonly IExchangeGateway _gateway;
    private readonly IGridOrderStore _store;

    public GridBotRunner(IExchangeGateway gateway, IGridOrderStore store)
    {
        _gateway = gateway; _store = store;
    }

    public async Task StartAsync(GridConfig cfg, decimal currentPrice, CancellationToken ct)
    {
        var levels = ServerGridStrategy.GenerateLevels(cfg.Lower, cfg.Upper, cfg.GridLevels);
        foreach (var order in ServerGridStrategy.InitialOrders(levels, currentPrice, cfg.Futures))
        {
            var level = Array.IndexOf(levels, order.Price);
            await PlaceAndTrackAsync(cfg, level, order.Side!, order.Price, ct);
        }
    }

    public async Task PollAsync(GridConfig cfg, CancellationToken ct)
    {
        var levels = ServerGridStrategy.GenerateLevels(cfg.Lower, cfg.Upper, cfg.GridLevels);
        var open = await _store.GetOpenAsync(cfg.BotId, ct);
        var live = (await _gateway.GetOpenOrdersAsync(cfg.Symbol)).Select(o => o.Id).ToHashSet();

        foreach (var tracked in open)
        {
            // Still resting on the exchange → nothing to do.
            if (tracked.ExtRef is not null && live.Contains(tracked.ExtRef)) continue;

            await _store.MarkFilledAsync(tracked.Id, ct);
            var next = ServerGridStrategy.OnFill(levels, tracked.Side, tracked.Level);
            if (next.Order.Side is null) continue; // grid edge — no opposite

            var level = Array.IndexOf(levels, next.Order.Price);
            await PlaceAndTrackAsync(cfg, level, next.Order.Side, next.Order.Price, ct);
        }
    }

    private async Task PlaceAndTrackAsync(GridConfig cfg, int level, string side, decimal price, CancellationToken ct)
    {
        var order = new Order
        {
            Symbol = cfg.Symbol,
            Side = string.Equals(side, "sell", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy,
            Type = OrderType.Limit,
            Price = price,
            Quantity = cfg.QtyPerGrid,
            MarketType = cfg.Futures ? TradingMarketType.FuturesUsdM : TradingMarketType.Spot,
        };
        var placed = await _gateway.PlaceOrderAsync(order);
        await _store.AddOpenAsync(cfg.BotId, cfg.UserId, level, side, price, cfg.QtyPerGrid, placed.Id, ct);
    }
}
