using System.Text.Json;
using CryptoAITerminal.Server.Common;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Serving;

/// <summary>
/// Перевод Anthropic Messages ↔ OpenAI Chat Completions.
///
/// От него зависит, работает ли выбор «ChatGPT» вообще: формат запроса зашит в клиенте, все ~20
/// провайдеров терминала и все фоновые задачи говорят в формате Anthropic. Ошибка перевода выглядит
/// не как ошибка перевода, а как плохая модель — ответ приходит, просто без инструментов, без
/// системного промпта или без учёта расхода.
/// </summary>
public class AiFormatBridgeTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void The_system_prompt_becomes_a_system_message()
    {
        // У Anthropic system — отдельное поле. Потеряв его, модель теряет все инструкции и отвечает
        // связно, но не то: самый дорогой в диагностике вид поломки.
        var src = """
        {"model":"x","max_tokens":500,"system":"Ты аналитик","messages":[{"role":"user","content":"привет"}]}
        """;

        var messages = Json(AiFormatBridge.RequestToOpenAi(src, "gpt-4o")!).GetProperty("messages");

        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Ты аналитик", messages[0].GetProperty("content").GetString());
        Assert.Equal("привет", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public void A_system_prompt_split_into_cached_blocks_still_arrives_whole()
    {
        // Клиент шлёт system массивом блоков с cache_control ради кэширования промпта. У OpenAI
        // такого поля нет; текст обязан склеиться, а cache_control — исчезнуть, а не уехать внутрь.
        var src = """
        {"model":"x","max_tokens":10,
         "system":[{"type":"text","text":"часть один","cache_control":{"type":"ephemeral"}},
                   {"type":"text","text":"часть два"}],
         "messages":[{"role":"user","content":"?"}]}
        """;

        var body = AiFormatBridge.RequestToOpenAi(src, "gpt-4o")!;
        var system = Json(body).GetProperty("messages")[0].GetProperty("content").GetString();

        Assert.Contains("часть один", system);
        Assert.Contains("часть два", system);
        Assert.DoesNotContain("cache_control", body);
    }

    [Fact]
    public void Model_and_output_limit_are_the_servers_choice()
    {
        var src = """{"model":"claude-sonnet-4-6","max_tokens":2000,"messages":[{"role":"user","content":"?"}]}""";
        var dst = Json(AiFormatBridge.RequestToOpenAi(src, "openai/gpt-4o")!);

        Assert.Equal("openai/gpt-4o", dst.GetProperty("model").GetString());
        Assert.Equal(2000, dst.GetProperty("max_tokens").GetInt32());
        Assert.False(dst.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void Tool_definitions_move_under_function()
    {
        var src = """
        {"model":"x","max_tokens":10,"messages":[{"role":"user","content":"?"}],
         "tools":[{"name":"get_price","description":"цена","input_schema":{"type":"object","properties":{"sym":{"type":"string"}}}}]}
        """;

        var tool = Json(AiFormatBridge.RequestToOpenAi(src, "gpt-4o")!).GetProperty("tools")[0];

        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("get_price", tool.GetProperty("function").GetProperty("name").GetString());
        // input_schema и parameters — один и тот же JSON Schema под разными именами.
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public void A_tool_call_keeps_its_id_and_arguments_become_a_string()
    {
        // Аргументы у OpenAI — строка с JSON внутри, а не объект. Отправив объект, получаешь 400 на
        // втором витке агентского цикла, то есть уже после того, как первый выглядел рабочим.
        var src = """
        {"model":"x","max_tokens":10,"messages":[
          {"role":"user","content":"?"},
          {"role":"assistant","content":[{"type":"tool_use","id":"tu_1","name":"get_price","input":{"sym":"BTC"}}]},
          {"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_1","content":"63000"}]}]}
        """;

        var messages = Json(AiFormatBridge.RequestToOpenAi(src, "gpt-4o")!).GetProperty("messages");

        var call = messages[1].GetProperty("tool_calls")[0];
        Assert.Equal("tu_1", call.GetProperty("id").GetString());
        var args = call.GetProperty("function").GetProperty("arguments").GetString();
        Assert.Equal("BTC", Json(args!).GetProperty("sym").GetString());

        // Ответ инструмента у OpenAI — отдельное сообщение роли tool, а не блок внутри пользовательского.
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("tu_1", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("63000", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public void The_answer_comes_back_in_the_shape_the_client_parses()
    {
        var upstream = """
        {"id":"chatcmpl-1","model":"gpt-4o",
         "choices":[{"message":{"role":"assistant","content":"BTC растёт"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":120,"completion_tokens":45}}
        """;

        var back = Json(AiFormatBridge.ResponseToAnthropic(upstream));

        Assert.Equal("message", back.GetProperty("type").GetString());
        Assert.Equal("text", back.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("BTC растёт", back.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("end_turn", back.GetProperty("stop_reason").GetString());
    }

    [Fact]
    public void Usage_survives_the_translation()
    {
        // На этих числах держатся суточные лимиты тарифов и весь учёт расхода. Потеряв их, сервер
        // раздаёт вызовы бесплатно и узнаёт об этом по счёту провайдера.
        var upstream = """
        {"id":"c","model":"m","choices":[{"message":{"content":"ок"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":120,"completion_tokens":45}}
        """;

        var back = AiFormatBridge.ResponseToAnthropic(upstream);

        Assert.Equal(120, AiRequestPolicy.SplitUsage(back, AiVendor.Anthropic).Input);
        Assert.Equal(45, AiRequestPolicy.SplitUsage(back, AiVendor.Anthropic).Output);
    }

    [Fact]
    public void A_returned_tool_call_becomes_a_tool_use_block()
    {
        var upstream = """
        {"id":"c","model":"m","choices":[{"message":{"content":null,
          "tool_calls":[{"id":"call_9","type":"function","function":{"name":"get_price","arguments":"{\"sym\":\"ETH\"}"}}]},
          "finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}
        """;

        var back = Json(AiFormatBridge.ResponseToAnthropic(upstream));
        var block = back.GetProperty("content")[0];

        Assert.Equal("tool_use", block.GetProperty("type").GetString());
        Assert.Equal("call_9", block.GetProperty("id").GetString());
        // Строка с JSON внутри обязана развернуться обратно в объект — агент читает input как структуру.
        Assert.Equal("ETH", block.GetProperty("input").GetProperty("sym").GetString());
        Assert.Equal("tool_use", back.GetProperty("stop_reason").GetString());
    }

    [Fact]
    public void A_truncated_answer_is_reported_as_truncated()
    {
        var upstream = """{"id":"c","choices":[{"message":{"content":"…"},"finish_reason":"length"}]}""";
        Assert.Equal("max_tokens", Json(AiFormatBridge.ResponseToAnthropic(upstream)).GetProperty("stop_reason").GetString());
    }

    [Fact]
    public void Garbage_in_never_throws()
    {
        // Слева сюда приходит тело от клиента, справа — от чужого сервиса. Ни то ни другое не должно
        // валить конвейер запроса.
        Assert.Null(AiFormatBridge.RequestToOpenAi("не json", "gpt-4o"));
        Assert.Null(AiFormatBridge.RequestToOpenAi("", "gpt-4o"));

        // Неразобранный ответ возвращается как есть: он попадёт в лог, и подменять его выдуманной
        // оболочкой значит стереть единственную улику.
        Assert.Equal("не json", AiFormatBridge.ResponseToAnthropic("не json"));
        Assert.Equal("""{"error":{"message":"boom"}}""",
            AiFormatBridge.ResponseToAnthropic("""{"error":{"message":"boom"}}"""));
    }
}
