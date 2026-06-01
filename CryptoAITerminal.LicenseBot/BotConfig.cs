using Microsoft.Extensions.Configuration;

namespace CryptoAITerminal.LicenseBot;

/// <summary>
/// Plan offered for sale. <paramref name="Stars"/> is the Telegram Stars price,
/// <paramref name="RubPrice"/> is the fiat price charged via crypto (customer pays
/// the crypto equivalent). Days=0 → perpetual.
/// </summary>
public sealed record Plan(string Code, string Title, string Description, int Stars, decimal RubPrice, int Days, string Edition);

public sealed class BotConfig
{
    public string BotToken { get; init; } = "";
    public string PrivateKeyPem { get; init; } = "";
    public string Currency { get; init; } = "XTR";              // Telegram Stars
    public string ProviderToken { get; init; } = "";            // empty for Stars
    public string DbPath { get; init; } = "licensebot.db";
    public long[] AdminIds { get; init; } = [];
    public IReadOnlyList<Plan> Plans { get; init; } = DefaultPlans;

    // ── Crypto payments (Crypto Pay API / @CryptoBot) ────────────────────────
    public string CryptoPayToken { get; init; } = "";           // app token from @CryptoBot
    public string CryptoPayAssets { get; init; } = "USDT,TON";  // accepted crypto assets
    public string CryptoPayApiBase { get; init; } = "https://pay.crypt.bot/api/";
    public string CryptoFiat { get; init; } = "RUB";            // fiat the price is quoted in
    public bool CryptoEnabled => !string.IsNullOrWhiteSpace(CryptoPayToken);

    public static readonly IReadOnlyList<Plan> DefaultPlans =
    [
        //                code          title              description                                  stars   ₽       days  edition
        new("lite_month",  "Lite · 1 month",  "Core terminal, 1 exchange, paper + live.",  250,    990m,   30,  "Lite"),
        new("pro_month",   "Pro · 1 month",   "All exchanges, DEX sniper, AI verdicts.",   600,    2000m,  30,  "Pro"),
        new("pro_year",    "Pro · 1 year",    "Pro, best value — 12 months.",              5000,   18000m, 365, "Pro"),
        new("pro_life",    "Pro · lifetime",  "Pay once, perpetual Pro license.",          12000,  40000m, 0,   "Pro"),
    ];

    public bool IsAdmin(long userId) => Array.IndexOf(AdminIds, userId) >= 0;

    public Plan? FindPlan(string code) =>
        Plans.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Loads from appsettings.json then environment variables (env wins).
    /// Env: BOT_TOKEN, LICENSE_PRIVATE_KEY (PEM) or LICENSE_PRIVATE_KEY_PATH,
    /// BOT_ADMIN_IDS (comma-separated), BOT_DB_PATH, BOT_CURRENCY, BOT_PROVIDER_TOKEN.
    /// </summary>
    public static BotConfig Load()
    {
        var cfg = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var token = Env("BOT_TOKEN") ?? cfg["BotToken"] ?? "";

        var pem = Env("LICENSE_PRIVATE_KEY") ?? cfg["PrivateKeyPem"] ?? "";
        var pemPath = Env("LICENSE_PRIVATE_KEY_PATH") ?? cfg["PrivateKeyPath"];
        if (string.IsNullOrWhiteSpace(pem) && !string.IsNullOrWhiteSpace(pemPath) && File.Exists(pemPath))
            pem = File.ReadAllText(pemPath);

        var adminRaw = Env("BOT_ADMIN_IDS") ?? cfg["AdminIds"] ?? "";
        var admins = adminRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? id : 0L)
            .Where(id => id != 0)
            .ToArray();

        return new BotConfig
        {
            BotToken      = token,
            PrivateKeyPem = pem,
            Currency      = Env("BOT_CURRENCY") ?? cfg["Currency"] ?? "XTR",
            ProviderToken = Env("BOT_PROVIDER_TOKEN") ?? cfg["ProviderToken"] ?? "",
            DbPath        = Env("BOT_DB_PATH") ?? cfg["DbPath"] ?? "licensebot.db",
            AdminIds      = admins,
            CryptoPayToken   = Env("CRYPTOPAY_TOKEN") ?? cfg["CryptoPayToken"] ?? "",
            CryptoPayAssets  = Env("CRYPTOPAY_ASSETS") ?? cfg["CryptoPayAssets"] ?? "USDT,TON",
            CryptoPayApiBase = Env("CRYPTOPAY_API_BASE") ?? cfg["CryptoPayApiBase"] ?? "https://pay.crypt.bot/api/",
            CryptoFiat       = Env("CRYPTO_FIAT") ?? cfg["CryptoFiat"] ?? "RUB",
        };
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
