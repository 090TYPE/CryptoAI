using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Server.Common;

namespace CryptoAITerminal.Executor;

/// <summary>Server-side manual futures trading: place at market, cancel, read open positions.</summary>
public interface ITradingService
{
    Task<PlaceOrderResult> PlaceMarketAsync(System.Guid uid, PlaceMarketCommand cmd, CancellationToken ct);
    Task<CancelResult> CancelAsync(System.Guid uid, string exchange, string symbol, string orderId, CancellationToken ct);
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

        var px = await _price.GetPriceAsync(cmd.Exchange, cmd.Symbol, ct);
        var (ok, reason) = _risk.Check(cmd, px);
        if (!ok)
            return new PlaceOrderResult(false, null, reason); // gate blocks before anything is recorded or sent

        // Idempotency: the claim is atomic, so exactly one caller may ever place this
        // (uid, ClientOrderId). Losing the claim means the order already exists — report its
        // recorded outcome and place nothing.
        var side = cmd.Side == OrderSide.Sell ? "sell" : "buy";
        var claimed = await _journal.TryClaimAsync(new TradeOrderRow(
            uid, cmd.Exchange, cmd.ClientOrderId, null, cmd.Symbol, side,
            cmd.Quantity, cmd.ReduceOnly, "accepted", null), ct);
        if (!claimed)
            return await ReplayAsync(uid, cmd.ClientOrderId, ct);

        var key = await _keys.FindAsync(uid, cmd.Exchange, ct);
        if (key is null)
            return await RejectAsync(uid, cmd, $"no trade key for {cmd.Exchange}", ct);

        var perms = key.Permissions ?? "";
        if (perms.Contains("withdraw", System.StringComparison.OrdinalIgnoreCase))
            return await RejectAsync(uid, cmd, "key has withdraw permission — refused (trade-only required)", ct);
        if (!perms.Contains("trade", System.StringComparison.OrdinalIgnoreCase))
            return await RejectAsync(uid, cmd, "key lacks trade permission", ct);

        var creds = await _cipher.DecryptAsync(key.Ciphertext, key.WrappedDek, ct);
        Order placed;
        try
        {
            var gateway = _factory.Create(cmd.Exchange, "futures", creds);

            // Leverage and margin mode must be pushed explicitly: only Binance and KuCoin re-apply
            // them from the order object, so on OKX/Bybit a 3× request would otherwise inherit
            // whatever the account was last left on — with a matching wrong liquidation price.
            // Best-effort by design, mirroring the desktop's EnsureManualFuturesSetupAsync: venues
            // routinely reject re-setting an unchanged value ("leverage not modified") and gateways
            // without futures support throw NotSupportedException. Neither is a reason to refuse a
            // legitimate order, so each call is swallowed individually.
            try { await gateway.SetLeverageAsync(cmd.Symbol, cmd.Leverage); } catch { /* venue may reject a no-op change; not fatal */ }
            try { await gateway.SetMarginModeAsync(cmd.Symbol, cmd.MarginMode); } catch { /* ditto */ }

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
            placed = await gateway.PlaceOrderAsync(order);
        }
        catch (System.Exception ex)
        {
            // Nothing reached the exchange (or the attempt failed outright) — record the rejection.
            // CancellationToken.None: a caller who walked away must still leave a truthful row.
            return await RejectAsync(uid, cmd, ex.Message, System.Threading.CancellationToken.None);
        }
        finally
        {
            creds = null!; // drop the decrypted secret from our local reference
        }

        // Past this line the exchange HAS the order, so the outcome is terminal and the caller
        // must be told "accepted" no matter what happens next. The journal write is deliberately
        // outside the try above, uses CancellationToken.None (the caller's token may already be
        // cancelled), and its failure is swallowed: a DB blip must never be reported as a
        // rejection, or the user re-clicks and doubles a real position.
        try
        {
            await _journal.MarkPlacedAsync(uid, cmd.ClientOrderId, placed.Id ?? "", System.Threading.CancellationToken.None);
        }
        catch
        {
            // Row stays 'accepted'; the order is live and the caller is told so.
        }
        return new PlaceOrderResult(true, placed.Id, null);
    }

    /// <summary>A ClientOrderId whose claim was already taken: report what actually happened
    /// instead of a blanket success, and place nothing.</summary>
    private async Task<PlaceOrderResult> ReplayAsync(System.Guid uid, string clientOrderId, CancellationToken ct)
    {
        var row = await _journal.GetAsync(uid, clientOrderId, ct);
        return row?.Status switch
        {
            "placed" => new PlaceOrderResult(true, row.ExchangeOrderId, null),
            "rejected" => new PlaceOrderResult(false, null, row.RejectReason ?? "previously rejected"),
            // Still 'accepted': the first attempt holds the claim and has not finished. This is the
            // ambiguous case — the order may already be live on the exchange — so the message must
            // say so rather than read as a plain rejection, or the user re-clicks and doubles up.
            _ => new PlaceOrderResult(false, null,
                "an identical request is still in flight — the order may already be live; check positions before retrying"),
        };
    }

    /// <summary>Mark the claimed row rejected. Never downgrades a row already marked 'placed'.</summary>
    private async Task<PlaceOrderResult> RejectAsync(System.Guid uid, PlaceMarketCommand cmd, string reason, CancellationToken ct)
    {
        await _journal.MarkRejectedAsync(uid, cmd.ClientOrderId, reason, ct);
        return new PlaceOrderResult(false, null, reason);
    }

    public async Task<CancelResult> CancelAsync(System.Guid uid, string exchange, string symbol, string orderId, CancellationToken ct)
    {
        var key = await _keys.FindAsync(uid, exchange, ct);
        if (key is null) return new CancelResult(false, $"no trade key for {exchange}");

        var creds = await _cipher.DecryptAsync(key.Ciphertext, key.WrappedDek, ct);
        try
        {
            // Two-arg overload: every futures gateway resolves the symbol for the 1-arg form from a
            // per-instance cache that is always empty here (a fresh gateway per request), so the
            // 1-arg call silently cancels nothing while we report success.
            await _factory.Create(exchange, "futures", creds).CancelOrderAsync(symbol, orderId);
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
