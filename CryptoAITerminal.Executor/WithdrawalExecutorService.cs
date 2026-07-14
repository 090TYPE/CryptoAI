using System.Text.Json;
using CryptoAITerminal.Server.Common;
using CryptoAITerminal.Server.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoAITerminal.Executor;

/// <summary>
/// Runs on the isolated core node. Claims withdrawals whose delay window has elapsed, decrypts
/// the owner's wallet key (via the envelope cipher — Vault in prod), hands it to the signer, and
/// records the outcome in the audit log. The public API never runs this.
/// </summary>
public sealed class WithdrawalExecutorService : BackgroundService
{
    private readonly WithdrawalsRepository _wd;
    private readonly SecretsRepository _secrets;
    private readonly AuditRepository _audit;
    private readonly IEnvelopeCipher _cipher;
    private readonly IWithdrawalSigner _signer;
    private readonly ILogger<WithdrawalExecutorService> _log;
    private readonly TimeSpan _tick;

    public WithdrawalExecutorService(WithdrawalsRepository wd, SecretsRepository secrets, AuditRepository audit,
        IEnvelopeCipher cipher, IWithdrawalSigner signer, IConfiguration cfg, ILogger<WithdrawalExecutorService> log)
    {
        _wd = wd; _secrets = secrets; _audit = audit; _cipher = cipher; _signer = signer; _log = log;
        _tick = TimeSpan.FromSeconds(int.TryParse(cfg["EXECUTOR_TICK_SECONDS"], out var t) ? t : 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("WithdrawalExecutor started (tick={Tick}s, signer={Signer})", _tick.TotalSeconds, _signer.GetType().Name);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "executor tick failed"); }
            try { await Task.Delay(_tick, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        foreach (var w in await _wd.ClaimDueAsync(20, ct))
        {
            try
            {
                var chain = AssetToChain(w.Asset);
                var secret = await _secrets.FindForUserAsync(w.UserId, "wallet_pk", chain, ct);
                if (secret is null)
                {
                    await Fail(w, "no wallet key on file", ct);
                    continue;
                }

                // Decrypt just-in-time. (String is immutable so it can't be zeroed — a production
                // MPC signer never materializes the whole key; this is the skeleton boundary.)
                var pk = await _cipher.DecryptAsync(secret.Ciphertext, secret.WrappedDek, ct);
                var tx = await _signer.SendAsync(pk, w.Asset, w.Amount, w.ToAddress, ct);

                await _wd.CompleteAsync(w.Id, "done", ct);
                await _audit.WriteAsync(w.UserId, "executor", "withdrawal_executed",
                    JsonSerializer.Serialize(new { id = w.Id, w.Asset, w.Amount, w.ToAddress, tx }), null, ct);
                _log.LogInformation("withdrawal {Id}: {Amount} {Asset} -> {To} (tx={Tx})", w.Id, w.Amount, w.Asset, w.ToAddress, tx);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "withdrawal {Id} failed", w.Id);
                await Fail(w, ex.Message, ct);
            }
        }
    }

    private async Task Fail(DueWithdrawal w, string reason, CancellationToken ct)
    {
        await _wd.CompleteAsync(w.Id, "failed", ct);
        await _audit.WriteAsync(w.UserId, "executor", "withdrawal_failed",
            JsonSerializer.Serialize(new { id = w.Id, reason }), null, ct);
    }

    private static string AssetToChain(string asset) => asset.Trim().ToUpperInvariant() switch
    {
        "ETH" or "WETH" => "eth",
        "BNB" => "bsc",
        "MATIC" or "POL" => "polygon",
        "SOL" => "solana",
        "AVAX" => "avax",
        _ => asset.Trim().ToLowerInvariant()
    };
}
