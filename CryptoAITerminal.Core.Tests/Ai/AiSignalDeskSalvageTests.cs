using CryptoAITerminal.AIEngine;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Ai;

/// <summary>
/// Что остаётся от ответа, оборванного лимитом длины.
///
/// Такой ответ оплачен целиком и обычно содержит десяток готовых сигналов, а выбрасывался весь —
/// из-за незакрытой последней строки. На экране это выглядело как «ответ не удалось использовать»,
/// то есть неотличимо от неудачного вызова к провайдеру, и увело диагностику совсем в другую сторону.
/// </summary>
public class AiSignalDeskSalvageTests
{
    // Как обрывается настоящий ответ: на середине строки, без закрывающих скобок.
    private const string Truncated = """
    {"regime":{"label":"Low-Volatility","tone":"neutral","summary":"flat","meta":""},
     "signals":[{"sym":"BTCUSDT","dir":"BUY","conf":0.62,"tf":"1h","exch":"Binance","reason":"holding range high"},
                {"sym":"ETHUSDT","dir":"HOLD","conf":0.5,"tf":"1h","exch":"Binance","reason":"awaiting break"},
                {"sym":"SOLUSDT","dir":"SELL","conf":0.4,"tf":"1h","exch":"Binance","reason":"losing momen
    """;

    [Fact]
    public void Signals_that_arrived_whole_are_kept()
    {
        var result = AiSignalDeskProvider.Parse(Truncated, "nordrouter claude");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Signals.Count);
        Assert.Equal("BTCUSDT", result.Signals[0].Sym);
        Assert.Equal("ETHUSDT", result.Signals[1].Sym);
    }

    [Fact]
    public void A_partial_answer_says_so()
    {
        // Иначе частичный ответ занимает на экране место полного, и отсутствие символа читается
        // как решение модели его пропустить.
        var result = AiSignalDeskProvider.Parse(Truncated, "nordrouter claude");

        Assert.Contains("partial", result!.Source);
    }

    [Fact]
    public void A_brace_inside_a_reason_does_not_split_an_object()
    {
        // Скобка внутри текста причины сдвинула бы ручной подсчёт глубины и склеила два объекта.
        var text = """
        {"signals":[{"sym":"BTCUSDT","dir":"BUY","conf":0.6,"tf":"1h","exch":"B","reason":"breakout {confirmed}"},
                    {"sym":"ETHUSDT","dir":"HOLD","conf":0.5,"tf":"1h","exch":"B","reason":"quo\"ted"},
                    {"sym":"SOL
        """;

        var result = AiSignalDeskProvider.Parse(text, "src");

        Assert.Equal(2, result!.Signals.Count);
        Assert.Equal("breakout {confirmed}", result.Signals[0].Reason);
    }

    [Fact]
    public void Nothing_complete_still_falls_back()
    {
        // Обрыв до первого целого сигнала обязан остаться отказом: показывать пустую панель как
        // ответ модели хуже, чем показать собственный расчёт терминала.
        var text = """{"regime":{"label":"x"},"signals":[{"sym":"BTC""";

        Assert.Null(AiSignalDeskProvider.Parse(text, "src"));
    }

    [Fact]
    public void A_whole_answer_is_untouched_by_any_of_this()
    {
        var text = """
        {"regime":{"label":"Trending","tone":"bull","summary":"up","meta":"ADX 39"},
         "signals":[{"sym":"BTCUSDT","dir":"BUY","conf":0.7,"tf":"1h","exch":"Binance","reason":"ok"}]}
        """;

        var result = AiSignalDeskProvider.Parse(text, "src");

        Assert.Single(result!.Signals);
        Assert.Equal("src", result.Source);
        Assert.Equal("Trending", result.Regime.Label);
    }
}
