using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class UiLocalizationServiceTests
{
    // A representative sample of English source strings that MUST have a Russian
    // translation (exact-match dictionary entries). Keeps the model honest: author
    // in English, render Russian via the dictionary.
    private static readonly string[] TranslatedKeys =
    {
        // Smart Order Router page
        "Smart Order Router", "Compute route", "Execute", "Exchange Quotes",
        "Price (raw)", "Eff. price", "Execution Plan", "Average price",
        // Market Scanner page
        "Real-time Market Scanner", "Preset:", "Resistance", "Support",
        "Change %", "Volume min $", "Alerts", "Price Levels",
        // API-key panels
        "How to get Binance API keys", "Show / hide", "💾 Save",
        "paste API Key…", "Permissions: ", "only once!",
        // View-model status strings
        "Order executed", "Ready", "Loading…", "Backtest complete.",
        "Run a backtest first.", "Optimizing…", "Executing…", "Res.", "Sup.",
    };

    private static bool ContainsCyrillic(string s)
    {
        foreach (var c in s)
        {
            if ((c >= 'А' && c <= 'я') || c == 'Ё' || c == 'ё')
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void Instance_Constructs_WithoutDuplicateKeys()
    {
        // Accessing the singleton initializes the EN->RU dictionary; a duplicate
        // key in the collection initializer would throw during construction.
        Assert.NotNull(UiLocalizationService.Instance);
    }

    [Fact]
    public void Translate_English_ReturnsSourceUnchanged()
    {
        var svc = UiLocalizationService.Instance;
        svc.SetLanguage(UiLanguage.English);

        foreach (var key in TranslatedKeys)
        {
            Assert.Equal(key, svc.Translate(key));
        }
    }

    [Fact]
    public void Translate_Russian_TranslatesKnownKeysToCyrillic()
    {
        var svc = UiLocalizationService.Instance;
        try
        {
            svc.SetLanguage(UiLanguage.Russian);

            foreach (var key in TranslatedKeys)
            {
                var translated = svc.Translate(key);
                Assert.NotEqual(key, translated);
                Assert.True(
                    ContainsCyrillic(translated),
                    $"Expected a Russian translation for '{key}', got '{translated}'.");
            }
        }
        finally
        {
            // Restore global singleton state for other tests.
            svc.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void Translate_Russian_PrefixStrings_TranslatePrefix()
    {
        var svc = UiLocalizationService.Instance;
        try
        {
            svc.SetLanguage(UiLanguage.Russian);

            Assert.Equal("Ошибка: boom", svc.Translate("Error: boom"));
            Assert.Equal("Лучшая биржа: Binance", svc.Translate("Best exchange: Binance"));
        }
        finally
        {
            svc.SetLanguage(UiLanguage.English);
        }
    }
}
