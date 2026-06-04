namespace CryptoAITerminal.AIEngine.Agent;

/// <summary>
/// Builds the right <see cref="IAgentRunner"/> for the active vendor. The two agent
/// hosts (Copilot, autonomous trader) call this instead of newing a vendor-specific
/// runner, so flipping <see cref="AiRuntime.Vendor"/> reroutes the tool-use loop too.
/// </summary>
public static class AgentRunnerFactory
{
    public static IAgentRunner Create(string apiKey, string? model = null, int maxIterations = 8, HttpClient? http = null)
        => AiRuntime.Vendor == AiVendor.OpenAi
            ? new OpenAiAgentRunner(apiKey, model, maxIterations, http)
            : new ClaudeAgentRunner(apiKey, model, maxIterations, http);
}
