using CryptoAITerminal.AIEngine;
using CryptoAITerminal.AIEngine.Agent;
using CryptoAITerminal.Server.Common;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Ai;

/// <summary>
/// Каталог функций на сервере против того, что клиент реально просит.
///
/// Эти два списка живут в разных сборках и намеренно не разделяют константы — тащить серверную
/// сборку в десктоп ради девятнадцати чисел было бы худшим разменом. Цена размена в том, что
/// разойтись они могут молча, и оба направления уже случались:
///
/// • Число в каталоге показывается оператору в админке как «столько просит клиент». Набрав
///   показанное, он своими руками ставит потолок ниже реального запроса — и ответ начинает
///   обрываться. Ровно это и произошло с signal_desk на числе 2000.
/// • Функция, добавленная в клиенте и забытая в каталоге, не появляется в админке вовсе: моделью
///   для неё управлять нечем, а узнать об этом можно только по счёту от вендора.
///
/// Поэтому список здесь — не копия каталога, а вычисление максимума из тех же констант, на которых
/// строится сам запрос. Изменение потолка в провайдере роняет этот тест, пока не поправлен каталог.
/// </summary>
public class AiFeatureCatalogueTests
{
    /// <summary>Самый длинный ответ, который клиент может запросить по каждой функции.</summary>
    private static readonly IReadOnlyDictionary<string, int> ClientCeilings = new Dictionary<string, int>
    {
        [AiFeatureIds.SignalDesk] = AiSignalDeskProvider.MaxTokensFor(AiSignalDeskProvider.MaxSymbols),
        [AiFeatureIds.SignalDeskStream] = AiSignalDeskService.AskMaxTokens,
        [AiFeatureIds.UiTranslate] = AiUiTranslator.MaxTokensFor([new string('x', 100_000)]),
        [AiFeatureIds.Agent] = AgentLimits.MaxTokensPerTurn,
        [AiFeatureIds.MarketRanking] = MarketRankingAiProvider.MaxTokens,
        [AiFeatureIds.DexTrending] = DexTrendingAiProvider.MaxTokens,
        [AiFeatureIds.RuleBuilder] = RuleBuilderAiProvider.MaxTokens,
        [AiFeatureIds.PortfolioRebalance] = PortfolioRebalanceAiProvider.MaxTokensFor(PortfolioRebalanceAiProvider.MaxHoldings),
        [AiFeatureIds.TradeJournalCoach] = TradeJournalCoachAiProvider.MaxTokens,
        [AiFeatureIds.MarketInsight] = MarketInsightAiProvider.MaxTokensFor(MarketInsightAiProvider.MaxDataLines),
        [AiFeatureIds.BotParameters] = BotParameterAiProvider.MaxTokens,
        [AiFeatureIds.BacktestReview] = BacktestReviewAiProvider.MaxTokens,
        [AiFeatureIds.TokenSecurity] = TokenSecurityAiProvider.MaxTokens,
        [AiFeatureIds.StatArbPair] = StatArbPairAiProvider.MaxTokens,
        [AiFeatureIds.ExecutionSchedule] = ExecutionScheduleAiProvider.MaxTokens,
        [AiFeatureIds.DynamicTpSl] = DynamicTpSlAiProvider.MaxTokens,
        [AiFeatureIds.Signal] = ClaudeSignalProvider.MaxTokens,
        [AiFeatureIds.NewsDigest] = NewsDigestAiProvider.MaxTokens,
        [AiFeatureIds.AlertSpec] = AlertSpecAiProvider.MaxTokens,
    };

    [Fact]
    public void The_catalogue_states_what_the_client_actually_asks_for()
    {
        foreach (var feature in AiFeatures.All)
        {
            Assert.True(ClientCeilings.TryGetValue(feature.Id, out var asked),
                $"функция '{feature.Id}' есть в каталоге сервера, но её потолок не проверяется здесь");
            Assert.True(feature.DefaultMaxTokens == asked,
                $"'{feature.Id}': в каталоге {feature.DefaultMaxTokens}, клиент просит {asked}. " +
                "Оператор увидит в админке первое число и, поставив его, срежет ответ.");
        }
    }

    [Fact]
    public void Every_feature_the_client_names_exists_in_the_catalogue()
    {
        // Функция без записи в каталоге не появляется в админке: моделью для неё управлять нечем,
        // и обнаруживается это по счёту от вендора, а не по интерфейсу.
        foreach (var id in ClientCeilings.Keys)
            Assert.True(AiFeatures.IsKnown(id), $"клиент шлёт '{id}', а сервер такой функции не знает");
    }

    [Fact]
    public void No_feature_asks_for_more_than_the_server_allows()
    {
        // Зажим на сервере молчаливый: запрос выше потолка не отвергается, а тихо укорачивается.
        var cap = AiPolicyOptions.FromConfig(_ => null).MaxTokensCap;

        foreach (var (id, asked) in ClientCeilings)
            Assert.True(asked <= cap, $"'{id}' просит {asked} при потолке сервера {cap} — ответ срежется молча");
    }

    [Fact]
    public void The_server_ceiling_is_no_higher_than_the_hungriest_feature()
    {
        // Запас сверх самого длинного законного запроса ничего не даёт честному клиенту, а
        // разогнавшемуся даёт ровно столько же лишних оплаченных токенов на каждый вызов. Так
        // потолок и дорос до 8192: его подняли, опираясь на суточную квоту как на настоящий
        // ограничитель трат, а квоту потом сняли — послабления расцепились.
        var cap = AiPolicyOptions.FromConfig(_ => null).MaxTokensCap;
        var hungriest = ClientCeilings.Values.Max();

        Assert.True(cap == hungriest,
            $"потолок сервера {cap} при самом длинном законном запросе {hungriest} — " +
            "лишнее оплачивается только при разгоне");
    }

    [Fact]
    public void Every_feature_id_is_safe_to_build_a_settings_key_from()
    {
        // Идентификатор приходит заголовком и превращается в ключ настройки. Наши собственные
        // должны проходить ту же проверку, что и чужие, иначе функция окажется без управления
        // моделью именно из-за опечатки в её имени.
        foreach (var id in ClientCeilings.Keys)
            Assert.True(AiFeatures.IsWellFormed(id), $"'{id}' не годится как ключ настройки");
    }

    [Fact]
    public void Every_feature_has_a_human_name_for_the_status_line()
    {
        // Строка общего состояния AI показывает имя раздела покупателю. Пропущенная запись
        // возвращает сырой идентификатор — не поломка, но и не объяснение.
        foreach (var id in ClientCeilings.Keys)
        {
            var title = AiFeatureIds.Title(id);
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.NotEqual(id, title);
        }
    }
}
