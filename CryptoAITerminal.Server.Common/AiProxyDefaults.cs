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
    public static bool LooksLikeRouter(string url, string vendorUrl)
    {
        // Сравнивается ХОСТ, а не строка целиком. Со строкой лишний слэш в конце адреса — а его
        // ставят руками в поле админки — делал api.openai.com «роутером», и дальше по цепочке
        // AiProxy подставлял ключ anthropic и отправлял его bearer-заголовком настоящему OpenAI.
        // То есть опечатка раскрывала ключ одного вендора другому, а ответ 401 читался как «плохой
        // ключ».
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            !Uri.TryCreate(vendorUrl, UriKind.Absolute, out var vendor))
            return !string.Equals(url, vendorUrl, StringComparison.OrdinalIgnoreCase);

        return !string.Equals(parsed.Host, vendor.Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where to ask an upstream what it actually serves, derived from the address already
    /// configured for it. Anthropic, OpenAI and every router in between expose the same
    /// <c>/v1/models</c> and answer in the same shape.
    ///
    /// This exists because the model names are the one part of a provider switch that cannot be
    /// guessed: they are visible only inside the provider's own dashboard, and a wrong guess fails
    /// as "model is not allowed" or a 404 from upstream — neither of which says what the right name
    /// would have been. Asking the provider turns that into a list to pick from.
    /// </summary>
    public static string ModelsUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return baseUrl;

        // Anchor on the version segment rather than the last one: the OpenAI path has two segments
        // after it (/v1/chat/completions), so trimming a single segment would ask for
        // /v1/chat/models.
        var version = baseUrl.LastIndexOf("/v1/", StringComparison.OrdinalIgnoreCase);
        if (version >= 0) return string.Concat(baseUrl.AsSpan(0, version + 4), "models");

        var slash = baseUrl.LastIndexOf('/');
        return slash > 0 ? string.Concat(baseUrl.AsSpan(0, slash + 1), "models") : baseUrl;
    }
}
