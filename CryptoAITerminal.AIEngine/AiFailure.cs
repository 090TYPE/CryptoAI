using System.Text.Json;

namespace CryptoAITerminal.AIEngine;

/// <summary>
/// A failed AI call, carrying the HTTP status and the error CODE separately from the response body.
///
/// The body is deliberately not part of <see cref="UserMessage"/>: the CryptoAI server and both
/// vendors put internals in it, and the old <c>HttpRequestException($"... {body}")</c> meant any
/// caller that surfaced <c>ex.Message</c> would leak them into the terminal UI.
///
/// Derives from <see cref="HttpRequestException"/> so every existing catch clause keeps working.
/// </summary>
public sealed class AiCallException : HttpRequestException
{
    public int StatusCode { get; }

    /// <summary>Machine-readable code when the response carried one (e.g. <c>ai_daily_budget_exhausted</c>).</summary>
    public string? ErrorCode { get; }

    /// <summary>Safe to show a user verbatim. Never contains the response body.</summary>
    public string UserMessage { get; }

    private AiCallException(string message, int status, string? errorCode, string userMessage)
        : base(message)
    {
        StatusCode = status;
        ErrorCode = errorCode;
        UserMessage = userMessage;
    }

    /// <summary>
    /// Build from a non-success response. <paramref name="body"/> is parsed for an error code and
    /// then discarded — it reaches the exception's diagnostic <c>Message</c> only, never the user
    /// message.
    /// </summary>
    public static AiCallException FromResponse(string vendor, int status, string body)
    {
        var code = TryReadErrorCode(body);
        return new AiCallException(
            $"{vendor} API {status}: {body}",
            status,
            code,
            DescribeStatus(status, code));
    }

    /// <summary>
    /// Our server answers <c>{"error":"ai_daily_budget_exhausted"}</c>; the vendors answer
    /// <c>{"error":{"type":"...","message":"..."}}</c>. Read either, guess nothing.
    /// </summary>
    private static string? TryReadErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var err)) return null;

            return err.ValueKind switch
            {
                JsonValueKind.String => err.GetString(),
                JsonValueKind.Object when err.TryGetProperty("type", out var t) => t.GetString(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DescribeStatus(int status, string? code) => (status, code) switch
    {
        (_, "ai_daily_budget_exhausted") => "Daily AI limit reached. It resets at 00:00 UTC.",
        (_, "ai_key_not_configured") => "AI is not set up on the server yet.",
        (_, "license_expired") => "Your license has expired — renew it to use AI.",
        (_, "license_invalid") => "The server did not accept this license.",
        (401 or 402, _) => "The server did not accept this license.",
        (403, _) => "This license is not allowed to use AI.",
        (413, _) => "Too much data for one AI request.",
        (429, _) => "Too many AI requests — try again in a moment.",
        (400, _) => "The AI service rejected this request.",
        (>= 500, _) => "The AI service is temporarily unavailable.",
        _ => "AI is unavailable right now.",
    };
}

/// <summary>
/// Turns any exception from an AI call into one short line safe to show a user. The point is that
/// six very different failures — a rejected model, an oversized request, an exhausted daily budget,
/// an unconfigured server key, a timeout, an unreachable server — used to be indistinguishable:
/// every service caught <c>Exception</c> and silently returned its offline heuristic, so a user who
/// had run out of budget got no indication of it at all.
/// </summary>
public static class AiFailure
{
    /// <summary>A user-safe one-liner. Never includes a response body or a stack trace.</summary>
    public static string Describe(Exception? ex) => ex switch
    {
        null => "AI is unavailable right now.",
        AiCallException ai => ai.UserMessage,
        // A cancellation the caller did not request is an HttpClient timeout. Callers that own a
        // real CancellationToken rethrow before reaching here.
        OperationCanceledException => "The AI request timed out.",
        HttpRequestException => "Could not reach the AI service.",
        _ => "AI is unavailable right now.",
    };

    /// <summary>True when retrying later is likely to help (timeouts, rate limits, server outages).</summary>
    public static bool IsTransient(Exception? ex) => ex switch
    {
        AiCallException ai => ai.StatusCode is 429 or >= 500,
        OperationCanceledException => true,
        HttpRequestException => true,
        _ => false,
    };
}
