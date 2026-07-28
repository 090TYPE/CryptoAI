using CryptoAITerminal.AIEngine;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Ai;

/// <summary>
/// Поиск рынка по символу при отрисовке ответа модели.
///
/// Здесь стоял ToDictionary, и повторяющийся тикер валил приложение целиком: исключение возникало
/// внутри реактивного конвейера, а его поведение по умолчанию — завершить процесс, а не показать
/// ошибку. Воспроизвелось на живом ответе в момент, когда вызовы к провайдеру наконец заработали:
/// PEPE присутствовал и в вотчлисте, и в списке трендовых DEX-токенов.
/// </summary>
public class AiSignalDeskLookupTests
{
    private static AiSignalMarketRow Row(string sym, string exch, decimal price) =>
        new(sym, sym + "/USDT", exch, price, 0m, 0m, 1m, "", "");

    [Fact]
    public void A_repeated_ticker_does_not_throw()
    {
        var markets = new[]
        {
            Row("BTC", "Binance", 63941m),
            Row("PEPE", "Binance", 0.0000012m),
            Row("PEPE", "DEX", 0.0000013m),
        };

        var lookup = AiSignalDeskViewModel.BuildSymbolLookup(markets);

        Assert.Equal(2, lookup.Count);
    }

    [Fact]
    public void The_exchange_row_wins_because_it_comes_first()
    {
        // У биржевой строки настоящие цена и объём, у трендовой записи — не всегда.
        var markets = new[] { Row("PEPE", "Binance", 0.0000012m), Row("PEPE", "DEX", 0.0000013m) };

        Assert.Equal("Binance", AiSignalDeskViewModel.BuildSymbolLookup(markets)["PEPE"].Exch);
    }

    [Fact]
    public void Lookup_is_case_insensitive_both_ways()
    {
        // Модель возвращает символы в своём регистре, и совпасть они обязаны в любом.
        var lookup = AiSignalDeskViewModel.BuildSymbolLookup(new[] { Row("btc", "Binance", 1m) });

        Assert.True(lookup.ContainsKey("BTC"));
        Assert.True(lookup.ContainsKey("btc"));
    }

    [Fact]
    public void A_row_without_a_symbol_is_skipped_rather_than_keyed_on_empty()
    {
        var markets = new[] { Row("", "Binance", 1m), Row("  ", "DEX", 2m), Row("ETH", "Binance", 1922m) };

        var lookup = AiSignalDeskViewModel.BuildSymbolLookup(markets);

        Assert.Single(lookup);
        Assert.True(lookup.ContainsKey("ETH"));
    }
}
