using System;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Host-side seam for the AI Signal desk. Wraps <see cref="AiSignalDeskProvider"/>
/// (structured desk generation) and a one-shot chat call, both routed through the
/// vendor-aware <see cref="ChatClient"/>/<see cref="AiRuntime"/>. Every method
/// degrades to <c>null</c> on missing key or any error so the ViewModel can fall
/// back to its deterministic demo desk / canned reply.
/// </summary>
public sealed class AiSignalDeskService
{
    private string? _apiKey;
    public string ApiKey { get => _apiKey ?? AiRuntime.ActiveApiKey; set => _apiKey = value; }

    private string? _model;
    public string Model { get => _model ?? AiRuntime.ActiveModel; set => _model = value; }

    /// <summary>True when the active vendor has a key configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>"Claude {model}" / "ChatGPT {model}" when live, else the offline label.</summary>
    public string SourceLabel => IsConfigured ? AiRuntime.ActiveSourceLabel : "offline · heuristic";

    /// <summary>Generate the full desk from live market context, or null to fall back.</summary>
    public async Task<AiSignalDeskResult?> GenerateAsync(AiSignalDeskContext ctx, CancellationToken ct = default)
    {
        if (!IsConfigured || ctx?.Markets is null || ctx.Markets.Count == 0) return null;
        try
        {
            var provider = new AiSignalDeskProvider(ApiKey, Model);
            return await provider.GenerateAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>One-shot Ask-AI reply, or null to fall back to the canned heuristic.</summary>
    public async Task<string?> AskAsync(string system, string question, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(question)) return null;
        try
        {
            var reply = await ChatClient.CompleteTextAsync(
                ApiKey, Model, maxTokens: 500, temperature: 0.5,
                system: system, userContent: question, ct: ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(reply) ? null : reply.Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }
}
