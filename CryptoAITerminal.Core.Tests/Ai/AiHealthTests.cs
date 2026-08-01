using CryptoAITerminal.AIEngine;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Ai;

/// <summary>
/// Общая строка состояния AI.
///
/// Существует потому, что панелей с AI девятнадцать, каждая честно откатывается на собственный
/// расчёт, а причина отказа почти всегда одна на все: не задан ключ на сервере, истекла лицензия,
/// провайдер не отвечает. Без одного места, где это видно, человек читает девятнадцать одинаковых
/// «Heuristic (offline)» и делает единственно возможный вывод — что сломан тот раздел, который он
/// открыл.
///
/// Проверяется поведение, а не хранение: тревога должна гаснуть от успеха любой функции, иначе она
/// переживёт починку и будет висеть до перезапуска.
/// </summary>
public class AiHealthTests
{
    private static void Reset() => ChatClient.ResetHealth();

    [Fact]
    public void Nothing_is_reported_before_the_first_call()
    {
        Reset();

        Assert.Null(ChatClient.LastFailure);
        Assert.Null(ChatClient.LastSuccessUtc);
    }

    [Fact]
    public void A_failure_is_reported_with_its_feature_and_reason()
    {
        Reset();
        ChatClient.NoteFailure(AiFeatureIds.TokenSecurity, "AI is not set up on the server yet.");

        var failure = ChatClient.LastFailure;
        Assert.NotNull(failure);
        Assert.Equal(AiFeatureIds.TokenSecurity, failure!.Value.Feature);
        Assert.Equal("AI is not set up on the server yet.", failure.Value.Reason);
        Assert.False(failure.Value.Ok);
    }

    [Fact]
    public void A_success_anywhere_clears_the_alarm()
    {
        // Причины общие для всех панелей. Держать «ключ не задан» на экране в тот момент, когда
        // соседний раздел только что получил ответ, — значит показывать заведомо неверное.
        Reset();
        ChatClient.NoteFailure(AiFeatureIds.NewsDigest, "Could not reach the AI service.");
        Assert.NotNull(ChatClient.LastFailure);

        ChatClient.NoteOk(AiFeatureIds.MarketInsight);

        Assert.Null(ChatClient.LastFailure);
        Assert.NotNull(ChatClient.LastSuccessUtc);
    }

    [Fact]
    public void A_failure_after_a_success_is_reported_again()
    {
        Reset();
        ChatClient.NoteOk(AiFeatureIds.MarketInsight);
        ChatClient.NoteFailure(AiFeatureIds.Agent, "Too many AI requests — try again in a moment.");

        Assert.NotNull(ChatClient.LastFailure);
        Assert.Equal(AiFeatureIds.Agent, ChatClient.LastFailure!.Value.Feature);
    }

    [Fact]
    public void An_empty_reason_is_not_an_alarm()
    {
        // Пустая строка приходит от вызывающего, который не смог описать сбой. Показать «⚠ » без
        // текста хуже, чем не показать ничего.
        Reset();
        ChatClient.NoteFailure(AiFeatureIds.Signal, "");

        Assert.Null(ChatClient.LastFailure);
    }

    [Fact]
    public void The_status_line_names_the_section_rather_than_its_id()
    {
        Assert.Equal("проверка токена", AiFeatureIds.Title(AiFeatureIds.TokenSecurity));
        Assert.Equal("AI", AiFeatureIds.Title(null));
        // Клиент может оказаться новее этого списка — сырое имя честнее, чем «неизвестно».
        Assert.Equal("something_new", AiFeatureIds.Title("something_new"));
    }
}
