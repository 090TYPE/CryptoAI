using Avalonia.Threading;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Text.Json;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class AlertHistoryItemViewModel
{
    public string Symbol { get; }
    public string ConditionLabel { get; }
    public string TriggerValueFormatted { get; }
    public string FiredAtFormatted { get; }

    public AlertHistoryItemViewModel(FiredAlertRecord rec)
    {
        Symbol = rec.Symbol;
        ConditionLabel = rec.ConditionLabel;
        TriggerValueFormatted = rec.TriggerValue.ToString("N4");
        FiredAtFormatted = rec.FiredAt.ToLocalTime().ToString("MM/dd HH:mm:ss");
    }
}

public class AlertItemViewModel : ReactiveObject
{
    private bool _hasFired;

    public string Id { get; }
    public string Symbol { get; }
    public string ConditionLabel { get; }
    public bool SendTelegram { get; }
    public bool SendDiscord  { get; }
    public bool SendNtfy     { get; }
    public bool SendEmail    { get; }

    public bool HasFired
    {
        get => _hasFired;
        set
        {
            this.RaiseAndSetIfChanged(ref _hasFired, value);
            this.RaisePropertyChanged(nameof(StatusColor));
            this.RaisePropertyChanged(nameof(StatusLabel));
        }
    }

    public string StatusColor => HasFired ? "#F4B860" : "#3DDC84";
    public string StatusLabel => HasFired ? "Fired" : "Active";

    public AlertItemViewModel(PriceAlert alert)
    {
        Id = alert.Id;
        Symbol = alert.Symbol;
        ConditionLabel = alert.ConditionLabel;
        SendTelegram = alert.SendTelegram;
        SendDiscord  = alert.SendDiscord;
        SendNtfy     = alert.SendNtfy;
        SendEmail    = alert.SendEmail;
        HasFired = alert.HasFired;
    }
}

public class AlertsViewModel : ReactiveObject
{
    private readonly AlertService _alertService;
    private readonly TelegramNotificationService _telegram;
    private readonly DiscordWebhookNotificationService _discord;
    private readonly NtfyNotificationService _ntfy;
    private readonly EmailNotificationService _email;
    private readonly string _historyPath;

    private string _newAlertSymbol = "BTCUSDT";
    private string _selectedCondition = "PriceAbove";
    private decimal _newAlertThreshold = 100000m;
    private bool _newAlertSendTelegram;
    private bool _newAlertSendDiscord;
    private bool _newAlertSendNtfy;
    private bool _newAlertSendEmail;
    private bool _newAlertRepeat;
    private string _telegramBotToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? string.Empty;
    private string _telegramChatId   = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID")   ?? string.Empty;
    private string _telegramStatus = "Not configured";
    private string _discordWebhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL") ?? string.Empty;
    private string _discordStatus     = "Not configured";
    private string _ntfyTopic   = Environment.GetEnvironmentVariable("NTFY_TOPIC") ?? string.Empty;
    private string _ntfyStatus  = "Not configured";
    private string _emailHost     = Environment.GetEnvironmentVariable("EMAIL_SMTP_HOST")     ?? "smtp.gmail.com";
    private int    _emailPort     = ParseIntEnv("EMAIL_SMTP_PORT", 587);
    private bool   _emailUseSsl   = (Environment.GetEnvironmentVariable("EMAIL_SMTP_SSL") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase);
    private string _emailUsername = Environment.GetEnvironmentVariable("EMAIL_SMTP_USER") ?? string.Empty;
    private string _emailPassword = Environment.GetEnvironmentVariable("EMAIL_SMTP_PASS") ?? string.Empty;
    private string _emailFrom     = Environment.GetEnvironmentVariable("EMAIL_FROM")      ?? string.Empty;
    private string _emailTo       = Environment.GetEnvironmentVariable("EMAIL_TO")        ?? string.Empty;
    private string _emailStatus   = "Not configured";

    private static int ParseIntEnv(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var v) && v > 0 ? v : fallback;
    }
    private string _logText = string.Empty;
    private bool _soundEnabled = true;

    public event Action<string>? ToastRequested;

    public ObservableCollection<AlertItemViewModel> Alerts { get; } = [];
    public ObservableCollection<AlertHistoryItemViewModel> AlertHistory { get; } = [];

    public string NewAlertSymbol
    {
        get => _newAlertSymbol;
        set => this.RaiseAndSetIfChanged(ref _newAlertSymbol, value.ToUpperInvariant().Trim());
    }

    public string SelectedCondition
    {
        get => _selectedCondition;
        set => this.RaiseAndSetIfChanged(ref _selectedCondition, value);
    }

    public decimal NewAlertThreshold
    {
        get => _newAlertThreshold;
        set => this.RaiseAndSetIfChanged(ref _newAlertThreshold, value);
    }

    public bool NewAlertSendTelegram
    {
        get => _newAlertSendTelegram;
        set => this.RaiseAndSetIfChanged(ref _newAlertSendTelegram, value);
    }

    public bool NewAlertSendDiscord
    {
        get => _newAlertSendDiscord;
        set => this.RaiseAndSetIfChanged(ref _newAlertSendDiscord, value);
    }

    public bool NewAlertSendNtfy
    {
        get => _newAlertSendNtfy;
        set => this.RaiseAndSetIfChanged(ref _newAlertSendNtfy, value);
    }

    public bool NewAlertSendEmail
    {
        get => _newAlertSendEmail;
        set => this.RaiseAndSetIfChanged(ref _newAlertSendEmail, value);
    }

    public bool NewAlertRepeat
    {
        get => _newAlertRepeat;
        set => this.RaiseAndSetIfChanged(ref _newAlertRepeat, value);
    }

    public bool SoundEnabled
    {
        get => _soundEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _soundEnabled, value);
            _alertService.SoundEnabled = value;
        }
    }

    public string TelegramBotToken
    {
        get => _telegramBotToken;
        set
        {
            this.RaiseAndSetIfChanged(ref _telegramBotToken, value);
            _telegram.Configure(_telegramBotToken, _telegramChatId);
        }
    }

    public string TelegramChatId
    {
        get => _telegramChatId;
        set
        {
            this.RaiseAndSetIfChanged(ref _telegramChatId, value);
            _telegram.Configure(_telegramBotToken, _telegramChatId);
        }
    }

    public string TelegramStatus
    {
        get => _telegramStatus;
        private set => this.RaiseAndSetIfChanged(ref _telegramStatus, value);
    }

    public string DiscordWebhookUrl
    {
        get => _discordWebhookUrl;
        set
        {
            this.RaiseAndSetIfChanged(ref _discordWebhookUrl, value);
            _discord.Configure(_discordWebhookUrl);
        }
    }

    public string DiscordStatus
    {
        get => _discordStatus;
        private set => this.RaiseAndSetIfChanged(ref _discordStatus, value);
    }

    public string NtfyTopic
    {
        get => _ntfyTopic;
        set
        {
            this.RaiseAndSetIfChanged(ref _ntfyTopic, value);
            _ntfy.Configure(_ntfyTopic);
        }
    }

    public string NtfyStatus
    {
        get => _ntfyStatus;
        private set => this.RaiseAndSetIfChanged(ref _ntfyStatus, value);
    }

    public string EmailHost
    {
        get => _emailHost;
        set { this.RaiseAndSetIfChanged(ref _emailHost, value); ReconfigureEmail(); }
    }

    public int EmailPort
    {
        get => _emailPort;
        set { this.RaiseAndSetIfChanged(ref _emailPort, value); ReconfigureEmail(); }
    }

    public bool EmailUseSsl
    {
        get => _emailUseSsl;
        set { this.RaiseAndSetIfChanged(ref _emailUseSsl, value); ReconfigureEmail(); }
    }

    public string EmailUsername
    {
        get => _emailUsername;
        set { this.RaiseAndSetIfChanged(ref _emailUsername, value); ReconfigureEmail(); }
    }

    public string EmailPassword
    {
        get => _emailPassword;
        set { this.RaiseAndSetIfChanged(ref _emailPassword, value); ReconfigureEmail(); }
    }

    public string EmailFrom
    {
        get => _emailFrom;
        set { this.RaiseAndSetIfChanged(ref _emailFrom, value); ReconfigureEmail(); }
    }

    public string EmailTo
    {
        get => _emailTo;
        set { this.RaiseAndSetIfChanged(ref _emailTo, value); ReconfigureEmail(); }
    }

    public string EmailStatus
    {
        get => _emailStatus;
        private set => this.RaiseAndSetIfChanged(ref _emailStatus, value);
    }

    public string LogText
    {
        get => _logText;
        private set => this.RaiseAndSetIfChanged(ref _logText, value);
    }

    public IReadOnlyList<string> AvailableConditions { get; } =
    [
        "PriceAbove", "PriceBelow", "ChangePercent5mAbove",
        "ChangePercent1hAbove", "ChangePercent24hAbove", "VolumeSpike"
    ];

    public ReactiveCommand<Unit, Unit> AddAlertCommand { get; }
    public ReactiveCommand<string, Unit> DeleteAlertCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearFiredCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> TestTelegramCommand { get; }
    public ReactiveCommand<Unit, Unit> TestDiscordCommand  { get; }
    public ReactiveCommand<Unit, Unit> TestNtfyCommand     { get; }
    public ReactiveCommand<Unit, Unit> TestEmailCommand    { get; }

    public AlertsViewModel(
        AlertService alertService,
        TelegramNotificationService telegram,
        DiscordWebhookNotificationService discord,
        NtfyNotificationService ntfy,
        EmailNotificationService email)
    {
        _alertService = alertService;
        _telegram = telegram;
        _discord  = discord;
        _ntfy     = ntfy;
        _email    = email;
        _historyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CryptoAITerminal", "alert_history.json");

        // Auto-configure Telegram if credentials were loaded from the secrets file
        if (!string.IsNullOrWhiteSpace(_telegramBotToken) && !string.IsNullOrWhiteSpace(_telegramChatId))
        {
            _telegram.Configure(_telegramBotToken, _telegramChatId);
            _telegramStatus = "✓ Configured from secrets file";
        }

        // Auto-configure Discord if the webhook URL was loaded from env
        if (!string.IsNullOrWhiteSpace(_discordWebhookUrl))
        {
            _discord.Configure(_discordWebhookUrl);
            if (_discord.IsConfigured) _discordStatus = "✓ Configured from environment";
        }

        // Auto-configure ntfy if topic was loaded from env
        if (!string.IsNullOrWhiteSpace(_ntfyTopic))
        {
            _ntfy.Configure(_ntfyTopic);
            if (_ntfy.IsConfigured) _ntfyStatus = "✓ Configured from environment";
        }

        // Auto-configure Email if SMTP fields were loaded from env
        ReconfigureEmail();
        if (_email.IsConfigured) _emailStatus = "✓ Configured from environment";

        _alertService.AlertFired += OnAlertFired;

        LoadHistory();

        AddAlertCommand = ReactiveCommand.Create(AddAlert, outputScheduler: App.UiScheduler);
        DeleteAlertCommand = ReactiveCommand.Create<string>(DeleteAlert, outputScheduler: App.UiScheduler);
        ClearFiredCommand = ReactiveCommand.Create(ClearFired, outputScheduler: App.UiScheduler);
        ClearHistoryCommand = ReactiveCommand.Create(ClearHistory, outputScheduler: App.UiScheduler);
        TestTelegramCommand = ReactiveCommand.CreateFromTask(TestTelegramAsync, outputScheduler: App.UiScheduler);
        TestDiscordCommand  = ReactiveCommand.CreateFromTask(TestDiscordAsync,  outputScheduler: App.UiScheduler);
        TestNtfyCommand     = ReactiveCommand.CreateFromTask(TestNtfyAsync,     outputScheduler: App.UiScheduler);
        TestEmailCommand    = ReactiveCommand.CreateFromTask(TestEmailAsync,    outputScheduler: App.UiScheduler);
    }

    private void ReconfigureEmail()
    {
        _email.Configure(_emailHost, _emailPort, _emailUseSsl, _emailUsername, _emailPassword, _emailFrom, _emailTo);
    }

    private void AddAlert()
    {
        var condition = SelectedCondition switch
        {
            "PriceBelow" => AlertCondition.PriceBelow,
            "ChangePercent5mAbove" => AlertCondition.ChangePercent5mAbove,
            "ChangePercent1hAbove" => AlertCondition.ChangePercent1hAbove,
            "ChangePercent24hAbove" => AlertCondition.ChangePercent24hAbove,
            "VolumeSpike" => AlertCondition.VolumeSpike,
            _ => AlertCondition.PriceAbove
        };

        var alert = new PriceAlert
        {
            Symbol = NewAlertSymbol,
            Condition = condition,
            Threshold = NewAlertThreshold,
            SendTelegram = NewAlertSendTelegram,
            SendDiscord  = NewAlertSendDiscord,
            SendNtfy     = NewAlertSendNtfy,
            SendEmail    = NewAlertSendEmail,
            RepeatAfterFire = NewAlertRepeat
        };

        _alertService.AddAlert(alert);
        Alerts.Add(new AlertItemViewModel(alert));
        AppendLog($"Alert added: {alert.Symbol} {alert.ConditionLabel}");
    }

    private void DeleteAlert(string id)
    {
        if (!_alertService.RemoveAlert(id)) return;
        var item = Alerts.FirstOrDefault(a => a.Id == id);
        if (item is not null)
        {
            Alerts.Remove(item);
            AppendLog($"Alert removed: {id}");
        }
    }

    private void ClearFired()
    {
        _alertService.ClearFired();
        var fired = Alerts.Where(a => a.HasFired).ToList();
        foreach (var item in fired)
            Alerts.Remove(item);
        AppendLog("Cleared all fired alerts.");
    }

    private void ClearHistory()
    {
        AlertHistory.Clear();
        SaveHistory([]);
        AppendLog("Alert history cleared.");
    }

    private async Task TestTelegramAsync()
    {
        TelegramStatus = "Testing…";
        var ok = await _telegram.TestConnectionAsync();
        TelegramStatus = ok ? "Connected ✓" : "Failed — check token and chat ID";
    }

    private async Task TestDiscordAsync()
    {
        if (!_discord.IsConfigured)
        {
            DiscordStatus = "Paste a Discord webhook URL first.";
            return;
        }

        DiscordStatus = "Testing…";
        var ok = await _discord.TestConnectionAsync();
        DiscordStatus = ok ? "Connected ✓" : "Failed — check the webhook URL";
    }

    private async Task TestNtfyAsync()
    {
        if (!_ntfy.IsConfigured)
        {
            NtfyStatus = "Paste an ntfy topic first (use a long random string!).";
            return;
        }

        NtfyStatus = "Testing…";
        var ok = await _ntfy.TestConnectionAsync();
        NtfyStatus = ok ? "Connected ✓ — subscribe to the topic in the ntfy app" : "Failed — check the topic/URL";
    }

    private async Task TestEmailAsync()
    {
        if (!_email.IsConfigured)
        {
            EmailStatus = "Fill in SMTP host, port, credentials, from and to addresses first.";
            return;
        }

        EmailStatus = "Sending test message…";
        var ok = await _email.TestConnectionAsync();
        EmailStatus = ok
            ? "Sent ✓ — check the inbox of the To address"
            : "Failed — check host/port/SSL flag, app password (for Gmail) and addresses";
    }

    private void OnAlertFired(object? sender, AlertFiredEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = Alerts.FirstOrDefault(a => a.Id == e.Alert.Id);
            if (item is not null) item.HasFired = true;

            var record = new FiredAlertRecord
            {
                AlertId = e.Alert.Id,
                Symbol = e.Alert.Symbol,
                ConditionLabel = e.Alert.ConditionLabel,
                TriggerValue = e.TriggerValue,
                FiredAt = DateTime.UtcNow
            };

            AlertHistory.Insert(0, new AlertHistoryItemViewModel(record));
            if (AlertHistory.Count > 200)
                AlertHistory.RemoveAt(AlertHistory.Count - 1);

            PersistHistoryRecord(record);

            AppendLog($"FIRED {e.Alert.Symbol} {e.Alert.ConditionLabel} — price: {e.TriggerValue:N4}");

            ToastRequested?.Invoke($"Alert: {e.Alert.Symbol} {e.Alert.ConditionLabel}");
        });
    }

    private void AppendLog(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        LogText = string.IsNullOrEmpty(LogText) ? line : LogText + "\n" + line;
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyPath)) return;
            var records = AtomicJsonFile.Read<List<FiredAlertRecord>>(_historyPath);
            if (records is null) return;

            // Show most recent first, cap at 200
            foreach (var rec in records.OrderByDescending(r => r.FiredAt).Take(200))
                AlertHistory.Add(new AlertHistoryItemViewModel(rec));
        }
        catch { /* corrupt file — start fresh */ }
    }

    private void PersistHistoryRecord(FiredAlertRecord newRecord)
    {
        try
        {
            List<FiredAlertRecord> existing = [];
            if (File.Exists(_historyPath))
            {
                try { existing = AtomicJsonFile.Read<List<FiredAlertRecord>>(_historyPath) ?? []; }
                catch { existing = []; }
            }

            existing.Insert(0, newRecord);
            if (existing.Count > 500) existing = existing.Take(500).ToList();
            SaveHistory(existing);
        }
        catch { /* disk errors are non-fatal */ }
    }

    private void SaveHistory(List<FiredAlertRecord> records)
    {
        try { AtomicJsonFile.Write(_historyPath, records); }
        catch { }
    }
}
