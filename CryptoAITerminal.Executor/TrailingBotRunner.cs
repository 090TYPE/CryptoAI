using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Executor;

/// <summary>A server-managed position with its live TP/SL/trailing state (persisted, restart-safe).
/// <paramref name="NativeSl"/> (futures only) keeps one exchange-native reduce-only stop that trails;
/// <paramref name="SlOrderId"/> is that resting order's id.</summary>
public sealed record TrailingPosition(
    Guid BotId, string Symbol, bool IsLong, decimal Entry, bool Futures,
    ServerTrailingStop.TslState State, bool Closed, bool NativeSl = false, string? SlOrderId = null);

/// <summary>Persistence for a trailing bot's single managed position.</summary>
public interface ITslPositionStore
{
    Task<TrailingPosition?> LoadAsync(Guid botId, CancellationToken ct);
    Task SaveAsync(TrailingPosition pos, CancellationToken ct);
}

/// <summary>
/// Stateful trailing runner: each tick loads the managed position, asks <see cref="ServerTrailingStop"/>
/// what the current price means, then fires a market close (full or partial) or just persists the
/// ratcheted stop. Software-simulated trailing (safe default) — the close fires when price crosses the
/// stop, so no exchange-native SL order churn. State is saved every tick so a restart resumes cleanly.
/// </summary>
public sealed class TrailingBotRunner
{
    private readonly IExchangeGateway _gateway;
    private readonly ITslPositionStore _store;

    public TrailingBotRunner(IExchangeGateway gateway, ITslPositionStore store)
    {
        _gateway = gateway; _store = store;
    }

    public async Task PollAsync(Guid botId, TpSlConfig cfg, decimal price, CancellationToken ct)
    {
        var pos = await _store.LoadAsync(botId, ct);
        if (pos is null || pos.Closed) return;

        if (pos.Futures && pos.NativeSl)
        {
            await NativePollAsync(pos, cfg, price, ct);
            return;
        }

        var d = ServerTrailingStop.Evaluate(cfg, pos.State, pos.IsLong, pos.Entry, price);

        switch (d.Action)
        {
            case ServerTrailingStop.TslAction.CloseAll:
                await CloseAsync(pos, d.Qty, ct);
                await _store.SaveAsync(pos with { State = d.Next, Closed = true }, ct);
                break;

            case ServerTrailingStop.TslAction.PartialClose:
                await CloseAsync(pos, d.Qty, ct);
                await _store.SaveAsync(pos with { State = d.Next }, ct);
                break;

            // MoveSl / None: no order — just carry the (possibly ratcheted) state forward.
            default:
                await _store.SaveAsync(pos with { State = d.Next }, ct);
                break;
        }
    }

    /// <summary>
    /// Futures native-SL path: keep one resting reduce-only stop that trails (cancel + replace on a
    /// move). The exchange fills the stop itself, so a price cross through SL is NOT market-closed here
    /// — only TP market-closes (and pulls the resting stop). Partial TP resizes the stop to the rest.
    /// </summary>
    private async Task NativePollAsync(TrailingPosition pos, TpSlConfig cfg, decimal price, CancellationToken ct)
    {
        var slId = pos.SlOrderId;

        // Ensure a resting stop exists (first tick after attach).
        if (cfg.SlEnabled && slId is null)
        {
            slId = await PlaceSlAsync(pos, pos.State.Sl, pos.State.RemainingQty, ct);
            pos = pos with { SlOrderId = slId };
        }

        var d = ServerTrailingStop.Evaluate(cfg, pos.State, pos.IsLong, pos.Entry, price);

        switch (d.Action)
        {
            case ServerTrailingStop.TslAction.MoveSl:
                if (slId is not null) await _gateway.CancelOrderAsync(pos.Symbol, slId);
                var moved = await PlaceSlAsync(pos, d.NewSlPrice, pos.State.RemainingQty, ct);
                await _store.SaveAsync(pos with { State = d.Next, SlOrderId = moved }, ct);
                break;

            case ServerTrailingStop.TslAction.PartialClose:
                await CloseAsync(pos, d.Qty, ct);
                // resize the resting stop down to the remaining quantity
                if (slId is not null) await _gateway.CancelOrderAsync(pos.Symbol, slId);
                var resized = await PlaceSlAsync(pos, d.Next.Sl, d.Next.RemainingQty, ct);
                await _store.SaveAsync(pos with { State = d.Next, SlOrderId = resized }, ct);
                break;

            case ServerTrailingStop.TslAction.CloseAll:
                // TP → market close + pull the resting stop. SL → the exchange already filled the stop.
                if (!string.Equals(d.Reason, "SL", StringComparison.OrdinalIgnoreCase))
                {
                    await CloseAsync(pos, d.Qty, ct);
                    if (slId is not null) await _gateway.CancelOrderAsync(pos.Symbol, slId);
                }
                await _store.SaveAsync(pos with { State = d.Next, Closed = true, SlOrderId = null }, ct);
                break;

            default: // MoveSl not triggered / None — carry state (and any newly-placed stop id) forward
                await _store.SaveAsync(pos with { State = d.Next, SlOrderId = slId }, ct);
                break;
        }
    }

    private async Task<string> PlaceSlAsync(TrailingPosition pos, decimal trigger, decimal qty, CancellationToken ct)
    {
        var closeSide = pos.IsLong ? OrderSide.Sell : OrderSide.Buy;
        var posSide = pos.IsLong ? FuturesPositionSide.Long : FuturesPositionSide.Short;
        var o = await _gateway.PlaceStopLossOrderAsync(pos.Symbol, closeSide, qty, trigger, posSide, reduceOnly: true);
        return o.Id;
    }

    private Task CloseAsync(TrailingPosition pos, decimal qty, CancellationToken ct)
        => _gateway.PlaceOrderAsync(new Order
        {
            Symbol = pos.Symbol,
            Side = pos.IsLong ? OrderSide.Sell : OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = qty,
            MarketType = pos.Futures ? TradingMarketType.FuturesUsdM : TradingMarketType.Spot,
            ReduceOnly = pos.Futures,
        });
}
