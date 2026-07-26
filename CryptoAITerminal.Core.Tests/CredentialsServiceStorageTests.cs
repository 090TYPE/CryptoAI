using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// Storage behaviour of the credentials file. Every test runs against a throwaway directory —
/// touching the real per-user file would mean reading and rewriting the user's own exchange keys
/// and DEX signing key.
/// </summary>
public class CredentialsServiceStorageTests
{
    // A payload that carries the DPAPI marker but cannot be decoded, which is how a real
    // credentials file breaks: copied between machines, or written by another Windows account.
    private const string BrokenPayload = "DPAPI:v1:this-is-not-valid-base64!!";

    [Fact]
    public void Unreadable_file_is_copied_aside_and_reported()
    {
        using var root = new TempCredentialsRoot();
        File.WriteAllText(root.CredentialsFile, BrokenPayload);

        var values = CredentialsService.LoadIntegrationFileValues();

        Assert.All(values.Values, v => Assert.Equal("", v));

        var warning = CredentialsService.LastStorageWarning;
        Assert.NotNull(warning);
        Assert.Contains(".unreadable", warning!, StringComparison.Ordinal);

        Assert.Equal(BrokenPayload, File.ReadAllText(root.CredentialsFile));
        Assert.Equal(BrokenPayload, File.ReadAllText(root.CredentialsFile + ".unreadable"));
    }

    [Fact]
    public void A_save_after_a_failed_read_cannot_destroy_the_original_file()
    {
        using var root = new TempCredentialsRoot();
        File.WriteAllText(root.CredentialsFile, BrokenPayload);

        // The regression this guards: the read fails, returns empty credentials, and the save
        // writes that empty object straight over the only copy of every key.
        CredentialsService.SaveBinance("new-key", "new-secret");

        Assert.Equal(BrokenPayload, File.ReadAllText(root.CredentialsFile + ".unreadable"));
        Assert.NotEqual(BrokenPayload, File.ReadAllText(root.CredentialsFile));
    }

    [Fact]
    public void Second_failed_read_leaves_the_first_rescue_copy_alone()
    {
        using var root = new TempCredentialsRoot();
        const string secondPayload = "DPAPI:v1:a-different-broken-payload!!";

        File.WriteAllText(root.CredentialsFile, BrokenPayload);
        CredentialsService.LoadIntegrationFileValues();

        File.WriteAllText(root.CredentialsFile, secondPayload);
        CredentialsService.LoadIntegrationFileValues();

        Assert.Equal(BrokenPayload, File.ReadAllText(root.CredentialsFile + ".unreadable"));
        Assert.Equal(secondPayload, File.ReadAllText(root.CredentialsFile + ".unreadable.1"));
    }

    [Fact]
    public void No_file_at_all_is_not_a_failure()
    {
        using var root = new TempCredentialsRoot();
        Assert.False(File.Exists(root.CredentialsFile));

        var values = CredentialsService.LoadIntegrationFileValues();

        Assert.All(values.Values, v => Assert.Equal("", v));
        Assert.Null(CredentialsService.LastStorageWarning);
        Assert.Empty(Directory.GetFiles(root.Root));
    }

    [Fact]
    public void Saved_credentials_come_back_unchanged()
    {
        using var root = new TempCredentialsRoot();
        const string dexKey = "0xdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef";

        CredentialsService.SaveAll(new CredentialsService.AllCredentials
        {
            BinanceKey         = "binance-key",
            BinanceSecret      = "binance-secret",
            DexPrivateKey      = dexKey,
            TelegramApiHash    = "0123456789abcdef0123456789abcdef",
            BinanceAffiliateUrl = "https://example.invalid/ref",
        });

        Assert.Null(CredentialsService.LastStorageWarning);

        var fileValues = CredentialsService.LoadIntegrationFileValues();
        Assert.Equal(dexKey, fileValues["dexPrivateKey"]);
        Assert.Equal("0123456789abcdef0123456789abcdef", fileValues["telegramApiHash"]);
        Assert.Null(CredentialsService.LastStorageWarning);

        // Affiliate links have no env-var override, so Load() can be asserted unconditionally.
        var loaded = CredentialsService.Load();
        Assert.Equal("https://example.invalid/ref", loaded.Credentials.BinanceAffiliateUrl);

        // An externally-set variable legitimately wins over the file, so only check the merged
        // exchange key when nothing outside the app is exporting it.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BINANCE_API_KEY")))
        {
            Assert.Equal("binance-key", loaded.Credentials.BinanceKey);
            Assert.Equal("binance-secret", loaded.Credentials.BinanceSecret);
        }

        // No warning above means DPAPI did encrypt, so the key must not be legible on disk.
        Assert.DoesNotContain(dexKey, File.ReadAllText(root.CredentialsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_plaintext_file_is_read_then_re_encrypted_on_the_next_save()
    {
        using var root = new TempCredentialsRoot();
        File.WriteAllText(root.CredentialsFile, """{"dexPrivateKey":"0xlegacy","etherscanApiKey":"legacy-etherscan"}""");

        var before = CredentialsService.LoadIntegrationFileValues();
        Assert.Equal("0xlegacy", before["dexPrivateKey"]);
        Assert.Equal("legacy-etherscan", before["etherscan"]);
        Assert.Null(CredentialsService.LastStorageWarning);

        CredentialsService.SaveBinance("k", "s");

        Assert.Null(CredentialsService.LastStorageWarning);
        Assert.DoesNotContain("0xlegacy", File.ReadAllText(root.CredentialsFile), StringComparison.Ordinal);
        Assert.Equal("0xlegacy", CredentialsService.LoadIntegrationFileValues()["dexPrivateKey"]);
    }

    /// <summary>
    /// Redirects the credentials store at a fresh temp directory and puts everything back on
    /// dispose — including the sticky warning, which is process-wide and would otherwise be read
    /// by the next test as its own.
    /// </summary>
    private sealed class TempCredentialsRoot : IDisposable
    {
        private readonly string _previousRoot;

        public string Root { get; }
        public string CredentialsFile => Path.Combine(Root, "api-credentials.json");

        public TempCredentialsRoot()
        {
            _previousRoot = CredentialsService.RootDirectory;
            Root = Path.Combine(Path.GetTempPath(), "cryptoai-credentials-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            CredentialsService.RootDirectory = Root;
            CredentialsService.LastStorageWarning = null;
        }

        public void Dispose()
        {
            CredentialsService.RootDirectory = _previousRoot;
            CredentialsService.LastStorageWarning = null;
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a passing test over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
