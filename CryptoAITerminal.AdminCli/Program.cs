using System.Text;
using CryptoAITerminal.Server.Data;
using Dapper;
using Npgsql;

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected */ }

// ── Connection ────────────────────────────────────────────────────────────────
var connStr = Environment.GetEnvironmentVariable("DB_CONN")
           ?? (args.Length > 0 ? args[0] : null)
           ?? "Host=localhost;Port=5432;Database=cryptoai;Username=postgres;Password=postgres";

await using var db = new Db(connStr);
NpgsqlConnection sql;
try { sql = await db.OpenConnectionAsync(); }
catch (Exception ex)
{
    Console.WriteLine($"Cannot connect: {ex.Message}\nSet DB_CONN or pass the connection string as arg 1.");
    return 1;
}

var store = new ProviderKeyStore(db);

// ── Main menu loop ────────────────────────────────────────────────────────────
while (true)
{
    ClearScreen();
    Banner();
    Menu("MAIN MENU", new[]
    {
        "Status overview",
        "Status (live)",
        "Collectors (24/7)",
        "Tracked tokens       ▶",
        "Provider keys        ▶",
        "Custodial secrets",
        "Withdrawals          ▶",
        "Bots                 ▶",
        "Audit log",
        "News",
        "Gas",
        "Sentiment",
        "On-chain metrics",
        "Users",
        "Price alerts",
    });
    var choice = Ask("Select");

    try
    {
        switch (choice)
        {
            case "0" or "q" or "quit" or "exit": Console.WriteLine("bye."); return 0;

            case "1": await View("STATUS", Status); break;
            case "2": await StatusLive(); break;
            case "3": await ViewSql("COLLECTORS", "SELECT name, last_ok AS ok, items, coalesce(left(last_error,28),'') AS error, last_run_utc AS last_run FROM collector_runs ORDER BY name"); break;
            case "4": await TokensMenu(); break;
            case "5": await KeysMenu(); break;
            case "6": await ViewSql("CUSTODIAL SECRETS (metadata only)", @"SELECT s.id, u.license_name AS ""user"", s.kind, coalesce(s.label,'') AS label, s.exchange_or_chain AS target, coalesce(s.permissions,'') AS perms, s.created_utc AS created FROM secrets s JOIN users u ON u.id=s.user_id ORDER BY s.created_utc DESC LIMIT 100"); break;
            case "7": await WithdrawalsMenu(); break;
            case "8": await BotsMenu(); break;
            case "9": await ViewSql("AUDIT LOG (last 30)", "SELECT ts, coalesce(actor,'') AS actor, action, coalesce(left(detail::text,44),'') AS detail FROM audit_log ORDER BY ts DESC LIMIT 30"); break;
            case "10": await ViewSql("NEWS (latest 20)", "SELECT source, left(title,58) AS title, published_utc AS published FROM news ORDER BY coalesce(published_utc,fetched_utc) DESC LIMIT 20"); break;
            case "11": await ViewSql("GAS (gwei)", "SELECT DISTINCT ON (chain) chain, round(standard,3) AS gwei, ts FROM gas_prices ORDER BY chain, ts DESC"); break;
            case "12": await ViewSql("SENTIMENT", "SELECT metric, value, label, ts FROM sentiment ORDER BY ts DESC LIMIT 5"); break;
            case "13": await ViewSql("ON-CHAIN METRICS", "SELECT DISTINCT ON (asset,metric) asset, metric, round(value,0) AS value, ts FROM onchain_metrics ORDER BY asset, metric, ts DESC LIMIT 20"); break;
            case "14": await ViewSql("USERS", "SELECT license_name AS license, email, created_utc AS created FROM users ORDER BY created_utc DESC LIMIT 100"); break;
            case "15": await ViewSql("PRICE ALERTS", @"SELECT a.id, u.license_name AS ""user"", a.symbol, a.condition, a.threshold, a.status, a.triggered_price AS ""triggered@"" FROM price_alerts a JOIN users u ON u.id=a.user_id ORDER BY a.created_utc DESC LIMIT 100"); break;

            default: Warn("pick a number from the menu."); Pause(); break;
        }
    }
    catch (Exception ex) { Warn("error: " + ex.Message); Pause(); }
}

// ── Provider keys submenu ─────────────────────────────────────────────────────
async Task KeysMenu()
{
    while (true)
    {
        ClearScreen();
        Header("PROVIDER KEYS");
        await Show("SELECT provider, enabled, CASE WHEN api_key='' THEN '-' ELSE '****'||right(api_key,4) END AS key, coalesce(note,'') AS note FROM provider_keys ORDER BY provider");
        Menu("KEY ACTIONS", new[]
        {
            "Set / change a key",
            "Reveal a key value",
            "Enable a key",
            "Disable a key",
            "Clear (remove) a key",
        });
        switch (Ask("Select"))
        {
            case "0": return;
            case "1":
            {
                var p = Ask("  provider"); if (p.Length == 0) break;
                var v = Ask("  api key");   if (v.Length == 0) break;
                await store.SetAsync(p, v, enabled: true); Ok($"'{p}' set + enabled"); Pause(); break;
            }
            case "2":
            {
                var p = Ask("  provider");
                var k = await sql.ExecuteScalarAsync<string?>("SELECT api_key FROM provider_keys WHERE provider=@p", new { p });
                if (k is null) Warn($"no such provider '{p}'");
                else Console.WriteLine($"\n  {p} = {(k.Length == 0 ? "(not set)" : k)}");
                Pause(); break;
            }
            case "3": { var p = Ask("  provider"); await Toggle(p, true); Pause(); break; }
            case "4": { var p = Ask("  provider"); await Toggle(p, false); Pause(); break; }
            case "5":
            {
                var p = Ask("  provider");
                var n = await sql.ExecuteAsync("UPDATE provider_keys SET api_key='', enabled=false, updated_utc=now() WHERE provider=@p", new { p });
                if (n > 0) Ok($"'{p}' cleared"); else Warn($"no such provider '{p}'");
                Pause(); break;
            }
            default: Warn("pick a number."); Pause(); break;
        }
    }
}

async Task Toggle(string p, bool on)
{
    var n = await sql.ExecuteAsync("UPDATE provider_keys SET enabled=@on, updated_utc=now() WHERE provider=@p", new { p, on });
    if (n > 0) Ok($"'{p}' {(on ? "enabled" : "disabled")}"); else Warn($"no such provider '{p}'");
}

// ── Withdrawals submenu ───────────────────────────────────────────────────────
async Task WithdrawalsMenu()
{
    while (true)
    {
        ClearScreen();
        Header("WITHDRAWALS");
        await Show(@"SELECT w.id, u.license_name AS ""user"", asset, amount, left(to_address,14) AS ""to"", status, execute_after_utc AS execute_after FROM withdrawals w JOIN users u ON u.id=w.user_id ORDER BY requested_utc DESC LIMIT 50");
        Menu("WITHDRAWAL ACTIONS", new[] { "Cancel a pending withdrawal" });
        switch (Ask("Select"))
        {
            case "0": return;
            case "1":
            {
                var id = Ask("  withdrawal id");
                if (!Guid.TryParse(id, out var g)) { Warn("bad id"); Pause(); break; }
                var n = await sql.ExecuteAsync("UPDATE withdrawals SET status='cancelled' WHERE id=@g AND status='pending'", new { g });
                if (n > 0) Ok("cancelled"); else Warn("not cancellable");
                Pause(); break;
            }
            default: Warn("pick a number."); Pause(); break;
        }
    }
}

// ── Tracked tokens submenu ────────────────────────────────────────────────────
async Task TokensMenu()
{
    while (true)
    {
        ClearScreen();
        Header("TRACKED TOKENS");
        await Show("SELECT chain, symbol, fav_count AS favs, is_active AS active, source, coalesce(left(pool_address,12),'') AS pool FROM tracked_tokens ORDER BY is_active DESC, fav_count DESC, symbol LIMIT 100");
        Menu("TOKEN ACTIONS", new[]
        {
            "Add a token to 24/7 tracking",
            "Stop tracking a token",
            "Resume tracking a token",
        });
        switch (Ask("Select"))
        {
            case "0": return;
            case "1":
            {
                var c = Ask("  chain (eth/bsc/base/solana/...)"); if (c.Length == 0) break;
                var t = Ask("  token address"); if (t.Length == 0) break;
                var s = Ask("  symbol");
                await sql.ExecuteAsync(@"
                    INSERT INTO tracked_tokens (chain, token_address, symbol, source, is_active, next_poll_utc)
                    VALUES (@c, @t, @s, 'manual', true, now())
                    ON CONFLICT (chain, token_address) DO UPDATE SET is_active=true, next_poll_utc=now(),
                        symbol=COALESCE(NULLIF(EXCLUDED.symbol,''), tracked_tokens.symbol)",
                    new { c, t, s });
                Ok($"tracking {(s.Length > 0 ? s : t)} on {c}"); Pause(); break;
            }
            case "2":
            {
                var c = Ask("  chain"); var t = Ask("  token address");
                var n = await sql.ExecuteAsync("UPDATE tracked_tokens SET is_active=false WHERE chain=@c AND token_address=@t", new { c, t });
                if (n > 0) Ok("stopped"); else Warn("not found"); Pause(); break;
            }
            case "3":
            {
                var c = Ask("  chain"); var t = Ask("  token address");
                var n = await sql.ExecuteAsync("UPDATE tracked_tokens SET is_active=true, next_poll_utc=now() WHERE chain=@c AND token_address=@t", new { c, t });
                if (n > 0) Ok("resumed"); else Warn("not found"); Pause(); break;
            }
            default: Warn("pick a number."); Pause(); break;
        }
    }
}

// ── Bots submenu ──────────────────────────────────────────────────────────────
async Task BotsMenu()
{
    while (true)
    {
        ClearScreen();
        Header("BOTS");
        await Show(@"SELECT b.id, u.license_name AS ""user"", strategy, enabled, updated_utc AS updated FROM bot_configs b JOIN users u ON u.id=b.user_id ORDER BY updated_utc DESC LIMIT 100");
        Menu("BOT ACTIONS", new[] { "Enable a bot", "Disable a bot", "Delete a bot" });
        var pick = Ask("Select");
        if (pick == "0") return;
        if (pick is not ("1" or "2" or "3")) { Warn("pick a number."); Pause(); continue; }

        var id = Ask("  bot id");
        if (!Guid.TryParse(id, out var g)) { Warn("bad id"); Pause(); continue; }
        var n = pick switch
        {
            "1" => await sql.ExecuteAsync("UPDATE bot_configs SET enabled=true, updated_utc=now() WHERE id=@g", new { g }),
            "2" => await sql.ExecuteAsync("UPDATE bot_configs SET enabled=false, updated_utc=now() WHERE id=@g", new { g }),
            _   => await sql.ExecuteAsync("DELETE FROM bot_configs WHERE id=@g", new { g }),
        };
        if (n > 0) Ok(pick == "1" ? "enabled" : pick == "2" ? "disabled" : "deleted"); else Warn("not found");
        Pause();
    }
}

// ── Live status (auto-refresh; press a key to stop) ───────────────────────────
async Task StatusLive()
{
    while (true)
    {
        ClearScreen();
        Header("STATUS (live — press any key to stop)");
        await Status();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n  refreshed {DateTime.Now:HH:mm:ss}");
        Console.ResetColor();
        try
        {
            for (var i = 0; i < 20; i++)
            {
                if (Console.KeyAvailable) { Console.ReadKey(true); return; }
                await Task.Delay(100);
            }
        }
        catch { return; } // redirected console — single pass
    }
}

// ── Views ─────────────────────────────────────────────────────────────────────
async Task ViewSql(string title, string query)
{
    ClearScreen(); Header(title);
    await Show(query);
    Pause();
}

async Task View(string title, Func<Task> body)
{
    ClearScreen(); Header(title);
    await body();
    Pause();
}

async Task Status()
{
    var s = (IDictionary<string, object>)await sql.QuerySingleAsync<dynamic>(@"
        SELECT (SELECT count(*) FROM users) AS users,
               (SELECT count(*) FROM tracked_tokens WHERE is_active) AS active_tokens,
               (SELECT count(*) FROM dex_candles) AS candles,
               (SELECT count(*) FROM secrets) AS secrets,
               (SELECT count(*) FROM withdrawals WHERE status='pending') AS pending_withdrawals,
               (SELECT count(*) FROM provider_keys WHERE enabled) AS active_keys,
               (SELECT count(*) FROM collector_runs WHERE last_ok) AS ok_collectors,
               (SELECT count(*) FROM collector_runs WHERE NOT last_ok) AS failing_collectors,
               (SELECT count(*) FROM news) AS news_items");
    foreach (var kv in s) Console.WriteLine($"  {kv.Key,-22} {kv.Value}");
}

// ── Generic table renderer ────────────────────────────────────────────────────
async Task Show(string query, object? p = null)
{
    var rows = (await sql.QueryAsync(query, p)).Cast<IDictionary<string, object>>().ToList();
    if (rows.Count == 0) { Console.WriteLine("  (empty)\n"); return; }

    var cols = rows[0].Keys.ToList();
    var w = cols.ToDictionary(c => c, c => Math.Max(c.Length, rows.Max(r => (r[c]?.ToString() ?? "").Length)));
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("  " + string.Join("  ", cols.Select(c => c.PadRight(w[c]))));
    Console.ResetColor();
    Console.WriteLine("  " + string.Join("  ", cols.Select(c => new string('-', w[c]))));
    foreach (var r in rows)
        Console.WriteLine("  " + string.Join("  ", cols.Select(c => (r[c]?.ToString() ?? "").PadRight(w[c]))));
    Console.WriteLine($"  ({rows.Count} rows)\n");
}

// ── UI helpers ────────────────────────────────────────────────────────────────
void Menu(string title, string[] items)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  == {title} ==");
    Console.ResetColor();
    for (var i = 0; i < items.Length; i++)
        Console.WriteLine($"   {i + 1,2}) {items[i]}");
    Console.WriteLine("    0) Back / Exit");
}

string Ask(string label)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Write($"\n  {label}: ");
    Console.ResetColor();
    return (Console.ReadLine() ?? "0").Trim();
}

void Pause()
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write("\n  -- press Enter to continue --");
    Console.ResetColor();
    Console.ReadLine();
}

void Ok(string s) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("  OK: " + s); Console.ResetColor(); }
void Warn(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("  ! " + s); Console.ResetColor(); }

void ClearScreen() { try { Console.Clear(); } catch { Console.WriteLine("\n\n"); } }

void Header(string t)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  === {t} ===\n");
    Console.ResetColor();
}

void Banner()
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("  CryptoAI  —  server admin console");
    Console.ResetColor();
}
