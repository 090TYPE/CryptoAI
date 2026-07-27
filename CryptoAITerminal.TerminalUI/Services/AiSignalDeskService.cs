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

    /// <summary>
    /// True when a real model call is possible: a local key, or a bound server that holds one.
    /// Testing only for a local key left the whole AI Signals tab — feed, regime, digest,
    /// opportunities, insights, coach and Ask-AI — permanently on its hardcoded heuristic for a
    /// licensed user, who by design has no key of their own.
    /// </summary>
    public bool IsConfigured => ChatClient.CanCallModel(ApiKey);

    /// <summary>"Claude {model}" / "ChatGPT {model}" when live, else the offline label.</summary>
    public string SourceLabel => IsConfigured ? AiRuntime.ActiveSourceLabel : "offline · heuristic";

    /// <summary>User-safe reason the last call fell back to the offline path, or null on success.</summary>
    public string? LastError { get; private set; }

    /// <summary>Generate the full desk from live market context, or null to fall back.</summary>
    public async Task<AiSignalDeskResult?> GenerateAsync(AiSignalDeskContext ctx, CancellationToken ct = default)
    {
        // Cleared before the guard too: returning null for a missing key or an empty market list is
        // not a failure, so it must not surface the previous call's message.
        LastError = null;

        if (!IsConfigured || ctx?.Markets is null || ctx.Markets.Count == 0) return null;
        try
        {
            var provider = new AiSignalDeskProvider(ApiKey, Model);
            return await provider.GenerateAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = AiFailure.Describe(ex);
            return null;
        }
    }

    /// <summary>One-shot Ask-AI reply, or null to fall back to the canned heuristic.</summary>
    public async Task<string?> AskAsync(string system, string question, CancellationToken ct = default)
    {
        LastError = null;

        if (!IsConfigured || string.IsNullOrWhiteSpace(question)) return null;
        try
        {
            var reply = await ChatClient.CompleteTextAsync(
                ApiKey, Model, maxTokens: 500, temperature: 0.5,
                system: system, userContent: question,
                feature: AiFeatureIds.SignalDeskStream, ct: ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(reply) ? null : reply.Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = AiFailure.Describe(ex);
            return null;
        }
    }
}
