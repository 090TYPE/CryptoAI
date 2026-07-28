namespace CryptoAITerminal.Server.Common;

/// <summary>
/// Where AI requests go when nothing overrides it.
///
/// In Common rather than beside the proxy because these are configuration facts, not proxy
/// internals: the admin surface shows them as the current value, and a test can assert them
/// without the data layer. A wrong constant here sends every request carrying the server's key
/// somewhere that is not the vendor, so it is worth having one place and a test on it.
/// </summary>
public static class AiProxyDefaults
{
    public const string Anthropic = "https://api.anthropic.com/v1/messages";
    public const string OpenAi = "https://api.openai.com/v1/chat/completions";

    /// <summary>
    /// True when the URL is not the vendor's own host, which in practice means a router — and
    /// every router authenticates with a bearer token rather than Anthropic's x-api-key. Used as
    /// the default so that changing the URL is enough on its own; getting it wrong produces a 401
    /// that reads like a bad key.
    /// </summary>
    public static bool LooksLikeRouter(string url, string vendorUrl) =>
        !string.Equals(url, vendorUrl, StringComparison.OrdinalIgnoreCase);
}
