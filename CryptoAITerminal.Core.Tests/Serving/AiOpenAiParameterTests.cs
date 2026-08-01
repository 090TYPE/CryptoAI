using System.Text.Json;
using CryptoAITerminal.Server.Common;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Serving;

/// <summary>
/// Совместимость с новыми линейками OpenAI.
///
/// Все ~16 провайдеров терминала шлют <c>max_tokens</c> и температуру 0.0–0.4 — ради
/// воспроизводимости ответа. Новые линейки OpenAI не принимают ни того, ни другого: первое отвечает
/// «Unsupported parameter … use max_completion_tokens», второе — «does not support 0.4 with this
/// model». То есть КАЖДЫЙ вызов вернул бы 400, а панель молча ушла бы в собственный расчёт — при
/// том что бейдж под ответом продолжал бы называть модель.
///
/// Сегодня это не срабатывает: сервер смотрит на роутер, который старые имена принимает (проверено
/// живьём — ответ пришёл от openai/gpt-5.6-luna). Сработает в тот момент, когда в админке нажмут
/// «Вернуть напрямую к вендорам», то есть когда роутер уже подвёл и разбираться будет некогда.
/// </summary>
public class AiOpenAiParameterTests
{
    private static AiPolicyOptions Options() => AiPolicyOptions.FromConfig(_ => null);
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-5.1-chat")]
    [InlineData("openai/gpt-5.6-luna")]
    [InlineData("o1-preview")]
    [InlineData("o3-mini")]
    [InlineData("o4-mini")]
    public void The_new_lines_are_recognised(string model) =>
        Assert.True(AiModelCatalog.RejectsLegacyParameters(model));

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4o-mini")]
    [InlineData("openai/gpt-4o")]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("")]
    [InlineData(null)]
    public void The_old_lines_are_left_alone(string? model) =>
        Assert.False(AiModelCatalog.RejectsLegacyParameters(model));

    [Fact]
    public void A_new_line_model_gets_max_completion_tokens_and_no_temperature()
    {
        var request = """{"model":"gpt-4o","max_tokens":1000,"temperature":0.2,"messages":[]}""";

        var result = AiRequestPolicy.Apply(request, Options(), AiVendor.OpenAi,
            overrideModel: "gpt-5.1", modelIsServerChosen: true);
        var body = Json(result.Body!);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1000, body.GetProperty("max_completion_tokens").GetInt32());
        // Оба имени разом — тоже отказ по неподдерживаемому параметру.
        Assert.False(body.TryGetProperty("max_tokens", out _));
        // Температуру не подменяем единицей: это другое поведение модели, и молча его переключать
        // хуже, чем не просить ничего.
        Assert.False(body.TryGetProperty("temperature", out _));
    }

    [Fact]
    public void An_old_line_model_keeps_what_the_client_sent()
    {
        var request = """{"model":"gpt-4o","max_tokens":1000,"temperature":0.2,"messages":[]}""";

        var body = Json(AiRequestPolicy.Apply(request, Options(), AiVendor.OpenAi).Body!);

        Assert.Equal(1000, body.GetProperty("max_tokens").GetInt32());
        Assert.False(body.TryGetProperty("max_completion_tokens", out _));
        Assert.Equal(0.2, body.GetProperty("temperature").GetDouble(), 3);
    }

    [Fact]
    public void The_output_ceiling_still_binds_on_the_new_field()
    {
        // Перепись имени не должна становиться обходом зажима длины.
        var request = """{"model":"gpt-4o","max_tokens":999999,"messages":[]}""";

        var body = Json(AiRequestPolicy.Apply(request, Options(), AiVendor.OpenAi,
            overrideModel: "gpt-5", modelIsServerChosen: true).Body!);

        Assert.Equal(Options().MaxTokensCap, body.GetProperty("max_completion_tokens").GetInt32());
    }

    [Fact]
    public void The_translated_request_uses_the_field_the_target_model_accepts()
    {
        // Тот же вопрос на пути «клиент прислал формат Anthropic, обслуживает ChatGPT».
        var anthropic = """{"model":"claude-sonnet-4-6","max_tokens":800,"temperature":0.4,"messages":[{"role":"user","content":"hi"}]}""";

        var toNew = Json(AiFormatBridge.RequestToOpenAi(anthropic, "gpt-5.1")!);
        Assert.Equal(800, toNew.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(toNew.TryGetProperty("max_tokens", out _));
        Assert.False(toNew.TryGetProperty("temperature", out _));

        var toOld = Json(AiFormatBridge.RequestToOpenAi(anthropic, "openai/gpt-4o")!);
        Assert.Equal(800, toOld.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.4, toOld.GetProperty("temperature").GetDouble(), 3);
    }

    [Fact]
    public void Auto_pick_never_selects_a_reasoning_model()
    {
        // То же правило, что и для opus: цена отличается на порядок, и такое решение принимает
        // человек. Комментарий в AiModelCatalog это обещал, а код — нет: Belongs пускал o1-/o3-,
        // Tier давал им высший разряд, и достаточно было провайдеру их показать.
        var listed = new[] { "o1-preview", "o3-mini", "gpt-4o", "gpt-4o-mini" };

        Assert.Equal("gpt-4o", AiModelCatalog.Pick(listed, AiFamily.ChatGpt, AiModelRole.Foreground));
        Assert.Equal("gpt-4o-mini", AiModelCatalog.Pick(listed, AiFamily.ChatGpt, AiModelRole.Background));

        // Если у провайдера НЕТ ничего, кроме рассуждающих, автоподбор честно отказывается —
        // сервер оставит имя, которое прислал клиент, а не назначит всем дорогую модель.
        Assert.Null(AiModelCatalog.Pick(["o1-preview", "o3-mini"], AiFamily.ChatGpt, AiModelRole.Foreground));
    }

    [Fact]
    public void Auto_pick_never_selects_the_pro_class_either()
    {
        // -pro стоит рядом с opus по цене, и решение о нём принимает человек. В фильтр
        // рассуждающих он не попадает намеренно: обычные параметры запроса он принимает, поэтому
        // RejectsLegacyParameters его касаться не должен — а вот автоподбор должен.
        Assert.Equal("gpt-4o", AiModelCatalog.Pick(["gpt-5-pro", "gpt-4o"], AiFamily.ChatGpt, AiModelRole.Foreground));
        Assert.False(AiModelCatalog.RejectsLegacyParameters("gpt-4o-pro"));
    }

    [Fact]
    public void A_pinned_reasoning_model_still_works_when_a_human_chose_it()
    {
        // Запрет касается только АВТОподбора. Оператор, вписавший имя руками, получает его — и
        // параметры под него переписываются.
        var request = """{"model":"gpt-4o","max_tokens":500,"temperature":0.1,"messages":[]}""";

        var body = Json(AiRequestPolicy.Apply(request, Options(), AiVendor.OpenAi,
            overrideModel: "o3-mini", modelIsServerChosen: true).Body!);

        Assert.Equal(500, body.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.TryGetProperty("temperature", out _));
    }
}
