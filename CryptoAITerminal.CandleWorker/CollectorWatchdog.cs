using System.Text.Json;
using CryptoAITerminal.Server.Common;
using CryptoAITerminal.Server.Data;

namespace CryptoAITerminal.CandleWorker;

/// <summary>
/// Watches the collectors and tells someone when they stop.
///
/// The server has always recorded which collector last ran, whether it succeeded and what it
/// failed with, and exposed all of it on /api/admin/collectors — but nothing ever read that
/// endpoint. In practice a broken collector was discovered by a customer noticing stale data, or
/// not at all: the retired-model outage that silenced all 31 digests ran for months this way.
///
/// Deliberately edge-triggered. A watchdog that repeats itself every ten minutes gets muted, and a
/// muted watchdog is worse than none because it is also believed to be working. This sends once
/// when something breaks, once when it recovers, and one reminder a day while it stays broken.
/// </summary>
public sealed class CollectorWatchdog : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Reminder = TimeSpan.FromHours(24);

    private readonly ApiReadRepository _read;
    private readonly SettingsStore _settings;
    private readonly IConfiguration _cfg;
    private readonly ILogger<CollectorWatchdog> _log;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private string _lastSignature = "";
    private DateTime _lastSentUtc = DateTime.MinValue;

    public CollectorWatchdog(ApiReadRepository read, SettingsStore settings, IConfiguration cfg,
        ILogger<CollectorWatchdog> log)
    {
        _read = read; _settings = settings; _cfg = cfg; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // The collectors have not run yet at process start; alerting on that would fire on every
        // deploy and train everyone to ignore this channel.
        try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await CheckAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "collector watchdog: check failed"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        if (!await _settings.GetBoolAsync(SettingKeys.AlertsEnabled, true, ct)) return;

        var staleAfter = await _settings.GetIntAsync(SettingKeys.AlertsStaleMinutes,
            SettingsStore.AsInt(_cfg["COLLECTOR_STALE_MINUTES"], 120), ct);

        var rows = await _read.GetCollectorHealthAsync(ct);
        if (rows.Count == 0) return;   // nothing has reported yet

        var failing = rows.Where(r => r.LastOk == false).Select(r => r.Name).OrderBy(n => n).ToList();
        var stale = rows.Where(r => r.MinutesAgo is null || r.MinutesAgo > staleAfter)
                        .Select(r => r.Name).OrderBy(n => n).ToList();

        // The signature is what makes this edge-triggered: the same set of broken collectors is the
        // same event, however many times it is observed.
        var signature = failing.Count == 0 && stale.Count == 0
            ? "ok"
            : $"fail:{string.Join(',', failing)}|stale:{string.Join(',', stale)}";

        var changed = signature != _lastSignature;
        var overdue = signature != "ok" && DateTime.UtcNow - _lastSentUtc > Reminder;
        if (!changed && !overdue) return;

        var text = signature == "ok"
            ? "✅ *Сборщики восстановились*\nВсе задачи снова отвечают."
            : BuildAlert(rows, failing, stale, staleAfter);

        if (await SendAsync(text, ct))
        {
            _lastSignature = signature;
            _lastSentUtc = DateTime.UtcNow;
        }
        // Not recording the signature on a send failure is intentional: an alert nobody received
        // has not happened, and the next tick must try again rather than consider it delivered.
    }

    private static string BuildAlert(IReadOnlyList<CollectorHealth> rows,
        List<string> failing, List<string> stale, int staleAfter)
    {
        var sb = new System.Text.StringBuilder("⚠ *Проблема со сборщиками*\n");

        if (failing.Count > 0)
        {
            sb.Append($"\n*Падают* ({failing.Count}):\n");
            foreach (var name in failing.Take(10))
            {
                var err = rows.FirstOrDefault(r => r.Name == name)?.LastError;
                sb.Append($"• `{name}`");
                // The error text is the whole reason this message is worth reading — it is what
                // turns "something is wrong" into a fix.
                if (!string.IsNullOrWhiteSpace(err))
                    sb.Append(" — ").Append(err.Length > 120 ? err[..120] + "…" : err);
                sb.Append('\n');
            }
        }

        if (stale.Count > 0)
        {
            sb.Append($"\n*Молчат дольше {staleAfter} мин* ({stale.Count}):\n");
            foreach (var name in stale.Take(10)) sb.Append($"• `{name}`\n");
        }

        if (failing.Count > 10 || stale.Count > 10)
            sb.Append("\n_Показаны первые 10 в каждой группе._");

        return sb.ToString();
    }

    /// <summary>True when Telegram accepted the message.</summary>
    private async Task<bool> SendAsync(string text, CancellationToken ct)
    {
        var token = await _settings.GetAsync(SettingKeys.AlertsTelegramToken, _cfg["ALERT_BOT_TOKEN"], ct);
        var chat = await _settings.GetAsync(SettingKeys.AlertsTelegramChat, _cfg["ALERT_CHAT_ID"], ct);

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chat))
        {
            // Logged rather than silent: an unconfigured watchdog looks exactly like a healthy one.
            _log.LogWarning("collector watchdog: alert not sent, telegram token or chat id is not configured");
            return false;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new { chat_id = chat, text, parse_mode = "Markdown" });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var res = await Http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content, ct);
            if (res.IsSuccessStatusCode) return true;

            _log.LogWarning("collector watchdog: telegram returned {Status}", (int)res.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "collector watchdog: could not reach telegram");
            return false;
        }
    }
}
