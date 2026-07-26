using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Loads and persists exchange API credentials.
/// Priority: environment variable > saved file.
/// </summary>
public static class CredentialsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal", "api-credentials.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented              = true,
        PropertyNamingPolicy       = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition     = JsonIgnoreCondition.Never,
    };

    // ── Data model ────────────────────────────────────────────────────────────

    public sealed class AllCredentials
    {
        // ── Exchange keys ─────────────────────────────────────────────────────
        public string BinanceKey    { get; set; } = "";
        public string BinanceSecret { get; set; } = "";
        public string BybitKey      { get; set; } = "";
        public string BybitSecret   { get; set; } = "";
        public string OkxKey        { get; set; } = "";
        public string OkxSecret     { get; set; } = "";
        public string OkxPassphrase { get; set; } = "";
        public string KucoinKey        { get; set; } = "";
        public string KucoinSecret     { get; set; } = "";
        public string KucoinPassphrase { get; set; } = "";

        // ── AI provider (Claude / ChatGPT) ────────────────────────────────────
        public string AiProvider     { get; set; } = "";   // CRYPTOAI_AI_PROVIDER ("anthropic"/"openai")
        public string AnthropicApiKey { get; set; } = "";   // ANTHROPIC_API_KEY
        public string OpenAiApiKey    { get; set; } = "";   // OPENAI_API_KEY
        public string AnthropicModel  { get; set; } = "";   // CRYPTOAI_CLAUDE_MODEL
        public string OpenAiModel     { get; set; } = "";   // CRYPTOAI_OPENAI_MODEL

        // ── On-chain / blockchain data ────────────────────────────────────────
        public string EtherscanApiKey  { get; set; } = "";   // ETHERSCAN_API_KEY
        public string BscscanApiKey    { get; set; } = "";   // BSCSCAN_API_KEY
        public string AlchemyApiKey    { get; set; } = "";   // ALCHEMY_API_KEY
        public string GlassnodeApiKey  { get; set; } = "";   // GLASSNODE_API_KEY
        public string CoinglassApiKey  { get; set; } = "";   // COINGLASS_API_KEY
        public string TrongridApiKey   { get; set; } = "";   // TRONGRID_API_KEY

        // ── News / sentiment ──────────────────────────────────────────────────
        public string CryptoPanicApiKey { get; set; } = "";  // CRYPTOPANIC_API_KEY

        // ── DEX data providers ────────────────────────────────────────────────
        public string BirdeyeApiKey    { get; set; } = "";   // BIRDEYE_API_KEY
        public string CoinGeckoApiKey  { get; set; } = "";   // COINGECKO_API_KEY
        public string CovalentApiKey   { get; set; } = "";   // COVALENT_API_KEY
        public string MoralisApiKey    { get; set; } = "";   // MORALIS_API_KEY
        public string JupiterApiKey    { get; set; } = "";   // JUPITER_API_KEY   (optional)

        // ── Notifications ─────────────────────────────────────────────────────
        // Every channel the alert engine can deliver to. Before these slots existed the
        // Notifications tab was session-only: the fields were read from env vars at start-up
        // and whatever the user typed was gone on the next launch.
        public string TelegramBotToken { get; set; } = "";   // TELEGRAM_BOT_TOKEN
        public string TelegramChatId   { get; set; } = "";   // TELEGRAM_CHAT_ID
        public string DiscordWebhookUrl { get; set; } = "";  // DISCORD_WEBHOOK_URL
        public string NtfyTopic         { get; set; } = "";  // NTFY_TOPIC
        public string EmailHost     { get; set; } = "";      // EMAIL_SMTP_HOST
        public string EmailPort     { get; set; } = "";      // EMAIL_SMTP_PORT
        public string EmailUseSsl   { get; set; } = "";      // EMAIL_SMTP_SSL  ("true"/"false")
        public string EmailUsername { get; set; } = "";      // EMAIL_SMTP_USER
        public string EmailPassword { get; set; } = "";      // EMAIL_SMTP_PASS
        public string EmailFrom     { get; set; } = "";      // EMAIL_FROM
        public string EmailTo       { get; set; } = "";      // EMAIL_TO

        // ── Telegram account (app credentials from my.telegram.org) ───────────
        public string TelegramApiId   { get; set; } = "";    // TELEGRAM_API_ID
        public string TelegramApiHash { get; set; } = "";    // TELEGRAM_API_HASH

        // ── DEX execution ─────────────────────────────────────────────────────
        public string DexPrivateKey    { get; set; } = "";   // CRYPTOAI_DEX_PRIVATE_KEY

        // ── Affiliate / referral links (opens in browser) ─────────────────────
        public string BinanceAffiliateUrl { get; set; } = "";
        public string BybitAffiliateUrl   { get; set; } = "";
        public string OkxAffiliateUrl     { get; set; } = "";
        public string KucoinAffiliateUrl  { get; set; } = "";
    }

    /// <summary>Origin of a credential — governs the status badge in the UI.</summary>
    public enum CredentialSource
    {
        /// <summary>Neither env var nor saved file has a value.</summary>
        None,
        /// <summary>Loaded from the JSON file on disk.</summary>
        File,
        /// <summary>Active via environment variable (overrides any file value).</summary>
        Env,
    }

    public sealed class LoadResult
    {
        public AllCredentials   Credentials    { get; init; } = new();
        public CredentialSource BybitSource    { get; init; }
        public CredentialSource OkxSource      { get; init; }
        public CredentialSource BinanceSource  { get; init; }
        public CredentialSource KucoinSource   { get; init; }
    }

    // ── Integration keys ──────────────────────────────────────────────────────
    // Everything that is not an exchange key: data providers, the Telegram app
    // credentials and the DEX signing key. This table is the single source of truth for
    // the Settings desk — which env var carries the value, what stops working without
    // it, and whether a new value is picked up live or only on the next start.

    private const string AppliesLive    = "applies as soon as you save";
    private const string AppliesRestart = "restart the terminal to apply";

    /// <summary>Everything the Settings desk needs to render one non-exchange credential.</summary>
    /// <param name="Applies">Whether the reader picks a new value up live or caches it at start-up.</param>
    /// <param name="ConsoleUrl">Where the user creates the key — empty when there is no such page.</param>
    public sealed record IntegrationKey(
        string Id,
        string Group,
        string Label,
        string EnvVar,
        string Powers,
        string Applies,
        string ConsoleUrl,
        bool Secret,
        string Placeholder);

    private static readonly (IntegrationKey Meta, Func<AllCredentials, string> Read, Action<AllCredentials, string> Write)[] IntegrationTable =
    {
        (new("etherscan", "On-chain & wallets", "Etherscan", "ETHERSCAN_API_KEY",
             "Wallet transaction history, and the whale tracker's Ethereum feed.",
             "wallet history applies at once · the whale tracker takes a restart",
             "https://etherscan.io/myapikey", true, "paste the API key"),
         c => c.EtherscanApiKey, (c, v) => c.EtherscanApiKey = v),

        (new("bscscan", "On-chain & wallets", "BscScan", "BSCSCAN_API_KEY",
             "The whale tracker's BNB Chain feed.", AppliesRestart,
             "https://bscscan.com/myapikey", true, "paste the API key"),
         c => c.BscscanApiKey, (c, v) => c.BscscanApiKey = v),

        (new("alchemy", "On-chain & wallets", "Alchemy", "ALCHEMY_API_KEY",
             "USD pricing for whale transfers — without it transfers show token amounts only.",
             AppliesRestart, "https://dashboard.alchemy.com/", true, "paste the API key"),
         c => c.AlchemyApiKey, (c, v) => c.AlchemyApiKey = v),

        (new("trongrid", "On-chain & wallets", "TronGrid", "TRONGRID_API_KEY",
             "Tron balances and transaction history. Works unauthenticated at a much lower rate limit.",
             AppliesLive, "https://www.trongrid.io/dashboard", true, "paste the API key"),
         c => c.TrongridApiKey, (c, v) => c.TrongridApiKey = v),

        (new("covalent", "On-chain & wallets", "Covalent", "COVALENT_API_KEY",
             "The token-approvals scanner in the wallet workspace. Without it the list stays empty.",
             AppliesLive, "https://www.covalenthq.com/platform/", true, "paste the API key"),
         c => c.CovalentApiKey, (c, v) => c.CovalentApiKey = v),

        (new("glassnode", "Derivatives & metrics", "Glassnode", "GLASSNODE_API_KEY",
             "The on-chain metrics desk (SOPR, MVRV, exchange flows).", AppliesRestart,
             "https://studio.glassnode.com/settings/api", true, "paste the API key"),
         c => c.GlassnodeApiKey, (c, v) => c.GlassnodeApiKey = v),

        (new("coinglass", "Derivatives & metrics", "Coinglass", "COINGLASS_API_KEY",
             "Liquidation levels and aggregated open interest.", AppliesRestart,
             "https://www.coinglass.com/pricing", true, "paste the API key"),
         c => c.CoinglassApiKey, (c, v) => c.CoinglassApiKey = v),

        (new("cryptopanic", "News", "CryptoPanic", "CRYPTOPANIC_API_KEY",
             "The CryptoPanic feed on the news desk. The RSS sources keep working without it.",
             AppliesRestart, "https://cryptopanic.com/developers/api/", true, "paste the auth token"),
         c => c.CryptoPanicApiKey, (c, v) => c.CryptoPanicApiKey = v),

        (new("jupiter", "DEX", "Jupiter", "JUPITER_API_KEY",
             "Higher rate limits on Solana swap quotes. Optional — quotes work without a key.",
             AppliesRestart, "https://station.jup.ag/", true, "optional"),
         c => c.JupiterApiKey, (c, v) => c.JupiterApiKey = v),

        (new("dexPrivateKey", "DEX", "DEX wallet private key", "CRYPTOAI_DEX_PRIVATE_KEY",
             "Imports a BSC signing wallet at start-up so DEX bots can swap unattended. " +
             "Keep only a working balance on it — anything this wallet holds can be spent by the terminal.",
             AppliesRestart, "", true, "0x… — leave empty to import the wallet by hand instead"),
         c => c.DexPrivateKey, (c, v) => c.DexPrivateKey = v),

        (new("telegramApiId", "Telegram account", "api_id", "TELEGRAM_API_ID",
             "Connecting your own Telegram account to read signals from channels you are in.",
             AppliesLive, "https://my.telegram.org/apps", false, "e.g. 1234567"),
         c => c.TelegramApiId, (c, v) => c.TelegramApiId = v),

        (new("telegramApiHash", "Telegram account", "api_hash", "TELEGRAM_API_HASH",
             "Pairs with api_id — both are needed before the account can log in.",
             AppliesLive, "https://my.telegram.org/apps", true, "32-character hash"),
         c => c.TelegramApiHash, (c, v) => c.TelegramApiHash = v),

        // Documented in README/SETUP and exported to the environment, but no code path in
        // this build reads them. Shown so a saved value is visible rather than silently
        // ignored — the hint says plainly that nothing consumes it yet.
        (new("moralis", "Documented, not read by this build", "Moralis", "MORALIS_API_KEY",
             "Listed for DEX OHLCV candles. Nothing in this build reads it — saving only exports the env var.",
             AppliesRestart, "https://admin.moralis.io/", true, "paste the API key"),
         c => c.MoralisApiKey, (c, v) => c.MoralisApiKey = v),

        (new("birdeye", "Documented, not read by this build", "Birdeye", "BIRDEYE_API_KEY",
             "Listed for Solana DEX analytics. Nothing in this build reads it — saving only exports the env var.",
             AppliesRestart, "https://bds.birdeye.so/", true, "paste the API key"),
         c => c.BirdeyeApiKey, (c, v) => c.BirdeyeApiKey = v),

        (new("coingecko", "Documented, not read by this build", "CoinGecko Pro", "COINGECKO_API_KEY",
             "Listed for CoinGecko Pro limits. Nothing in this build reads it — saving only exports the env var.",
             AppliesRestart, "https://www.coingecko.com/en/developers/dashboard", true, "paste the API key"),
         c => c.CoinGeckoApiKey, (c, v) => c.CoinGeckoApiKey = v),
    };

    /// <summary>Metadata for every non-exchange credential, in display order.</summary>
    public static IReadOnlyList<IntegrationKey> IntegrationKeys { get; } =
        IntegrationTable.Select(e => e.Meta).ToArray();

    // Notification channels. The alert view model reads these env vars in its field
    // initialisers, so applying them before it is constructed is all the restore that
    // is needed — no changes to the alert engine itself.
    private static readonly (string Env, Func<AllCredentials, string> Read, Action<AllCredentials, string> Write)[] NotificationTable =
    {
        ("TELEGRAM_BOT_TOKEN",  c => c.TelegramBotToken,  (c, v) => c.TelegramBotToken = v),
        ("TELEGRAM_CHAT_ID",    c => c.TelegramChatId,    (c, v) => c.TelegramChatId = v),
        ("DISCORD_WEBHOOK_URL", c => c.DiscordWebhookUrl, (c, v) => c.DiscordWebhookUrl = v),
        ("NTFY_TOPIC",          c => c.NtfyTopic,         (c, v) => c.NtfyTopic = v),
        ("EMAIL_SMTP_HOST",     c => c.EmailHost,         (c, v) => c.EmailHost = v),
        ("EMAIL_SMTP_PORT",     c => c.EmailPort,         (c, v) => c.EmailPort = v),
        ("EMAIL_SMTP_SSL",      c => c.EmailUseSsl,       (c, v) => c.EmailUseSsl = v),
        ("EMAIL_SMTP_USER",     c => c.EmailUsername,     (c, v) => c.EmailUsername = v),
        ("EMAIL_SMTP_PASS",     c => c.EmailPassword,     (c, v) => c.EmailPassword = v),
        ("EMAIL_FROM",          c => c.EmailFrom,         (c, v) => c.EmailFrom = v),
        ("EMAIL_TO",            c => c.EmailTo,           (c, v) => c.EmailTo = v),
    };

    private static readonly string[] ExchangeAndAiEnv =
    {
        "CRYPTOAI_AI_PROVIDER", "ANTHROPIC_API_KEY", "OPENAI_API_KEY",
        "CRYPTOAI_CLAUDE_MODEL", "CRYPTOAI_OPENAI_MODEL",
        "BINANCE_API_KEY", "BINANCE_API_SECRET",
        "BYBIT_API_KEY", "BYBIT_API_SECRET",
        "OKX_API_KEY", "OKX_API_SECRET", "OKX_API_PASSPHRASE",
        "KUCOIN_API_KEY", "KUCOIN_API_SECRET", "KUCOIN_API_PASSPHRASE",
    };

    // Variables that were already set when the process started. LoadAndApplyAll() injects
    // saved file values into the process environment, so from that moment on "the variable
    // is set" no longer means "the user set a variable" — without this snapshot every key
    // saved from the UI would report itself as coming from the environment, and the source
    // badge would be wrong for every single credential.
    private static readonly HashSet<string> ExternalEnv;

    static CredentialsService()
    {
        ExternalEnv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ExchangeAndAiEnv
                     .Concat(IntegrationTable.Select(e => e.Meta.EnvVar))
                     .Concat(NotificationTable.Select(e => e.Env)))
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                ExternalEnv.Add(name);
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads ALL credentials from disk and injects every non-empty value as a
    /// process-level environment variable (only if the env var isn't already set).
    /// Call this once, very early in startup, before any service reads env vars.
    /// </summary>
    public static void LoadAndApplyAll()
    {
        var creds = ReadFromDisk();

        // Map: field value → env-var name
        Apply("CRYPTOAI_AI_PROVIDER",     creds.AiProvider);
        Apply("ANTHROPIC_API_KEY",        creds.AnthropicApiKey);
        Apply("OPENAI_API_KEY",           creds.OpenAiApiKey);
        Apply("CRYPTOAI_CLAUDE_MODEL",    creds.AnthropicModel);
        Apply("CRYPTOAI_OPENAI_MODEL",    creds.OpenAiModel);
        Apply("BINANCE_API_KEY",         creds.BinanceKey);
        Apply("BINANCE_API_SECRET",       creds.BinanceSecret);
        Apply("BYBIT_API_KEY",            creds.BybitKey);
        Apply("BYBIT_API_SECRET",         creds.BybitSecret);
        Apply("OKX_API_KEY",              creds.OkxKey);
        Apply("OKX_API_SECRET",           creds.OkxSecret);
        Apply("OKX_API_PASSPHRASE",       creds.OkxPassphrase);
        Apply("KUCOIN_API_KEY",           creds.KucoinKey);
        Apply("KUCOIN_API_SECRET",        creds.KucoinSecret);
        Apply("KUCOIN_API_PASSPHRASE",    creds.KucoinPassphrase);

        // Data providers, Telegram app credentials, DEX signing key.
        foreach (var (meta, read, _) in IntegrationTable) Apply(meta.EnvVar, read(creds));

        // Alert channels — restoring these before AlertsViewModel is constructed is what
        // makes the Notifications tab survive a restart.
        foreach (var (env, read, _) in NotificationTable) Apply(env, read(creds));

        static void Apply(string envName, string fileValue)
        {
            // A variable the user set outside the app always wins.
            if (ExternalEnv.Contains(envName)) return;
            if (!string.IsNullOrWhiteSpace(fileValue))
                Environment.SetEnvironmentVariable(envName, fileValue, EnvironmentVariableTarget.Process);
        }
    }

    // ── Integration keys: read / save ─────────────────────────────────────────

    /// <summary>Every integration key's effective value (external env var wins), in one disk read.</summary>
    public static Dictionary<string, string> LoadIntegrations()
    {
        var file = ReadFromDisk();
        return IntegrationTable.ToDictionary(
            e => e.Meta.Id,
            e => PickFirst(e.Meta.EnvVar, e.Read(file)),
            StringComparer.Ordinal);
    }

    /// <summary>Only the values stored in the file — an env var is never echoed back into an input.</summary>
    public static Dictionary<string, string> LoadIntegrationFileValues()
    {
        var file = ReadFromDisk();
        return IntegrationTable.ToDictionary(e => e.Meta.Id, e => e.Read(file), StringComparer.Ordinal);
    }

    /// <summary>Where each integration key currently comes from — drives the source badge.</summary>
    public static Dictionary<string, CredentialSource> IntegrationSources()
    {
        var file = ReadFromDisk();
        return IntegrationTable.ToDictionary(
            e => e.Meta.Id,
            e => DetermineSource(e.Meta.EnvVar, e.Read(file)),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Persists the supplied integration keys (ids missing from <paramref name="values"/> are
    /// left untouched) and pushes them into the process environment, so anything that reads its
    /// variable per call picks the new value up without a restart. Keys the user set as a real
    /// environment variable are never overwritten — those still win.
    /// </summary>
    public static void SaveIntegrations(IReadOnlyDictionary<string, string> values)
    {
        var current = ReadFromDisk();
        foreach (var (meta, _, write) in IntegrationTable)
            if (values.TryGetValue(meta.Id, out var v)) write(current, (v ?? "").Trim());

        WriteToDisk(current);

        foreach (var (meta, read, _) in IntegrationTable)
        {
            if (ExternalEnv.Contains(meta.EnvVar)) continue;
            var v = read(current);
            // Setting a variable to "" removes it, which is exactly right for a cleared key.
            Environment.SetEnvironmentVariable(meta.EnvVar, v, EnvironmentVariableTarget.Process);
        }
    }

    // ── Notification channels: read / save ────────────────────────────────────

    /// <summary>Alert-channel settings as stored on disk.</summary>
    public sealed record NotificationSettings(
        string TelegramBotToken, string TelegramChatId,
        string DiscordWebhookUrl, string NtfyTopic,
        string EmailHost, int EmailPort, bool EmailUseSsl,
        string EmailUsername, string EmailPassword, string EmailFrom, string EmailTo);

    /// <summary>
    /// Persists the alert channels and applies them to the process environment. The values are
    /// re-read on the next start by <see cref="LoadAndApplyAll"/>, which is what turns the
    /// Notifications tab from session-only into something that survives a restart.
    /// </summary>
    public static void SaveNotifications(NotificationSettings s)
    {
        var current = ReadFromDisk();
        current.TelegramBotToken  = (s.TelegramBotToken ?? "").Trim();
        current.TelegramChatId    = (s.TelegramChatId ?? "").Trim();
        current.DiscordWebhookUrl = (s.DiscordWebhookUrl ?? "").Trim();
        current.NtfyTopic         = (s.NtfyTopic ?? "").Trim();
        current.EmailHost         = (s.EmailHost ?? "").Trim();
        current.EmailPort         = s.EmailPort > 0 ? s.EmailPort.ToString(CultureInfo.InvariantCulture) : "";
        current.EmailUseSsl       = s.EmailUseSsl ? "true" : "false";
        current.EmailUsername     = (s.EmailUsername ?? "").Trim();
        current.EmailPassword     = s.EmailPassword ?? "";
        current.EmailFrom         = (s.EmailFrom ?? "").Trim();
        current.EmailTo           = (s.EmailTo ?? "").Trim();
        WriteToDisk(current);

        foreach (var (env, read, _) in NotificationTable)
        {
            if (ExternalEnv.Contains(env)) continue;
            Environment.SetEnvironmentVariable(env, read(current), EnvironmentVariableTarget.Process);
        }
    }

    /// <summary>
    /// Loads all credentials.  Environment variables always win over saved file values.
    /// </summary>
    public static LoadResult Load()
    {
        var file = ReadFromDisk();

        // Merge: env overrides file
        var merged = new AllCredentials
        {
            BinanceKey        = PickFirst("BINANCE_API_KEY",      file.BinanceKey),
            BinanceSecret     = PickFirst("BINANCE_API_SECRET",   file.BinanceSecret),
            BybitKey          = PickFirst("BYBIT_API_KEY",        file.BybitKey),
            BybitSecret       = PickFirst("BYBIT_API_SECRET",     file.BybitSecret),
            OkxKey            = PickFirst("OKX_API_KEY",          file.OkxKey),
            OkxSecret         = PickFirst("OKX_API_SECRET",       file.OkxSecret),
            OkxPassphrase     = PickFirst("OKX_API_PASSPHRASE",   file.OkxPassphrase),
            KucoinKey         = PickFirst("KUCOIN_API_KEY",       file.KucoinKey),
            KucoinSecret      = PickFirst("KUCOIN_API_SECRET",    file.KucoinSecret),
            KucoinPassphrase  = PickFirst("KUCOIN_API_PASSPHRASE", file.KucoinPassphrase),
        };

        // Copy file-only fields (no env-var override for these)
        merged.BinanceAffiliateUrl = file.BinanceAffiliateUrl;
        merged.BybitAffiliateUrl   = file.BybitAffiliateUrl;
        merged.OkxAffiliateUrl     = file.OkxAffiliateUrl;
        merged.KucoinAffiliateUrl  = file.KucoinAffiliateUrl;

        return new LoadResult
        {
            Credentials   = merged,
            BinanceSource = DetermineSource("BINANCE_API_KEY", file.BinanceKey),
            BybitSource   = DetermineSource("BYBIT_API_KEY",   file.BybitKey),
            OkxSource     = DetermineSource("OKX_API_KEY",     file.OkxKey),
            KucoinSource  = DetermineSource("KUCOIN_API_KEY",  file.KucoinKey),
        };
    }

    /// <summary>
    /// Persists the complete credentials object to disk (used when inserting all keys at once).
    /// </summary>
    public static void SaveAll(AllCredentials creds) => WriteToDisk(creds);

    /// <summary>Current AI provider settings, for populating the Settings UI.</summary>
    public sealed record AiSettings(
        AiVendor Provider,
        string AnthropicKey,
        string OpenAiKey,
        string AnthropicModel,
        string OpenAiModel);

    /// <summary>Reads the saved AI provider settings (file values; blank models mean "use the default").</summary>
    public static AiSettings LoadAiSettings()
    {
        var f = ReadFromDisk();
        return new AiSettings(
            AiRuntime.ParseVendor(PickFirst("CRYPTOAI_AI_PROVIDER", f.AiProvider)),
            PickFirst("ANTHROPIC_API_KEY", f.AnthropicApiKey),
            PickFirst("OPENAI_API_KEY",    f.OpenAiApiKey),
            f.AnthropicModel,
            f.OpenAiModel);
    }

    /// <summary>
    /// Persists the AI provider choice + keys/models and applies them live: sets the
    /// process env vars <see cref="AiRuntime"/> reads and flips <see cref="AiRuntime.Vendor"/>,
    /// so every AI feature switches between Claude and ChatGPT without a restart.
    /// </summary>
    public static void SaveAiSettings(AiSettings s)
    {
        var current = ReadFromDisk();
        current.AiProvider      = AiRuntime.ToToken(s.Provider);
        current.AnthropicApiKey = (s.AnthropicKey ?? "").Trim();
        current.OpenAiApiKey    = (s.OpenAiKey ?? "").Trim();
        current.AnthropicModel  = (s.AnthropicModel ?? "").Trim();
        current.OpenAiModel     = (s.OpenAiModel ?? "").Trim();
        WriteToDisk(current);

        // Apply live (overriding any existing process value, since the user just changed it).
        Environment.SetEnvironmentVariable("CRYPTOAI_AI_PROVIDER",  current.AiProvider,     EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY",     current.AnthropicApiKey, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY",        current.OpenAiApiKey,    EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("CRYPTOAI_CLAUDE_MODEL", current.AnthropicModel,  EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("CRYPTOAI_OPENAI_MODEL", current.OpenAiModel,     EnvironmentVariableTarget.Process);
        AiRuntime.Vendor = s.Provider;
    }

    /// <summary>Persists Binance API key and secret to disk.</summary>
    public static void SaveBinance(string key, string secret)
    {
        var current = ReadFromDisk();
        current.BinanceKey    = key.Trim();
        current.BinanceSecret = secret.Trim();
        WriteToDisk(current);
    }

    /// <summary>Persists Bybit API key and secret to disk.</summary>
    public static void SaveBybit(string key, string secret)
    {
        var current = ReadFromDisk();
        current.BybitKey    = key.Trim();
        current.BybitSecret = secret.Trim();
        WriteToDisk(current);
    }

    /// <summary>Persists OKX API key, secret and passphrase to disk.</summary>
    public static void SaveOkx(string key, string secret, string passphrase)
    {
        var current = ReadFromDisk();
        current.OkxKey        = key.Trim();
        current.OkxSecret     = secret.Trim();
        current.OkxPassphrase = passphrase.Trim();
        WriteToDisk(current);
    }

    /// <summary>Persists KuCoin API key, secret and passphrase to disk.</summary>
    public static void SaveKucoin(string key, string secret, string passphrase)
    {
        var current = ReadFromDisk();
        current.KucoinKey        = key.Trim();
        current.KucoinSecret     = secret.Trim();
        current.KucoinPassphrase = passphrase.Trim();
        WriteToDisk(current);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // DPAPI-encrypted credential blobs start with this marker. Anything without
    // it is treated as a legacy plaintext file and silently re-encrypted on the
    // next save — so existing installations migrate seamlessly.
    private const string DpapiMarker = "DPAPI:v1:";

    // Per-app entropy makes the ciphertext useless to other apps running under
    // the same Windows user account, even though they could call DPAPI too.
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("CryptoAITerminal::api-credentials::v1");

    /// <summary>
    /// Last storage problem worth telling the user about: an unreadable credentials file, or
    /// secrets that had to be written without DPAPI. Null when everything is normal.
    /// </summary>
    public static string? LastStorageWarning { get; private set; }

    private static AllCredentials ReadFromDisk()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var raw = File.ReadAllText(FilePath);
            var json = TryDecrypt(raw);
            return JsonSerializer.Deserialize<AllCredentials>(json, JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            // A read failure used to return empty credentials, and the next SaveXxx wrote that
            // empty object straight over the file — the DEX signing key and every exchange key
            // gone for good, from one transient DPAPI or IO error. Keep the original bytes before
            // anything can overwrite them.
            PreserveUnreadableFile(ex);
            return new();
        }
    }

    private static void PreserveUnreadableFile(Exception cause)
    {
        LastStorageWarning =
            $"Could not read {FilePath}: {cause.Message}. A copy was kept so nothing is lost; " +
            "saved credentials will start from empty until it is restored.";

        try
        {
            if (!File.Exists(FilePath)) return;

            // One backup per broken file: never overwrite an earlier rescue copy, that would defeat
            // the point on the second start.
            var backup = FilePath + ".unreadable";
            for (var i = 1; File.Exists(backup) && i < 100; i++)
                backup = FilePath + ".unreadable." + i;

            if (!File.Exists(backup))
            {
                File.Copy(FilePath, backup);
                LastStorageWarning += $" Copy: {backup}";
            }
        }
        catch
        {
            // Best-effort. The warning above is still set, which is the part that matters.
        }
    }

    private static void WriteToDisk(AllCredentials creds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(creds, JsonOpts);
        var payload = TryEncrypt(json);

        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, payload);
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static string TryEncrypt(string plaintextJson)
    {
        // DPAPI is Windows-only. On other platforms (or if DPAPI throws — e.g. a
        // local profile without crypto support) we fall back to plaintext rather
        // than locking the user out of their own credentials.
        if (!OperatingSystem.IsWindows())
        {
            LastStorageWarning =
                $"DPAPI is unavailable on this platform — API keys and the DEX signing key are " +
                $"stored in clear text in {FilePath}.";
            return plaintextJson;
        }

        try
        {
            var plain = Encoding.UTF8.GetBytes(plaintextJson);
            var cipher = ProtectedData.Protect(plain, DpapiEntropy, DataProtectionScope.CurrentUser);
            return DpapiMarker + Convert.ToBase64String(cipher);
        }
        catch (Exception ex)
        {
            // Still falling back rather than locking the user out of their own keys — but not
            // silently: writing a wallet private key in clear text is not something to discover
            // by opening the file.
            LastStorageWarning =
                $"Windows encryption (DPAPI) failed: {ex.Message}. API keys and the DEX signing " +
                $"key were written in CLEAR TEXT to {FilePath}.";
            return plaintextJson;
        }
    }

    private static string TryDecrypt(string fileContent)
    {
        if (string.IsNullOrEmpty(fileContent) || !fileContent.StartsWith(DpapiMarker, StringComparison.Ordinal))
            return fileContent; // Legacy plaintext file — read as-is, will be re-encrypted on next save.

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI-encrypted credentials cannot be opened on this platform.");

        var base64 = fileContent[DpapiMarker.Length..];
        var cipher = Convert.FromBase64String(base64);
        var plain = ProtectedData.Unprotect(cipher, DpapiEntropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>An externally-set env var wins; otherwise fall back to the file value.</summary>
    private static string PickFirst(string envName, string fileValue) =>
        ExternalEnvValue(envName) ?? fileValue;

    /// <summary>The variable's value only when the user set it outside the app, else null.</summary>
    private static string? ExternalEnvValue(string envName)
    {
        if (!ExternalEnv.Contains(envName)) return null;
        var v = Environment.GetEnvironmentVariable(envName);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static CredentialSource DetermineSource(string envName, string fileValue)
    {
        if (ExternalEnvValue(envName) is not null)
            return CredentialSource.Env;
        if (!string.IsNullOrWhiteSpace(fileValue))
            return CredentialSource.File;
        return CredentialSource.None;
    }
}
