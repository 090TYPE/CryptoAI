using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

public sealed record AuditEntry(DateTime AtUtc, string ActionId, string Preview, string Outcome, string? Detail);

/// <summary>Persisted trail of every proposed/approved/executed/failed action (bounded).</summary>
public sealed class AppActionAuditLog
{
    private const int MaxEntries = 500;
    private readonly string _path;
    private readonly List<AuditEntry> _entries;

    public AppActionAuditLog(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoAITerminal", "agent-actions.json");
        _entries = TryLoad();
    }

    public void Record(string actionId, string preview, string outcome, string? detail)
    {
        _entries.Add(new AuditEntry(DateTime.UtcNow, actionId, preview, outcome, detail));
        while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
        try { Services.AtomicJsonFile.Write(_path, _entries); } catch { /* non-fatal */ }
    }

    public IReadOnlyList<AuditEntry> Recent(int n) =>
        _entries.AsEnumerable().Reverse().Take(n).ToList();

    private List<AuditEntry> TryLoad()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return Services.AtomicJsonFile.Read<List<AuditEntry>>(_path) ?? new();
        }
        catch { return new(); }
    }
}
