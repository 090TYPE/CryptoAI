using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Executor;
using CryptoAITerminal.Server.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoAITerminal.Server.Api;

/// <summary>Manual futures trading over REST. Identity comes from the licence-auth middleware
/// (<c>ctx.Items["uid"]</c>); the endpoints never take a user id from the caller.</summary>
public static class TradeEndpoints
{
    public static void MapTradeEndpoints(this IEndpointRouteBuilder app)
    {
        static System.Guid Uid(HttpContext ctx) => (System.Guid)ctx.Items["uid"]!;

        // TradingService needs IEnvelopeCipher to decrypt the user's trade key, and the cipher
        // is only registered when CRYPTOAI_KEK_B64 is set. Resolve the service from the request
        // scope AFTER checking the cipher, so an unconfigured deployment answers 503 like the
        // other custodial endpoints instead of throwing a DI failure as a 500.
        static ITradingService? Trading(HttpContext ctx) =>
            ctx.RequestServices.GetService<IEnvelopeCipher>() is null
                ? null
                : ctx.RequestServices.GetRequiredService<ITradingService>();

        static IResult NotConfigured() =>
            Results.Json(new { error = "encryption_not_configured" }, statusCode: 503);

        app.MapPost("/api/trade/order", async (HttpContext ctx, PlaceMarketCommand cmd, ITradeNotifier notif) =>
        {
            var svc = Trading(ctx);
            if (svc is null) return NotConfigured();

            var uid = Uid(ctx);
            var result = await svc.PlaceMarketAsync(uid, cmd, ctx.RequestAborted);
            if (result.Accepted && result.OrderId is not null)
                await notif.OrderStatusAsync(uid, new OrderStatusDto(result.OrderId, cmd.ClientOrderId, "placed", cmd.Quantity, 0m, System.DateTime.UtcNow));
            else if (!result.Accepted)
                await notif.NotifyAsync(uid, new NotificationDto("order", "error", result.RejectReason ?? "rejected", System.DateTime.UtcNow));
            return Results.Ok(result);
        });

        // symbol is a required query parameter: futures cancels need it, and without it the
        // gateway call is a silent no-op that still reports success.
        app.MapPost("/api/trade/order/{orderId}/cancel", async (HttpContext ctx, string orderId, string exchange, string symbol) =>
        {
            var svc = Trading(ctx);
            if (svc is null) return NotConfigured();
            return Results.Ok(await svc.CancelAsync(Uid(ctx), exchange, symbol, orderId, ctx.RequestAborted));
        });

        app.MapGet("/api/trade/positions", async (HttpContext ctx, string exchange) =>
        {
            var svc = Trading(ctx);
            if (svc is null) return NotConfigured();
            return Results.Ok(await svc.GetPositionsAsync(Uid(ctx), exchange, ctx.RequestAborted));
        });
    }
}
