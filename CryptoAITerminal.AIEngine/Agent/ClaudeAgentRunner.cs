using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoAITerminal.AIEngine.Agent;

/// <summary>
/// Agentic loop over the Anthropic Messages API with tool use. Unlike
/// <see cref="ClaudeSignalProvider"/> (a single buy/sell verdict), the runner
/// lets Claude drive: it inspects the market through tools, decides what to
/// look at next, and places trades by calling the host-supplied tools — i.e.
/// "the AI trades by itself". The host gates every money-touching tool
/// (paper/live, RiskManager, kill-switch); the runner only relays.
///
/// Hand-rolled HTTP, mirroring the rest of AIEngine, so the terminal keeps
/// building offline without an extra NuGet graph.
/// </summary>
public sealed class ClaudeAgentRunner
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxIterations;

    /// <param name="maxIterations">
    /// Hard cap on model round-trips per turn — bounds cost and prevents a runaway
    /// tool loop. Each iteration is one Claude call; the loop stops early on end_turn.
    /// </param>
    public ClaudeAgentRunner(string apiKey, string? model = null, int maxIterations = 8, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-6" : model;
        _maxIterations = Math.Clamp(maxIterations, 1, 24);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>
    /// Runs one agent turn: hands Claude the system prompt + a user instruction and
    /// the tool set, then services tool calls until the model ends its turn or the
    /// iteration cap is hit. <paramref name="onEvent"/> streams progress for the UI.
    /// </summary>
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
            name = t.Name,
            description = t.Description,
            input_schema = t.InputSchema
        }).ToArray();

        // Conversation transcript. Assistant turns are stored as the raw content
        // array returned by the API (cloned) so tool_result blocks reference valid
        // tool_use ids on the next request.
        var messages = new List<object>
        {
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
                system = systemPrompt,
                tools = toolDefs,
                messages
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, DefaultEndpoint)
            {
                Content = JsonContent.Create(payload)
            };
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", AnthropicVersion);

            string body;
            try
            {
                using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    onEvent?.Invoke(new AgentEvent(AgentEventKind.Error, "api", $"Anthropic API {(int)res.StatusCode}: {Truncate(body, 300)}"));
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

            var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;

            if (!root.TryGetProperty("content", out var contentArr) || contentArr.ValueKind != JsonValueKind.Array)
            {
                onEvent?.Invoke(new AgentEvent(AgentEventKind.Error, "parse", "No content array in response."));
                return new AgentRunResult(finalText, toolCalls, iteration, "error");
            }

            // Surface any visible reasoning text and collect tool_use blocks.
            var toolUses = new List<(string Id, string Name, JsonElement Input)>();
            foreach (var block in contentArr.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                if (type == "text" && block.TryGetProperty("text", out var tv))
                {
                    var text = tv.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        finalText = text.Trim();
                        onEvent?.Invoke(new AgentEvent(AgentEventKind.Text, "thinking", finalText));
                    }
                }
                else if (type == "tool_use")
                {
                    var id = block.TryGetProperty("id", out var idv) ? idv.GetString() ?? "" : "";
                    var name = block.TryGetProperty("name", out var nv) ? nv.GetString() ?? "" : "";
                    var input = block.TryGetProperty("input", out var inv) ? inv.Clone() : default;
                    toolUses.Add((id, name, input));
                }
            }

            // Echo the assistant turn back verbatim so tool_result ids resolve.
            messages.Add(new { role = "assistant", content = contentArr.Clone() });

            if (toolUses.Count == 0 || stopReason != "tool_use")
            {
                onEvent?.Invoke(new AgentEvent(AgentEventKind.Done, "done", finalText));
                return new AgentRunResult(finalText, toolCalls, iteration, stopReason ?? "end_turn");
            }

            // Execute every requested tool and assemble the tool_result user turn.
            var results = new List<object>();
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
                results.Add(new { type = "tool_result", tool_use_id = id, content = result });
            }

            messages.Add(new { role = "user", content = results });
        }

        onEvent?.Invoke(new AgentEvent(AgentEventKind.Done, "done", $"Reached max {_maxIterations} iterations."));
        return new AgentRunResult(finalText, toolCalls, _maxIterations, "max_iterations");
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
