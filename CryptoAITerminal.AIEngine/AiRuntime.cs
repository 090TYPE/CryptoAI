namespace CryptoAITerminal.AIEngine;

/// <summary>Which LLM vendor the terminal's AI features talk to.</summary>
public enum AiVendor
{
    /// <summary>Anthropic Claude (the original/default provider).</summary>
    Anthropic,
    /// <summary>OpenAI ChatGPT.</summary>
    OpenAi
}

/// <summary>
/// Ambient, process-wide AI provider configuration — the single place that decides
/// whether the terminal's ~15 AI features call Claude or ChatGPT, and with which key
/// and model. Every provider and agent runner reads <see cref="Vendor"/> from here, so
/// the user's choice in Settings applies everywhere at once.
///
/// Keys and models are read from environment variables on every access (so a change
/// saved in Settings — which updates the process env — takes effect immediately, no
/// restart). <see cref="CredentialsService"/> in the host populates those env vars at
/// startup from the encrypted credentials file. <see cref="Vendor"/> is a settable
/// latch that defaults from the <c>CRYPTOAI_AI_PROVIDER</c> env var.
/// </summary>
public static class AiRuntime
{
    private static AiVendor? _vendor;

    /// <summary>
    /// The active vendor. Defaults from <c>CRYPTOAI_AI_PROVIDER</c> ("openai"/"chatgpt"
    /// → OpenAI, anything else → Anthropic). The Settings panel sets this directly so a
    /// provider switch is live without a restart.
    /// </summary>
    public static AiVendor Vendor
    {
        get => _vendor ??= ParseVendor(Environment.GetEnvironmentVariable("CRYPTOAI_AI_PROVIDER"));
        set => _vendor = value;
    }

    /// <summary>Anthropic API key from env (ANTHROPIC_API_KEY, then legacy CRYPTOAI_CLAUDE_KEY).</summary>
    public static string AnthropicKey =>
        FirstNonEmpty(
            Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
            Environment.GetEnvironmentVariable("CRYPTOAI_CLAUDE_KEY"));

    /// <summary>OpenAI API key from env (OPENAI_API_KEY, then legacy CRYPTOAI_OPENAI_KEY).</summary>
    public static string OpenAiKey =>
        FirstNonEmpty(
            Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            Environment.GetEnvironmentVariable("CRYPTOAI_OPENAI_KEY"));

    /// <summary>Anthropic model id (CRYPTOAI_CLAUDE_MODEL or a sensible default).</summary>
    public static string AnthropicModel =>
        FirstNonEmpty(Environment.GetEnvironmentVariable("CRYPTOAI_CLAUDE_MODEL"), "claude-sonnet-4-6");

    /// <summary>OpenAI model id (CRYPTOAI_OPENAI_MODEL or a sensible default).</summary>
    public static string OpenAiModel =>
        FirstNonEmpty(Environment.GetEnvironmentVariable("CRYPTOAI_OPENAI_MODEL"), "gpt-4o");

    /// <summary>The API key for the active vendor.</summary>
    public static string ActiveApiKey => Vendor == AiVendor.OpenAi ? OpenAiKey : AnthropicKey;

    /// <summary>The model id for the active vendor.</summary>
    public static string ActiveModel => Vendor == AiVendor.OpenAi ? OpenAiModel : AnthropicModel;

    /// <summary>Short human label for the active vendor ("Claude" / "ChatGPT").</summary>
    public static string VendorLabel => Vendor == AiVendor.OpenAi ? "ChatGPT" : "Claude";

    /// <summary>"Claude {model}" / "ChatGPT {model}" — used as the Source badge on AI results.</summary>
    public static string ActiveSourceLabel => $"{VendorLabel} {ActiveModel}";

    /// <summary>True when the active vendor has a key configured.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ActiveApiKey);

    /// <summary>Maps a stored/raw provider string to a vendor (defaults to Anthropic).</summary>
    public static AiVendor ParseVendor(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "openai" or "chatgpt" or "gpt" or "oai" => AiVendor.OpenAi,
            _ => AiVendor.Anthropic
        };

    /// <summary>Canonical lowercase token for persistence ("anthropic"/"openai").</summary>
    public static string ToToken(AiVendor v) => v == AiVendor.OpenAi ? "openai" : "anthropic";

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v!;
        return string.Empty;
    }
}
