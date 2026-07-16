using System.Globalization;
using System.Text.Json;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.CandleWorker.Collectors;

/// <summary>
/// Evaluates server-side price alerts against the latest token_snapshot and fires the ones whose
/// condition is met (status → triggered + audit). Runs frequently so alerts are near-real-time.
/// A push channel (Telegram/ntfy/mobile) can hook onto the same audit event later.
/// </summary>
public sealed class AlertCollector : IDataCollector
{
    private readonly PriceAlertsRepository _alerts;
    private readonly AuditRepository _audit;
    private readonly INotifier _notifier;
    private readonly ILogger<AlertCollector> _log;

    public AlertCollector(PriceAlertsRepository alerts, AuditRepository audit, INotifier notifier, ILogger<AlertCollector> log)
    {
        _alerts = alerts;
        _audit = audit;
        _notifier = notifier;
        _log = log;
    }

    public string Name => "alerts";
    public TimeSpan Interval => TimeSpan.FromSeconds(20);

    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var due = await _alerts.GetTriggeredAsync(ct);
        foreach (var a in due)
        {
            await _alerts.MarkTriggeredAsync(a.Id, a.Price, ct);
            await _audit.WriteAsync(a.UserId, "alerts", "alert_triggered",
                JsonSerializer.Serialize(new { a.Id, a.Chain, a.Symbol, a.Condition, a.Threshold, price = a.Price }),
                null, ct);
            // Invariant formatting — the message must not depend on the server's locale.
            await _notifier.SendAsync(a.UserId, "Price alert",
                string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} — now {3}",
                    a.Symbol ?? a.TokenAddress, a.Condition, a.Threshold, a.Price), "alert", ct);
            _log.LogInformation("alert {Id} fired: {Symbol} {Condition} {Threshold} (price {Price})",
                a.Id, a.Symbol, a.Condition, a.Threshold, a.Price);
        }
        return due.Count;
    }
}
