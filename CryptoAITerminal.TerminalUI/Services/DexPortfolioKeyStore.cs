using System;
using System.IO;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Persists the optional Covalent (GoldRush) API key that unlocks the cross-chain portfolio.
/// Best-effort plain-text store in appdata — never throws.
/// </summary>
public sealed class DexPortfolioKeyStore
{
    private readonly string _path;

    public DexPortfolioKeyStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CryptoAITerminal",
            "dex_portfolio_key.txt");
    }

    public string Load()
    {
        try
        {
            return File.Exists(_path) ? File.ReadAllText(_path).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Save(string key)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, (key ?? string.Empty).Trim());
        }
        catch
        {
            // best-effort
        }
    }
}
