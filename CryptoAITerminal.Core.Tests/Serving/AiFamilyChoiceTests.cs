using System.Text.Json;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.Server.Common;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Serving;

/// <summary>
/// Выбор семейства пользователем и обратный перевод формата.
///
/// Выбор приходит с клиента заголовком, а формат запроса определяется тем, какой провайдер внутри
/// терминала сработал. Без обратного перевода выбор действовал бы на часть вызовов — то есть
/// работал бы через раз, что диагностируется хуже, чем если бы не работал вовсе.
/// </summary>
public class AiFamilyChoiceTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void A_missing_or_broken_header_is_not_a_choice()
    {
        // Мягкий разбор здесь означал бы, что пустой заголовок молча перебивает выбор сервера:
        // одна опечатка в клиенте переводит пользователя на другое семейство без следа.
        Assert.False(AiModelCatalog.TryParseFamily("", out _));
        Assert.False(AiModelCatalog.TryParseFamily(null, out _));
        Assert.False(AiModelCatalog.TryParseFamily("chatgtp", out _));

        Assert.True(AiModelCatalog.TryParseFamily("chatgpt", out var gpt));
        Assert.Equal(AiFamily.ChatGpt, gpt);
        Assert.True(AiModelCatalog.TryParseFamily(" Claude ", out var claude));
        Assert.Equal(AiFamily.Claude, claude);
    }

    [Fact]
    public void A_pinned_model_from_the_other_family_is_not_honoured()
    {
        // Прибитое имя claude-модели, оставшееся от прежней настройки, не должно переживать
        // переключение на ChatGPT — иначе переключатель срабатывает для одних функций и молча
        // не срабатывает для других.
        Assert.True(AiModelCatalog.BelongsTo("anthropic/claude-sonnet-5", AiFamily.Claude));
        Assert.False(AiModelCatalog.BelongsTo("anthropic/claude-sonnet-5", AiFamily.ChatGpt));
        Assert.True(AiModelCatalog.BelongsTo("openai/gpt-4o", AiFamily.ChatGpt));
        Assert.False(AiModelCatalog.BelongsTo("openai/gpt-4o", AiFamily.Claude));
        Assert.False(AiModelCatalog.BelongsTo(null, AiFamily.Claude));
    }

    [Fact]
    public void A_name_from_the_chosen_family_is_left_alone()
    {
        // Обычный случай: терминал прислал имя своего семейства. Переписывать его не за чем, и
        // подстановка здесь молча перевела бы весь терминал на одну зашитую модель.
        Assert.Null(AiModelCatalog.ForegroundFallback("claude-sonnet-4-6", AiFamily.Claude));
        Assert.Null(AiModelCatalog.ForegroundFallback("gpt-4o", AiFamily.ChatGpt));
        Assert.Null(AiModelCatalog.ForegroundFallback("anthropic/claude-sonnet-9.9", AiFamily.Claude));
        // Имени нет — решает клиент, как и до появления семейств.
        Assert.Null(AiModelCatalog.ForegroundFallback("", AiFamily.ChatGpt));
        Assert.Null(AiModelCatalog.ForegroundFallback(null, AiFamily.ChatGpt));
    }

    [Fact]
    public void A_name_from_the_other_family_is_replaced_rather_than_forwarded()
    {
        // Формат запроса и семейство расходятся, когда оператор запретил выбор в терминале или
        // когда сборка на руках старше заголовка семейства. Отправить claude-имя на OpenAI —
        // гарантированный 404 на КАЖДОМ вызове из терминала, то есть «переключили — всё умерло».
        Assert.Equal(SettingKeys.DefaultForegroundChatGptModel,
            AiModelCatalog.ForegroundFallback("claude-sonnet-4-6", AiFamily.ChatGpt));
        Assert.Equal(SettingKeys.DefaultForegroundModel,
            AiModelCatalog.ForegroundFallback("gpt-4o", AiFamily.Claude));
    }

    [Fact]
    public void The_substituted_names_are_ones_the_default_allowlist_accepts()
    {
        // Подстановка, которую отвергает собственный белый список сервера, меняла бы 404 у
        // провайдера на 400 у себя — то же самое молчание, только с другим кодом.
        var options = AiPolicyOptions.FromConfig(_ => null);
        Assert.Contains(SettingKeys.DefaultForegroundModel, options.AllowedAnthropicModels);
        Assert.Contains(SettingKeys.DefaultForegroundChatGptModel, options.AllowedOpenAiModels);
        Assert.Contains(SettingKeys.DefaultBackgroundModel, options.AllowedAnthropicModels);
        Assert.Contains(SettingKeys.DefaultBackgroundChatGptModel, options.AllowedOpenAiModels);
    }

    [Fact]
    public void An_openai_request_becomes_a_messages_request()
    {
        var src = """
        {"model":"gpt-4o","max_tokens":800,"messages":[
          {"role":"system","content":"Ты аналитик"},
          {"role":"user","content":"привет"}]}
        """;

        var dst = Json(AiFormatBridge.RequestToAnthropic(src, "anthropic/claude-sonnet-5")!);

        Assert.Equal("anthropic/claude-sonnet-5", dst.GetProperty("model").GetString());
        // system у Anthropic — отдельное поле, а не сообщение с ролью system.
        Assert.Equal("Ты аналитик", dst.GetProperty("system").GetString());
        Assert.Equal(800, dst.GetProperty("max_tokens").GetInt32());
        Assert.Single(dst.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public void Max_tokens_is_always_present_because_anthropic_requires_it()
    {
        // У OpenAI поле необязательное. Пропустив его, получаешь 400 на каждом вызове, который
        // пришёл в формате OpenAI, — то есть ровно на тех, ради которых этот перевод и написан.
        var noLimit = """{"model":"gpt-4o","messages":[{"role":"user","content":"?"}]}""";
        Assert.True(Json(AiFormatBridge.RequestToAnthropic(noLimit, "m")!).GetProperty("max_tokens").GetInt32() > 0);

        var altName = """{"model":"gpt-4o","max_completion_tokens":1234,"messages":[{"role":"user","content":"?"}]}""";
        Assert.Equal(1234, Json(AiFormatBridge.RequestToAnthropic(altName, "m")!).GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void Consecutive_tool_answers_merge_into_one_message()
    {
        // Anthropic требует чередования ролей, а агентский виток у OpenAI возвращает ответы
        // инструментов отдельными сообщениями. Без слияния запрос отвергается по формату — то есть
        // выбор «Claude» ломал бы именно агента.
        var src = """
        {"model":"gpt-4o","max_tokens":10,"messages":[
          {"role":"user","content":"?"},
          {"role":"assistant","content":null,"tool_calls":[
            {"id":"c1","type":"function","function":{"name":"a","arguments":"{}"}},
            {"id":"c2","type":"function","function":{"name":"b","arguments":"{}"}}]},
          {"role":"tool","tool_call_id":"c1","content":"one"},
          {"role":"tool","tool_call_id":"c2","content":"two"}]}
        """;

        var messages = Json(AiFormatBridge.RequestToAnthropic(src, "m")!).GetProperty("messages");
        var roles = messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()).ToList();

        Assert.Equal(new[] { "user", "assistant", "user" }, roles);
        // Оба ответа инструментов в одном сообщении, каждый своим блоком.
        Assert.Equal(2, messages[2].GetProperty("content").GetArrayLength());
        Assert.Equal("tool_result", messages[2].GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Tool_definitions_lose_the_function_wrapper()
    {
        var src = """
        {"model":"gpt-4o","max_tokens":10,"messages":[],
         "tools":[{"type":"function","function":{"name":"get_price","description":"цена",
                   "parameters":{"type":"object","properties":{"sym":{"type":"string"}}}}}]}
        """;

        var tool = Json(AiFormatBridge.RequestToAnthropic(src, "m")!).GetProperty("tools")[0];

        Assert.Equal("get_price", tool.GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
    }

    [Fact]
    public void The_answer_goes_back_in_the_shape_an_openai_client_parses()
    {
        var upstream = """
        {"id":"msg_1","model":"anthropic/claude-sonnet-5","type":"message","role":"assistant",
         "content":[{"type":"text","text":"BTC растёт"}],"stop_reason":"end_turn",
         "usage":{"input_tokens":120,"output_tokens":45}}
        """;

        var back = Json(AiFormatBridge.ResponseToOpenAi(upstream));
        var choice = back.GetProperty("choices")[0];

        Assert.Equal("BTC растёт", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        // На этих числах держатся суточные квоты тарифов; потеряв их, сервер раздаёт вызовы даром.
        Assert.Equal(120, back.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(45, back.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
    }

    [Fact]
    public void A_tool_use_block_becomes_a_tool_call()
    {
        var upstream = """
        {"id":"m","model":"x","content":[{"type":"tool_use","id":"tu_1","name":"get_price","input":{"sym":"ETH"}}],
         "stop_reason":"tool_use","usage":{"input_tokens":1,"output_tokens":1}}
        """;

        var call = Json(AiFormatBridge.ResponseToOpenAi(upstream))
            .GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0];

        Assert.Equal("tu_1", call.GetProperty("id").GetString());
        // Аргументы у OpenAI — строка с JSON внутри, а не объект.
        var args = call.GetProperty("function").GetProperty("arguments").GetString();
        Assert.Equal("ETH", Json(args!).GetProperty("sym").GetString());
    }

    [Fact]
    public void A_round_trip_through_both_bridges_keeps_the_conversation()
    {
        // Оба направления живут в одном классе, и рассинхрон между ними — самая вероятная поломка
        // при следующей правке.
        var original = """
        {"model":"claude-sonnet-4-6","max_tokens":500,"system":"инструкция",
         "messages":[{"role":"user","content":"вопрос"}]}
        """;

        var asOpenAi = AiFormatBridge.RequestToOpenAi(original, "openai/gpt-4o")!;
        var back = Json(AiFormatBridge.RequestToAnthropic(asOpenAi, "anthropic/claude-sonnet-5")!);

        Assert.Equal("инструкция", back.GetProperty("system").GetString());
        Assert.Equal(500, back.GetProperty("max_tokens").GetInt32());
        Assert.Equal("вопрос",
            back.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Garbage_never_throws_in_either_direction()
    {
        Assert.Null(AiFormatBridge.RequestToAnthropic("не json", "m"));
        Assert.Equal("не json", AiFormatBridge.ResponseToOpenAi("не json"));
    }

    [Fact]
    public void The_desk_asks_for_room_that_grows_with_the_watchlist()
    {
        // Фиксированное число здесь уже стоило обрезанного ответа на полном списке и впустую
        // широкого запроса на коротком.
        var few = AiSignalDeskProvider.MaxTokensFor(3);
        var many = AiSignalDeskProvider.MaxTokensFor(AiSignalDeskProvider.MaxSymbols);

        Assert.True(many > few);
        // Измерено на живых ответах: 14 символов — около 1900 токенов. Запас должен быть кратным,
        // потому что многословность модели гуляет от прогона к прогону.
        Assert.True(many >= 3800, $"на полном списке запрошено {many}, этого мало");

        // И не выше серверного потолка, иначе зажим срежет молча — та самая невидимая поломка.
        var cap = AiPolicyOptions.FromConfig(_ => null).MaxTokensCap;
        Assert.True(many <= cap, $"запрошено {many} при потолке сервера {cap}");
    }

    [Fact]
    public void A_watchlist_longer_than_the_prompt_does_not_inflate_the_request()
    {
        // Модели отдаётся не более MaxSymbols строк, поэтому 200 монет в вотчлисте не должны
        // превращаться в запрос на 50 тысяч токенов.
        Assert.Equal(AiSignalDeskProvider.MaxTokensFor(AiSignalDeskProvider.MaxSymbols),
                     AiSignalDeskProvider.MaxTokensFor(200));
    }
}
