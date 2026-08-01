using System.Collections.Generic;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Ai;

/// <summary>
/// Как вердикт AI выглядит для человека.
///
/// Вердикты — единственное, ради чего в терминале вообще есть модель, и читают их взглядом, а не
/// чтением. Поэтому проверяется не «свойство вернуло строку», а те свойства показа, потеря которых
/// уже случалась:
///
/// • «избегать» и «моментум» не должны выходить одним цветом. В списке трендов DEX так и было:
///   свой <c>switch</c> знал LONG и SHORT, а весь словарь DEX попадал в «прочее».
/// • причина, по которой ответила не модель, не должна попадать внутрь текста вердикта — там она
///   читается как вывод о рынке.
/// • незнакомое слово нельзя красить наугад: модель возвращает что угодно, и уверенный цвет на
///   догадке хуже серого.
/// </summary>
public class AiVerdictPresentationTests
{
    [Fact]
    public void Opposite_verdicts_never_share_a_colour()
    {
        // Словарь трендов DEX — тот самый, где раньше все четыре слова были одинаковыми.
        Assert.NotEqual(AiVerdictPalette.ColorOf("MOMENTUM"), AiVerdictPalette.ColorOf("AVOID"));
        Assert.NotEqual(AiVerdictPalette.ColorOf("EARLY"), AiVerdictPalette.ColorOf("AVOID"));
        Assert.NotEqual(AiVerdictPalette.ColorOf("FADING"), AiVerdictPalette.ColorOf("MOMENTUM"));

        Assert.NotEqual(AiVerdictPalette.ColorOf("BULLISH"), AiVerdictPalette.ColorOf("BEARISH"));
        Assert.NotEqual(AiVerdictPalette.ColorOf("APPROVE"), AiVerdictPalette.ColorOf("BLOCK"));
        Assert.NotEqual(AiVerdictPalette.ColorOf("ROBUST"), AiVerdictPalette.ColorOf("OVERFIT"));
        Assert.NotEqual(AiVerdictPalette.ColorOf("ACCUMULATION"), AiVerdictPalette.ColorOf("DISTRIBUTION"));
        Assert.NotEqual(AiVerdictPalette.ColorOf("MAGNET_ABOVE"), AiVerdictPalette.ColorOf("MAGNET_BELOW"));
    }

    [Fact]
    public void Both_statarb_entries_read_as_actionable()
    {
        // SHORT_A_LONG_B — про падение одной ноги, но это рабочий вход, а не предупреждение.
        // Покрасить его как BEARISH значило бы отговаривать от сделки, которую сама модель советует.
        Assert.Equal(AiVerdictPalette.Tone.Good, AiVerdictPalette.ToneOf("LONG_A_SHORT_B"));
        Assert.Equal(AiVerdictPalette.Tone.Good, AiVerdictPalette.ToneOf("SHORT_A_LONG_B"));
        Assert.Equal(AiVerdictPalette.Tone.Caution, AiVerdictPalette.ToneOf("WAIT"));
    }

    /// <summary>
    /// Слова, которыми сервисы отвечают на самом деле. Читаются из самих сервисов, а не
    /// переписаны сюда: списки-копии расходятся ровно так же, как разошлись рельс Bots-деска и
    /// пред-торговая проверка — там красили ALLOW и REJECT, которых в словаре нет, и оба главных
    /// исхода выходили серыми.
    /// </summary>
    public static TheoryData<string> ShippedVerdicts()
    {
        var data = new TheoryData<string>();
        foreach (var w in PreTradeRiskService.Vocabulary) data.Add(w);
        foreach (var w in CorrelationInsightService.Vocabulary) data.Add(w);
        foreach (var w in DailyBriefingService.Vocabulary) data.Add(w);
        foreach (var w in OptionsStrategyService.Vocabulary) data.Add(w);
        return data;
    }

    /// <summary>
    /// Слова-середины своих словарей: ответ «ничего определённого». Серый для них — это решение,
    /// а не пробел в палитре, и список нужен именно затем, чтобы одно нельзя было принять за другое.
    ///
    /// BALANCED здесь потому, что раньше он красился янтарным — из-за ветки «всё остальное» в
    /// разборе корреляций, а не потому, что сбалансированный портфель о чём-то предупреждает.
    /// </summary>
    private static readonly HashSet<string> DeliberatelyNeutral = ["NEUTRAL", "BALANCED"];

    [Theory]
    [MemberData(nameof(ShippedVerdicts))]
    public void Every_word_a_service_can_answer_with_has_a_colour(string verdict)
    {
        var tone = AiVerdictPalette.ToneOf(verdict);

        if (DeliberatelyNeutral.Contains(verdict))
        {
            Assert.Equal(AiVerdictPalette.Tone.Neutral, tone);
            return;
        }

        // Серый у остальных означает «палитра про это слово не знает» — то есть вердикт показан
        // без смысла, который в нём есть.
        Assert.NotEqual(AiVerdictPalette.Tone.Neutral, tone);
    }

    [Fact]
    public void An_unknown_word_is_neutral_rather_than_guessed()
    {
        Assert.Equal(AiVerdictPalette.Tone.Neutral, AiVerdictPalette.ToneOf("SOMETHING_NEW"));
        Assert.Equal(AiVerdictPalette.Tone.Neutral, AiVerdictPalette.ToneOf(null));
        Assert.Equal(AiVerdictPalette.Tone.Neutral, AiVerdictPalette.ToneOf(""));
    }

    [Fact]
    public void The_word_shown_is_not_the_schema_name()
    {
        Assert.Equal("MAGNET ABOVE", AiVerdictPalette.Display("MAGNET_ABOVE"));
        Assert.Equal("LONG A SHORT B", AiVerdictPalette.Display("long_a_short_b".ToUpperInvariant()));
        Assert.Equal("", AiVerdictPalette.Display(null));
    }

    [Fact]
    public void The_chip_tint_is_the_verdict_colour_made_transparent()
    {
        // Плашка другого цвета, чем текст на ней, читается как два сообщения рядом.
        var color = AiVerdictPalette.ColorOf("BULLISH");
        Assert.EndsWith(color[^6..], AiVerdictPalette.TintOf("BULLISH"));
        Assert.EndsWith(color[^6..], AiVerdictPalette.StrokeOf("BULLISH"));
    }

    [Fact]
    public void An_insight_becomes_a_chip_a_summary_and_separate_bullets()
    {
        var vm = new AiVerdictVM();
        vm.Set(new InsightResult("Крупные потоки уходят с бирж.", "ACCUMULATION",
            ["отток $12M", "приток $3M"], "claude-sonnet-4-6", IsFallback: false));

        Assert.True(vm.HasContent);
        Assert.True(vm.HasVerdict);
        Assert.Equal("ACCUMULATION", vm.VerdictText);
        Assert.Equal("Крупные потоки уходят с бирж.", vm.Summary);
        // Доводы строками, а не одной склейкой: их сравнивают между собой.
        Assert.Equal(2, vm.Bullets.Count);
        Assert.True(vm.HasBullets);
        Assert.Equal("claude-sonnet-4-6", vm.Source);
        Assert.False(vm.IsFallback);
        Assert.Equal("", vm.FallbackText);
    }

    [Fact]
    public void The_reason_the_model_stayed_out_never_enters_the_verdict_text()
    {
        // Ровно та поломка, ради которой полоса и заведена: причина отказа, вписанная в сводку,
        // читается как вывод модели о рынке.
        var vm = new AiVerdictVM();
        vm.Set(new InsightResult("Кластеры сбалансированы.", "BALANCED", [], "Heuristic (offline)", IsFallback: true),
            reason: "AI is not set up on the server yet.");

        Assert.Equal("Кластеры сбалансированы.", vm.Summary);
        Assert.DoesNotContain("server", vm.Summary);
        Assert.True(vm.IsFallback);
        Assert.Contains("AI is not set up on the server yet.", vm.FallbackText);
    }

    [Fact]
    public void A_fallback_without_a_reason_still_says_so()
    {
        // Встроенный расчёт бывает и без ошибки — например, когда ключа нет вовсе. Молчать об
        // этом нельзя: ответ по виду неотличим от ответа модели.
        var vm = new AiVerdictVM();
        vm.Set(new InsightResult("s", "NEUTRAL", [], "Heuristic (offline)", IsFallback: true));

        Assert.True(vm.IsFallback);
        Assert.NotEqual("", vm.FallbackText);
    }

    [Fact]
    public void The_score_bar_stays_inside_its_track()
    {
        var vm = new AiVerdictVM();

        vm.Fill("AVOID", "", null, "", false, score: 0);
        Assert.Equal(0, vm.ScoreBarWidth);

        vm.Fill("AVOID", "", null, "", false, score: 100);
        Assert.Equal(220, vm.ScoreBarWidth);

        // Модель возвращает число сама, и 140/100 из неё уже приходило.
        vm.Fill("AVOID", "", null, "", false, score: 140);
        Assert.Equal(220, vm.ScoreBarWidth);
        Assert.True(vm.HasScore);

        vm.Fill("AVOID", "", null, "", false);
        Assert.False(vm.HasScore);
    }

    [Fact]
    public void The_score_is_coloured_by_the_verdict_not_by_its_size()
    {
        // 80 у «избегать» и 80 у «моментум» — разные вещи. Шкала, покрашенная по величине,
        // говорила бы, что оба одинаково хороши.
        var avoid = new AiVerdictVM();
        avoid.Fill("AVOID", "", null, "", false, score: 80);
        var momentum = new AiVerdictVM();
        momentum.Fill("MOMENTUM", "", null, "", false, score: 80);

        Assert.NotEqual(avoid.ScoreColor, momentum.ScoreColor);
    }

    [Fact]
    public void Blank_bullets_are_dropped_rather_than_drawn_as_empty_rows()
    {
        var vm = new AiVerdictVM();
        vm.Fill("NEUTRAL", "s", ["ок", "", "   "], "src", false);

        Assert.Single(vm.Bullets);
    }

    [Fact]
    public void Clearing_hides_the_card_completely()
    {
        var vm = new AiVerdictVM();
        vm.Set(new InsightResult("s", "BULLISH", ["b"], "src", IsFallback: true), "why");

        vm.Clear();

        Assert.False(vm.HasContent);
        Assert.False(vm.HasVerdict);
        Assert.False(vm.HasBullets);
        Assert.False(vm.IsFallback);
        Assert.Equal("", vm.FallbackText);
    }
}
