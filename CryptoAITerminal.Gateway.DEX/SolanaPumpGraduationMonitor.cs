using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>
/// Monitors pump.fun for token graduation events (migration to Raydium CPMM).
///
/// When a token on pump.fun reaches its bonding curve target (~$69k market cap),
/// it "graduates" and gets a Raydium pool. This moment has peak hype and liquidity —
/// ideal for sniping the first few minutes of real-market trading.
///
/// Program: pump.fun migration = 39azUYFWPz3VHgKCf3VChUwbpURdCHRxjWVowf5jUJjg
/// The migration instruction transfers tokens to a new Raydium CPMM pool.
/// We detect it by watching signatures on the migration program + Raydium CPMM.
/// </summary>
public sealed class SolanaPumpGraduationMonitor : IDisposable
{
    // pump.fun migration / withdraw program
    public const string PumpMigrationProgram = "39azUYFWPz3VHgKCf3VChUwbpURdCHRxjWVowf5jUJjg";

    // Raydium CPMM (where graduated pools land)
    public const string RaydiumCpmmProgram   = "CPMMoo8L3F4NbTegBCKVNunggL7H1ZpdTHKxQB5qKP1C";

    // SOL mint
    private const string SolMint = "So11111111111111111111111111111111111111112";

    private readonly SolanaRpcClient _rpcClient;
    private string? _lastSeenSignature;

    public SolanaPumpGraduationMonitor(string rpcUrl = "https://api.mainnet-beta.solana.com")
    {
        _rpcClient = new SolanaRpcClient(rpcUrl);
    }

    /// <summary>
    /// Polls for new pump.fun graduation events.
    /// Returns one signal per graduated token found since the last poll.
    /// </summary>
    public async Task<IReadOnlyList<PumpGraduationSignal>> PollAsync(
        CancellationToken ct = default)
    {
        var signatures = await _rpcClient.GetRecentSignaturesForAddressAsync(
            PumpMigrationProgram,
            limit: 20,
            cancellationToken: ct);

        if (_lastSeenSignature is null)
        {
            _lastSeenSignature = signatures.FirstOrDefault()?.Signature;
            return Array.Empty<PumpGraduationSignal>();
        }

        var results = new List<PumpGraduationSignal>();
        foreach (var sig in signatures)
        {
            if (string.Equals(sig.Signature, _lastSeenSignature, StringComparison.OrdinalIgnoreCase))
                break;

            // Skip failed transactions
            if (!string.IsNullOrWhiteSpace(sig.ErrorJson) && sig.ErrorJson != "null")
                continue;

            try
            {
                var parsed = await _rpcClient.GetParsedTransactionAsync(sig.Signature, ct);
                if (parsed is null) continue;

                var signal = TryExtractGraduation(parsed, sig.Signature, sig.BlockTimeUnixSeconds);
                if (signal is not null)
                    results.Add(signal);
            }
            catch
            {
                // RPC errors on individual tx — skip, don't abort poll
            }
        }

        if (signatures.Count > 0)
            _lastSeenSignature = signatures[0].Signature;

        return results;
    }

    private static PumpGraduationSignal? TryExtractGraduation(
        SolanaParsedTransaction tx, string signature, long? blockTime)
    {
        var tokenMint = string.Empty;

        // Strategy 1: InitializedMints contains mints created in this tx.
        // The graduated token (not SOL, not wrapped SOL) is the one we want.
        // Migration txs typically initialize the LP token and sometimes the base token.
        // We pick the first non-SOL mint that looks like a pump.fun token (32-44 chars).
        if (tx.InitializedMints.Count > 0)
        {
            tokenMint = tx.InitializedMints
                .FirstOrDefault(m =>
                    !string.IsNullOrEmpty(m) &&
                    !string.Equals(m, SolMint, StringComparison.OrdinalIgnoreCase) &&
                    m.Length >= 32)
                ?? string.Empty;
        }

        // Strategy 2: PostTokenBalances — look for non-SOL mint with significant balance
        if (string.IsNullOrEmpty(tokenMint) && tx.PostTokenBalances.Count > 0)
        {
            tokenMint = tx.PostTokenBalances
                .Where(b =>
                    !string.IsNullOrEmpty(b.Mint) &&
                    !string.Equals(b.Mint, SolMint, StringComparison.OrdinalIgnoreCase) &&
                    b.UiAmount > 0)
                .OrderByDescending(b => b.UiAmount)
                .Select(b => b.Mint)
                .FirstOrDefault()
                ?? string.Empty;
        }

        // Strategy 3: AccountKeys — migration tx has the token mint as 3rd–5th account
        if (string.IsNullOrEmpty(tokenMint) && tx.AccountKeys.Count >= 3)
        {
            tokenMint = tx.AccountKeys
                .Skip(2).Take(4)
                .FirstOrDefault(k =>
                    !string.IsNullOrEmpty(k) &&
                    k.Length >= 32 &&
                    !k.Equals("11111111111111111111111111111111", StringComparison.Ordinal) &&
                    !k.Equals(SolMint, StringComparison.OrdinalIgnoreCase))
                ?? string.Empty;
        }

        // Verify it looks like a graduation via log messages
        var hasGraduationLog = tx.LogMessages.Any(log =>
            log.Contains("MigrateToAMM",   StringComparison.OrdinalIgnoreCase) ||
            log.Contains("migrate",         StringComparison.OrdinalIgnoreCase) ||
            log.Contains("WithdrawAndMigrate", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("CreatePool",      StringComparison.OrdinalIgnoreCase));

        // If no graduation log, be conservative and skip unless we found a clear mint
        if (!hasGraduationLog && string.IsNullOrEmpty(tokenMint)) return null;
        if (string.IsNullOrEmpty(tokenMint)) return null;

        var graduatedAt = blockTime.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(blockTime.Value).UtcDateTime
            : DateTime.UtcNow;

        return new PumpGraduationSignal(
            TokenMint: tokenMint,
            Signature: signature,
            GraduatedAtUtc: graduatedAt,
            NewPoolProgram: RaydiumCpmmProgram);
    }

    public DexTokenInfo ToPlaceholderToken(PumpGraduationSignal signal) => new()
    {
        ChainId      = "solana",
        DexId        = "raydium-cpmm",
        TokenAddress = signal.TokenMint,
        QuoteSymbol  = "SOL",
        Symbol       = string.Empty,
        Name         = string.Empty,
        Url          = string.Empty,
        LastUpdatedUtc = signal.GraduatedAtUtc
    };

    public void Dispose() => _rpcClient.Dispose();
}

/// <summary>
/// Signal emitted when a pump.fun token graduates to Raydium.
/// This is one of the highest-alpha moments in Solana DeFi.
/// </summary>
public sealed record PumpGraduationSignal(
    string TokenMint,
    string Signature,
    DateTime GraduatedAtUtc,
    string NewPoolProgram)
{
    public string ShortMint => TokenMint.Length > 8 ? TokenMint[..8] + "..." : TokenMint;
    public string AgeLabel  => (DateTime.UtcNow - GraduatedAtUtc).TotalSeconds < 60
        ? $"{(int)(DateTime.UtcNow - GraduatedAtUtc).TotalSeconds}s ago"
        : $"{(int)(DateTime.UtcNow - GraduatedAtUtc).TotalMinutes}m ago";
}
