using Microsoft.Extensions.Configuration;

namespace CryptoAITerminal.LicenseBot;

/// <summary>
/// Plan offered for sale.
///
/// Priced in dollars because the cost side is in dollars: the AI spend behind every tier is billed
/// per token by the vendor. Pricing in roubles meant the margin moved with the exchange rate on
/// its own, and at 120 ₽/$ the cheapest tier stopped covering its own allowance.
///
/// <paramref name="UsdPrice"/> is the number on the price list; the rouble figure shown next to it
/// is derived from <see cref="BotConfig.UsdRubRate"/> at display time, so a rate change does not
/// need a redeploy and cannot leave the two prices contradicting each other.
///
/// Telegram Stars are gone. They paid out roughly a third of what the same tier fetched in crypto,
/// so the same product had two prices that differed by 3x depending on which button was pressed.
/// </summary>
public sealed record Plan(string Code, string Title, string Description, decimal UsdPrice, int Days, string Edition);

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

    /// <summary>
    /// Rate used to show a rouble price next to the dollar one. Overridable via USD_RUB_RATE so a
    /// move in the exchange rate is a config change, not a release.
    /// </summary>
    public decimal UsdRubRate { get; init; } = 80m;

    /// <summary>
    /// Weekly tiers. Что их разделяет — набор функций: биржи, DEX-снайпер, режим агента.
    ///
    /// Суточный лимит токенов в описаниях НЕ упоминается намеренно: на время тестового периода он
    /// снят (<c>SettingKeys.AiBudgetEnforced</c>), и продавать число, которое сервер не проверяет,
    /// значит писать в прайсе неправду. Прежние 35k / 70k / 105k были взяты до появления агента и
    /// панелей с длинным контекстом; когда квоты вернутся, числа придут из накопленного
    /// <c>ai_usage</c>, и описания надо будет дополнить вместе с ними.
    ///
    /// No perpetual plan. A lifetime licence bought by an active trader paid for itself in about
    /// thirteen months of AI spend and cost money every month after that, with no way to stop.
    /// </summary>
    public static readonly IReadOnlyList<Plan> DefaultPlans =
    [
        //             code           title           description                                                          $    дней  edition
        new("lite_week", "Lite · неделя", "Терминал, одна биржа, бумажная и живая торговля, базовые AI-вердикты.",  5m,   7, "Lite"),
        new("pro_week",  "Pro · неделя",  "Все биржи, DEX-снайпер, AI Signal Desk, ранжирование рынка, агент в бумажном режиме.", 10m,  7, "Pro"),
        new("max_week",  "Max · неделя",  "Всё из Pro плюс агент в боевом режиме, приоритет и ежедневное портфельное ревью.",     15m,  7, "Max"),

        // Monthly, at a 17% discount against four weeks. The weekly tier is the way in; this is
        // what keeps someone from having to remember to pay 52 times a year.
        new("lite_month", "Lite · месяц", "То же, что Lite, выгоднее на 17%.", 18m, 30, "Lite"),
        new("pro_month",  "Pro · месяц",  "То же, что Pro, выгоднее на 17%.",  36m, 30, "Pro"),
        new("max_month",  "Max · месяц",  "То же, что Max, выгоднее на 17%.",  54m, 30, "Max"),
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
            // USD, not RUB: the invoice is quoted in the currency the price list is written in, so
            // the amount charged cannot drift from the advertised price when the rate moves.
            CryptoFiat       = Env("CRYPTO_FIAT") ?? cfg["CryptoFiat"] ?? "USD",
            UsdRubRate       = decimal.TryParse(Env("USD_RUB_RATE") ?? cfg["UsdRubRate"],
                                   System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var rate) && rate > 0 ? rate : 80m,
        };
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
