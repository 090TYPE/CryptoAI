using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>One message in the copilot chat transcript.</summary>
public sealed class CopilotMessageVM
{
    public bool   IsUser { get; init; }
    public string Text   { get; init; } = "";
    public string Source { get; init; } = "";

    public string RoleLabel => IsUser ? "You" : "Copilot";
    public string RoleBrush  => IsUser ? SemanticColor.Muted : SemanticColor.Accent;
    public string Align      => IsUser ? "Right" : "Left";
    public bool   HasSource  => !string.IsNullOrWhiteSpace(Source);
}

/// <summary>
/// UI for the read-only AI copilot. The user types a question, Claude answers by
/// inspecting the account/market through read-only tools (see
/// <see cref="CopilotAgentService"/>). The copilot can never trade — it is advisory.
/// Without a key it answers from the offline assistant, so the panel always works.
/// The Claude key/model are shared from the AI Bot tab via <see cref="ConfigureAi"/>,
/// matching every other AI feature.
/// </summary>
public sealed class CopilotViewModel : ReactiveObject
{
    private readonly CopilotAgentService _service;
    private readonly AppAgentService? _agent;

    private string _question = string.Empty;
    private bool _isBusy;
    private bool _isAutoMode;
    private bool _autoConsented;
    private bool _showAutoConsent;

    /// <summary>The approval tray for mutating actions the agent proposes (null in read-only mode).</summary>
    public AgentActionTrayViewModel? Tray { get; }

    /// <summary>True when an agent is wired in, so the copilot can act (not just advise).</summary>
    public bool CanAct => _agent is not null;

    /// <summary>True while the inline AUTO consent strip is asking the user to confirm.</summary>
    public bool ShowAutoConsent { get => _showAutoConsent; private set => this.RaiseAndSetIfChanged(ref _showAutoConsent, value); }

    public bool IsAutoMode
    {
        get => _isAutoMode;
        set
        {
            if (value && !_autoConsented) { ShowAutoConsent = true; this.RaisePropertyChanged(nameof(IsAutoMode)); return; }
            this.RaiseAndSetIfChanged(ref _isAutoMode, value);
            if (!value) { _autoConsented = false; ShowAutoConsent = false; }
            this.RaisePropertyChanged(nameof(AutoModeWarning));
        }
    }

    public string AutoModeWarning => _isAutoMode
        ? "AUTO ON — the agent places orders without asking. Risk/wallet/testnet gates still apply."
        : "AUTO off — every order waits for your approval.";

    public CopilotViewModel(CopilotAgentService.CopilotDataSource dataSource)
    {
        _service = new CopilotAgentService(dataSource);

        AskCommand = ReactiveCommand.CreateFromTask(AskAsync, outputScheduler: App.UiScheduler);
        AskSuggestionCommand = ReactiveCommand.CreateFromTask<string>(AskSuggestionAsync, outputScheduler: App.UiScheduler);
        ClearCommand = ReactiveCommand.Create(Clear, outputScheduler: App.UiScheduler);
        ConfirmAutoModeCommand = ReactiveCommand.Create(ConfirmAutoMode, outputScheduler: App.UiScheduler);
        CancelAutoModeCommand = ReactiveCommand.Create(CancelAutoMode, outputScheduler: App.UiScheduler);

        Messages.Add(new CopilotMessageVM
        {
            IsUser = false,
            Source = "Copilot",
            Text = "Hi! I'm your read-only AI copilot. Ask me about your balance, positions, " +
                   "P&L, risk or the market. I can't place trades — only advise."
        });
    }

    /// <summary>
    /// Agentic overload: the copilot can also take actions through <paramref name="agent"/>,
    /// surfacing mutating proposals in <paramref name="tray"/> for approval.
    /// </summary>
    public CopilotViewModel(
        CopilotAgentService.CopilotDataSource dataSource,
        AppAgentService agent,
        AgentActionTrayViewModel tray)
        : this(dataSource)
    {
        _agent = agent;
        Tray = tray;
    }

    /// <summary>
    /// Answers a one-off question without touching the chat transcript — used by the
    /// global Ctrl+K command bar, which shows the reply inline. Reuses the same
    /// configured service (active provider + offline fallback) as the chat panel.
    /// </summary>
    public Task<CopilotAgentService.CopilotAnswer> AskInlineAsync(string question, CancellationToken ct = default)
        => _service.AskAsync(question, ct);

    /// <summary>Forward the Claude key/model shared from the AI Bot tab (like Sniper/News/AiTrader).</summary>
    public void ConfigureAi(string? apiKey, string? model = null)
    {
        if (apiKey is not null) _service.ApiKey = apiKey;
        if (!string.IsNullOrWhiteSpace(model)) _service.Model = model;
        if (_agent is not null)
        {
            if (apiKey is not null) _agent.ApiKey = apiKey;
            if (!string.IsNullOrWhiteSpace(model)) _agent.Model = model;
        }
        this.RaisePropertyChanged(nameof(ModeLabel));
        this.RaisePropertyChanged(nameof(ModeBrush));
    }

    public ObservableCollection<CopilotMessageVM> Messages { get; } = [];

    /// <summary>Suggested prompts shown as quick-ask chips.</summary>
    public IReadOnlyList<string> Suggestions { get; } =
    [
        "What's my biggest risk right now?",
        "Summarize my open positions.",
        "How is the market looking?",
        "What are the top opportunities?"
    ];

    public string Question
    {
        get => _question;
        set => this.RaiseAndSetIfChanged(ref _question, value ?? string.Empty);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            this.RaisePropertyChanged(nameof(StatusLabel));
        }
    }

    public string ModeLabel => _service.UsesLiveModel ? $"Claude · {_service.Model}" : "Offline assistant";
    public string ModeBrush => _service.UsesLiveModel ? SemanticColor.Accent : SemanticColor.Muted;
    public string StatusLabel => IsBusy ? "Thinking…" : "Ready";

    public ReactiveCommand<Unit, Unit> AskCommand { get; }
    public ReactiveCommand<string, Unit> AskSuggestionCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmAutoModeCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAutoModeCommand { get; }

    /// <summary>User accepted the AUTO warning — record consent and turn AUTO on.</summary>
    private void ConfirmAutoMode()
    {
        _autoConsented = true;
        ShowAutoConsent = false;
        IsAutoMode = true;
    }

    /// <summary>User declined — hide the strip and snap the toggle back to off.</summary>
    private void CancelAutoMode()
    {
        ShowAutoConsent = false;
        this.RaisePropertyChanged(nameof(IsAutoMode));
    }

    /// <summary>Fill the box from a suggestion chip and immediately ask.</summary>
    private async Task AskSuggestionAsync(string prompt)
    {
        Question = prompt;
        await AskAsync();
    }

    private async Task AskAsync()
    {
        var q = Question.Trim();
        if (q.Length == 0 || IsBusy) return;

        Messages.Add(new CopilotMessageVM { IsUser = true, Text = q });
        Question = string.Empty;
        IsBusy = true;

        try
        {
            if (_agent is not null && _agent.UsesLiveModel)
            {
                var reply = await _agent.RunTurnAsync(q, CancellationToken.None);
                if (Tray?.HasPending == true)
                    reply += $"\n\n{Tray.Pending.Count} action(s) awaiting your approval below.";
                Messages.Add(new CopilotMessageVM { IsUser = false, Text = reply, Source = "Agent" });
            }
            else
            {
                var answer = await _service.AskAsync(q, CancellationToken.None);
                Messages.Add(new CopilotMessageVM
                {
                    IsUser = false,
                    Text = answer.Text,
                    Source = answer.Source + (answer.ToolCalls > 0 ? $" · {answer.ToolCalls} tool call(s)" : "")
                });
            }
        }
        catch (Exception ex)
        {
            Messages.Add(new CopilotMessageVM { IsUser = false, Source = "error", Text = $"Sorry — {ex.Message}" });
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Clear()
    {
        Messages.Clear();
        Messages.Add(new CopilotMessageVM
        {
            IsUser = false,
            Source = "Copilot",
            Text = "Cleared. Ask me anything about your account or the market."
        });
    }
}
