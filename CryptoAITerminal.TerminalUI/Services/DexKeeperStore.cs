using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CryptoAITerminal.Core.Trading;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Persists the DEX keeper's working conditional orders to disk so armed limit/stop/
/// TP-SL/trailing/DCA orders survive an app restart. Best-effort — never throws.
/// </summary>
public sealed class DexKeeperStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly string _path;
    // Save is fired from four places as `_ = Task.Run(...)`; two of those overlapping on a plain
    // File.WriteAllText left a half-written file, and the next launch armed nothing.
    private readonly object _writeGate = new();

    public DexKeeperStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CryptoAITerminal",
            "dex_keeper_orders.json");
    }

    /// <summary>
    /// Set when the last <see cref="Load"/> found a file it could not read — the armed orders are
    /// gone and the user has to be told, because silently returning an empty list looks exactly
    /// like "you had no stop-losses".
    /// </summary>
    public string? LastLoadError { get; private set; }

    public List<DexKeeperOrder> Load()
    {
        LastLoadError = null;
        try
        {
            if (!File.Exists(_path)) return new List<DexKeeperOrder>();
            var json = File.ReadAllText(_path);
            return string.IsNullOrWhiteSpace(json)
                ? new List<DexKeeperOrder>()
                : JsonSerializer.Deserialize<List<DexKeeperOrder>>(json, Options) ?? new List<DexKeeperOrder>();
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            var backup = string.Empty;
            try { backup = AtomicJsonFile.BackupCorruptFile(_path); } catch { /* nothing else to try */ }
            CrashLog.Write("ERROR",
                $"DexKeeperStore: armed orders lost, file unreadable ({ex.Message})" +
                (backup.Length > 0 ? $"; kept a copy at {backup}" : string.Empty));
            return new List<DexKeeperOrder>();
        }
    }

    public void Save(IEnumerable<DexKeeperOrder> orders)
    {
        try
        {
            // Materialise outside the gate so a writer never blocks on the caller's enumeration.
            var snapshot = new List<DexKeeperOrder>(orders);
            lock (_writeGate)
                AtomicJsonFile.Write(_path, snapshot, Options);
        }
        catch (Exception ex)
        {
            CrashLog.Write("ERROR", "DexKeeperStore: could not persist armed orders — " + ex.Message);
        }
    }
}
