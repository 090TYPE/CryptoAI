using Microsoft.Data.Sqlite;

namespace CryptoAITerminal.LicenseBot;

public sealed record CustomerRow(long TelegramId, string? Username, string FullName, DateTime CreatedUtc);

public sealed record OrderRow(
    long Id, long TelegramId, string PlanCode, string Edition,
    int Stars, string Currency, string? ChargeId, string LicenseKey,
    DateTime? Expires, string? Machine, string Status, DateTime CreatedUtc);

/// <summary>
/// SQLite-backed customer + order/license store. Single file, no server.
/// </summary>
public sealed class Store
{
    private readonly string _connStr;

    public Store(string dbPath)
    {
        _connStr = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        Init();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        return c;
    }

    private void Init()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS customers (
                telegram_id INTEGER PRIMARY KEY,
                username    TEXT,
                full_name   TEXT NOT NULL,
                machine_id  TEXT,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS orders (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                telegram_id INTEGER NOT NULL,
                plan_code   TEXT NOT NULL,
                edition     TEXT NOT NULL,
                stars       INTEGER NOT NULL,
                currency    TEXT NOT NULL,
                charge_id   TEXT,
                license_key TEXT NOT NULL,
                expires     TEXT,
                machine     TEXT,
                status      TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_orders_tg ON orders(telegram_id);
            """;
        cmd.ExecuteNonQuery();

        // Migration for older DBs created before machine binding.
        try
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE customers ADD COLUMN machine_id TEXT;";
            alter.ExecuteNonQuery();
        }
        catch { /* column already exists */ }
    }

    public void SetCustomerMachine(long telegramId, string machineId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE customers SET machine_id=$m WHERE telegram_id=$id;";
        cmd.Parameters.AddWithValue("$m", machineId);
        cmd.Parameters.AddWithValue("$id", telegramId);
        cmd.ExecuteNonQuery();
    }

    public string? GetCustomerMachine(long telegramId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT machine_id FROM customers WHERE telegram_id=$id;";
        cmd.Parameters.AddWithValue("$id", telegramId);
        var v = cmd.ExecuteScalar();
        return v is string s && !string.IsNullOrWhiteSpace(s) ? s : null;
    }

    public void UpsertCustomer(long telegramId, string? username, string fullName)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO customers (telegram_id, username, full_name, created_utc)
            VALUES ($id, $u, $n, $t)
            ON CONFLICT(telegram_id) DO UPDATE SET username=$u, full_name=$n;
            """;
        cmd.Parameters.AddWithValue("$id", telegramId);
        cmd.Parameters.AddWithValue("$u", (object?)username ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", fullName);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public long AddOrder(OrderRow o)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO orders
                (telegram_id, plan_code, edition, stars, currency, charge_id, license_key, expires, machine, status, created_utc)
            VALUES ($tg, $pc, $ed, $st, $cur, $ch, $key, $exp, $mac, $status, $created);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$tg", o.TelegramId);
        cmd.Parameters.AddWithValue("$pc", o.PlanCode);
        cmd.Parameters.AddWithValue("$ed", o.Edition);
        cmd.Parameters.AddWithValue("$st", o.Stars);
        cmd.Parameters.AddWithValue("$cur", o.Currency);
        cmd.Parameters.AddWithValue("$ch", (object?)o.ChargeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$key", o.LicenseKey);
        cmd.Parameters.AddWithValue("$exp", (object?)o.Expires?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mac", (object?)o.Machine ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", o.Status);
        cmd.Parameters.AddWithValue("$created", o.CreatedUtc.ToString("o"));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Mark a pending order paid and attach the issued license key.</summary>
    public void MarkOrderPaid(long id, string licenseKey, string? chargeId = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE orders SET status='paid', license_key=$key, charge_id=COALESCE($ch, charge_id) WHERE id=$id;";
        cmd.Parameters.AddWithValue("$key", licenseKey);
        cmd.Parameters.AddWithValue("$ch", (object?)chargeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public OrderRow? GetOrder(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM orders WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadOrders(cmd).FirstOrDefault();
    }

    public List<OrderRow> GetOrders(long telegramId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM orders WHERE telegram_id=$tg ORDER BY id DESC;";
        cmd.Parameters.AddWithValue("$tg", telegramId);
        return ReadOrders(cmd);
    }

    public List<OrderRow> RecentOrders(int limit = 20)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM orders ORDER BY id DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$l", limit);
        return ReadOrders(cmd);
    }

    public (int Customers, int PaidOrders, long StarsTotal) Stats()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM customers),
              (SELECT COUNT(*) FROM orders WHERE status='paid'),
              (SELECT COALESCE(SUM(stars),0) FROM orders WHERE status='paid');
            """;
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1), r.GetInt64(2)) : (0, 0, 0L);
    }

    public int CustomerCount()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM customers;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static List<OrderRow> ReadOrders(SqliteCommand cmd)
    {
        var list = new List<OrderRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new OrderRow(
                Id:        r.GetInt64(r.GetOrdinal("id")),
                TelegramId:r.GetInt64(r.GetOrdinal("telegram_id")),
                PlanCode:  r.GetString(r.GetOrdinal("plan_code")),
                Edition:   r.GetString(r.GetOrdinal("edition")),
                Stars:     r.GetInt32(r.GetOrdinal("stars")),
                Currency:  r.GetString(r.GetOrdinal("currency")),
                ChargeId:  GetNullableString(r, "charge_id"),
                LicenseKey:r.GetString(r.GetOrdinal("license_key")),
                Expires:   ParseDate(GetNullableString(r, "expires")),
                Machine:   GetNullableString(r, "machine"),
                Status:    r.GetString(r.GetOrdinal("status")),
                CreatedUtc:ParseDate(r.GetString(r.GetOrdinal("created_utc"))) ?? DateTime.UtcNow));
        }
        return list;
    }

    private static string? GetNullableString(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
}
