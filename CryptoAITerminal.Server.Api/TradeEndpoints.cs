using CryptoAITerminal.Core.Contracts;
using CryptoAITerminal.Executor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CryptoAITerminal.Server.Api;

/// <summary>Manual futures trading over REST. Identity comes from the licence-auth middleware
/// (<c>ctx.Items["uid"]</c>); the endpoints never take a user id from the caller.</summary>
public static class TradeEndpoints
{
    public static void MapTradeEndpoints(this IEndpointRouteBuilder app)
    {
        static System.Guid Uid(HttpContext ctx) => (System.Guid)ctx.Items["uid"]!;

        app.MapPost("/api/trade/order", async (HttpContext ctx, PlaceMarketCommand cmd, ITradingService svc, ITradeNotifier notif) =>
        {
            var uid = Uid(ctx);
            var result = await svc.PlaceMarketAsync(uid, cmd, ctx.RequestAborted);
            if (result.Accepted && result.OrderId is not null)
                await notif.OrderStatusAsync(uid, new OrderStatusDto(result.OrderId, cmd.ClientOrderId, "placed", cmd.Quantity, 0m, System.DateTime.UtcNow));
            else if (!result.Accepted)
                await notif.NotifyAsync(uid, new NotificationDto("order", "error", result.RejectReason ?? "rejected", System.DateTime.UtcNow));
            return Results.Ok(result);
        });

        app.MapPost("/api/trade/order/{orderId}/cancel", async (HttpContext ctx, string orderId, string exchange, ITradingService svc) =>
            Results.Ok(await svc.CancelAsync(Uid(ctx), exchange, orderId, ctx.RequestAborted)));

        app.MapGet("/api/trade/positions", async (HttpContext ctx, string exchange, ITradingService svc) =>
            Results.Ok(await svc.GetPositionsAsync(Uid(ctx), exchange, ctx.RequestAborted)));
    }
}
