using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        public string TelegramBotToken { get; set; } = "";   // TELEGRAM_BOT_TOKEN
        public string TelegramChatId   { get; set; } = "";   // TELEGRAM_CHAT_ID

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
        Apply("ETHERSCAN_API_KEY",        creds.EtherscanApiKey);
        Apply("BSCSCAN_API_KEY",          creds.BscscanApiKey);
        Apply("ALCHEMY_API_KEY",          creds.AlchemyApiKey);
        Apply("GLASSNODE_API_KEY",        creds.GlassnodeApiKey);
        Apply("COINGLASS_API_KEY",        creds.CoinglassApiKey);
        Apply("TRONGRID_API_KEY",         creds.TrongridApiKey);
        Apply("CRYPTOPANIC_API_KEY",      creds.CryptoPanicApiKey);
        Apply("BIRDEYE_API_KEY",          creds.BirdeyeApiKey);
        Apply("COINGECKO_API_KEY",        creds.CoinGeckoApiKey);
        Apply("COVALENT_API_KEY",         creds.CovalentApiKey);
        Apply("MORALIS_API_KEY",          creds.MoralisApiKey);
        Apply("JUPITER_API_KEY",          creds.JupiterApiKey);
        Apply("TELEGRAM_BOT_TOKEN",       creds.TelegramBotToken);
        Apply("TELEGRAM_CHAT_ID",         creds.TelegramChatId);
        Apply("CRYPTOAI_DEX_PRIVATE_KEY", creds.DexPrivateKey);

        static void Apply(string envName, string fileValue)
        {
            // Env var already set externally → don't override
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envName))) return;
            if (!string.IsNullOrWhiteSpace(fileValue))
                Environment.SetEnvironmentVariable(envName, fileValue, EnvironmentVariableTarget.Process);
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

    private static AllCredentials ReadFromDisk()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var raw = File.ReadAllText(FilePath);
            var json = TryDecrypt(raw);
            return JsonSerializer.Deserialize<AllCredentials>(json, JsonOpts) ?? new();
        }
        catch { return new(); }
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
        if (!OperatingSystem.IsWindows()) return plaintextJson;

        try
        {
            var plain = Encoding.UTF8.GetBytes(plaintextJson);
            var cipher = ProtectedData.Protect(plain, DpapiEntropy, DataProtectionScope.CurrentUser);
            return DpapiMarker + Convert.ToBase64String(cipher);
        }
        catch
        {
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

    /// <summary>Env var wins if set; otherwise fall back to the file value.</summary>
    private static string PickFirst(string envName, string fileValue)
    {
        var env = Environment.GetEnvironmentVariable(envName);
        return !string.IsNullOrWhiteSpace(env) ? env : fileValue;
    }

    private static CredentialSource DetermineSource(string envName, string fileValue)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envName)))
            return CredentialSource.Env;
        if (!string.IsNullOrWhiteSpace(fileValue))
            return CredentialSource.File;
        return CredentialSource.None;
    }
}
