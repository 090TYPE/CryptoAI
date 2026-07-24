namespace CryptoAITerminal.Executor;

/// <summary>
/// Symbol allowlist guardrail (Track 4 §4). When the node is configured with an allowlist
/// (BOT_SYMBOL_ALLOWLIST), live bots may only trade those symbols — a typo or a rogue config can't
/// send an order for an unexpected market. An empty allowlist means no restriction.
/// </summary>
public sealed class SymbolAllowlist
{
    private readonly IReadOnlySet<string> _allowed;

    public SymbolAllowlist(string? configValue) => _allowed = Parse(configValue);

    public bool IsAllowed(string symbol) => IsAllowed(symbol, _allowed);

    /// <summary>Pure check: empty allowlist → allowed; otherwise case-insensitive membership.</summary>
    public static bool IsAllowed(string symbol, IReadOnlyCollection<string> allowlist)
        => allowlist.Count == 0 || allowlist.Contains(symbol.ToUpperInvariant());

    /// <summary>Parse a comma/space/semicolon-separated list into an upper-cased set.</summary>
    public static IReadOnlySet<string> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new HashSet<string>();
        return value
            .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant())
            .ToHashSet();
    }
}
