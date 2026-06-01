using System;
using System.Reactive;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// License activation panel + status. Validates a pasted key offline (RSA
/// signature) via <see cref="LicenseService"/>, persists it on success, and
/// exposes the current trial/licensed/expired state for the UI and the
/// live-execution gate.
/// </summary>
public sealed class LicenseViewModel : ReactiveObject
{
    private readonly LicenseService _service;

    private bool _isVisible;
    private string _keyInput = "";
    private string _statusMessage = "";
    private LicenseSnapshot _snapshot;

    /// <summary>Raised whenever the license state changes (activation/refresh).</summary>
    public event Action<LicenseSnapshot>? LicenseChanged;

    public LicenseViewModel(LicenseService? service = null)
    {
        _service  = service ?? new LicenseService();
        _snapshot = _service.GetSnapshot();

        ActivateCommand = ReactiveCommand.Create(Activate, outputScheduler: App.UiScheduler);
        OpenCommand     = ReactiveCommand.Create(() => { IsVisible = true; }, outputScheduler: App.UiScheduler);
        CloseCommand    = ReactiveCommand.Create(() => { IsVisible = false; }, outputScheduler: App.UiScheduler);
    }

    public ReactiveCommand<Unit, Unit> ActivateCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand     { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand    { get; }

    public LicenseSnapshot Snapshot => _snapshot;

    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    public string KeyInput
    {
        get => _keyInput;
        set => this.RaiseAndSetIfChanged(ref _keyInput, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    // ── Derived display ──────────────────────────────────────────────────────

    public string StateLabel => _snapshot.State switch
    {
        LicenseState.Licensed => "Licensed",
        LicenseState.Trial    => $"Trial · {_snapshot.TrialDaysRemaining} day(s) left",
        _                     => "Trial expired"
    };

    public string StateBrush => _snapshot.State switch
    {
        LicenseState.Licensed => "#3DDC84",
        LicenseState.Trial    => "#F4B860",
        _                     => "#FF5D73"
    };

    public string DetailLabel => _snapshot.State switch
    {
        LicenseState.Licensed => $"{_snapshot.LicensedTo} · {_snapshot.Edition}"
                                 + (_snapshot.Expires is { } e ? $" · until {e:yyyy-MM-dd}" : " · perpetual"),
        LicenseState.Trial    => "Add a license key any time to unlock live trading permanently.",
        _                     => "Live trading is disabled until you activate a license. Demo (paper) mode stays available."
    };

    public bool ShowTrialBanner => _snapshot.State != LicenseState.Licensed;

    public string MachineId => LicenseService.GetMachineId();

    // ── Actions ──────────────────────────────────────────────────────────────

    private void Activate()
    {
        if (_service.TryActivate(KeyInput, out var message))
        {
            StatusMessage = message;
            KeyInput = "";
            Refresh();
            IsVisible = false;
        }
        else
        {
            StatusMessage = message;
        }
    }

    /// <summary>Recompute the snapshot and notify listeners.</summary>
    public void Refresh()
    {
        _snapshot = _service.GetSnapshot();
        this.RaisePropertyChanged(nameof(Snapshot));
        this.RaisePropertyChanged(nameof(StateLabel));
        this.RaisePropertyChanged(nameof(StateBrush));
        this.RaisePropertyChanged(nameof(DetailLabel));
        this.RaisePropertyChanged(nameof(ShowTrialBanner));
        LicenseChanged?.Invoke(_snapshot);
    }
}
