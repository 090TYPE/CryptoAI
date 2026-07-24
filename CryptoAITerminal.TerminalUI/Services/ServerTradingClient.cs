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
    Task<CancelResult> CancelAsync(string exchange, string symbol, string orderId);
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
    private readonly IReadOnlyList<TimeSpan> _retryBackoff;
    private HubConnection? _hub;
    private readonly Subject<OrderStatusDto> _status = new();
    private readonly Subject<NotificationDto> _notif = new();

    /// <summary>One entry per EXTRA attempt after the first, so two entries = up to three POSTs.</summary>
    private static readonly TimeSpan[] DefaultRetryBackoff =
        // Deliberately longer than a typical exchange round-trip. Retrying sooner tends to land
        // while the first attempt still holds the claim, which the server can only answer with the
        // ambiguous "still in flight" — technically safe, but a confusing result for a live order.
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    /// <param name="http">BaseAddress = server root, with the X-License default header set.</param>
    /// <param name="retryBackoff">Overridable so tests do not pay the real delays.</param>
    public ServerTradingClient(HttpClient http, string hubUrl, string license, IReadOnlyList<TimeSpan>? retryBackoff = null)
    { _http = http; _hubUrl = hubUrl; _license = license; _retryBackoff = retryBackoff ?? DefaultRetryBackoff; }

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

    /// <summary>
    /// Places one order, surviving a lost transport without ever placing twice.
    /// <para>
    /// Every attempt sends the SAME <paramref name="cmd"/>, hence the same ClientOrderId. That is
    /// precisely what makes the retry safe: the server claims (uid, ClientOrderId) atomically, so
    /// a duplicate attempt replays the first attempt's recorded outcome instead of placing a
    /// second real order. One user click = one intent = one id; the id is deliberately NOT derived
    /// from a time bucket, which would silently swallow a trader's second, genuinely-wanted order
    /// while scaling into the same symbol.
    /// </para>
    /// Only transport-level failures are retried. Once a response arrives the server has decided,
    /// so it is final whatever the status code.
    /// </summary>
    public async Task<PlaceOrderResult> PlaceMarketAsync(PlaceMarketCommand cmd)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var resp = await _http.PostAsJsonAsync("/api/trade/order", cmd);
                if (!resp.IsSuccessStatusCode)
                {
                    // A 401/429/503 has no PlaceOrderResult body: deserializing it would yield a
                    // rejection with a null reason, and a non-JSON 500 body would throw out of the
                    // command entirely. Report the status and body instead.
                    var body = await resp.Content.ReadAsStringAsync();
                    return new PlaceOrderResult(false, null, $"server returned {(int)resp.StatusCode}: {body}");
                }
                return await resp.Content.ReadFromJsonAsync<PlaceOrderResult>()
                       ?? new PlaceOrderResult(false, null, "server returned an empty response body");
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // TaskCanceledException derives from OperationCanceledException, so this also covers
                // the HttpClient timeout — the dangerous case, because the order may already be live.
                if (attempt >= _retryBackoff.Count)
                    return new PlaceOrderResult(false, null,
                        "no response from server; the order may or may not have been placed — check positions before retrying");
                await Task.Delay(_retryBackoff[attempt]);
            }
        }
    }

    public async Task<CancelResult> CancelAsync(string exchange, string symbol, string orderId)
    {
        var resp = await _http.PostAsync($"/api/trade/order/{orderId}/cancel?exchange={exchange}&symbol={symbol}", null);
        return (await resp.Content.ReadFromJsonAsync<CancelResult>())!;
    }

    public async Task<IReadOnlyList<FuturesPositionDto>> GetPositionsAsync(string exchange)
        => await _http.GetFromJsonAsync<List<FuturesPositionDto>>($"/api/trade/positions?exchange={exchange}") ?? new List<FuturesPositionDto>();

    public async ValueTask DisposeAsync() { if (_hub is not null) await _hub.DisposeAsync(); }
}
