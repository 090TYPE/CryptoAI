using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Подбирает market-ордера из <c>%APPDATA%/CryptoAITerminal/webapi/queue/&lt;id&gt;.json</c>
/// (туда их складывает <c>CryptoAITerminal.WebApi</c>), маршрутизирует в нужный
/// gateway и пишет результат в <c>processed/&lt;id&gt;.json</c>. Файл из queue
/// удаляется после обработки.
///
/// Контракт ордера (camelCase JSON):
/// <code>
/// {
///   "id": "...",
///   "enqueuedUtc": "...",
///   "order": { "exchange": "Binance"|"Bybit"|"OKX", "market": "Spot"|"Futures",
///              "symbol": "BTCUSDT", "side": "Buy"|"Sell", "quantity": 0.001 }
/// }
/// </code>
/// </summary>
public sealed class WebApiQueueProcessor : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Dictionary<(string Exchange, string Market), IExchangeGateway> _gateways;
    private readonly string _queueDir;
    private readonly string _processedDir;
    private readonly Timer _timer;
    private readonly Action<string>? _log;
    private int _polling; // 0 idle, 1 polling

    public string QueueDir     => _queueDir;
    public string ProcessedDir => _processedDir;

    public WebApiQueueProcessor(
        IReadOnlyDictionary<(string Exchange, string Market), IExchangeGateway> gateways,
        Action<string>? logger = null,
        TimeSpan? interval = null)
    {
        _gateways = new Dictionary<(string, string), IExchangeGateway>(
            gateways.Count, KeyComparer.Instance);
        foreach (var kv in gateways)
            _gateways[(kv.Key.Exchange, kv.Key.Market)] = kv.Value;

        _log = logger;

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CryptoAITerminal", "webapi");
        _queueDir     = Path.Combine(root, "queue");
        _processedDir = Path.Combine(root, "processed");
        Directory.CreateDirectory(_queueDir);
        Directory.CreateDirectory(_processedDir);

        var dueTime = TimeSpan.FromSeconds(3);
        var period  = interval ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(_ => _ = TickAsync(), null, dueTime, period);
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            string[] files;
            try { files = Directory.GetFiles(_queueDir, "*.json"); }
            catch { return; }

            foreach (var path in files.OrderBy(p => p, StringComparer.Ordinal))
            {
                try { await ProcessFileAsync(path); }
                catch (Exception ex) { _log?.Invoke($"[WebApi queue] {Path.GetFileName(path)} failed: {ex.Message}"); }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private async Task ProcessFileAsync(string path)
    {
        QueuedOrderRecord? record;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            record = JsonSerializer.Deserialize<QueuedOrderRecord>(fs, JsonOpts);
        }
        catch (Exception ex)
        {
            WriteResult(path, false, $"parse error: {ex.Message}", null);
            SafeDelete(path);
            return;
        }

        if (record?.Order is null)
        {
            WriteResult(path, false, "empty order payload", null);
            SafeDelete(path);
            return;
        }

        var ord = record.Order;
        var exchange = (ord.Exchange ?? "").Trim();
        var market   = (ord.Market   ?? "").Trim();
        var action   = string.IsNullOrWhiteSpace(record.Action) ? "place" : record.Action.Trim().ToLowerInvariant();

        if (!_gateways.TryGetValue((exchange, market), out var gw))
        {
            WriteResult(path, false, $"unknown gateway {exchange}/{market}", record);
            SafeDelete(path);
            return;
        }

        if (action == "cancel")
        {
            if (string.IsNullOrWhiteSpace(record.OrderIdToCancel))
            {
                WriteResult(path, false, "orderIdToCancel is required for cancel", record);
                SafeDelete(path);
                return;
            }

            try
            {
                // Use symbol-aware cancel when available (required for Binance Futures).
                var cancelSymbol = ord?.Symbol?.Trim().ToUpperInvariant() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(cancelSymbol))
                    await gw.CancelOrderAsync(cancelSymbol, record.OrderIdToCancel);
                else
                    await gw.CancelOrderAsync(record.OrderIdToCancel);
                _log?.Invoke($"[WebApi queue] ✓ cancel {exchange}/{market} orderId={record.OrderIdToCancel}");
                WriteResult(path, true, $"cancelled orderId={record.OrderIdToCancel}", record);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[WebApi queue] ✗ cancel {exchange}/{market} orderId={record.OrderIdToCancel}: {ex.Message}");
                WriteResult(path, false, ex.Message, record);
            }

            SafeDelete(path);
            return;
        }

        // action == "place" (по умолчанию)
        var side = string.Equals(ord.Side, "Sell", StringComparison.OrdinalIgnoreCase)
            ? OrderSide.Sell : OrderSide.Buy;

        if (string.IsNullOrWhiteSpace(ord.Symbol))
        {
            WriteResult(path, false, "symbol is required", record);
            SafeDelete(path);
            return;
        }
        if (ord.Quantity <= 0m)
        {
            WriteResult(path, false, "quantity must be > 0", record);
            SafeDelete(path);
            return;
        }

        try
        {
            var placed = await gw.PlaceOrderAsync(new Order
            {
                Symbol     = ord.Symbol.ToUpperInvariant(),
                Side       = side,
                Type       = OrderType.Market,
                Quantity   = ord.Quantity,
                MarketType = string.Equals(market, "Futures", StringComparison.OrdinalIgnoreCase)
                    ? TradingMarketType.FuturesUsdM : TradingMarketType.Spot,
            });

            _log?.Invoke($"[WebApi queue] ✓ {exchange}/{market} {ord.Side} {ord.Quantity} {ord.Symbol} → orderId={placed.Id}");
            WriteResult(path, true, $"placed orderId={placed.Id}", record);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[WebApi queue] ✗ {exchange}/{market} {ord.Symbol}: {ex.Message}");
            WriteResult(path, false, ex.Message, record);
        }

        SafeDelete(path);
    }

    private void WriteResult(string sourcePath, bool success, string message, QueuedOrderRecord? record)
    {
        try
        {
            var id = record?.Id ?? Path.GetFileNameWithoutExtension(sourcePath);
            var resultPath = Path.Combine(_processedDir, $"{id}.json");
            var resultJson = JsonSerializer.Serialize(new
            {
                id,
                processedUtc = DateTime.UtcNow,
                success,
                message,
                order = record?.Order,
            }, JsonOpts);

            var tmp = resultPath + ".tmp";
            File.WriteAllText(tmp, resultJson);
            File.Move(tmp, resultPath, overwrite: true);
        }
        catch { /* best-effort */ }
    }

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }

    public void Dispose() => _timer.Dispose();

    private sealed class QueuedOrderRecord
    {
        public string Id { get; set; } = "";
        public DateTime EnqueuedUtc { get; set; }
        public string Action { get; set; } = "place";
        public MarketOrderPayload? Order { get; set; }
        public string OrderIdToCancel { get; set; } = "";
    }

    private sealed class MarketOrderPayload
    {
        public string Exchange { get; set; } = "";
        public string Market   { get; set; } = "";
        public string Symbol   { get; set; } = "";
        public string Side     { get; set; } = "Buy";
        public decimal Quantity{ get; set; }
    }

    private sealed class KeyComparer : IEqualityComparer<(string Exchange, string Market)>
    {
        public static readonly KeyComparer Instance = new();
        public bool Equals((string Exchange, string Market) a, (string Exchange, string Market) b) =>
            string.Equals(a.Exchange, b.Exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Market,   b.Market,   StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Exchange, string Market) obj) =>
            HashCode.Combine(
                obj.Exchange?.ToLowerInvariant().GetHashCode() ?? 0,
                obj.Market  ?.ToLowerInvariant().GetHashCode() ?? 0);
    }
}
