using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using CryptoAITerminal.Core.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Thin client for the server trade API. The desktop sends intents and renders what
/// the server streams back; it does not touch the exchange gateway on this path.</summary>
public interface IServerTradingClient
{
    Task<PlaceOrderResult> PlaceMarketAsync(PlaceMarketCommand cmd);
    Task<CancelResult> CancelAsync(string exchange, string orderId);
    Task<IReadOnlyList<FuturesPositionDto>> GetPositionsAsync(string exchange);
    IObservable<OrderStatusDto> OrderStatus { get; }
    IObservable<NotificationDto> Notifications { get; }
    Task ConnectAsync();
}

public sealed class ServerTradingClient : IServerTradingClient, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _hubUrl;
    private readonly string _license;
    private HubConnection? _hub;
    private readonly Subject<OrderStatusDto> _status = new();
    private readonly Subject<NotificationDto> _notif = new();

    /// <param name="http">BaseAddress = server root, with the X-License default header set.</param>
    public ServerTradingClient(HttpClient http, string hubUrl, string license)
    { _http = http; _hubUrl = hubUrl; _license = license; }

    public IObservable<OrderStatusDto> OrderStatus => _status;
    public IObservable<NotificationDto> Notifications => _notif;

    public async Task ConnectAsync()
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(_hubUrl, o => o.Headers["X-License"] = _license)
            .WithAutomaticReconnect()
            .Build();
        _hub.On<OrderStatusDto>("orderStatus", s => _status.OnNext(s));
        _hub.On<NotificationDto>("notification", n => _notif.OnNext(n));
        await _hub.StartAsync();
    }

    public async Task<PlaceOrderResult> PlaceMarketAsync(PlaceMarketCommand cmd)
    {
        var resp = await _http.PostAsJsonAsync("/api/trade/order", cmd);
        return (await resp.Content.ReadFromJsonAsync<PlaceOrderResult>())!;
    }

    public async Task<CancelResult> CancelAsync(string exchange, string orderId)
    {
        var resp = await _http.PostAsync($"/api/trade/order/{orderId}/cancel?exchange={exchange}", null);
        return (await resp.Content.ReadFromJsonAsync<CancelResult>())!;
    }

    public async Task<IReadOnlyList<FuturesPositionDto>> GetPositionsAsync(string exchange)
        => await _http.GetFromJsonAsync<List<FuturesPositionDto>>($"/api/trade/positions?exchange={exchange}") ?? new List<FuturesPositionDto>();

    public async ValueTask DisposeAsync() { if (_hub is not null) await _hub.DisposeAsync(); }
}
