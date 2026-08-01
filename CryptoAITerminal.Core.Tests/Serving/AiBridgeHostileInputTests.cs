using System.Text.Json;
using CryptoAITerminal.Server.Common;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Serving;

/// <summary>
/// Тело запроса приходит от клиента, тело ответа — от чужого сервиса. Ни то ни другое не должно
/// валить конвейер.
///
/// Собственный doc-комментарий моста это и обещает, но выполнял он обещание только для мусора,
/// который не разбирается как JSON. Валидный JSON с полем не того типа проходил дальше и падал на
/// <c>GetValue&lt;string&gt;()</c> с <c>InvalidOperationException</c> — мимо единственного
/// <c>catch (JsonException)</c>. Цена: 500 вместо 400, а повтор в терминале считает 500 возвратным
/// отказом и шлёт то же тело второй раз. На стороне ОТВЕТА дороже: падение происходит после вызова
/// вендора, но до записи расхода — оплаченные токены не попадают в <c>ai_usage</c>, из которого
/// потом считается тариф.
///
/// Соседний <c>AiRequestPolicy.Apply</c> этот же класс отказа ловит отдельным
/// <c>catch (InvalidOperationException)</c> — сюда это просто не распространили.
/// </summary>
public class AiBridgeHostileInputTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Theory]
    // Число вместо строки в каждом поле, которое мост читает как строку.
    [InlineData("""{"model":"m","max_tokens":10,"messages":[{"role":1,"content":"hi"}]}""")]
    [InlineData("""{"model":"m","max_tokens":10,"messages":[{"role":"user","content":[{"type":7}]}]}""")]
    [InlineData("""{"model":"m","max_tokens":10,"messages":[{"role":"user","content":[{"type":"text","text":42}]}]}""")]
    [InlineData("""{"model":"m","max_tokens":10,"messages":[{"role":"user","content":[{"type":"tool_use","id":1,"name":2,"input":{}}]}]}""")]
    [InlineData("""{"model":"m","max_tokens":10,"messages":[],"tools":[{"name":5,"input_schema":{}}]}""")]
    [InlineData("""{"model":"m","max_tokens":10,"messages":[],"tools":[{"name":"ok","description":9,"input_schema":{}}]}""")]
    public void A_wrong_typed_field_in_a_request_does_not_throw(string body)
    {
        // Раньше это был HTTP 500 плюс один бессмысленный повтор с тем же телом.
        var record = Record.Exception(() => AiFormatBridge.RequestToOpenAi(body, "openai/gpt-4o"));
        Assert.Null(record);
    }

    [Theory]
    [InlineData("""{"model":"m","max_tokens":10,"messages":[{"role":3,"content":"hi"}]}""")]
    [InlineData("""{"model":"m","messages":[{"role":"assistant","tool_calls":[{"id":1,"function":{"name":2,"arguments":3}}]}]}""")]
    [InlineData("""{"model":"m","messages":[],"tools":[{"type":"function","function":{"name":4}}]}""")]
    public void A_wrong_typed_field_in_the_other_direction_does_not_throw(string body)
    {
        var record = Record.Exception(() => AiFormatBridge.RequestToAnthropic(body, "anthropic/claude-sonnet-5"));
        Assert.Null(record);
    }

    [Fact]
    public void A_hostile_upstream_response_does_not_throw_before_the_spend_is_recorded()
    {
        // Это дороже прочего: падение здесь случается ПОСЛЕ оплаченного вызова вендора и ДО
        // budget.Charge — токены оплачены и не записаны никуда.
        var weird = """
        {"id":7,"model":9,"choices":[{"finish_reason":5,"message":{"content":"ok",
         "tool_calls":[{"id":1,"function":{"name":2,"arguments":3}}]}}],
         "usage":{"prompt_tokens":120,"completion_tokens":45}}
        """;

        var record = Record.Exception(() => AiFormatBridge.ResponseToAnthropic(weird));
        Assert.Null(record);
    }

    [Fact]
    public void Tool_arguments_sent_as_an_object_survive_the_translation()
    {
        // По спецификации OpenAI здесь строка, но роутеры — ради которых мост и написан — присылают
        // структуру. Раньше это роняло перевод; отдать пустые аргументы тоже нельзя, иначе агент
        // вызовет инструмент не с теми параметрами.
        var upstream = """
        {"id":"m","model":"x","choices":[{"finish_reason":"tool_calls","message":{"content":null,
         "tool_calls":[{"id":"tu_1","function":{"name":"get_price","arguments":{"sym":"ETH"}}}]}}],
         "usage":{"prompt_tokens":1,"completion_tokens":1}}
        """;

        var block = Json(AiFormatBridge.ResponseToAnthropic(upstream)).GetProperty("content")[0];

        Assert.Equal("tool_use", block.GetProperty("type").GetString());
        Assert.Equal("ETH", block.GetProperty("input").GetProperty("sym").GetString());
    }

    [Fact]
    public void The_usage_block_survives_a_response_with_broken_neighbours()
    {
        // Расход должен дойти до вызывающего даже когда остальной ответ странный: именно из этих
        // чисел считается тариф.
        var weird = """
        {"id":7,"model":9,"choices":[{"finish_reason":5,"message":{"content":"ok"}}],
         "usage":{"prompt_tokens":120,"completion_tokens":45}}
        """;

        var translated = AiFormatBridge.ResponseToAnthropic(weird);

        Assert.Equal(165, AiRequestPolicy.CountUsage(translated, AiVendor.Anthropic));
    }
}
