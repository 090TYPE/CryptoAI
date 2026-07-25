using System;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// UI for the AUTONOMOUS trading agent. Unlike the copilot (which advises / proposes),
/// this drives the app on a timer: every interval it runs one agent turn whose mutating
/// actions execute immediately THROUGH a <see cref="GuardedAppActionContext"/>. The guard
/// enforces per-session caps (allowlist / max-trades / budget) and PAPER-vs-LIVE — in PAPER
/// (the default) no real order is ever sent. LIVE requires an explicit consent click, and the
/// kill-switch (<see cref="StopCommand"/>) halts the loop instantly.
/// </summary>
public sealed class AutonomousAgentViewModel : ReactiveObject
{
    private readonly AgentGuardrails _rails;
    private readonly GuardedAppActionContext _guarded;
    private readonly AppAgentService _agent;
    private readonly AutonomousAgentLoop _loop;

    private string _objective =
        "Scan BTC and ETH for a favourable scalp, size small, always set a stop, and manage open risk.";
    private int _intervalSeconds = 30;
    private bool _liveEnabled;
    private decimal _sessionBudgetUsd = 1000m;
    private int _maxTrades = 20;
    private string _allowlistText = string.Empty;

    private bool _isRunning;
    private bool _showLiveConsent;
    private bool _liveConsented;
    private readonly StringBuilder _log = new();

    public AutonomousAgentViewModel(IAppActionContext realContext, AppActionRegistry registry, AppActionAuditLog audit)
    {
        _rails = new AgentGuardrails();
        _guarded = new GuardedAppActionContext(realContext, _rails);
        _agent = new AppAgentService(
            registry, _guarded,
            proposalSink: p => Task.FromResult(AppActionResult.Ok("auto")),
            autoMode: () => true, // autonomous always auto-executes THROUGH the guard
            audit: audit);
        _loop = new AutonomousAgentLoop((obj, ct) => _agent.RunTurnAsync(obj, ct), _rails);
        _loop.OnStatus += s => AppendStatus(s);

        ArmCommand = ReactiveCommand.Create(Arm, outputScheduler: App.UiScheduler);
        ConfirmLiveCommand = ReactiveCommand.Create(ConfirmLive, outputScheduler: App.UiScheduler);
        CancelLiveCommand = ReactiveCommand.Create(CancelLive, outputScheduler: App.UiScheduler);
        StopCommand = ReactiveCommand.Create(Stop, outputScheduler: App.UiScheduler);
    }

    // ----- Two-way bindable settings -----

    public string Objective
    {
        get => _objective;
        set => this.RaiseAndSetIfChanged(ref _objective, value ?? string.Empty);
    }

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set => this.RaiseAndSetIfChanged(ref _intervalSeconds, Math.Clamp(value, 5, 3600));
    }

    public bool LiveEnabled
    {
        get => _liveEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _liveEnabled, value);
            this.RaisePropertyChanged(nameof(ModeBadge));
        }
    }

    public decimal SessionBudgetUsd
    {
        get => _sessionBudgetUsd;
        set => this.RaiseAndSetIfChanged(ref _sessionBudgetUsd, value < 0m ? 0m : value);
    }

    /// <summary>How much of the session budget the guardrails have actually spent.</summary>
    public decimal SessionSpentUsd => _rails.SpentUsd;

    public int MaxTrades
    {
        get => _maxTrades;
        set => this.RaiseAndSetIfChanged(ref _maxTrades, value < 0 ? 0 : value);
    }

    public string AllowlistText
    {
        get => _allowlistText;
        set => this.RaiseAndSetIfChanged(ref _allowlistText, value ?? string.Empty);
    }

    // ----- Read-only state -----

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isRunning, value);
            this.RaisePropertyChanged(nameof(ModeBadge));
        }
    }

    public bool ShowLiveConsent
    {
        get => _showLiveConsent;
        private set => this.RaiseAndSetIfChanged(ref _showLiveConsent, value);
    }

    public string StatusLog => _log.ToString();

    public string ModeBadge => IsRunning
        ? (LiveEnabled ? "LIVE·RUNNING" : "PAPER·RUNNING")
        : "IDLE";

    // ----- Commands -----

    public ReactiveCommand<Unit, Unit> ArmCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmLiveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelLiveCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }

    /// <summary>Forward the Claude key/model shared from the AI Bot tab (like every other AI feature).</summary>
    public void ConfigureAi(string? apiKey, string? model = null)
    {
        if (apiKey is not null) _agent.ApiKey = apiKey;
        if (!string.IsNullOrWhiteSpace(model)) _agent.Model = model;
    }

    private void Arm()
    {
        if (IsRunning) return;
        if (LiveEnabled && !_liveConsented)
        {
            ShowLiveConsent = true;
            return;
        }
        StartSession();
    }

    private void ConfirmLive()
    {
        _liveConsented = true;
        ShowLiveConsent = false;
        StartSession();
    }

    private void CancelLive() => ShowLiveConsent = false;

    /// <summary>Kill-switch: stop the loop instantly.</summary>
    private void Stop()
    {
        _loop.Stop();
        IsRunning = false;
        AppendStatus("Kill-switch: stopped.");
    }

    private void StartSession()
    {
        if (!_agent.UsesLiveModel)
        {
            AppendStatus("Add a Claude/OpenAI API key in the AI Bot panel before arming the autonomous agent.");
            _liveConsented = false; // reset so a later LIVE arm re-consents
            return;
        }

        var parsed = AllowlistText
            .Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.ToUpperInvariant())
            .ToArray();

        _rails.LiveEnabled = LiveEnabled;
        _rails.MaxTrades = MaxTrades;
        _rails.SessionBudgetUsd = SessionBudgetUsd;
        _rails.AllowedSymbols = parsed;

        IsRunning = true;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            await _loop.RunAsync(Objective, TimeSpan.FromSeconds(IntervalSeconds), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _liveConsented = false; // each LIVE arm must re-consent
            await Dispatcher.UIThread.InvokeAsync(() => IsRunning = false);
        }
    }

    private void AppendStatus(string line)
    {
        void Do()
        {
            _log.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(line);
            this.RaisePropertyChanged(nameof(StatusLog));
        }

        if (Dispatcher.UIThread.CheckAccess()) Do();
        else Dispatcher.UIThread.Post(Do);
    }
}
