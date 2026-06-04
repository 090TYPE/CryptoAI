using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>One message in the copilot chat transcript.</summary>
public sealed class CopilotMessageVM
{
    public bool   IsUser { get; init; }
    public string Text   { get; init; } = "";
    public string Source { get; init; } = "";

    public string RoleLabel => IsUser ? "You" : "Copilot";
    public string RoleBrush  => IsUser ? "#8FA3B8" : "#21E6C1";
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

    private string _question = string.Empty;
    private bool _isBusy;

    public CopilotViewModel(CopilotAgentService.CopilotDataSource dataSource)
    {
        _service = new CopilotAgentService(dataSource);

        AskCommand = ReactiveCommand.CreateFromTask(AskAsync, outputScheduler: App.UiScheduler);
        AskSuggestionCommand = ReactiveCommand.CreateFromTask<string>(AskSuggestionAsync, outputScheduler: App.UiScheduler);
        ClearCommand = ReactiveCommand.Create(Clear, outputScheduler: App.UiScheduler);

        Messages.Add(new CopilotMessageVM
        {
            IsUser = false,
            Source = "Copilot",
            Text = "Hi! I'm your read-only AI copilot. Ask me about your balance, positions, " +
                   "P&L, risk or the market. I can't place trades — only advise."
        });
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
    public string ModeBrush => _service.UsesLiveModel ? "#21E6C1" : "#8FA3B8";
    public string StatusLabel => IsBusy ? "Thinking…" : "Ready";

    public ReactiveCommand<Unit, Unit> AskCommand { get; }
    public ReactiveCommand<string, Unit> AskSuggestionCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

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
            var answer = await _service.AskAsync(q, CancellationToken.None);
            Messages.Add(new CopilotMessageVM
            {
                IsUser = false,
                Text = answer.Text,
                Source = answer.Source + (answer.ToolCalls > 0 ? $" · {answer.ToolCalls} tool call(s)" : "")
            });
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
