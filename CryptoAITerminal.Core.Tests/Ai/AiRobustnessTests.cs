using System.Text.Json;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Ai;

/// <summary>
/// Устойчивость разбора ответов модели и размер запросов.
///
/// Общая форма всех этих дефектов одна: исключение, которое не JsonException, пролетает мимо
/// <c>catch (JsonException)</c> у вызывающего и уничтожает целиком ответ, за который уже заплачено.
/// Снаружи это неотличимо от «модель не вызывалась», поэтому такие поломки живут долго.
/// </summary>
public class AiRobustnessTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_bullet_that_arrived_as_an_object_is_read_not_fatal()
    {
        // Соседняя схема в этом же коде объявляет пункты как {"text":…,"tone":…}, и модель
        // переносит эту привычку. GetString() на таком элементе бросает InvalidOperationException.
        var root = El("""{"bullets":["строка",{"text":"объект","tone":"bull"},{"nope":1},null]}""");

        var items = AiJson.StrArray(root, "bullets");

        Assert.Equal(new[] { "строка", "объект" }, items);
    }

    [Fact]
    public void A_fractional_score_does_not_destroy_the_answer()
    {
        // 87.5 — это тоже Number, а GetInt32() на нём бросает FormatException, и он не
        // JsonException. В схеме «0..100» дробь ничем не запрещена.
        var o = El("""{"score":87.5}""");

        Assert.Equal(87.5m, AiJson.Num(o, "score", 50m));
        Assert.Equal(87, (int)System.Math.Clamp(AiJson.Num(o, "score", 50m), 0m, 100m));
    }

    [Fact]
    public void An_out_of_range_number_falls_back_instead_of_throwing()
    {
        Assert.Equal(50m, AiJson.Num(El("""{"score":1.5e30}"""), "score", 50m));
        Assert.Equal(50m, AiJson.Num(El("""{"score":"много"}"""), "score", 50m));
    }

    [Fact]
    public void The_rebalance_request_grows_with_the_portfolio()
    {
        // Фиксированные 600 покрывали около пятнадцати позиций; на портфеле шире ответ обрывался,
        // и пользователь видел «модель не вернула пригодных весов» — тот же текст, что и при
        // полностью отключённом AI.
        var small = PortfolioRebalanceAiProvider.MaxTokensFor(3);
        var full = PortfolioRebalanceAiProvider.MaxTokensFor(PortfolioRebalanceAiProvider.MaxHoldings);

        Assert.True(full > small);
        Assert.True(full >= 2000, $"на полном портфеле запрошено {full}, этого мало");
        Assert.Equal(full, PortfolioRebalanceAiProvider.MaxTokensFor(500));
    }

    [Fact]
    public void The_translation_request_grows_with_the_text_not_the_line_count()
    {
        // Две длинные строки дороже двадцати коротких подписей: считать надо символы.
        var manyShort = Enumerable.Repeat("OK", 20).ToList();
        var fewLong = new List<string> { new string('a', 600), new string('b', 600) };

        Assert.True(AiUiTranslator.MaxTokensFor(fewLong) > AiUiTranslator.MaxTokensFor(manyShort));
    }

    [Fact]
    public void Every_request_stays_under_the_server_cap()
    {
        // Зажим сервера молчаливый: запрос выше потолка режется без ошибки, и это ровно тот способ
        // сломать ответ так, чтобы поломку никто не увидел.
        var cap = CryptoAITerminal.Server.Common.AiPolicyOptions.FromConfig(_ => null).MaxTokensCap;

        Assert.True(AiSignalDeskProvider.MaxTokensFor(AiSignalDeskProvider.MaxSymbols) <= cap);
        Assert.True(PortfolioRebalanceAiProvider.MaxTokensFor(PortfolioRebalanceAiProvider.MaxHoldings) <= cap);
        Assert.True(AiUiTranslator.MaxTokensFor(Enumerable.Repeat(new string('x', 600), 20).ToList()) <= cap);
        Assert.True(CryptoAITerminal.AIEngine.Agent.AgentLimits.MaxTokensPerTurn <= cap);
    }

    [Fact]
    public void The_catalogue_the_admin_panel_shows_matches_what_the_client_asks()
    {
        // Админка показывает эти числа как ориентир, и оператор, набравший показанное, своими
        // руками воссоздаёт обрыв ответа. Здесь это уже случилось.
        var byId = CryptoAITerminal.Server.Common.AiFeatures.All.ToDictionary(f => f.Id);

        Assert.True(byId["signal_desk"].DefaultMaxTokens
                    >= AiSignalDeskProvider.MaxTokensFor(AiSignalDeskProvider.MaxSymbols));
        Assert.True(byId["portfolio_rebalance"].DefaultMaxTokens
                    >= PortfolioRebalanceAiProvider.MaxTokensFor(PortfolioRebalanceAiProvider.MaxHoldings));
        Assert.True(byId["agent"].DefaultMaxTokens
                    >= CryptoAITerminal.AIEngine.Agent.AgentLimits.MaxTokensPerTurn);
    }

    [Fact]
    public void Every_client_feature_id_exists_in_the_server_catalogue()
    {
        // Незнакомый серверу идентификатор не фатален — вызов проходит как есть, — но означает
        // функцию, которой нельзя назначить ни модель, ни лимит, то есть дыру в управлении.
        var known = CryptoAITerminal.Server.Common.AiFeatures.All.Select(f => f.Id).ToHashSet();

        var clientIds = typeof(AiFeatureIds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        foreach (var id in clientIds)
            Assert.True(known.Contains(id), $"функция '{id}' есть в терминале, но её нет в каталоге сервера");
    }
}
