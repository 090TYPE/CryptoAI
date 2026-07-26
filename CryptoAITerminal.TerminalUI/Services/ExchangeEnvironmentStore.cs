using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Per-exchange testnet opt-in. Off means the live exchange, as before.</summary>
public sealed class ExchangeEnvironments
{
    public bool Binance { get; set; }
    public bool Bybit { get; set; }
    public bool Okx { get; set; }

    public ExchangeEnvironments Clone() => new() { Binance = Binance, Bybit = Bybit, Okx = Okx };
}

/// <summary>
/// Chooses the live or test endpoint per exchange. The gateways always accepted the switch —
/// the exchange libraries expose it as a client environment — but nothing set it, so the
/// TESTNET toggle in Settings said outright that it was not wired up.
/// </summary>
/// <remarks>
/// KuCoin is deliberately absent: it retired its public sandbox and the client library carries
/// no test environment, so there is nothing to point a KuCoin gateway at.
/// </remarks>
public static class ExchangeEnvironmentStore
{
    /// <summary>Which exchanges can be switched, and what the exchange calls its test environment.</summary>
    public static readonly (string Id, bool Supported, string EnvName, string Note)[] Support =
    {
        ("binance", true, "Binance Testnet",
            "testnet.binance.vision issues its own API keys — a mainnet key is rejected there."),
        ("bybit", true, "Bybit Testnet",
            "testnet.bybit.com issues its own API keys and has its own balances."),
        ("okx", true, "OKX Demo trading",
            "OKX calls it demo trading; create the key inside the demo account."),
        ("kucoin", false, "",
            "KuCoin retired its public sandbox and the client library has no test environment."),
    };

    public static bool IsSupported(string? id) =>
        Support.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase) && s.Supported);

    public static (string EnvName, string Note) Describe(string? id)
    {
        var row = Support.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        return (row.EnvName ?? "", row.Note ?? "");
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal", "exchange_environments.json");

    private static ExchangeEnvironments? _cached;

    /// <summary>The live opt-ins. Read on first use, then kept in memory.</summary>
    public static ExchangeEnvironments Current => _cached ??= Load();

    public static ExchangeEnvironments Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new ExchangeEnvironments();
            var json = File.ReadAllText(FilePath);
            return string.IsNullOrWhiteSpace(json)
                ? new ExchangeEnvironments()
                : JsonSerializer.Deserialize<ExchangeEnvironments>(json, Options) ?? new ExchangeEnvironments();
        }
        catch
        {
            return new ExchangeEnvironments();
        }
    }

    /// <summary>Persists the opt-ins. Returns an error message, or null on success.</summary>
    public static string? Save(ExchangeEnvironments environments)
    {
        ArgumentNullException.ThrowIfNull(environments);
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(environments, Options));
            _cached = environments.Clone();
            return null;
        }
        catch (Exception ex)
        {
            return "Could not save the exchange environments: " + ex.Message;
        }
    }

    /// <summary>True when this exchange should talk to its test endpoint.</summary>
    public static bool IsTestnet(string? id) => id?.ToLowerInvariant() switch
    {
        "binance" => Current.Binance,
        "bybit" => Current.Bybit,
        "okx" => Current.Okx,
        _ => false,
    };

    /// <summary>Ids currently pointed at a test endpoint, in display order.</summary>
    public static IReadOnlyList<string> ActiveTestnets() =>
        Support.Where(s => s.Supported && IsTestnet(s.Id)).Select(s => s.Id).ToList();
}
