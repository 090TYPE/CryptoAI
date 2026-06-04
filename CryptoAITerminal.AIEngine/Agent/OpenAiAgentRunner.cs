using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine.Agent;

/// <summary>
/// OpenAI (ChatGPT) counterpart to <see cref="ClaudeAgentRunner"/>: the same agentic
/// tool-use loop, expressed in the OpenAI Chat Completions function-calling format
/// (tools as <c>function</c> specs, assistant <c>tool_calls</c>, <c>role:"tool"</c>
/// results). Emits the identical <see cref="AgentEvent"/> stream so the UI log and the
/// host (Copilot, autonomous trader) are vendor-agnostic.
/// </summary>
public sealed class OpenAiAgentRunner : IAgentRunner
{
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxIterations;

    public OpenAiAgentRunner(string apiKey, string? model = null, int maxIterations = 8, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("OpenAI API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-4o" : model;
        _maxIterations = Math.Clamp(maxIterations, 1, 24);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<AgentRunResult> RunAsync(
        string systemPrompt,
        string userInstruction,
        IReadOnlyList<AgentTool> tools,
        Action<AgentEvent>? onEvent = null,
        CancellationToken ct = default)
    {
        var toolMap = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var toolDefs = tools.Select(t => new
        {
            type = "function",
            function = new { name = t.Name, description = t.Description, parameters = t.InputSchema }
        }).ToArray();

        // OpenAI keeps the system prompt as the first message, not a top-level field.
        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userInstruction }
        };

        int toolCalls = 0;
        var finalText = "";

        for (int iteration = 1; iteration <= _maxIterations; iteration++)
        {
            if (ct.IsCancellationRequested)
            {
                onEvent?.Invoke(new AgentEvent(AgentEventKind.Error, "cancelled", "Run cancelled."));
                return new AgentRunResult(finalText, toolCalls, iteration - 1, "cancelled");
            }

            var payload = new
            {
                model = _model,
                max_tokens = 1024,
                messages,
                tools = toolDefs,
                tool_choice = "auto"
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(payload)
            };
            req.Headers.Add("Authorization", "Bearer " + _apiKey);

            string body;
            try
            {
                using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    onEvent?.Invoke(new AgentEvent(AgentEventKind.Error, "api", $"OpenAI API {(int)res.StatusCode}: {Truncate(body, 300)}"));
                    return new AgentRunResult(finalText, toolCalls, iteration, "error");
                }
            }
            catch (OperationCanceledException)
            {
                onEvent?.Invoke(new AgentEvent(AgentEventKind.Error, "cancelled", "Run cancelled."));
                return new AgentRunResult(finalText, toolCalls, iteration, "cancelled");
            }
            catch (Exception ex)
            {
                onEvent?.Invoke(new AgentEvent(AgentEventKind.Error, "http", ex.Message));
                return new AgentRunResult(finalText, toolCalls, iteration, "error");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                onEvent?.Invoke(new AgentEvent(AgentEventKind.Error, "parse", "No choices in response."));
                return new AgentRunResult(finalText, toolCalls, iteration, "error");
            }

            var choice = choices[0];
            var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
            if (!choice.TryGetProperty("message", out var message))
            {
                onEvent?.Invoke(new AgentEvent(AgentEventKind.Error, "parse", "No message in choice."));
                return new AgentRunResult(finalText, toolCalls, iteration, "error");
            }

            // Visible assistant text (may be null when the model only calls tools).
            var contentText = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(contentText))
            {
                finalText = contentText.Trim();
                onEvent?.Invoke(new AgentEvent(AgentEventKind.Text, "thinking", finalText));
            }

            // Collect tool calls.
            var toolUses = new List<(string Id, string Name, JsonElement Input)>();
            if (message.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    var id = tc.TryGetProperty("id", out var idv) ? idv.GetString() ?? "" : "";
                    var fn = tc.TryGetProperty("function", out var fnv) ? fnv : default;
                    var name = fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("name", out var nv)
                        ? nv.GetString() ?? "" : "";
                    var args = fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("arguments", out var av)
                        ? av.GetString() ?? "" : "";
                    toolUses.Add((id, name, ParseArgs(args)));
                }
            }

            // Echo the assistant turn so tool results resolve against their call ids.
            messages.Add(BuildAssistantEcho(contentText, message));

            if (toolUses.Count == 0 || finishReason != "tool_calls")
            {
                onEvent?.Invoke(new AgentEvent(AgentEventKind.Done, "done", finalText));
                return new AgentRunResult(finalText, toolCalls, iteration, finishReason ?? "stop");
            }

            foreach (var (id, name, input) in toolUses)
            {
                toolCalls++;
                var inputText = input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText();
                onEvent?.Invoke(new AgentEvent(AgentEventKind.ToolCall, name, Truncate(inputText, 400)));

                string result;
                try
                {
                    result = toolMap.TryGetValue(name, out var tool)
                        ? await tool.ExecuteAsync(input, ct).ConfigureAwait(false)
                        : $"{{\"error\":\"unknown tool '{name}'\"}}";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = $"{{\"error\":{JsonSerializer.Serialize(ex.Message)}}}";
                }

                onEvent?.Invoke(new AgentEvent(AgentEventKind.ToolResult, name, Truncate(result, 400)));
                messages.Add(new { role = "tool", tool_call_id = id, content = result });
            }
        }

        onEvent?.Invoke(new AgentEvent(AgentEventKind.Done, "done", $"Reached max {_maxIterations} iterations."));
        return new AgentRunResult(finalText, toolCalls, _maxIterations, "max_iterations");
    }

    /// <summary>
    /// Re-sends the assistant turn. We keep only the fields the API needs to thread the
    /// conversation (content + tool_calls) and clone tool_calls verbatim so their ids
    /// match the role:"tool" results we append next.
    /// </summary>
    private static object BuildAssistantEcho(string contentText, JsonElement message)
    {
        if (message.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            return new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = string.IsNullOrEmpty(contentText) ? null : contentText,
                ["tool_calls"] = tcs.Clone()
            };

        return new { role = "assistant", content = contentText };
    }

    private static JsonElement ParseArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return default;
        try
        {
            using var d = JsonDocument.Parse(args);
            return d.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
