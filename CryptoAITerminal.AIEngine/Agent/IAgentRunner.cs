namespace CryptoAITerminal.AIEngine.Agent;

/// <summary>
/// A vendor-neutral agentic loop: hand the model a system prompt, a user instruction
/// and a tool set, then service tool calls until it ends its turn. Implemented once
/// per vendor (<see cref="ClaudeAgentRunner"/>, <see cref="OpenAiAgentRunner"/>) and
/// selected by <see cref="AgentRunnerFactory"/> from <see cref="AiRuntime.Vendor"/>.
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Runs one agent turn. <paramref name="onEvent"/> streams progress (thinking,
    /// tool call, tool result) for the live UI log.
    /// </summary>
    Task<AgentRunResult> RunAsync(
        string systemPrompt,
        string userInstruction,
        IReadOnlyList<AgentTool> tools,
        Action<AgentEvent>? onEvent = null,
        CancellationToken ct = default);
}
