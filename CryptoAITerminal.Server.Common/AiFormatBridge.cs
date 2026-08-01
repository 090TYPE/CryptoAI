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

        // Имя поля и температура зависят от модели, а не от того, что было в исходном теле: новые
        // линейки OpenAI max_tokens не принимают вовсе, а температуру принимают только равной
        // единице. Терминал шлёт и то и другое всегда, поэтому решение принимается здесь.
        var legacyRejected = AiModelCatalog.RejectsLegacyParameters(model);
        if (src["max_tokens"] is JsonValue mt && mt.TryGetValue<int>(out var maxTokens))
            dst[legacyRejected ? "max_completion_tokens" : "max_tokens"] = maxTokens;
        if (!legacyRejected && src["temperature"] is JsonValue tv && tv.TryGetValue<double>(out var temperature))
            dst["temperature"] = temperature;

        if (src["tools"] is JsonArray tools && tools.Count > 0)
        {
            var converted = new JsonArray();
            foreach (var t in tools)
            {
                if (t is not JsonObject tool || Str(tool["name"]) is not { Length: > 0 } name)
                    continue;

                converted.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = name,
                        ["description"] = Str(tool["description"]) ?? "",
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
        var role = Str(message["role"]) ?? "user";

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
            switch (Str(block["type"]))
            {
                case "text":
                    if (Str(block["text"]) is { Length: > 0 } t) text.Add(t);
                    break;

                case "tool_use":
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = Str(block["id"]) ?? "",
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = Str(block["name"]) ?? "",
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
                        ["tool_call_id"] = Str(block["tool_use_id"]) ?? "",
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
                var args = Str(fn?["arguments"]);

                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = Str(call["id"]) ?? "",
                    ["name"] = Str(fn?["name"]) ?? "",
                    // Строка с JSON внутри разворачивается обратно в объект; если провайдер прислал
                    // невалидный JSON, пустой объект честнее, чем строка там, где ждут структуру.
                    //
                    // Объект принимается и как объект: по спецификации OpenAI здесь строка, но
                    // роутеры — ради которых этот мост и написан — присылают и структуру. Раньше
                    // такой ответ ронял перевод; отдать пустые аргументы вместо настоящих значило
                    // бы вызвать инструмент агента не с теми параметрами.
                    ["input"] = fn?["arguments"] is JsonObject direct ? direct.DeepClone() : ParseObject(args),
                });
            }

        var usage = src["usage"] as JsonObject;

        return new JsonObject
        {
            ["id"] = Str(src["id"]) ?? "",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = Str(src["model"]) ?? "",
            ["content"] = content,
            ["stop_reason"] = StopReason(Str(choice["finish_reason"])),
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
                var role = Str(message["role"]) ?? "user";

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
                        ["tool_use_id"] = Str(message["tool_call_id"]) ?? "",
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
                            ["id"] = Str(call["id"]) ?? "",
                            ["name"] = Str(fn?["name"]) ?? "",
                            // Как и в обратную сторону: аргументы приходят и строкой, и объектом.
                            ["input"] = fn?["arguments"] is JsonObject argObj
                                ? argObj.DeepClone()
                                : ParseObject(Str(fn?["arguments"])),
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
                if (Str(fn?["name"]) is not { Length: > 0 } name) continue;
                converted.Add(new JsonObject
                {
                    ["name"] = name,
                    // Через fn?[...]: проверка выше сужала fn по старому выражению, а Str такого
                    // сужения не даёт — компилятор прав, что здесь возможен null.
                    ["description"] = Str(fn?["description"]) ?? "",
                    ["input_schema"] = fn?["parameters"]?.DeepClone() ?? new JsonObject(),
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
            Str(last["role"]) == role && last["content"] is JsonArray blocks)
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
            switch (Str(block["type"]))
            {
                case "text":
                    if (Str(block["text"]) is { Length: > 0 } t) text.Add(t);
                    break;
                case "tool_use":
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = Str(block["id"]) ?? "",
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = Str(block["name"]) ?? "",
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
            ["id"] = Str(src["id"]) ?? "",
            ["object"] = "chat.completion",
            ["model"] = Str(src["model"]) ?? "",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = message,
                    ["finish_reason"] = FinishReason(Str(src["stop_reason"])),
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
                    else if (item is JsonObject o && Str(o["text"]) is { Length: > 0 } t) parts.Add(t);
                }
                return string.Join("\n", parts);
            default:
                return "";
        }
    }

    /// <summary>
    /// Строка из узла, или null, если там что угодно другое.
    ///
    /// Существует потому, что <c>GetValue&lt;string&gt;()</c> на числе бросает
    /// <see cref="InvalidOperationException"/>, а не <see cref="JsonException"/> — то есть мимо
    /// единственного catch в <see cref="Parse"/>. Тело <c>{"messages":[{"role":1}]}</c> проходит
    /// проверку политики (она форму сообщений не смотрит) и роняет перевод формата: клиент получает
    /// 500 вместо 400, а повтор в терминале считает 500 возвратным отказом и шлёт то же тело ещё
    /// раз. На стороне ОТВЕТА хуже: падение случается после вызова вендора, но до
    /// <c>budget.Charge</c> и записи в <c>ai_usage</c> — оплаченные токены не попадают никуда,
    /// а именно из этих строк потом считается тариф.
    ///
    /// Ровно этот же класс отказа соседний <c>AiRequestPolicy.Apply</c> уже ловит отдельным
    /// <c>catch (InvalidOperationException)</c>; сюда это просто не распространили.
    /// </summary>
    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

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
