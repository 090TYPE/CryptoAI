using System;
using System.Reactive;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// First-run onboarding wizard: pick an exchange, paste API keys, save them
/// (encrypted via DPAPI) and learn that live trading needs a restart. Until
/// then the terminal stays in Demo (paper) mode. Shown as an overlay over the
/// main window; can also be reopened from the welcome screen.
/// </summary>
public sealed class OnboardingViewModel : ReactiveObject
{
    public enum Step { ChooseExchange = 0, EnterKeys = 1, Done = 2 }

    private bool _isVisible;
    private Step _currentStep = Step.ChooseExchange;
    private string _selectedExchange = "";
    private string _keyInput = "";
    private string _secretInput = "";
    private string _passphraseInput = "";
    private string _statusMessage = "";

    public OnboardingViewModel()
    {
        ChooseExchangeCommand = ReactiveCommand.Create<string>(ChooseExchange, outputScheduler: App.UiScheduler);
        BackCommand           = ReactiveCommand.Create(Back, outputScheduler: App.UiScheduler);
        SaveKeysCommand       = ReactiveCommand.Create(SaveKeys, outputScheduler: App.UiScheduler);
        FinishCommand         = ReactiveCommand.Create(Close, outputScheduler: App.UiScheduler);
        SkipCommand           = ReactiveCommand.Create(Close, outputScheduler: App.UiScheduler);
    }

    // ── State ────────────────────────────────────────────────────────────────

    public bool IsVisible
    {
        get => _isVisible;
        private set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    public Step CurrentStep
    {
        get => _currentStep;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentStep, value);
            this.RaisePropertyChanged(nameof(IsChooseStep));
            this.RaisePropertyChanged(nameof(IsKeysStep));
            this.RaisePropertyChanged(nameof(IsDoneStep));
        }
    }

    public bool IsChooseStep => _currentStep == Step.ChooseExchange;
    public bool IsKeysStep   => _currentStep == Step.EnterKeys;
    public bool IsDoneStep   => _currentStep == Step.Done;

    public string SelectedExchange
    {
        get => _selectedExchange;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedExchange, value);
            this.RaisePropertyChanged(nameof(KeysStepTitle));
            this.RaisePropertyChanged(nameof(NeedsPassphrase));
            this.RaisePropertyChanged(nameof(ApiKeyHelpUrl));
        }
    }

    public string KeyInput        { get => _keyInput;        set => this.RaiseAndSetIfChanged(ref _keyInput, value); }
    public string SecretInput     { get => _secretInput;     set => this.RaiseAndSetIfChanged(ref _secretInput, value); }
    public string PassphraseInput { get => _passphraseInput; set => this.RaiseAndSetIfChanged(ref _passphraseInput, value); }
    public string StatusMessage   { get => _statusMessage;   private set => this.RaiseAndSetIfChanged(ref _statusMessage, value); }

    public bool NeedsPassphrase =>
        _selectedExchange is "OKX" or "KuCoin";

    public string KeysStepTitle => $"Connect {_selectedExchange}";

    public string ApiKeyHelpUrl => _selectedExchange switch
    {
        "Binance" => "https://www.binance.com/en/my/settings/api-management",
        "Bybit"   => "https://www.bybit.com/app/user/api-management",
        "OKX"     => "https://www.okx.com/account/my-api",
        "KuCoin"  => "https://www.kucoin.com/account/api",
        _         => "https://www.binance.com/en/my/settings/api-management"
    };

    // ── Commands ─────────────────────────────────────────────────────────────

    public ReactiveCommand<string, Unit> ChooseExchangeCommand { get; }
    public ReactiveCommand<Unit, Unit>   BackCommand           { get; }
    public ReactiveCommand<Unit, Unit>   SaveKeysCommand       { get; }
    public ReactiveCommand<Unit, Unit>   FinishCommand         { get; }
    public ReactiveCommand<Unit, Unit>   SkipCommand           { get; }

    /// <summary>Open the wizard from the start.</summary>
    public void Open()
    {
        CurrentStep      = Step.ChooseExchange;
        SelectedExchange = "";
        KeyInput         = "";
        SecretInput      = "";
        PassphraseInput  = "";
        StatusMessage    = "";
        IsVisible        = true;
    }

    private void Close() => IsVisible = false;

    private void ChooseExchange(string exchange)
    {
        SelectedExchange = exchange;
        StatusMessage    = "";
        CurrentStep      = Step.EnterKeys;
    }

    private void Back()
    {
        StatusMessage = "";
        CurrentStep   = Step.ChooseExchange;
    }

    private void SaveKeys()
    {
        var key = (_keyInput ?? "").Trim();
        var secret = (_secretInput ?? "").Trim();
        var passphrase = (_passphraseInput ?? "").Trim();

        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
        {
            StatusMessage = "Enter both the API key and secret.";
            return;
        }

        if (NeedsPassphrase && string.IsNullOrWhiteSpace(passphrase))
        {
            StatusMessage = $"{_selectedExchange} also requires a passphrase.";
            return;
        }

        try
        {
            switch (_selectedExchange)
            {
                case "Binance": CredentialsService.SaveBinance(key, secret); break;
                case "Bybit":   CredentialsService.SaveBybit(key, secret); break;
                case "OKX":     CredentialsService.SaveOkx(key, secret, passphrase); break;
                case "KuCoin":  CredentialsService.SaveKucoin(key, secret, passphrase); break;
                default:
                    StatusMessage = "Pick an exchange first.";
                    return;
            }

            // Don't keep secrets in memory longer than needed.
            KeyInput = SecretInput = PassphraseInput = "";
            CurrentStep = Step.Done;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save: {ex.Message}";
        }
    }
}
