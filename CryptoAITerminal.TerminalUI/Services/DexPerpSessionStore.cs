using System;
using System.IO;
using System.Text.Json;
using CryptoAITerminal.Core.Trading;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Persists the paper PERP desk state (position, working orders, fills, realised P&L,
/// funding) to disk so a session survives an app restart. Best-effort — any IO or
/// serialization failure is swallowed so it can never break trading.
/// </summary>
public sealed class DexPerpSessionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly string _path;

    public DexPerpSessionStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CryptoAITerminal",
            "dex_perp_session.json");
    }

    public DexPerpEngineSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var json = File.ReadAllText(_path);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<DexPerpEngineSnapshot>(json, Options);
        }
        catch
        {
            return null;
        }
    }

    public void Save(DexPerpEngineSnapshot snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, Options));
        }
        catch
        {
            // best-effort; never throw from persistence
        }
    }
}
