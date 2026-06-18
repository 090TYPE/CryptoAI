using System;
using System.Collections.Generic;
using System.IO;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Persists the global wall-highlight mode and per-symbol thresholds to
/// %LOCALAPPDATA%\CryptoAITerminal\book-walls.json. Best-effort: any IO error
/// falls back to in-memory defaults.
/// </summary>
public sealed class BookWallSettingsStore
{
    public sealed class PersistedState
    {
        public WallHighlightMode Mode { get; set; } = WallHighlightMode.Usd;
        public Dictionary<string, BookWallSettings> Symbols { get; set; } = new();
    }

    private readonly string _path;
    private readonly PersistedState _state;

    public BookWallSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoAITerminal", "book-walls.json");
        _state = Load(_path);
    }

    public WallHighlightMode Mode
    {
        get => _state.Mode;
        set { _state.Mode = value; Save(); }
    }

    public BookWallSettings Get(string symbol)
    {
        if (_state.Symbols.TryGetValue(symbol, out var existing))
        {
            return existing;
        }

        var created = new BookWallSettings();
        _state.Symbols[symbol] = created;
        return created;
    }

    public void Set(string symbol, BookWallSettings settings)
    {
        _state.Symbols[symbol] = settings;
        Save();
    }

    private void Save()
    {
        try { AtomicJsonFile.Write(_path, _state); }
        catch { /* best-effort persistence */ }
    }

    private static PersistedState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return AtomicJsonFile.Read<PersistedState>(path) ?? new PersistedState();
            }
        }
        catch { /* fall through to defaults */ }
        return new PersistedState();
    }
}
