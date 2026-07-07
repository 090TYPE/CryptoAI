using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine.Agent;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>A mutating action awaiting user approval.</summary>
public sealed record ActionProposal(string ActionId, string Preview, JsonElement Args);

/// <summary>
/// Agentic replacement for the read-only copilot loop. Read actions run immediately;
/// mutating actions either queue as a proposal (CONFIRM) or run now (AUTO). Money-gates
/// live in the context implementation, not here.
/// </summary>
public sealed class AppAgentService
{
    private readonly AppActionRegistry _registry;
    private readonly IAppActionContext _ctx;
    private readonly Func<ActionProposal, Task<AppActionResult>> _proposalSink;
    private readonly Func<bool> _autoMode;
    private readonly AppActionAuditLog? _audit;

    public AppAgentService(
        AppActionRegistry registry,
        IAppActionContext ctx,
        Func<ActionProposal, Task<AppActionResult>> proposalSink,
        Func<bool> autoMode,
        AppActionAuditLog? audit = null)
    {
        _registry = registry;
        _ctx = ctx;
        _proposalSink = proposalSink;
        _autoMode = autoMode;
        _audit = audit;
    }

    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    private string Key => ApiKey ?? CryptoAITerminal.AIEngine.AiRuntime.ActiveApiKey;
    private string Mdl => Model ?? CryptoAITerminal.AIEngine.AiRuntime.ActiveModel;
    public bool UsesLiveModel => !string.IsNullOrWhiteSpace(Key);
    public event Action<AgentEvent>? OnEvent;

    /// <summary>Build one AgentTool per action; the delegate routes through <see cref="InvokeActionToolAsync"/>.</summary>
    public IReadOnlyList<AgentTool> BuildTools() =>
        _registry.All.Select(a => new AgentTool(
            AppActionRegistry.ToolName(a.Id),
            a.Description + (a.IsMutating ? " (mutating — needs user confirmation unless auto mode is on)" : ""),
            a.ParamSchema,
            (input, ct) => InvokeActionToolAsync(AppActionRegistry.ToolName(a.Id), input, ct))).ToList();

    /// <summary>Core routing seam (also called directly by unit tests). Returns the model-facing string.</summary>
    public async Task<string> InvokeActionToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        var action = _registry.FindByToolName(toolName);
        if (action is null) return Json(AppActionResult.Fail($"unknown action '{toolName}'"));

        var preview = SafePreview(action, args);

        if (action.IsMutating && !_autoMode())
        {
            _audit?.Record(action.Id, preview, "proposed", null);
            var queued = await _proposalSink(new ActionProposal(action.Id, preview, args)).ConfigureAwait(false);
            return Json(AppActionResult.Ok($"Proposed for confirmation: {preview}. {queued.Message}"));
        }

        AppActionResult result;
        try { result = await action.ExecuteAsync(args, _ctx, ct).ConfigureAwait(false); }
        catch (Exception ex) { result = AppActionResult.Fail(ex.Message); }

        _audit?.Record(action.Id, preview, result.Success ? "executed" : "failed", result.Detail ?? result.Message);
        return Json(result);
    }

    /// <summary>Approve a queued proposal — executes it now (called by the tray on user OK).</summary>
    public async Task<AppActionResult> ExecuteApprovedAsync(ActionProposal p, CancellationToken ct = default)
    {
        var action = _registry.Find(p.ActionId);
        if (action is null) return AppActionResult.Fail($"unknown action '{p.ActionId}'");
        AppActionResult result;
        try { result = await action.ExecuteAsync(p.Args, _ctx, ct).ConfigureAwait(false); }
        catch (Exception ex) { result = AppActionResult.Fail(ex.Message); }
        _audit?.Record(action.Id, p.Preview, result.Success ? "executed" : "failed", result.Detail ?? result.Message);
        return result;
    }

    /// <summary>Run one natural-language turn (network). Falls back to a plain message when no key.</summary>
    public async Task<string> RunTurnAsync(string instruction, CancellationToken ct = default)
    {
        if (!UsesLiveModel)
            return "Add a Claude/OpenAI API key in the AI Bot panel to let the assistant act.";
        var runner = AgentRunnerFactory.Create(Key, Mdl, maxIterations: 10);
        var result = await runner.RunAsync(SystemPrompt(), instruction, BuildTools(), OnEvent, ct).ConfigureAwait(false);
        return result.FinalText;
    }

    private static string SystemPrompt() =>
        "You are the built-in agent for CryptoAI Terminal. You can navigate the app, read account/market data, " +
        "and (with the user's confirmation) fill tickets, arm and place orders, set alerts, apply signals, and configure bots. " +
        "Use read/navigation tools freely. For any mutating tool, the app will ask the user to approve unless auto mode is on — " +
        "call the tool and tell the user what you proposed. Never claim an order filled unless the tool result says it did. " +
        "Be concise. Not financial advice.";

    private static string SafePreview(IAppAction a, JsonElement args)
    {
        try { return a.Preview(args); } catch { return a.Id; }
    }

    private static string Json(AppActionResult r) => JsonSerializer.Serialize(new { ok = r.Success, message = r.Message, detail = r.Detail });
}
