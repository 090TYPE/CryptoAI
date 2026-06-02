namespace CryptoAITerminal.AIEngine.Agent;

/// <summary>Kind of step emitted during an agent run, for the live UI log.</summary>
public enum AgentEventKind
{
    /// <summary>The model produced visible reasoning / commentary text.</summary>
    Text,
    /// <summary>The model decided to call a tool.</summary>
    ToolCall,
    /// <summary>A tool finished and returned a result to the model.</summary>
    ToolResult,
    /// <summary>The run finished (end_turn or limit reached).</summary>
    Done,
    /// <summary>Something went wrong (HTTP error, parse failure, cancellation).</summary>
    Error
}

/// <summary>
/// One step in an agent run. Surfaced to the host via a callback so the UI can
/// stream "🧠 thinking → 🔧 get_positions → ✅ result → 💰 place_order …".
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Title">Short label (tool name, or "thinking", or "done").</param>
/// <param name="Detail">Free text — reasoning, tool input JSON, or tool result.</param>
public sealed record AgentEvent(AgentEventKind Kind, string Title, string Detail);

/// <summary>
/// Final outcome of a single <see cref="ClaudeAgentRunner.RunAsync"/> turn.
/// </summary>
/// <param name="FinalText">The model's closing commentary (its portfolio view / rationale).</param>
/// <param name="ToolCallCount">How many tools were invoked across the loop.</param>
/// <param name="Iterations">How many model round-trips were made.</param>
/// <param name="StoppedReason">"end_turn", "max_iterations", "cancelled", or "error".</param>
public sealed record AgentRunResult(
    string FinalText,
    int ToolCallCount,
    int Iterations,
    string StoppedReason);
