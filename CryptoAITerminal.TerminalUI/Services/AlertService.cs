using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

public class AlertFiredEventArgs(PriceAlert alert, decimal triggerValue) : EventArgs
{
    public PriceAlert Alert { get; } = alert;
    public decimal TriggerValue { get; } = triggerValue;
}

public sealed class AlertService : IDisposable
{
    private readonly TelegramNotificationService _telegram;
    private readonly DiscordWebhookNotificationService _discord;
    private readonly NtfyNotificationService _ntfy;
    private readonly EmailNotificationService _email;
    private readonly List<PriceAlert> _alerts = [];
    private readonly object _alertsLock = new();
    private IDisposable? _subscription;

    // Rolling price history per symbol for change% calculations
    private readonly Dictionary<string, Queue<(DateTime Time, decimal Price)>> _priceHistory =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _historyLock = new();

    // Volume spike: 24h volume history (last 7 readings = ~1 week at daily intervals)
    private readonly Dictionary<string, Queue<decimal>> _volumeHistory =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _volumeLock = new();

    public bool SoundEnabled { get; set; } = true;

    public event EventHandler<AlertFiredEventArgs>? AlertFired;

    public IReadOnlyList<PriceAlert> Alerts
    {
        get { lock (_alertsLock) return _alerts.ToList(); }
    }

    public AlertService(
        TelegramNotificationService telegram,
        DiscordWebhookNotificationService discord,
        NtfyNotificationService ntfy,
        EmailNotificationService email)
    {
        _telegram = telegram;
        _discord  = discord;
        _ntfy     = ntfy;
        _email    = email;
    }

    public void SubscribeToStream(IObservable<MarketData> stream)
    {
        _subscription?.Dispose();
        _subscription = stream
            .Sample(TimeSpan.FromSeconds(2))
            .Subscribe(data => CheckAlerts(data));
    }

    public void AddAlert(PriceAlert alert)
    {
        lock (_alertsLock) _alerts.Add(alert);
    }

    public bool RemoveAlert(string id)
    {
        lock (_alertsLock)
        {
            var alert = _alerts.FirstOrDefault(a => a.Id == id);
            return alert is not null && _alerts.Remove(alert);
        }
    }

    public void ClearFired()
    {
        lock (_alertsLock) _alerts.RemoveAll(a => a.HasFired && !a.RepeatAfterFire);
    }

    private void TrackPrice(string symbol, decimal price)
    {
        lock (_historyLock)
        {
            if (!_priceHistory.TryGetValue(symbol, out var queue))
            {
                queue = new Queue<(DateTime, decimal)>();
                _priceHistory[symbol] = queue;
            }

            queue.Enqueue((DateTime.UtcNow, price));

            // Keep at most 25 hours of history
            var cutoff = DateTime.UtcNow.AddHours(-25);
            while (queue.Count > 0 && queue.Peek().Time < cutoff)
                queue.Dequeue();
        }
    }

    private decimal? GetChangePercent(string symbol, int periodMinutes)
    {
        (DateTime Time, decimal Price)[] history;
        lock (_historyLock)
        {
            if (!_priceHistory.TryGetValue(symbol, out var queue) || queue.Count < 2)
                return null;
            history = queue.ToArray();
        }

        var current = history[^1].Price;
        var targetTime = DateTime.UtcNow.AddMinutes(-periodMinutes);

        decimal? baseline = null;
        for (var i = history.Length - 1; i >= 0; i--)
        {
            if (history[i].Time <= targetTime)
            {
                baseline = history[i].Price;
                break;
            }
        }

        if (baseline is null or 0) return null;

        return (current - baseline.Value) / baseline.Value * 100m;
    }

    private void CheckAlerts(MarketData data)
    {
        TrackPrice(data.Symbol, data.LastPrice);

        List<PriceAlert> snapshot;
        lock (_alertsLock)
        {
            snapshot = _alerts.Where(a => a.IsActive &&
                string.Equals(a.Symbol, data.Symbol, StringComparison.OrdinalIgnoreCase) &&
                (!a.HasFired || a.RepeatAfterFire)).ToList();
        }

        foreach (var alert in snapshot)
        {
            bool triggered;

            switch (alert.Condition)
            {
                case AlertCondition.PriceAbove:
                    triggered = data.LastPrice >= alert.Threshold;
                    break;

                case AlertCondition.PriceBelow:
                    triggered = data.LastPrice <= alert.Threshold;
                    break;

                case AlertCondition.ChangePercent5mAbove:
                {
                    var change = GetChangePercent(data.Symbol, 5);
                    triggered = change.HasValue && change.Value >= alert.Threshold;
                    break;
                }

                case AlertCondition.ChangePercent1hAbove:
                {
                    var change = GetChangePercent(data.Symbol, 60);
                    triggered = change.HasValue && change.Value >= alert.Threshold;
                    break;
                }

                case AlertCondition.ChangePercent24hAbove:
                {
                    var change = GetChangePercent(data.Symbol, 1440);
                    triggered = change.HasValue && change.Value >= alert.Threshold;
                    break;
                }

                case AlertCondition.VolumeSpike:
                    // Volume spike evaluated separately via FeedVolume24h()
                    // This path is only reached from the price stream — skip here.
                    triggered = false;
                    break;

                default:
                    triggered = false;
                    break;
            }

            if (!triggered) continue;

            alert.HasFired = true;
            alert.FiredAt = DateTime.UtcNow;

            if (SoundEnabled)
#pragma warning disable CA1416
                _ = Task.Run(() =>
                {
                    try { Console.Beep(880, 200); Console.Beep(1100, 150); }
                    catch { /* not supported on all platforms */ }
                });
#pragma warning restore CA1416

            AlertFired?.Invoke(this, new AlertFiredEventArgs(alert, data.LastPrice));

            if (alert.SendTelegram || alert.SendDiscord || alert.SendNtfy || alert.SendEmail)
            {
                var msg = $"🔔 <b>Alert fired!</b>\n{alert.Symbol} — {alert.ConditionLabel}\nCurrent price: <b>{data.LastPrice:N4}</b>";
                if (alert.SendTelegram) _ = _telegram.SendAsync(msg);
                if (alert.SendDiscord)  _ = _discord.SendAsync(msg);
                if (alert.SendNtfy)     _ = _ntfy.SendAsync(msg, title: $"Alert: {alert.Symbol}");
                if (alert.SendEmail)
                {
                    var subject = $"[CryptoAI] {alert.Symbol} {alert.ConditionLabel}";
                    var body    = $"Alert fired.\n\nSymbol: {alert.Symbol}\nCondition: {alert.ConditionLabel}\nCurrent price: {data.LastPrice:N4}\nFired at (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";
                    _ = _email.SendAsync(subject, body);
                }
            }
        }
    }

    /// <summary>
    /// Feed current 24h volume for a symbol. Call this periodically from a REST ticker
    /// (e.g. Binance /api/v3/ticker/24hr). Evaluates VolumeSpike alerts immediately.
    /// A spike is defined as: current volume > average(last N samples) × threshold multiplier.
    /// </summary>
    public void FeedVolume24h(string symbol, decimal volume24h)
    {
        if (volume24h <= 0m || string.IsNullOrWhiteSpace(symbol)) return;

        Queue<decimal> history;
        lock (_volumeLock)
        {
            if (!_volumeHistory.TryGetValue(symbol, out history!))
            {
                history = new Queue<decimal>();
                _volumeHistory[symbol] = history;
            }

            history.Enqueue(volume24h);
            if (history.Count > 7) history.Dequeue(); // keep ~1 week
        }

        // Need at least 3 readings to compute a meaningful average
        if (history.Count < 3) return;

        var avg = history.Average();
        if (avg <= 0m) return;

        List<PriceAlert> snapshot;
        lock (_alertsLock)
        {
            snapshot = _alerts
                .Where(a => a.IsActive &&
                    a.Condition == AlertCondition.VolumeSpike &&
                    string.Equals(a.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                    (!a.HasFired || a.RepeatAfterFire))
                .ToList();
        }

        foreach (var alert in snapshot)
        {
            // Threshold is the multiplier, e.g. 2.0 = volume is 2× the average
            var multiplier = alert.Threshold > 0m ? alert.Threshold : 2m;
            if (volume24h < avg * multiplier) continue;

            alert.HasFired = true;
            alert.FiredAt  = DateTime.UtcNow;

            AlertFired?.Invoke(this, new AlertFiredEventArgs(alert, volume24h));

            var msg = $"📈 Volume Spike on {symbol}\n"
                    + $"Current 24h vol: {volume24h:N0}\n"
                    + $"Average: {avg:N0} (×{volume24h / avg:0.##})";

            if (alert.SendTelegram) _ = _telegram.SendAsync(msg);
            if (alert.SendDiscord)  _ = _discord.SendAsync(msg);
            if (alert.SendNtfy)     _ = _ntfy.SendAsync(symbol, msg);
            if (alert.SendEmail)    _ = _email.SendAsync($"Volume Spike: {symbol}", msg);
        }
    }

    public void Dispose() => _subscription?.Dispose();
}
