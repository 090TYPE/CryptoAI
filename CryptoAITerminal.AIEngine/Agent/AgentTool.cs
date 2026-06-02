using System.Text.Json;

namespace CryptoAITerminal.AIEngine.Agent;

/// <summary>
/// A single tool exposed to Claude in the agentic trading loop.
///
/// The engine stays dependency-light: a tool is just a name, a description,
/// a JSON-schema for its input, and an async delegate that runs it. The
/// money-sensitive wiring (gateway calls, RiskManager gating, paper/live
/// mode) lives in the host (TerminalUI) which constructs these delegates —
/// the engine never decides whether a trade is allowed, it only relays
/// whatever the host's delegate returns back to the model as a tool_result.
/// </summary>
public sealed class AgentTool
{
    /// <summary>Tool name as the model sees it (snake_case, e.g. "place_order").</summary>
    public string Name { get; }

    /// <summary>One/two sentence description — this is what teaches the model when to call it.</summary>
    public string Description { get; }

    /// <summary>
    /// JSON-schema object for the tool input (the "input_schema" field in the
    /// Anthropic tools API). Pass an anonymous object, e.g.
    /// <c>new { type = "object", properties = new { symbol = new { type = "string" } }, required = new[] { "symbol" } }</c>.
    /// </summary>
    public object InputSchema { get; }

    private readonly Func<JsonElement, CancellationToken, Task<string>> _execute;

    public AgentTool(
        string name,
        string description,
        object inputSchema,
        Func<JsonElement, CancellationToken, Task<string>> execute)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tool name required.", nameof(name));
        Name = name;
        Description = description ?? "";
        InputSchema = inputSchema ?? new { type = "object", properties = new { } };
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>
    /// Runs the tool. The returned string is fed back to the model verbatim as the
    /// tool_result content, so it should be compact JSON or a short human-readable line.
    /// Implementations should never throw — return an error string instead so the model
    /// can recover; the runner also guards against exceptions defensively.
    /// </summary>
    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
        => _execute(input, ct);
}
