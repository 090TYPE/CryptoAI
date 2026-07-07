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

    public DexKeeperStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CryptoAITerminal",
            "dex_keeper_orders.json");
    }

    public List<DexKeeperOrder> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<DexKeeperOrder>();
            var json = File.ReadAllText(_path);
            return string.IsNullOrWhiteSpace(json)
                ? new List<DexKeeperOrder>()
                : JsonSerializer.Deserialize<List<DexKeeperOrder>>(json, Options) ?? new List<DexKeeperOrder>();
        }
        catch
        {
            return new List<DexKeeperOrder>();
        }
    }

    public void Save(IEnumerable<DexKeeperOrder> orders)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(orders, Options));
        }
        catch
        {
            // best-effort
        }
    }
}
