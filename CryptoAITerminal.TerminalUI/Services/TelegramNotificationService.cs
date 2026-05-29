using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Callback fired when the user taps an inline button in a Telegram message.
/// </summary>
public sealed record TelegramCallback(
    long   MessageId,
    string CallbackData,
    string QueryId,
    long   FromUserId);

public sealed class TelegramNotificationService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private string _botToken = string.Empty;
    private string _chatId   = string.Empty;

    // ── long-poll offset ──────────────────────────────────────────────────────
    private long _pollOffset = 0;
    private CancellationTokenSource? _pollCts;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_botToken) && !string.IsNullOrWhiteSpace(_chatId);

    /// <summary>Raised on a thread-pool thread when an inline-button callback arrives.</summary>
    public event Action<TelegramCallback>? CallbackReceived;

    // ── configuration ─────────────────────────────────────────────────────────

    public void Configure(string botToken, string chatId)
    {
        _botToken = botToken.Trim();
        _chatId   = chatId.Trim();
    }

    // ── send simple message ───────────────────────────────────────────────────

    public async Task<bool> SendAsync(string message)
    {
        if (!IsConfigured) return false;
        try
        {
            var url     = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var payload = new { chat_id = _chatId, text = message, parse_mode = "HTML" };
            using var response = await _http.PostAsJsonAsync(url, payload);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> TestConnectionAsync()
    {
        if (!IsConfigured) return false;
        return await SendAsync("✅ CryptoAI Terminal — Telegram notifications connected.");
    }

    // ── send message with inline buttons ─────────────────────────────────────

    /// <summary>
    /// Sends a message with up to 4 inline buttons arranged in a single row.
    /// Returns the Telegram message_id on success, or -1 on failure.
    /// </summary>
    public async Task<long> SendWithButtonsAsync(
        string message,
        params (string Label, string CallbackData)[] buttons)
    {
        if (!IsConfigured) return -1;
        try
        {
            var buttonArray = new JsonArray();
            var row         = new JsonArray();
            foreach (var (label, data) in buttons)
                row.Add(new JsonObject { ["text"] = label, ["callback_data"] = data });
            buttonArray.Add(row);

            var url  = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var body = new JsonObject
            {
                ["chat_id"]      = _chatId,
                ["text"]         = message,
                ["parse_mode"]   = "HTML",
                ["reply_markup"] = new JsonObject { ["inline_keyboard"] = buttonArray }
            };

            var content  = new System.Net.Http.StringContent(body.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync(url, content);
            if (!res.IsSuccessStatusCode) return -1;

            var json = await res.Content.ReadAsStringAsync();
            var node = JsonNode.Parse(json);
            return node?["result"]?["message_id"]?.GetValue<long>() ?? -1;
        }
        catch { return -1; }
    }

    /// <summary>Answers a callback_query to remove the "loading" indicator from the button.</summary>
    public async Task AnswerCallbackQueryAsync(string queryId, string text = "")
    {
        if (!IsConfigured) return;
        try
        {
            var url     = $"https://api.telegram.org/bot{_botToken}/answerCallbackQuery";
            var payload = new { callback_query_id = queryId, text };
            await _http.PostAsJsonAsync(url, payload);
        }
        catch { /* best-effort */ }
    }

    // ── callback polling ──────────────────────────────────────────────────────

    /// <summary>Starts a background long-poll loop for callback_query updates.</summary>
    public void StartPolling()
    {
        if (_pollCts is not null) return;
        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
    }

    public void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{_botToken}" +
                          $"/getUpdates?offset={_pollOffset}&timeout=20" +
                          $"&allowed_updates=%5B%22callback_query%22%5D";

                using var res = await _http.GetAsync(url, ct);
                if (!res.IsSuccessStatusCode) { await Task.Delay(5000, ct); continue; }

                var json = await res.Content.ReadAsStringAsync(ct);
                var node = JsonNode.Parse(json);
                var arr  = node?["result"]?.AsArray();
                if (arr is null || arr.Count == 0) continue;

                foreach (var update in arr)
                {
                    var updateId = update?["update_id"]?.GetValue<long>() ?? 0;
                    _pollOffset  = Math.Max(_pollOffset, updateId + 1);

                    var cq = update?["callback_query"];
                    if (cq is null) continue;

                    var msgId  = cq["message"]?["message_id"]?.GetValue<long>() ?? -1;
                    var data   = cq["data"]?.GetValue<string>()    ?? "";
                    var qid    = cq["id"]?.GetValue<string>()      ?? "";
                    var fromId = cq["from"]?["id"]?.GetValue<long>() ?? 0;

                    CallbackReceived?.Invoke(new TelegramCallback(msgId, data, qid, fromId));
                }
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(5000, ct); }
        }
    }

    public void Dispose()
    {
        StopPolling();
        _http.Dispose();
    }
}
