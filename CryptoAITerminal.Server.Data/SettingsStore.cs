using System.Globalization;
using Dapper;

namespace CryptoAITerminal.Server.Data;

public sealed record ServerSetting(string Key, string Value, DateTime UpdatedUtc, string? UpdatedBy);

public sealed record SettingChange(long Id, string Key, string? OldValue, string? NewValue, DateTime ChangedUtc, string? ChangedBy);

/// <summary>
/// Runtime-editable server configuration, read on the hot path.
///
/// The problem it solves: every knob the server has today is an environment variable captured at
/// startup — the AI model each background job runs on, poll cadence, anomaly thresholds, cache
/// TTLs, the daily token cap. Changing one means editing .env and restarting the contour. Worse,
/// for anything the CLIENT decides (the model on every AI call) a restart does not help at all,
/// because the value ships inside terminals that are already installed.
///
/// Reads go through a whole-table snapshot refreshed at most every <see cref="Ttl"/>. One query
/// per refresh regardless of how many keys are read, so a call site can ask for four settings
/// without four round trips, and a change reaches every caller within the TTL without anyone
/// signalling anyone. Writes refresh immediately, so the admin who just saved a value sees it
/// applied on the very next request rather than after a wait.
///
/// A missing key is not an error — it means "use the in-code default", which is what every
/// Get overload takes as its fallback. That keeps this store additive: nothing has to be seeded
/// for the server to run, and deleting a row is a supported way to revert.
/// </summary>
public sealed class SettingsStore
{
    /// <summary>
    /// How stale a read may be. Fifteen seconds is the trade: an edit is live "immediately" by
    /// any human measure, while a burst of AI calls costs at most four database queries a minute
    /// between them.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    private readonly Db _db;
    private readonly Func<DateTime> _clock;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedUtc = DateTime.MinValue;
    private volatile bool _tableMissing;

    public SettingsStore(Db db, Func<DateTime>? clock = null)
    {
        _db = db;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    /// <summary>The stored value, or <paramref name="fallback"/> when the key is unset or blank.</summary>
    public async Task<string?> GetAsync(string key, string? fallback = null, CancellationToken ct = default)
    {
        var snapshot = await SnapshotAsync(ct).ConfigureAwait(false);
        return snapshot.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
    }

    /// <summary>
    /// Integer setting. An unparsable or non-positive value falls back rather than throwing:
    /// a typo in the admin screen must not take the server down, and every numeric knob here
    /// (batch size, interval, cap) is meaningless at zero or below.
    /// </summary>
    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) =>
        AsInt(await GetAsync(key, null, ct).ConfigureAwait(false), fallback);

    public async Task<long> GetLongAsync(string key, long fallback, CancellationToken ct = default) =>
        AsLong(await GetAsync(key, null, ct).ConfigureAwait(false), fallback);

    /// <summary>Decimal setting. Invariant culture only — the admin API speaks one number format.</summary>
    public async Task<decimal> GetDecimalAsync(string key, decimal fallback, CancellationToken ct = default) =>
        AsDecimal(await GetAsync(key, null, ct).ConfigureAwait(false), fallback);

    /// <summary>
    /// Boolean setting. Accepts true/false, 1/0, on/off, yes/no — the admin screen is typed into
    /// by a human, and rejecting "on" because the parser wanted "true" is a support ticket.
    /// </summary>
    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default) =>
        AsBool(await GetAsync(key, null, ct).ConfigureAwait(false), fallback);

    // ── Coercion ─────────────────────────────────────────────────────────────
    // Static and public so the rules are testable without a database. Everything a human can type
    // into the admin screen arrives here as a string, and the one behaviour that matters is that
    // garbage falls back to the caller's default instead of throwing on the hot path.

    public static int AsInt(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    public static long AsLong(string? raw, long fallback) =>
        long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    // NumberStyles.Float, not Number: Number allows a thousands separator, so under the invariant
    // culture "15,5" parses as 155 rather than being rejected. An operator typing the European
    // decimal form for a 15% anomaly threshold would have got 155% — a threshold nothing can
    // cross, i.e. the detector silently switched off.
    public static decimal AsDecimal(string? raw, decimal fallback) =>
        decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    public static bool AsBool(string? raw, bool fallback) => raw?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "on" or "yes" => true,
        "false" or "0" or "off" or "no" => false,
        _ => fallback
    };

    // ── Writes ───────────────────────────────────────────────────────────────

    /// <summary>Create or update a setting and record the change. Refreshes the cache before returning.</summary>
    public async Task SetAsync(string key, string value, string? by = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.", nameof(key));

        const string sql = @"
            WITH prev AS (SELECT value FROM server_settings WHERE key = @key),
            upsert AS (
                INSERT INTO server_settings (key, value, updated_utc, updated_by)
                VALUES (@key, @value, now(), @by)
                ON CONFLICT (key) DO UPDATE
                  SET value = EXCLUDED.value, updated_utc = now(), updated_by = EXCLUDED.updated_by
            )
            INSERT INTO server_settings_audit (key, old_value, new_value, changed_by)
            SELECT @key, (SELECT value FROM prev), @value, @by;";

        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { key, value, by }, cancellationToken: ct)).ConfigureAwait(false);
        await ReloadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Remove a setting so the in-code default applies again. Recorded in the audit trail.</summary>
    public async Task<bool> DeleteAsync(string key, string? by = null, CancellationToken ct = default)
    {
        const string sql = @"
            WITH gone AS (DELETE FROM server_settings WHERE key = @key RETURNING value)
            INSERT INTO server_settings_audit (key, old_value, new_value, changed_by)
            SELECT @key, value, NULL, @by FROM gone
            RETURNING 1;";

        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        var removed = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql, new { key, by }, cancellationToken: ct)).ConfigureAwait(false);
        await ReloadAsync(ct).ConfigureAwait(false);
        return removed is not null;
    }

    // ── Admin listings ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ServerSetting>> ListAsync(CancellationToken ct = default)
    {
        const string sql = @"SELECT key AS Key, value AS Value, updated_utc AS UpdatedUtc, updated_by AS UpdatedBy
                             FROM server_settings ORDER BY key;";
        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ServerSetting>(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <summary>Recent changes, newest first. Optionally narrowed to one key.</summary>
    public async Task<IReadOnlyList<SettingChange>> HistoryAsync(string? key = null, int limit = 100, CancellationToken ct = default)
    {
        const string sql = @"SELECT id AS Id, key AS Key, old_value AS OldValue, new_value AS NewValue,
                                    changed_utc AS ChangedUtc, changed_by AS ChangedBy
                             FROM server_settings_audit
                             WHERE (@key IS NULL OR key = @key)
                             ORDER BY changed_utc DESC, id DESC
                             LIMIT @limit;";
        await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SettingChange>(
            new CommandDefinition(sql, new { key, limit = Math.Clamp(limit, 1, 1000) }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    // ── Cache ────────────────────────────────────────────────────────────────

    /// <summary>Drop the snapshot so the next read hits the database. For tests and for admin writes.</summary>
    public void Invalidate() => _loadedUtc = DateTime.MinValue;

    private async Task<Dictionary<string, string>> SnapshotAsync(CancellationToken ct)
    {
        if (_clock() - _loadedUtc < Ttl) return _cache;

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside the lock: a burst of concurrent readers at TTL expiry should cost
            // one query, not one per reader.
            if (_clock() - _loadedUtc < Ttl) return _cache;
            await ReloadCoreAsync(ct).ConfigureAwait(false);
            return _cache;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ReloadCoreAsync(ct).ConfigureAwait(false); }
        finally { _refreshLock.Release(); }
    }

    private async Task ReloadCoreAsync(CancellationToken ct)
    {
        try
        {
            const string sql = "SELECT key, value FROM server_settings;";
            await using var conn = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
            var rows = await conn.QueryAsync<(string Key, string Value)>(
                new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);

            var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in rows) next[k] = v;
            _cache = next;
            _tableMissing = false;
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Table not there yet: a box that has not run migration 021. Every caller supplies a
            // fallback, so the correct behaviour is to serve defaults quietly rather than fail
            // the request that happened to be first after a deploy. Logged once by the caller
            // through _tableMissing rather than on every read.
            if (!_tableMissing) _tableMissing = true;
            _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        _loadedUtc = _clock();
    }

    /// <summary>True when the settings table is absent — migration 021 has not been applied.</summary>
    public bool TableMissing => _tableMissing;
}
