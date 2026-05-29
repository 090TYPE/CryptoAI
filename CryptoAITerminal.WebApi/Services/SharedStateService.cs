using System.Text.Json;
using CryptoAITerminal.WebApi.Models;

namespace CryptoAITerminal.WebApi.Services;

/// <summary>
/// Читает/пишет shared-файлы которые координируют WebApi с процессом TerminalUI.
///
/// Layout (под %APPDATA%/CryptoAITerminal/webapi):
///   snapshot.json       — снимок позиций/кандидатов/PnL (пишет TerminalUI)
///   queue/&lt;id&gt;.json    — очередь market-ордеров (пишет WebApi, обрабатывает TerminalUI)
/// </summary>
public sealed class SharedStateService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string RootDir { get; }
    public string SnapshotPath => Path.Combine(RootDir, "snapshot.json");
    public string QueueDir     => Path.Combine(RootDir, "queue");

    public SharedStateService(string? rootOverride = null)
    {
        RootDir = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CryptoAITerminal", "webapi");

        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(QueueDir);
    }

    public TerminalSnapshot ReadSnapshot()
    {
        if (!File.Exists(SnapshotPath))
            return new TerminalSnapshot { UpdatedUtc = DateTime.MinValue };

        try
        {
            using var fs = new FileStream(SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<TerminalSnapshot>(fs, JsonOpts)
                ?? new TerminalSnapshot { UpdatedUtc = DateTime.MinValue };
        }
        catch (Exception)
        {
            return new TerminalSnapshot { UpdatedUtc = DateTime.MinValue };
        }
    }

    public QueuedOrderRecord EnqueueOrder(MarketOrderRequest order)
    {
        var record = new QueuedOrderRecord { Order = order, Action = "place" };
        WriteQueueRecord(record);
        return record;
    }

    public QueuedOrderRecord EnqueueCancel(CancelOrderRequest cancel)
    {
        var record = new QueuedOrderRecord
        {
            Action          = "cancel",
            OrderIdToCancel = cancel.OrderId,
            Order = new MarketOrderRequest
            {
                Exchange = cancel.Exchange,
                Market   = cancel.Market,
            },
        };
        WriteQueueRecord(record);
        return record;
    }

    public QueuedOrderRecord EnqueueTradingViewAlert(TradingViewAlertDto alert)
    {
        var side = alert.Action.Trim().ToLowerInvariant() switch
        {
            "sell"  or "short" => "Sell",
            "close"            => "Sell",
            _                  => "Buy"
        };
        var order = new MarketOrderRequest
        {
            Exchange = NormalizeExchange(alert.Exchange),
            Market   = NormalizeMarket(alert.Market),
            Symbol   = alert.Symbol.Trim().ToUpperInvariant(),
            Side     = side,
            Quantity = alert.Qty,
        };
        var record = new QueuedOrderRecord
        {
            Action = "place",
            Order  = order,
        };
        WriteQueueRecord(record);
        LogWebhook(alert, record.Id);
        return record;
    }

    private void LogWebhook(TradingViewAlertDto alert, string orderId)
    {
        try
        {
            var logDir  = Path.Combine(RootDir, "webhook-log");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            var entry   = JsonSerializer.Serialize(new
            {
                receivedUtc = DateTime.UtcNow,
                orderId,
                action   = alert.Action,
                symbol   = alert.Symbol,
                qty      = alert.Qty,
                exchange = alert.Exchange,
                market   = alert.Market,
                comment  = alert.Comment,
            }, JsonOpts);
            File.AppendAllText(logPath, entry + "\n");
        }
        catch { /* logging is best-effort */ }
    }

    private static string NormalizeExchange(string raw) =>
        raw.Trim() switch
        {
            { } s when s.Equals("Bybit",  StringComparison.OrdinalIgnoreCase) => "Bybit",
            { } s when s.Equals("OKX",    StringComparison.OrdinalIgnoreCase) => "OKX",
            { } s when s.Equals("KuCoin", StringComparison.OrdinalIgnoreCase) => "KuCoin",
            _                                                                   => "Binance",
        };

    private static string NormalizeMarket(string raw) =>
        raw.Trim().Equals("Futures", StringComparison.OrdinalIgnoreCase) ? "Futures" : "Spot";

    private void WriteQueueRecord(QueuedOrderRecord record)
    {
        var path = Path.Combine(QueueDir, $"{record.Id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOpts));
    }
}
