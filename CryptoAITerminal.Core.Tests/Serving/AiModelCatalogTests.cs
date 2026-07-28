using CryptoAITerminal.Server.Common;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Serving;

/// <summary>
/// Подбор модели из того, что отдаёт провайдер.
///
/// Это замена ручному вводу имён в админке. Ошибка здесь не видна как ошибка: сервер молча возьмёт
/// не ту модель, терминал ответит, счёт вырастет, и понять это можно будет только по расходу.
/// </summary>
public class AiModelCatalogTests
{
    // Как список выглядит у роутера: имена с префиксом вендора и вперемешку с чужим семейством.
    private static readonly string[] Router =
    {
        "anthropic/claude-sonnet-4.6",
        "anthropic/claude-sonnet-4.5",
        "anthropic/claude-haiku-4.5",
        "anthropic/claude-opus-4.1",
        "openai/gpt-4o",
        "openai/gpt-4o-mini",
        "openai/text-embedding-3-large",
    };

    [Fact]
    public void Terminal_calls_get_the_strongest_reasonable_claude()
    {
        Assert.Equal("anthropic/claude-sonnet-4.6",
            AiModelCatalog.Pick(Router, AiFamily.Claude, AiModelRole.Foreground));
    }

    [Fact]
    public void Background_jobs_get_the_cheap_claude()
    {
        // Фоновых вызовов в сутки на два порядка больше терминальных; sonnet тут — прямая переплата.
        Assert.Equal("anthropic/claude-haiku-4.5",
            AiModelCatalog.Pick(Router, AiFamily.Claude, AiModelRole.Background));
    }

    [Fact]
    public void Opus_is_never_chosen_by_itself()
    {
        // Разница в цене у самого сильного класса на порядок, а не в разы: такое решение принимает
        // человек, иначе новый opus в списке провайдера однажды тихо умножит счёт.
        var onlyOpus = new[] { "anthropic/claude-opus-4.1" };
        Assert.Null(AiModelCatalog.Pick(onlyOpus, AiFamily.Claude, AiModelRole.Foreground));
    }

    [Fact]
    public void Chatgpt_splits_the_same_way()
    {
        Assert.Equal("openai/gpt-4o", AiModelCatalog.Pick(Router, AiFamily.ChatGpt, AiModelRole.Foreground));
        Assert.Equal("openai/gpt-4o-mini", AiModelCatalog.Pick(Router, AiFamily.ChatGpt, AiModelRole.Background));
    }

    [Fact]
    public void Non_chat_models_are_never_picked()
    {
        // Эмбеддинги и распознавание речи лежат в том же /v1/models. Запрос к ним в формате чата
        // вернёт ошибку, которая выглядит как поломка провайдера.
        var junk = new[] { "openai/text-embedding-3-large", "openai/whisper-1", "openai/tts-1-hd" };
        Assert.Null(AiModelCatalog.Pick(junk, AiFamily.ChatGpt, AiModelRole.Foreground));
    }

    [Fact]
    public void A_newer_generation_wins_over_an_older_one()
    {
        var mixed = new[] { "claude-3-5-sonnet-20241022", "claude-sonnet-4-5" };
        Assert.Equal("claude-sonnet-4-5", AiModelCatalog.Pick(mixed, AiFamily.Claude, AiModelRole.Foreground));
    }

    [Fact]
    public void A_build_date_is_not_a_version_number()
    {
        // claude-haiku-4-5-20251001 содержит 20251001. Считая его минорной версией, отбор выбирал бы
        // модель по дате сборки — то есть всегда самую свежую сборку самого старого поколения.
        var mixed = new[] { "claude-haiku-4-5-20251001", "claude-haiku-5-0" };
        Assert.Equal("claude-haiku-5-0", AiModelCatalog.Pick(mixed, AiFamily.Claude, AiModelRole.Background));
    }

    [Fact]
    public void Vendor_names_without_a_prefix_work_too()
    {
        // Прямое подключение к Anthropic отдаёт имена без префикса — тот же отбор обязан работать.
        var vendor = new[] { "claude-sonnet-4-6", "claude-haiku-4-5-20251001" };
        Assert.Equal("claude-sonnet-4-6", AiModelCatalog.Pick(vendor, AiFamily.Claude, AiModelRole.Foreground));
    }

    [Fact]
    public void An_empty_list_picks_nothing_rather_than_guessing()
    {
        Assert.Null(AiModelCatalog.Pick(Array.Empty<string>(), AiFamily.Claude, AiModelRole.Foreground));
    }

    [Fact]
    public void The_family_setting_falls_back_to_claude()
    {
        Assert.Equal(AiFamily.ChatGpt, AiModelCatalog.ParseFamily("chatgpt"));
        Assert.Equal(AiFamily.ChatGpt, AiModelCatalog.ParseFamily(" OpenAI "));
        Assert.Equal(AiFamily.Claude, AiModelCatalog.ParseFamily("claude"));
        // Опечатка в настройке не должна уводить сервер на другого провайдера.
        Assert.Equal(AiFamily.Claude, AiModelCatalog.ParseFamily("chtgpt"));
        Assert.Equal(AiFamily.Claude, AiModelCatalog.ParseFamily(null));
    }

    [Theory]
    [InlineData("https://api.anthropic.com/v1/messages", "https://api.anthropic.com/v1/models")]
    [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1/models")]
    [InlineData("https://nordrouter.com/v1/messages", "https://nordrouter.com/v1/models")]
    public void The_models_endpoint_is_derived_from_the_configured_address(string configured, string expected)
    {
        // У OpenAI после версии два сегмента (/v1/chat/completions) — отрезая один, получили бы
        // /v1/chat/models и «список моделей не получен» на верной настройке.
        Assert.Equal(expected, AiProxyDefaults.ModelsUrl(configured));
    }
}
