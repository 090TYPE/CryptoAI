using System.Text.Json;
using System.Text.Json.Nodes;

namespace CryptoAITerminal.Server.Common;

/// <summary>
/// Перевод между форматом Anthropic Messages и OpenAI Chat Completions.
///
/// Нужен потому, что выбор семейства должен быть настройкой сервера, а формат запроса зашит в
/// клиенте: все ~20 провайдеров терминала и все фоновые задачи говорят в формате Anthropic. Без
/// перевода переключатель «ChatGPT» переключал бы только название и возвращал 400 от OpenAI —
/// то есть был бы кнопкой, которая делает вид.
///
/// Перевод сознательно неполный там, где полнота недостижима: у форматов разные наборы причин
/// остановки и разная модель кэширования промпта. Всё, что не переводится, отбрасывается, а не
/// подменяется похожим — в ответе, который читает человек, лучше отсутствие поля, чем правдоподобно
/// неверное.
/// </summary>
public static class AiFormatBridge
{
    /// <summary>
    /// Запрос Anthropic → запрос Chat Completions. Возвращает null, если тело не разобрать; вызвавший
    /// обязан в этом случае отказать, а не отправлять как есть.
    /// </summary>
    public static string? RequestToOpenAi(string anthropicJson, string model)
    {
        if (Parse(anthropicJson) is not JsonObject src) return null;

        var messages = new JsonArray();

        // system у Anthropic — отдельное поле (строка или массив блоков), у OpenAI — первое
        // сообщение роли system.
        var system = TextOf(src["system"]);
        if (!string.IsNullOrWhiteSpace(system))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });

        if (src["messages"] is JsonArray src_messages)
            foreach (var m in src_messages)
                AppendMessage(messages, m as JsonObject);

        var dst = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = false,
        };

        if (src["max_tokens"] is JsonValue mt && mt.TryGetValue<int>(out var maxTokens))
            dst["max_tokens"] = maxTokens;
        if (src["temperature"] is JsonValue tv && tv.TryGetValue<double>(out var temperature))
            dst["temperature"] = temperature;

        if (src["tools"] is JsonArray tools && tools.Count > 0)
        {
            var converted = new JsonArray();
            foreach (var t in tools)
            {
                if (t is not JsonObject tool || tool["name"]?.GetValue<string>() is not { Length: > 0 } name)
                    continue;

                converted.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = name,
                        ["description"] = tool["description"]?.GetValue<string>() ?? "",
                        // input_schema и parameters — один и тот же JSON Schema под разными именами.
                        ["parameters"] = tool["input_schema"]?.DeepClone() ?? new JsonObject(),
                    },
                });
            }

            if (converted.Count > 0) dst["tools"] = converted;
        }

        return dst.ToJsonString();
    }

    private static void AppendMessage(JsonArray into, JsonObject? message)
    {
        if (message is null) return;
        var role = message["role"]?.GetValue<string>() ?? "user";

        // Простой случай: content — строка.
        if (message["content"] is JsonValue simple && simple.TryGetValue<string>(out var plain))
        {
            into.Add(new JsonObject { ["role"] = role, ["content"] = plain });
            return;
        }

        if (message["content"] is not JsonArray blocks)
            return;

        var text = new List<string>();
        var toolCalls = new JsonArray();

        foreach (var b in blocks)
        {
            if (b is not JsonObject block) continue;
            switch (block["type"]?.GetValue<string>())
            {
                case "text":
                    if (block["text"]?.GetValue<string>() is { Length: > 0 } t) text.Add(t);
                    break;

                case "tool_use":
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = block["id"]?.GetValue<string>() ?? "",
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = block["name"]?.GetValue<string>() ?? "",
                            // Аргументы у OpenAI — строка с JSON внутри, а не объект.
                            ["arguments"] = (block["input"] ?? new JsonObject()).ToJsonString(),
                        },
                    });
                    break;

                case "tool_result":
                    // Ответ инструмента у OpenAI — отдельное сообщение роли tool, а не блок внутри
                    // пользовательского. Поэтому кладётся сразу, до общего text/tool_calls ниже.
                    into.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = block["tool_use_id"]?.GetValue<string>() ?? "",
                        ["content"] = TextOf(block["content"]),
                    });
                    break;
            }
        }

        if (text.Count == 0 && toolCalls.Count == 0) return;

        var converted = new JsonObject { ["role"] = role };
        converted["content"] = text.Count > 0 ? string.Join("\n", text) : null;
        if (toolCalls.Count > 0) converted["tool_calls"] = toolCalls;
        into.Add(converted);
    }

    /// <summary>
    /// Ответ Chat Completions → ответ в форме Anthropic, потому что разбирают его клиенты, которые
    /// про OpenAI не знают. Неразбираемое тело возвращается как есть: оно всё равно попадёт в лог
    /// ошибки, и подменять его выдуманной оболочкой хуже.
    /// </summary>
    public static string ResponseToAnthropic(string openAiJson)
    {
        if (Parse(openAiJson) is not JsonObject src) return openAiJson;
        if (src["choices"] is not JsonArray choices || choices.Count == 0) return openAiJson;
        if (choices[0] is not JsonObject choice) return openAiJson;

        var message = choice["message"] as JsonObject;
        var content = new JsonArray();

        if (message?["content"] is JsonValue cv && cv.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        if (message?["tool_calls"] is JsonArray calls)
            foreach (var c in calls)
            {
                if (c is not JsonObject call) continue;
                var fn = call["function"] as JsonObject;
                var args = fn?["arguments"]?.GetValue<string>();

                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = call["id"]?.GetValue<string>() ?? "",
                    ["name"] = fn?["name"]?.GetValue<string>() ?? "",
                    // Строка с JSON внутри разворачивается обратно в объект; если провайдер прислал
                    // невалидный JSON, пустой объект честнее, чем строка там, где ждут структуру.
                    ["input"] = ParseObject(args),
                });
            }

        var usage = src["usage"] as JsonObject;

        return new JsonObject
        {
            ["id"] = src["id"]?.GetValue<string>() ?? "",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = src["model"]?.GetValue<string>() ?? "",
            ["content"] = content,
            ["stop_reason"] = StopReason(choice["finish_reason"]?.GetValue<string>()),
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = Int(usage?["prompt_tokens"]),
                ["output_tokens"] = Int(usage?["completion_tokens"]),
            },
        }.ToJsonString();
    }

    /// <summary>
    /// Запрос Chat Completions → запрос Anthropic Messages. Зеркало <see cref="RequestToOpenAi"/>.
    ///
    /// Нужно ровно затем же: семейство выбирает человек, а формат запроса определяется тем, какой
    /// провайдер внутри терминала сработал. Без обратного перевода выбор «Claude» не действовал бы
    /// на вызовы, ушедшие в формате OpenAI, — и работал бы через раз, что хуже, чем не работать.
    /// </summary>
    public static string? RequestToAnthropic(string openAiJson, string model)
    {
        if (Parse(openAiJson) is not JsonObject src) return null;

        var system = new List<string>();
        var messages = new JsonArray();

        if (src["messages"] is JsonArray srcMessages)
            foreach (var m in srcMessages)
            {
                if (m is not JsonObject message) continue;
                var role = message["role"]?.GetValue<string>() ?? "user";

                // system у Anthropic — отдельное поле, а не сообщение.
                if (role == "system")
                {
                    var s = TextOf(message["content"]);
                    if (!string.IsNullOrWhiteSpace(s)) system.Add(s);
                    continue;
                }

                if (role == "tool")
                {
                    Append(messages, "user", new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message["tool_call_id"]?.GetValue<string>() ?? "",
                        ["content"] = TextOf(message["content"]),
                    });
                    continue;
                }

                var text = TextOf(message["content"]);
                if (!string.IsNullOrWhiteSpace(text))
                    Append(messages, role, new JsonObject { ["type"] = "text", ["text"] = text });

                if (message["tool_calls"] is JsonArray calls)
                    foreach (var c in calls)
                    {
                        if (c is not JsonObject call) continue;
                        var fn = call["function"] as JsonObject;
                        Append(messages, role, new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call["id"]?.GetValue<string>() ?? "",
                            ["name"] = fn?["name"]?.GetValue<string>() ?? "",
                            ["input"] = ParseObject(fn?["arguments"]?.GetValue<string>()),
                        });
                    }
            }

        var dst = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = false,
            // Anthropic требует max_tokens. К этому месту политика его уже проставила, но значение
            // может лежать под именем max_completion_tokens — у OpenAI это то же самое поле.
            ["max_tokens"] = Int32(src["max_tokens"]) ?? Int32(src["max_completion_tokens"]) ?? 1024,
        };

        if (system.Count > 0) dst["system"] = string.Join("\n", system);
        if (src["temperature"] is JsonValue tv && tv.TryGetValue<double>(out var temperature))
            dst["temperature"] = temperature;

        if (src["tools"] is JsonArray tools && tools.Count > 0)
        {
            var converted = new JsonArray();
            foreach (var t in tools)
            {
                var fn = (t as JsonObject)?["function"] as JsonObject;
                if (fn?["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;
                converted.Add(new JsonObject
                {
                    ["name"] = name,
                    ["description"] = fn["description"]?.GetValue<string>() ?? "",
                    ["input_schema"] = fn["parameters"]?.DeepClone() ?? new JsonObject(),
                });
            }
            if (converted.Count > 0) dst["tools"] = converted;
        }

        return dst.ToJsonString();
    }

    /// <summary>
    /// Кладёт блок в сообщение, сливая его с предыдущим той же роли.
    ///
    /// Anthropic требует чередования ролей, а у OpenAI подряд идущие ответы инструментов — обычное
    /// дело: агентский виток возвращает их отдельными сообщениями. Без слияния получился бы запрос,
    /// который отвергается по формату, то есть выбор «Claude» ломал бы именно агента.
    /// </summary>
    private static void Append(JsonArray messages, string role, JsonObject block)
    {
        if (messages.Count > 0 && messages[^1] is JsonObject last &&
            last["role"]?.GetValue<string>() == role && last["content"] is JsonArray blocks)
        {
            blocks.Add(block);
            return;
        }

        messages.Add(new JsonObject { ["role"] = role, ["content"] = new JsonArray { block } });
    }

    /// <summary>
    /// Ответ Anthropic → ответ в форме Chat Completions, для клиента, который ждёт choices.
    /// </summary>
    public static string ResponseToOpenAi(string anthropicJson)
    {
        if (Parse(anthropicJson) is not JsonObject src) return anthropicJson;
        if (src["content"] is not JsonArray content) return anthropicJson;

        var text = new List<string>();
        var toolCalls = new JsonArray();

        foreach (var b in content)
        {
            if (b is not JsonObject block) continue;
            switch (block["type"]?.GetValue<string>())
            {
                case "text":
                    if (block["text"]?.GetValue<string>() is { Length: > 0 } t) text.Add(t);
                    break;
                case "tool_use":
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = block["id"]?.GetValue<string>() ?? "",
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = block["name"]?.GetValue<string>() ?? "",
                            ["arguments"] = (block["input"] ?? new JsonObject()).ToJsonString(),
                        },
                    });
                    break;
            }
        }

        var message = new JsonObject { ["role"] = "assistant" };
        message["content"] = text.Count > 0 ? string.Join("\n", text) : null;
        if (toolCalls.Count > 0) message["tool_calls"] = toolCalls;

        var usage = src["usage"] as JsonObject;

        return new JsonObject
        {
            ["id"] = src["id"]?.GetValue<string>() ?? "",
            ["object"] = "chat.completion",
            ["model"] = src["model"]?.GetValue<string>() ?? "",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = message,
                    ["finish_reason"] = FinishReason(src["stop_reason"]?.GetValue<string>()),
                },
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = Int(usage?["input_tokens"]),
                ["completion_tokens"] = Int(usage?["output_tokens"]),
            },
        }.ToJsonString();
    }

    private static string? FinishReason(string? stop) => stop switch
    {
        "end_turn" or "stop_sequence" => "stop",
        "max_tokens" => "length",
        "tool_use" => "tool_calls",
        _ => stop,
    };

    private static int? Int32(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    /// <summary>Имя модели из тела запроса, чтобы перевод не терял выбор, уже сделанный сервером.</summary>
    public static string? ModelOf(string json) =>
        Parse(json) is JsonObject o && o["model"] is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : null;

    private static string? StopReason(string? finish) => finish switch
    {
        "stop" => "end_turn",
        "length" => "max_tokens",
        "tool_calls" or "function_call" => "tool_use",
        _ => finish,
    };

    private static long Int(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;

    private static JsonNode ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    /// <summary>
    /// Текст из того, что у Anthropic может быть строкой, массивом блоков или отсутствовать —
    /// system, содержимое сообщения и содержимое tool_result имеют одну и ту же вольную форму.
    /// </summary>
    private static string TextOf(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "";
            case JsonValue v when v.TryGetValue<string>(out var s):
                return s;
            case JsonArray arr:
                var parts = new List<string>();
                foreach (var item in arr)
                {
                    if (item is JsonValue iv && iv.TryGetValue<string>(out var istr)) parts.Add(istr);
                    else if (item is JsonObject o && o["text"]?.GetValue<string>() is { Length: > 0 } t) parts.Add(t);
                }
                return string.Join("\n", parts);
            default:
                return "";
        }
    }

    /// <summary>
    /// Мусор на входе обязан стать null, а не исключением: сюда приходит тело запроса от клиента и
    /// тело ответа от чужого сервиса, и ни то ни другое не должно валить конвейер запроса.
    /// </summary>
    private static JsonNode? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
