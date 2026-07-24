using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Server.Common;

namespace CryptoAITerminal.Executor;

/// <summary>One recorded manual order. Status: accepted | placed | rejected.</summary>
public sealed record TradeOrderRow(
    System.Guid UserId, string Exchange, string ClientOrderId, string? ExchangeOrderId,
    string Symbol, string Side, decimal Quantity, bool ReduceOnly, string Status, string? RejectReason);

/// <summary>Idempotent journal of manual orders. Writes are keyed by (UserId, ClientOrderId).</summary>
public interface IOrderJournal
{
    Task<bool> ExistsAsync(System.Guid userId, string clientOrderId, CancellationToken ct);
    Task InsertAsync(TradeOrderRow row, CancellationToken ct);
    Task MarkPlacedAsync(System.Guid userId, string clientOrderId, string exchangeOrderId, CancellationToken ct);
}

/// <summary>Server-side manual futures trading: place at market, cancel, read open positions.</summary>
public interface ITradingService
{
    Task<PlaceOrderResult> PlaceMarketAsync(System.Guid uid, PlaceMarketCommand cmd, CancellationToken ct);
    Task<CancelResult> CancelAsync(System.Guid uid, string exchange, string orderId, CancellationToken ct);
    Task<IReadOnlyList<FuturesPositionDto>> GetPositionsAsync(System.Guid uid, string exchange, CancellationToken ct);
}

/// <summary>
/// The one place a manual order is risk-gated, journaled idempotently and pushed through the
/// user's trade-only CEX key. Withdraw-scoped keys are refused; decrypted creds never outlive the call.
/// </summary>
public sealed class TradingService : ITradingService
{
    private readonly ICexKeyProvider _keys;
    private readonly IEnvelopeCipher _cipher;
    private readonly IGatewayFactory _factory;
    private readonly IPriceSource _price;
    private readonly IManualRiskGate _risk;
    private readonly IOrderJournal _journal;

    public TradingService(
        ICexKeyProvider keys, IEnvelopeCipher cipher, IGatewayFactory factory,
        IPriceSource price, IManualRiskGate risk, IOrderJournal journal)
    {
        _keys = keys; _cipher = cipher; _factory = factory; _price = price; _risk = risk; _journal = journal;
    }

    public async Task<PlaceOrderResult> PlaceMarketAsync(System.Guid uid, PlaceMarketCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.ClientOrderId))
            return new PlaceOrderResult(false, null, "ClientOrderId is required.");

        // Idempotency: a replayed ClientOrderId never places twice.
        if (await _journal.ExistsAsync(uid, cmd.ClientOrderId, ct))
            return new PlaceOrderResult(true, null, null);

        var px = await _price.GetPriceAsync(cmd.Exchange, cmd.Symbol, ct);
        var (ok, reason) = _risk.Check(cmd, px);
        if (!ok)
            return new PlaceOrderResult(false, null, reason); // gate blocks before anything is recorded or sent

        var side = cmd.Side == OrderSide.Sell ? "sell" : "buy";
        await _journal.InsertAsync(new TradeOrderRow(
            uid, cmd.Exchange, cmd.ClientOrderId, null, cmd.Symbol, side,
            cmd.Quantity, cmd.ReduceOnly, "accepted", null), ct);

        var key = await _keys.FindAsync(uid, cmd.Exchange, ct);
        if (key is null)
            return await RejectAsync(uid, cmd, $"no trade key for {cmd.Exchange}", ct);

        var perms = key.Permissions ?? "";
        if (perms.Contains("withdraw", System.StringComparison.OrdinalIgnoreCase))
            return await RejectAsync(uid, cmd, "key has withdraw permission — refused (trade-only required)", ct);
        if (!perms.Contains("trade", System.StringComparison.OrdinalIgnoreCase))
            return await RejectAsync(uid, cmd, "key lacks trade permission", ct);

        var creds = await _cipher.DecryptAsync(key.Ciphertext, key.WrappedDek, ct);
        try
        {
            var gateway = _factory.Create(cmd.Exchange, "futures", creds);
            var order = new Order
            {
                Symbol = cmd.Symbol,
                Side = cmd.Side,
                Type = OrderType.Market,
                Quantity = cmd.Quantity,
                ReduceOnly = cmd.ReduceOnly,
                Leverage = cmd.Leverage,
                MarginMode = cmd.MarginMode,
                PositionSide = cmd.PositionSide,
                MarketType = TradingMarketType.FuturesUsdM,
                ClientOrderId = cmd.ClientOrderId,
            };
            var placed = await gateway.PlaceOrderAsync(order);
            await _journal.MarkPlacedAsync(uid, cmd.ClientOrderId, placed.Id ?? "", ct);
            return new PlaceOrderResult(true, placed.Id, null);
        }
        catch (System.Exception ex)
        {
            return await RejectAsync(uid, cmd, ex.Message, ct);
        }
        finally
        {
            creds = null!; // drop the decrypted secret from our local reference
        }
    }

    /// <summary>Overwrite the accepted row (same (uid, ClientOrderId) key) with a rejected one.</summary>
    private async Task<PlaceOrderResult> RejectAsync(System.Guid uid, PlaceMarketCommand cmd, string reason, CancellationToken ct)
    {
        var side = cmd.Side == OrderSide.Sell ? "sell" : "buy";
        await _journal.InsertAsync(new TradeOrderRow(
            uid, cmd.Exchange, cmd.ClientOrderId, null, cmd.Symbol, side,
            cmd.Quantity, cmd.ReduceOnly, "rejected", reason), ct);
        return new PlaceOrderResult(false, null, reason);
    }

    public async Task<CancelResult> CancelAsync(System.Guid uid, string exchange, string orderId, CancellationToken ct)
    {
        var key = await _keys.FindAsync(uid, exchange, ct);
        if (key is null) return new CancelResult(false, $"no trade key for {exchange}");

        var creds = await _cipher.DecryptAsync(key.Ciphertext, key.WrappedDek, ct);
        try
        {
            await _factory.Create(exchange, "futures", creds).CancelOrderAsync(orderId);
            return new CancelResult(true, null);
        }
        catch (System.Exception ex)
        {
            return new CancelResult(false, ex.Message);
        }
        finally
        {
            creds = null!;
        }
    }

    public async Task<IReadOnlyList<FuturesPositionDto>> GetPositionsAsync(System.Guid uid, string exchange, CancellationToken ct)
    {
        var key = await _keys.FindAsync(uid, exchange, ct);
        if (key is null) return [];

        var creds = await _cipher.DecryptAsync(key.Ciphertext, key.WrappedDek, ct);
        try
        {
            var positions = await _factory.Create(exchange, "futures", creds).GetOpenPositionsAsync();
            return positions.Select(p => new FuturesPositionDto(
                p.Symbol, p.Quantity, p.EntryPrice, p.MarkPrice,
                p.UnrealizedPnl, p.LiquidationPrice, p.Leverage)).ToList();
        }
        finally
        {
            creds = null!;
        }
    }
}
