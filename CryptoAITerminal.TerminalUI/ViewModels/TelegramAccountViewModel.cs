using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public sealed class TelegramAccountViewModel : ReactiveObject
{
    private readonly TelegramUserClientService _svc;

    public TelegramAccountViewModel(TelegramUserClientService svc)
    {
        _svc = svc;
        _svc.StateChanged += (_, _) => Dispatcher.UIThread.Post(RaiseAll);

        ConnectCommand    = ReactiveCommand.CreateFromTask(ConnectAsync,       outputScheduler: App.UiScheduler);
        DisconnectCommand = ReactiveCommand.CreateFromTask(_svc.DisconnectAsync, outputScheduler: App.UiScheduler);

        // Attempt silent restore on construction.
        _ = _svc.TryRestoreSessionAsync();
    }

    public ReactiveCommand<Unit, Unit> ConnectCommand    { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }

    private string _phone = "";
    public string Phone { get => _phone; set => this.RaiseAndSetIfChanged(ref _phone, value); }

    private string _code = "";
    public string Code { get => _code; set => this.RaiseAndSetIfChanged(ref _code, value); }

    private string _password = "";
    public string Password { get => _password; set => this.RaiseAndSetIfChanged(ref _password, value); }

    public bool IsCodeVisible     => _svc.State == TelegramConnectionState.AwaitingCode;
    public bool IsPasswordVisible => _svc.State == TelegramConnectionState.AwaitingPassword;
    public bool IsConnected       => _svc.State == TelegramConnectionState.Connected;

    public string StatusText => _svc.State switch
    {
        TelegramConnectionState.Connected        => $"Connected as @{_svc.Username}",
        TelegramConnectionState.AwaitingCode     => "Enter the code from Telegram",
        TelegramConnectionState.AwaitingPassword => "Enter your two-factor password",
        TelegramConnectionState.Error            => _svc.ErrorMessage ?? "Connection error",
        _                                        => "Not connected"
    };

    private async Task ConnectAsync()
    {
        switch (_svc.State)
        {
            case TelegramConnectionState.AwaitingCode:     await _svc.SubmitCodeAsync(Code); break;
            case TelegramConnectionState.AwaitingPassword: await _svc.SubmitPasswordAsync(Password); break;
            default:                                       await _svc.BeginLoginAsync(Phone); break;
        }
    }

    private void RaiseAll()
    {
        this.RaisePropertyChanged(nameof(IsCodeVisible));
        this.RaisePropertyChanged(nameof(IsPasswordVisible));
        this.RaisePropertyChanged(nameof(IsConnected));
        this.RaisePropertyChanged(nameof(StatusText));
    }
}
