using CryptoAITerminal.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Leader side of copy trading: captures every executed trade and
/// appends it to %APPDATA%/CryptoAITerminal/webapi/copy-trades.json
/// so followers (and the /api/copy-trades WebApi endpoint) can read it.
/// Thread-safe; keeps at most MaxTrades entries.
/// </summary>
public sealed class CopyTradingLeaderService : IDisposable
{
    private const int MaxTrades = 100;

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly List<CopyTrade> _trades = [];

    public event Action<CopyTrade>? TradePublished;

    public CopyTradingLeaderService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CryptoAITerminal", "webapi");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "copy-trades.json");

        // Load existing trades on startup so history is preserved across restarts
        TryLoadExisting();
    }

    /// <summary>Records a trade executed by any bot/strategy and persists it.</summary>
    public void PublishTrade(string exchange, string market, string symbol,
        string side, decimal qty, decimal price, string source)
    {
        var trade = new CopyTrade
        {
            Exchange    = exchange,
            Market      = market,
            Symbol      = symbol,
            Side        = side,
            Quantity    = qty,
            Price       = price,
            Source      = source,
        };

        lock (_lock)
        {
            _trades.Add(trade);
            if (_trades.Count > MaxTrades)
                _trades.RemoveRange(0, _trades.Count - MaxTrades);
            WriteAtomic();
        }

        TradePublished?.Invoke(trade);
    }

    public IReadOnlyList<CopyTrade> RecentTrades
    {
        get { lock (_lock) { return _trades.ToList(); } }
    }

    private void WriteAtomic()
    {
        var tmp = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(_trades,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, json);
        File.Move(tmp, _filePath, overwrite: true);
    }

    private void TryLoadExisting()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<CopyTrade>>(json);
            if (loaded is { Count: > 0 })
                _trades.AddRange(loaded.TakeLast(MaxTrades));
        }
        catch { }
    }

    public void Dispose() { }
}
