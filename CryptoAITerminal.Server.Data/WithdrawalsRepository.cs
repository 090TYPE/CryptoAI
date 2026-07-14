using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// Withdrawal requests. Every request carries a delay window (execute_after_utc): it stays
/// 'pending' and cancellable until then, so a drain attempt is visible and reversible before
/// the executor acts. This is the key compensating control for a custodial hot wallet.
/// </summary>
/// <summary>A withdrawal the executor has claimed for processing.</summary>
public sealed record DueWithdrawal(Guid Id, Guid UserId, string Asset, decimal Amount, string ToAddress);

public sealed class WithdrawalsRepository
{
    private readonly Db _db;
    public WithdrawalsRepository(Db db) => _db = db;

    public async Task<Guid> CreateAsync(Guid userId, string asset, decimal amount, string toAddress,
        DateTime executeAfterUtc, string? ip, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO withdrawals (user_id, asset, amount, to_address, status, execute_after_utc, audit_ip)
            VALUES (@userId, @asset, @amount, @toAddress, 'pending', @executeAfterUtc, @ip::inet)
            RETURNING id;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql,
            new { userId, asset, amount, toAddress, executeAfterUtc, ip }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<WithdrawalView>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = @"SELECT id AS Id, asset AS Asset, amount AS Amount, to_address AS ToAddress,
                                    status AS Status, requested_utc AS RequestedUtc, execute_after_utc AS ExecuteAfterUtc
                             FROM withdrawals WHERE user_id = @userId ORDER BY requested_utc DESC;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<WithdrawalView>(new CommandDefinition(sql, new { userId }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Executor: atomically claim requests whose delay window has passed (pending → executing),
    /// so the user can no longer cancel and two executors never grab the same row.
    /// </summary>
    public async Task<IReadOnlyList<DueWithdrawal>> ClaimDueAsync(int batch, CancellationToken ct = default)
    {
        const string sql = @"
            WITH due AS (
                SELECT id FROM withdrawals
                WHERE status = 'pending' AND execute_after_utc <= now()
                ORDER BY execute_after_utc
                LIMIT @batch
                FOR UPDATE SKIP LOCKED
            )
            UPDATE withdrawals w SET status = 'executing'
            FROM due WHERE w.id = due.id
            RETURNING w.id AS Id, w.user_id AS UserId, w.asset AS Asset, w.amount AS Amount, w.to_address AS ToAddress;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<DueWithdrawal>(new CommandDefinition(sql, new { batch }, cancellationToken: ct))).ToList();
    }

    /// <summary>Executor: finalize a claimed request ('done' | 'failed' | 'held').</summary>
    public async Task CompleteAsync(Guid id, string status, CancellationToken ct = default)
    {
        const string sql = @"UPDATE withdrawals SET status = @status WHERE id = @id;";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { id, status }, cancellationToken: ct));
    }

    /// <summary>Cancel a still-pending request. Returns 1 if cancelled, 0 if already executing/done/not found.</summary>
    public async Task<int> CancelAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        const string sql = @"UPDATE withdrawals SET status = 'cancelled'
                             WHERE id = @id AND user_id = @userId AND status = 'pending';";
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { id, userId }, cancellationToken: ct));
    }
}
