using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Executor;

/// <summary>A server-managed position with its live TP/SL/trailing state (persisted, restart-safe).</summary>
public sealed record TrailingPosition(
    Guid BotId, string Symbol, bool IsLong, decimal Entry, bool Futures,
    ServerTrailingStop.TslState State, bool Closed);

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
