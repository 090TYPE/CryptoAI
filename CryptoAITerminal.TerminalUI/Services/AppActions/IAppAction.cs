using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>Broad grouping of an action, used for UI badges and tool namespacing.</summary>
public enum AppActionCategory { Navigation, Read, Trading, Signals, Bots, Settings, Wallet }

/// <summary>Structured outcome of an action, fed to both the model and the UI.</summary>
public sealed record AppActionResult(bool Success, string Message, string? Detail = null)
{
    public static AppActionResult Ok(string message, string? detail = null) => new(true, message, detail);
    public static AppActionResult Fail(string message, string? detail = null) => new(false, message, detail);
}

/// <summary>
/// One thing the AI can do in the app. Metadata + logic only; all app effects go through
/// <see cref="IAppActionContext"/>, so actions are unit-testable with a fake context.
/// </summary>
public interface IAppAction
{
    /// <summary>snake_case id the model sees, e.g. "nav.goto" → tool name "nav_goto".</summary>
    string Id { get; }
    AppActionCategory Category { get; }
    /// <summary>One/two sentences teaching the model when to call it.</summary>
    string Description { get; }
    /// <summary>JSON-schema object for the input (same shape as AgentTool.InputSchema).</summary>
    object ParamSchema { get; }
    /// <summary>True if the action changes state (needs confirmation in CONFIRM mode).</summary>
    bool IsMutating { get; }
    /// <summary>Human sentence shown before execution, e.g. "Arm BUY LIMIT 0.5 ETH @ 3200".</summary>
    string Preview(JsonElement args);
    Task<AppActionResult> ExecuteAsync(JsonElement args, IAppActionContext ctx, CancellationToken ct);
}
