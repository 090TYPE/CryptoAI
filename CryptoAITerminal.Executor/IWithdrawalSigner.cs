namespace CryptoAITerminal.Executor;

/// <summary>
/// Signs and broadcasts a withdrawal. This is the ONLY place real funds move — deliberately an
/// interface so the money-movement stays isolated and swappable.
///
/// ⚠️ The shipped <see cref="StubWithdrawalSigner"/> does NOT move funds. A production signer
/// must NOT take the raw private key like this — it should use MPC / threshold signing (the key
/// never exists whole on one machine) plus a policy engine (address allowlist, velocity caps).
/// Wiring a naive "decrypt the key and send" here means one server compromise drains everyone.
/// </summary>
public interface IWithdrawalSigner
{
    Task<string> SendAsync(string secretPlaintext, string asset, decimal amount, string toAddress, CancellationToken ct);
}

/// <summary>Skeleton signer: records intent and returns a fake tx reference. Moves NO money.</summary>
public sealed class StubWithdrawalSigner : IWithdrawalSigner
{
    public Task<string> SendAsync(string secretPlaintext, string asset, decimal amount, string toAddress, CancellationToken ct)
        => Task.FromResult("stub-tx-" + Guid.NewGuid().ToString("N")[..12]);
}
