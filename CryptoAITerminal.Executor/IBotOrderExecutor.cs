namespace CryptoAITerminal.Executor;

/// <summary>
/// Places a bot's order. Isolated behind an interface so the money-movement stays swappable.
///
/// ⚠️ The shipped <see cref="StubBotOrderExecutor"/> does NOT trade — it returns a paper ref.
/// A production impl decrypts the user's exchange key (via the envelope cipher) and places the
/// order through the exchange gateway, with the same risk controls as the desktop app
/// (RiskManager, per-user caps, kill-switch).
/// </summary>
public interface IBotOrderExecutor
{
    Task<(string Status, string ExtRef)> PlaceAsync(Guid userId, string side, string asset, decimal amount, CancellationToken ct);
}

/// <summary>Skeleton executor: records a paper order, moves NO money.</summary>
public sealed class StubBotOrderExecutor : IBotOrderExecutor
{
    public Task<(string Status, string ExtRef)> PlaceAsync(Guid userId, string side, string asset, decimal amount, CancellationToken ct)
        => Task.FromResult(("paper", "paper-" + Guid.NewGuid().ToString("N")[..12]));
}
