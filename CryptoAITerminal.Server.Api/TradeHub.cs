using CryptoAITerminal.Core.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace CryptoAITerminal.Server.Api;

/// <summary>Per-user trade stream. Each connection joins a group named by its uid.</summary>
public sealed class TradeHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var uid = Context.GetHttpContext()?.Items["uid"]?.ToString();
        if (!string.IsNullOrEmpty(uid))
            await Groups.AddToGroupAsync(Context.ConnectionId, uid);
        await base.OnConnectedAsync();
    }
}

/// <summary>Server-side push for order lifecycle events, scoped to one user.</summary>
public interface ITradeNotifier
{
    Task OrderStatusAsync(System.Guid uid, OrderStatusDto status);
    Task NotifyAsync(System.Guid uid, NotificationDto notification);
}

public sealed class TradeNotifier : ITradeNotifier
{
    private readonly IHubContext<TradeHub> _hub;
    public TradeNotifier(IHubContext<TradeHub> hub) => _hub = hub;
    public Task OrderStatusAsync(System.Guid uid, OrderStatusDto s) => _hub.Clients.Group(uid.ToString()).SendAsync("orderStatus", s);
    public Task NotifyAsync(System.Guid uid, NotificationDto n) => _hub.Clients.Group(uid.ToString()).SendAsync("notification", n);
}
