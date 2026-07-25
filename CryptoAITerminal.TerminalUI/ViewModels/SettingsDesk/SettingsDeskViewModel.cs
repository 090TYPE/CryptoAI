using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.SettingsDesk;

/// <summary>
/// Settings tab: nav + six sections (AI provider, License &amp; profiles, Exchange
/// keys, Notifications, Alerts, Execution &amp; hotkeys). Every value is read from —
/// and written back to — the shell VM via <see cref="Attach"/>; the desk owns no
/// settings state of its own. Before Attach it renders empty, never sample data.
/// </summary>
public sealed class SettingsDeskViewModel : ReactiveObject
{
    private const string NotConfigured = "not configured";
    private const string Dash = "—";

    private MainWindowViewModel? _host;
    private readonly DispatcherTimer _toastTimer;

    // ── desk-local UI state (not settings) ───────────────────────────────────
    private string _section = "ai", _navSearch = "";
    private bool _dirty;
    private DateTime? _lastSaved;
    private bool _aiRevealed;
    private string _exTab = "binance";
    private string? _ntOpen = "telegram";
    private string? _capturing;
    private bool _refreshQueued;
    private string _naThrText = "";

    // stable field instances bound to live sources
    private readonly List<(SetField field, Func<string> read)> _boundFields = new();
    private readonly List<(HotkeyRowVM row, Func<string> read)> _boundHotkeys = new();
    private readonly Dictionary<string, List<SetField>> _exFieldsByTab = new();

    // ── collections (names are bound by SettingsView.axaml — do not rename) ──
    public ObservableCollection<NavGroup> NavGroups { get; } = new();
    public ObservableCollection<ProviderCard> Providers { get; } = new();
    public ObservableCollection<GuardToggle> Features { get; } = new();
    public ObservableCollection<ProfileCard> Profiles { get; } = new();
    public ObservableCollection<ExTabVM> ExTabs { get; } = new();
    public ObservableCollection<SetField> ExFields { get; } = new();
    public ObservableCollection<PermToggle> Perms { get; } = new();

    // Both stay empty in this build (no per-feature AI switches, no readable key scopes),
    // so the view hides the surrounding block rather than showing an empty card.
    public bool HasFeatures => Features.Count > 0;
    public bool HasPerms => Perms.Count > 0;
    public ObservableCollection<SetField> AffFields { get; } = new();
    public ObservableCollection<NtChannelVM> NtChannels { get; } = new();
    public ObservableCollection<PendingSignalVM> Pending { get; } = new();
    public ObservableCollection<ChannelChip> NewChannels { get; } = new();
    public ObservableCollection<ExampleChip> Examples { get; } = new();
    public ObservableCollection<AlertRowVM> Alerts { get; } = new();
    public ObservableCollection<HistoryRow> History { get; } = new();
    public ObservableCollection<SetField> ExecFields { get; } = new();
    public ObservableCollection<GuardToggle> ExecFlags { get; } = new();
    public ObservableCollection<HotkeyRowVM> Hotkeys { get; } = new();

    /// <summary>Alert conditions understood by the live alert engine.</summary>
    public IReadOnlyList<string> Conditions =>
        _host?.AlertsVM?.AvailableConditions ?? Array.Empty<string>();

    public SettingsDeskViewModel()
    {
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3200) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HasToast = false; };

        RevertCommand = new RelayCommand(OnRevert);
        ExportProfileCommand = new RelayCommand(() => RunHost(h => h.ExportProfileCommand, "Profile exported"));
        SaveAllCommand = new RelayCommand(SaveAll);
        RevealCommand = new RelayCommand(() => { _aiRevealed = !_aiRevealed; RaiseAi(); });
        AiConsoleCommand = new RelayCommand(OpenAiConsole);
        AiSaveCommand = new RelayCommand(AiSave);
        ServerSaveCommand = new RelayCommand(ServerSave);
        AiTestCommand = new RelayCommand(() =>
            Toast("This build has no AI test call — press SAVE PROVIDER, the status line reports the result", "info"));
        ProfSaveCommand = new RelayCommand(() => RunHost(h => h.SaveProfileCommand, "Profile saved"));
        ProfImportCommand = new RelayCommand(() => RunHost(h => h.ImportProfileCommand, "Imported from the shared folder"));
        LicCopyCommand = new RelayCommand(() =>
            Toast("Select the machine id in the field above and press Ctrl+C", "info"));
        LicActivateCommand = new RelayCommand(LicActivate);
        ExToggleHelpCommand = new RelayCommand(ToggleExHelp);
        ExSaveCommand = new RelayCommand(ExSave);
        ExTestCommand = new RelayCommand(() =>
            Toast("No connection test in this build — save the keys and restart to apply them", "info"));
        ExClearCommand = new RelayCommand(ExClear);
        TestnetCommand = new RelayCommand(() =>
            Toast("No testnet switch is wired up — keys are used against the live endpoints", "info"));
        WalletCommand = new RelayCommand(WalletToggle);
        WalletRotateCommand = new RelayCommand(() => RunWallet(w => w.RefreshWalletCommand, "Wallet session refreshed"));
        NtTestAllCommand = new RelayCommand(NtTestAll);
        TgConnectCommand = new RelayCommand(TgConnect);
        TgToggleInlineCommand = new RelayCommand(TgToggleInline);
        NlGenerateCommand = new RelayCommand(() => RunAlerts(a => a.GenerateAlertFromTextCommand, null));
        AlAddCommand = new RelayCommand(AlAdd);
        SoundCommand = new RelayCommand(() =>
        {
            if (_host?.AlertsVM is not { } a) { Toast("Alerts are not connected yet", "warn"); return; }
            a.SoundEnabled = !a.SoundEnabled; RaiseAl();
        });
        RepeatCommand = new RelayCommand(() =>
        {
            if (_host?.AlertsVM is not { } a) { Toast("Alerts are not connected yet", "warn"); return; }
            a.NewAlertRepeat = !a.NewAlertRepeat; RaiseAl();
        });
        ClearTriggeredCommand = new RelayCommand(() => RunAlerts(a => a.ClearFiredCommand, "Fired alerts removed"));
        ClearHistoryCommand = new RelayCommand(() => RunAlerts(a => a.ClearHistoryCommand, "Alert history cleared"));
        HkResetCommand = new RelayCommand(HkReset);
        CloseModalCommand = new RelayCommand(() => { _modal = null; this.RaisePropertyChanged(nameof(ModalConfirm)); });
        ConfirmRunCommand = new RelayCommand(RunConfirm);

        BuildStableFields();
        Refresh();
    }

    // ── live wiring ──────────────────────────────────────────────────────────

    /// <summary>Hands the desk its live settings sources. Safe to call once.</summary>
    public void Attach(MainWindowViewModel host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_host is not null) return;
        _host = host;

        host.PropertyChanged += OnHostPropertyChanged;
        if (host.AlertsVM is { } alerts)
        {
            alerts.PropertyChanged += OnSourcePropertyChanged;
            alerts.Alerts.CollectionChanged += OnSourceCollectionChanged;
            alerts.AlertHistory.CollectionChanged += OnSourceCollectionChanged;
        }
        if (host.LicenseVM is { } lic) lic.PropertyChanged += OnSourcePropertyChanged;
        if (host.WalletVM is { } wal) wal.PropertyChanged += OnWalletPropertyChanged;
        if (host.TelegramAccountVM is { } tga) tga.PropertyChanged += OnSourcePropertyChanged;
        if (host.TelegramSignalVM is { } tgs)
        {
            tgs.PropertyChanged += OnSourcePropertyChanged;
            tgs.PendingRows.CollectionChanged += OnSourceCollectionChanged;
        }

        BuildExecFields();   // needs the live option lists off WalletVM
        SyncFields();
        _naThrText = FormatThreshold(host.AlertsVM?.NewAlertThreshold ?? 0m);
        Refresh();
        RaiseInputs();
    }

    private static readonly HashSet<string> HostWatched = new(StringComparer.Ordinal)
    {
        "AiIsClaude", "AiIsChatGpt", "AnthropicKeyInput", "OpenAiKeyInput",
        "AnthropicModelInput", "OpenAiModelInput", "AiSettingsStatus",
        "BinanceKeyInput", "BinanceSecretInput", "BybitKeyInput", "BybitSecretInput",
        "OkxKeyInput", "OkxSecretInput", "OkxPassphraseInput",
        "KucoinKeyInput", "KucoinSecretInput", "KucoinPassphraseInput",
        "BinanceSaveStatus", "BybitSaveStatus", "OkxSaveStatus", "KucoinSaveStatus",
        "IsBinanceHelpVisible", "IsBybitHelpVisible", "IsOkxHelpVisible", "IsKucoinHelpVisible",
        "BinanceAffiliateUrl", "BybitAffiliateUrl", "OkxAffiliateUrl", "KucoinAffiliateUrl",
        "AffiliateSaveStatus", "ProfileName", "ProfileStatus",
    };

    private static readonly HashSet<string> WalletWatched = new(StringComparer.Ordinal)
    {
        "GlobalPaperOnlyMode", "GlobalLiveExecutionEnabled", "GlobalExecutionModeLabel",
        "GlobalQuoteAssetSymbol", "GlobalPositionSizingPercent", "GlobalMaxSpendPerTradeUsdt",
        "GlobalMaxDailyLossUsdt", "GlobalMaxOpenExposureUsdt", "LicenseAllowsLive",
        "ConnectedAddress", "SelectedProvider", "SelectedNetwork",
    };

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || HostWatched.Contains(e.PropertyName)) QueueRefresh();
    }

    private void OnWalletPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || WalletWatched.Contains(e.PropertyName)) QueueRefresh();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e) => QueueRefresh();
    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRefresh();

    /// <summary>Coalesces bursts of source notifications into one rebuild.</summary>
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() => { _refreshQueued = false; Refresh(); }, DispatcherPriority.Background);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void Touch() { _dirty = true; RaiseDirty(); }
    private void MarkSaved() { _dirty = false; _lastSaved = DateTime.Now; RaiseDirty(); }
    private void RaiseDirty()
    {
        foreach (var n in new[] { nameof(DirtyLabel), nameof(DirtyColor), nameof(FooterState), nameof(FooterColor), nameof(FooterSaved) })
            this.RaisePropertyChanged(n);
    }

    private static void Exec(ICommand? cmd, object? p = null)
    {
        if (cmd is not null && cmd.CanExecute(p)) cmd.Execute(p);
    }

    private void RunHost(Func<MainWindowViewModel, ICommand?> pick, string? okMsg)
    {
        if (_host is not { } h) { Toast("Settings are not connected yet", "warn"); return; }
        Exec(pick(h));
        if (okMsg is not null) Toast(okMsg, "ok");
        Refresh();
    }

    private void RunAlerts(Func<AlertsViewModel, ICommand?> pick, string? okMsg)
    {
        if (_host?.AlertsVM is not { } a) { Toast("Alerts are not connected yet", "warn"); return; }
        Exec(pick(a));
        if (okMsg is not null) Toast(okMsg, "info");
        Refresh();
    }

    private void RunWallet(Func<WalletWorkspaceViewModel, ICommand?> pick, string? okMsg)
    {
        if (_host?.WalletVM is not { } w) { Toast("Wallet is not connected yet", "warn"); return; }
        Exec(pick(w));
        if (okMsg is not null) Toast(okMsg, "info");
        Refresh();
    }

    private static string OrNotConfigured(string? s) => string.IsNullOrWhiteSpace(s) ? NotConfigured : s.Trim();
    private static string FormatThreshold(decimal d) => d.ToString("0.########", CultureInfo.InvariantCulture);

    // ── commands (names bound by the view) ───────────────────────────────────
    public ICommand RevertCommand { get; }
    public ICommand ExportProfileCommand { get; }
    public ICommand SaveAllCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand AiConsoleCommand { get; }
    public ICommand AiSaveCommand { get; }
    public ICommand ServerSaveCommand { get; }
    public ICommand AiTestCommand { get; }
    public ICommand ProfSaveCommand { get; }
    public ICommand ProfImportCommand { get; }
    public ICommand LicCopyCommand { get; }
    public ICommand LicActivateCommand { get; }
    public ICommand ExToggleHelpCommand { get; }
    public ICommand ExSaveCommand { get; }
    public ICommand ExTestCommand { get; }
    public ICommand ExClearCommand { get; }
    public ICommand TestnetCommand { get; }
    public ICommand WalletCommand { get; }
    public ICommand WalletRotateCommand { get; }
    public ICommand NtTestAllCommand { get; }
    public ICommand TgConnectCommand { get; }
    public ICommand TgToggleInlineCommand { get; }
    public ICommand NlGenerateCommand { get; }
    public ICommand AlAddCommand { get; }
    public ICommand SoundCommand { get; }
    public ICommand RepeatCommand { get; }
    public ICommand ClearTriggeredCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand HkResetCommand { get; }
    public ICommand CloseModalCommand { get; }
    public ICommand ConfirmRunCommand { get; }

    // ── sections ─────────────────────────────────────────────────────────────
    public bool IsAi => _section == "ai";
    public bool IsLicense => _section == "license";
    public bool IsKeys => _section == "keys";
    public bool IsNotify => _section == "notify";
    public bool IsAlerts => _section == "alerts";
    public bool IsExec => _section == "exec";

    public string NavSearch { get => _navSearch; set { this.RaiseAndSetIfChanged(ref _navSearch, value ?? ""); RebuildNav(); } }

    // ── AI provider ──────────────────────────────────────────────────────────
    public bool AiClaude => _host?.AiIsClaude ?? true;

    public string AiKey
    {
        get => _host is not { } h ? "" : (AiClaude ? h.AnthropicKeyInput : h.OpenAiKeyInput);
        set
        {
            if (_host is not { } h) return;
            if (AiClaude) h.AnthropicKeyInput = value ?? ""; else h.OpenAiKeyInput = value ?? "";
            Touch();
        }
    }

    public string AiModel
    {
        get => _host is not { } h ? "" : (AiClaude ? h.AnthropicModelInput : h.OpenAiModelInput);
        set
        {
            if (_host is not { } h) return;
            if (AiClaude) h.AnthropicModelInput = value ?? ""; else h.OpenAiModelInput = value ?? "";
            Touch();
        }
    }

    public char AiKeyChar => _aiRevealed ? '\0' : '•';
    public string AiKeyPlaceholder => AiClaude ? "sk-ant-…" : "sk-…";
    // Must be a model the server's allowlist accepts (AiRequestPolicy.FromConfig) — a user who
    // types the placeholder verbatim otherwise gets HTTP 400 "model is not allowed".
    public string AiModelPlaceholder => AiClaude ? "claude-sonnet-4-6" : "gpt-4o";
    public string AiKeyLabel => AiClaude ? "ANTHROPIC API KEY" : "OPENAI API KEY";
    public string AiRevealIcon => _aiRevealed ? "HIDE" : "SHOW";
    public string AiActiveBadge => _host is null ? "NOT CONNECTED" : (AiClaude ? "CLAUDE" : "CHATGPT") + " ACTIVE";
    public string AiStatusText => _host is null ? NotConfigured : OrNotConfigured(_host.AiSettingsStatus);
    public string AiStatusColor => _host is not null && !string.IsNullOrWhiteSpace(_host.AiSettingsStatus)
        && !_host.AiSettingsStatus.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
        ? SettingsData.Green : SettingsData.Amber;

    private void AiSave()
    {
        if (_host is not { } h) { Toast("Settings are not connected yet", "warn"); return; }
        if (AiKey.Trim().Length == 0) { Toast("Paste an API key first", "warn"); return; }
        Exec(h.SaveAiSettingsCommand);
        MarkSaved(); Refresh();
        Toast("AI provider saved · encrypted with DPAPI", "ok");
    }

    private void OpenAiConsole()
    {
        if (_host is not { } h) { Toast("Settings are not connected yet", "warn"); return; }
        Exec(AiClaude ? h.OpenAnthropicConsoleCommand : h.OpenOpenAiConsoleCommand);
    }

    // ── CryptoAI server ──────────────────────────────────────────────────────
    // Until this section existed the only way to bind a terminal to a server was to create
    // server_url.txt by hand. Unbound, every server feature is off: no 24/7 candle collection for
    // your watchlist, no shared AI digests, and AI runs on your own key instead of the server's.
    // None of that announced itself, so "nothing works" and "not connected" looked identical.

    private string? _serverUrl;

    public string ServerUrl
    {
        get => _serverUrl ??= Services.ServerEndpoint.ResolveBaseUrl() ?? "";
        set { _serverUrl = value ?? ""; Touch(); RaiseServer(); }
    }

    public string ServerUrlPlaceholder => "https://api.example.com";

    public bool ServerConnected => !string.IsNullOrWhiteSpace(AIEngine.ChatClient.ServerBaseUrl);

    public string ServerBadge => ServerConnected ? "CONNECTED" : "LOCAL ONLY";

    public string ServerStatusText => _serverStatus ?? (ServerConnected
        ? "Watchlist tokens are tracked 24/7 and AI runs on the server's key."
        : "Not connected — the terminal works fully locally, on your own AI key.");

    public string ServerStatusColor => _serverStatus is not null && _serverStatus.StartsWith("Could not", StringComparison.OrdinalIgnoreCase)
        ? SettingsData.Amber
        : ServerConnected ? SettingsData.Green : SettingsData.Amber;

    private string? _serverStatus;

    private void ServerSave()
    {
        var error = Services.ServerEndpoint.Save(ServerUrl);
        _serverStatus = error;

        if (error is null)
        {
            MarkSaved();
            Toast(string.IsNullOrWhiteSpace(ServerUrl) ? "Disconnected — running locally" : "Connected to the server", "ok");
        }
        else
        {
            Toast(error, "warn");
        }

        RaiseServer();
    }

    private void RaiseServer()
    {
        foreach (var n in new[] { nameof(ServerUrl), nameof(ServerConnected), nameof(ServerBadge),
                                  nameof(ServerStatusText), nameof(ServerStatusColor) })
            this.RaisePropertyChanged(n);
    }

    // ── license ──────────────────────────────────────────────────────────────
    private LicenseViewModel? Lic => _host?.LicenseVM;

    public string LicBadge => Lic?.Snapshot.State switch
    {
        LicenseState.Licensed => "PRO",
        LicenseState.Trial => "TRIAL",
        LicenseState.Expired => "DEMO",
        _ => Dash,
    };
    public string LicColor => Lic?.StateBrush ?? SettingsData.Faint;
    public bool LicOk => Lic?.Snapshot.State == LicenseState.Licensed;
    public string LicBorder => LicOk ? "#14302e" : "#3a2a12";
    public string LicBg => LicOk ? "#061615" : "#150f04";
    public string LicDetail => Lic?.DetailLabel ?? NotConfigured;
    public string LicStatus => Lic is null ? NotConfigured : OrNotConfigured(
        string.IsNullOrWhiteSpace(Lic.StatusMessage) ? Lic.StateLabel : Lic.StatusMessage);
    public string LicStatusColor => Lic?.StateBrush ?? SettingsData.Faint;
    public string MachineId => Lic?.MachineId ?? Dash;

    public string LicKey
    {
        get => Lic?.KeyInput ?? "";
        set { if (Lic is { } l) l.KeyInput = value ?? ""; }
    }

    private void LicActivate()
    {
        if (Lic is not { } l) { Toast("License is not connected yet", "warn"); return; }
        if (l.KeyInput.Trim().Length == 0) { Toast("Paste a license key first", "warn"); return; }
        Exec(l.ActivateCommand);
        Refresh(); RaiseInputs();
        Toast(OrNotConfigured(l.StatusMessage), l.Snapshot.State == LicenseState.Licensed ? "ok" : "warn");
    }

    // ── configuration profiles ───────────────────────────────────────────────
    public string ProfileName
    {
        get => _host?.ProfileName ?? "";
        set { if (_host is { } h) { h.ProfileName = value ?? ""; Touch(); } }
    }

    public string ProfStatus => _host is not { } h
        ? NotConfigured
        : !string.IsNullOrWhiteSpace(h.ProfileStatus)
            ? h.ProfileStatus
            : h.AvailableProfiles.Count + " saved · keys excluded";

    private void RebuildProfiles()
    {
        Profiles.Clear();
        if (_host is not { } h) return;
        foreach (var name in h.AvailableProfiles)
        {
            var pn = name;
            var active = string.Equals(pn, h.ProfileName, StringComparison.OrdinalIgnoreCase);
            Profiles.Add(new ProfileCard
            {
                Name = pn,
                Meta = ProfileMeta(h, pn) + (active ? " · selected" : ""),
                Border = active ? "#14302e" : "#152233",
                Bg = active ? "#061615" : "#050f14",
                LoadCommand = new RelayCommand(() => { h.ProfileName = pn; Exec(h.LoadProfileCommand); Refresh(); RaiseInputs(); Toast(OrNotConfigured(h.ProfileStatus), "ok"); }),
                ExportCommand = new RelayCommand(() => { h.ProfileName = pn; Exec(h.ExportProfileCommand); Refresh(); Toast(OrNotConfigured(h.ProfileStatus), "ok"); }),
                DeleteCommand = new RelayCommand(() => AskConfirm(new ConfirmConfig
                {
                    Kind = "delProfile", BotId = pn, Accent = "#3a1620", IconColor = SettingsData.Red, Icon = "✕",
                    Title = "Delete “" + pn + "”",
                    Body = "The profile file is removed from the profile store. Running bots keep their current settings.",
                    Cta = "DELETE", BtnBg = "#14060a", BtnFg = SettingsData.Red, BtnBorder = SettingsData.Red,
                })),
            });
        }
    }

    /// <summary>Real "saved" timestamp read off the profile file — no placeholder.</summary>
    private static string ProfileMeta(MainWindowViewModel h, string name)
    {
        try
        {
            var safe = string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' '));
            var path = Path.Combine(h.ProfileService.ProfileDir, safe + ".json");
            if (File.Exists(path)) return "saved " + File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
        }
        catch { /* unreadable — fall through */ }
        return "";
    }

    // ── header / dirty / footer ──────────────────────────────────────────────
    private int KeysConfigured => SettingsData.Exchanges.Count(e => ExHasKey(e.id));
    private int ChannelsConfigured => NtChannels.Count(c => IsChannelConfigured(c.Id));
    private int ActiveAlertCount => _host?.AlertsVM?.Alerts.Count(a => !a.HasFired) ?? 0;
    private int FiredAlertCount => _host?.AlertsVM?.Alerts.Count(a => a.HasFired) ?? 0;

    public string HdrKeys => _host is null ? Dash : KeysConfigured.ToString();
    public string HdrKeysSub => "of 4";
    public string HdrChannels => _host is null ? Dash : ChannelsConfigured.ToString();
    public string HdrChannelsSub => "configured";
    public string HdrAlerts => _host is null ? Dash : ActiveAlertCount.ToString();
    public string HdrAlertsSub => _host is null ? "" : FiredAlertCount + " fired";
    public string DirtyLabel => _host is null ? "not connected" : _dirty ? "unsaved changes in this session" : "everything saved";
    public string DirtyColor => _dirty ? SettingsData.Amber : SettingsData.Faint;

    public string FooterState => _dirty ? "modified" : "in sync";
    public string FooterColor => _dirty ? SettingsData.Amber : SettingsData.Green;
    public string FooterStore => "store %LOCALAPPDATA%\\CryptoAITerminal";
    public string FooterEncryption => "encryption DPAPI (per-user)";
    public string FooterProfile => "profile " + (string.IsNullOrWhiteSpace(_host?.ProfileName) ? "none" : _host!.ProfileName);
    public string FooterSaved => _lastSaved is { } t ? "last saved " + t.ToString("HH:mm:ss") : "not saved in this session";

    public (string n, string text)[] HelpSteps => new[]
    {
        ("1", "Open the exchange → API Management and create a new key labelled “CryptoAI”."),
        ("2", "Enable Read + Spot/Futures trading. Leave Withdrawals disabled — the terminal never needs it."),
        ("3", "Bind the key to this machine's IP if the exchange offers it, then paste key and secret here."),
        ("4", "Press SAVE KEYS and restart the terminal — the gateways pick the key up on start."),
    };

    // ── refresh orchestration ────────────────────────────────────────────────
    private void Refresh()
    {
        SyncFields();
        RebuildNav();
        RebuildProviders();
        RebuildProfiles();
        RebuildExTabs();
        RebuildExFields();
        RefreshNtDisplay();
        RebuildTgPending();
        RebuildNewChannels();
        RebuildExamples();
        RebuildExecFlags();
        RebuildAlerts();
        RebuildHistory();
        RaiseScalars();
    }

    /// <summary>Pull live values into the stable input instances (no-op when unchanged).</summary>
    private void SyncFields()
    {
        foreach (var (field, read) in _boundFields) field.SetSilent(read());
        foreach (var (row, read) in _boundHotkeys) row.SetSilentValue(read());

        // Threshold text follows the live decimal, but a half-typed value
        // ("1200.") is left alone so the caret is never yanked mid-edit.
        if (_host?.AlertsVM is { } a)
        {
            var live = a.NewAlertThreshold;
            var parsed = decimal.TryParse(_naThrText, NumberStyles.Any, CultureInfo.InvariantCulture, out var typed);
            var stale = _naThrText.Length == 0 || (parsed && typed != live);
            if (stale)
            {
                var next = FormatThreshold(live);
                if (_naThrText != next) { _naThrText = next; this.RaisePropertyChanged(nameof(NaThr)); }
            }
        }
    }

    private static readonly string[] Scalars =
    {
        nameof(IsAi), nameof(IsLicense), nameof(IsKeys), nameof(IsNotify), nameof(IsAlerts), nameof(IsExec),
        nameof(AiClaude), nameof(AiKeyLabel), nameof(AiKeyChar), nameof(AiKeyPlaceholder), nameof(AiModelPlaceholder),
        nameof(AiRevealIcon), nameof(AiActiveBadge), nameof(AiStatusText), nameof(AiStatusColor),
        nameof(LicBadge), nameof(LicColor), nameof(LicBorder), nameof(LicBg), nameof(LicDetail),
        nameof(LicStatus), nameof(LicStatusColor), nameof(MachineId), nameof(ProfStatus),
        nameof(HdrKeys), nameof(HdrKeysSub), nameof(HdrChannels), nameof(HdrChannelsSub),
        nameof(HdrAlerts), nameof(HdrAlertsSub), nameof(DirtyLabel), nameof(DirtyColor),
        nameof(FooterState), nameof(FooterColor), nameof(FooterStore), nameof(FooterEncryption),
        nameof(FooterProfile), nameof(FooterSaved), nameof(HelpSteps), nameof(HelpPath), nameof(ExHelp),
        nameof(ExCurName), nameof(ExSrcLabel), nameof(ExSrcColor), nameof(ExSrcBg), nameof(ExSrcBorder),
        nameof(ExMask), nameof(ExStatus), nameof(ExStatusColor), nameof(ExTestnet),
        nameof(TnColor), nameof(TnBorder), nameof(TnBg), nameof(TnTrack), nameof(TnKnob), nameof(TnKnobLeft),
        nameof(WalletBadge), nameof(WalletColor), nameof(WalletBorder), nameof(WalletBg), nameof(WalletDetail),
        nameof(WalletAddr), nameof(WalletAddrColor), nameof(WalletExpires), nameof(WalletExpiresColor),
        nameof(WalletBtnLabel), nameof(WalletBtnColor), nameof(WalletBtnBorder), nameof(WalletBtnBg),
        nameof(TgBadge), nameof(TgColor), nameof(TgBorder), nameof(TgBg), nameof(TgNeedCode), nameof(TgStatus),
        nameof(TgStatusColor), nameof(TgBtnLabel), nameof(TgBtnColor), nameof(TgBtnBorder), nameof(TgBtnBg),
        nameof(TgInlineTrack), nameof(TgInlineKnob), nameof(TgInlineKnobLeft), nameof(TgInlineBorder),
        nameof(TgInlineBg), nameof(TgHasPending), nameof(TgNoPending),
        nameof(NlStatus), nameof(NlBtn), nameof(RepeatLabel), nameof(RepeatColor), nameof(RepeatBorder),
        nameof(RepeatBg), nameof(RepeatMark), nameof(RepeatBoxBorder), nameof(RepeatBoxBg),
        nameof(SoundMark), nameof(SoundBorder), nameof(SoundBg), nameof(AlNote), nameof(ActiveMeta),
        nameof(NoActive), nameof(Conditions),
    };

    private void RaiseScalars() { foreach (var n in Scalars) this.RaisePropertyChanged(n); }

    /// <summary>Raise the two-way text properties. Only called on attach/explicit
    /// resets so typing is never interrupted by a background refresh.</summary>
    private void RaiseInputs()
    {
        foreach (var n in new[] { nameof(AiKey), nameof(AiModel), nameof(LicKey), nameof(ProfileName),
            nameof(TgPhone), nameof(TgCode), nameof(NlPrompt), nameof(NaSym), nameof(NaCond), nameof(NaThr) })
            this.RaisePropertyChanged(n);
    }

    private void RaiseAi()
    {
        foreach (var n in new[] { nameof(AiClaude), nameof(AiKeyLabel), nameof(AiKeyChar), nameof(AiKeyPlaceholder),
            nameof(AiModelPlaceholder), nameof(AiRevealIcon), nameof(AiActiveBadge), nameof(AiStatusText), nameof(AiStatusColor),
            nameof(AiKey), nameof(AiModel) })
            this.RaisePropertyChanged(n);
    }

    private void RaiseAl()
    {
        foreach (var n in new[] { nameof(RepeatLabel), nameof(RepeatColor), nameof(RepeatBorder), nameof(RepeatBg),
            nameof(RepeatMark), nameof(RepeatBoxBorder), nameof(RepeatBoxBg), nameof(SoundMark), nameof(SoundBorder),
            nameof(SoundBg), nameof(ActiveMeta), nameof(NlStatus), nameof(NlBtn) })
            this.RaisePropertyChanged(n);
    }

    // ── nav ──────────────────────────────────────────────────────────────────
    private void RebuildNav()
    {
        var q = _navSearch.Trim().ToLowerInvariant();
        NavGroups.Clear();
        foreach (var (label, items) in SettingsData.Nav)
        {
            var g = new NavGroup { Label = label };
            foreach (var (id, il) in items)
            {
                if (q.Length > 0 && !il.ToLowerInvariant().Contains(q)) continue;
                var on = _section == id; var key = id;
                g.Items.Add(new NavItem
                {
                    Label = il, Badge = NavBadge(id),
                    BadgeColor = on ? SettingsData.Accent : "#1e3048",
                    Bg = on ? "#0e2a2a" : "transparent",
                    Fg = on ? SettingsData.Text : SettingsData.Text3,
                    Mark = on ? SettingsData.Accent : "transparent",
                    Command = new RelayCommand(() => { _section = key; RaiseSections(); Refresh(); }),
                });
            }
            if (g.Items.Count > 0) NavGroups.Add(g);
        }
    }

    private string NavBadge(string id)
    {
        if (_host is not { } h) return Dash;
        return id switch
        {
            "ai" => AiClaude ? "claude" : "gpt",
            "license" => LicBadge.ToLowerInvariant(),
            "keys" => KeysConfigured + "/4",
            "notify" => ChannelsConfigured + "/4",
            "alerts" => ActiveAlertCount.ToString(),
            "exec" => h.HotkeySettings is { } hk ? hk.BuyMarketDisplay + "/" + hk.SellMarketDisplay : Dash,
            _ => "",
        };
    }

    private void RaiseSections()
    {
        foreach (var n in new[] { nameof(IsAi), nameof(IsLicense), nameof(IsKeys), nameof(IsNotify), nameof(IsAlerts), nameof(IsExec) })
            this.RaisePropertyChanged(n);
    }

    // ── AI provider cards ────────────────────────────────────────────────────
    private void RebuildProviders()
    {
        Providers.Clear();
        if (_host is not { } h) return;

        void P(bool claude, string name, string hint)
        {
            var on = AiClaude == claude;
            var hasKey = (claude ? h.AnthropicKeyInput : h.OpenAiKeyInput).Trim().Length > 0;
            Providers.Add(new ProviderCard
            {
                Name = name, Hint = hint,
                Border = on ? "#14302e" : "#152233", Bg = on ? "#061615" : "#050f14",
                Fg = on ? SettingsData.Text : SettingsData.Text3,
                DotBorder = on ? SettingsData.Accent : "#2a3f54", DotBg = on ? SettingsData.Accent : "transparent",
                Status = hasKey ? "key set" : "no key",
                StatusColor = hasKey ? SettingsData.Green : SettingsData.Faint,
                Command = new RelayCommand(() =>
                {
                    if (claude) h.AiIsClaude = true; else h.AiIsChatGpt = true;
                    Touch(); Refresh(); RaiseAi();
                }),
            });
        }

        P(true, "Claude (Anthropic)", "Best at tool use and long reasoning chains — the default for bots and copilot.");
        P(false, "ChatGPT (OpenAI)", "Cheaper per call, faster on short prompts like alert parsing and ranking.");
        // Features stays empty: this build has no per-feature AI switches to read.
    }

    // ── exchange keys ────────────────────────────────────────────────────────
    private static string ExName(string id) => SettingsData.Exchanges.First(e => e.id == id).name;

    private string ExMaskOf(string id) => _host is not { } h ? "" : id switch
    {
        "binance" => h.BinanceApiKeyMask,
        "bybit" => h.BybitApiKeyMask,
        "okx" => h.OkxApiKeyMask,
        _ => h.KucoinApiKeyMask,
    };

    private bool ExHasKey(string id)
    {
        var m = ExMaskOf(id);
        return m.Length > 0 && !m.Equals("not set", StringComparison.OrdinalIgnoreCase);
    }

    private string ExSourceOf(string id) => _host is not { } h ? Dash : id switch
    {
        "binance" => h.BinanceCredentialSourceLabel,
        "bybit" => h.BybitCredentialSourceLabel,
        "okx" => h.OkxCredentialSourceLabel,
        _ => h.KucoinCredentialSourceLabel,
    };

    private bool ExIsEnv(string id) => _host is { } h && id switch
    {
        "binance" => h.IsBinanceCredSourceEnv,
        "bybit" => h.IsBybitCredSourceEnv,
        "okx" => h.IsOkxCredSourceEnv,
        _ => h.IsKucoinCredSourceEnv,
    };

    private string ExSaveStatusOf(string id) => _host is not { } h ? "" : id switch
    {
        "binance" => h.BinanceSaveStatus,
        "bybit" => h.BybitSaveStatus,
        "okx" => h.OkxSaveStatus,
        _ => h.KucoinSaveStatus,
    };

    private string ExApiStatusOf(string id) => _host is not { } h ? "" : id switch
    {
        "binance" => h.BinanceApiStatus,
        "bybit" => h.BybitApiStatus,
        "okx" => h.OkxApiStatus,
        _ => h.KucoinApiStatus,
    };

    private ICommand? ExSaveCommandOf(string id) => _host is not { } h ? null : id switch
    {
        "binance" => h.SaveBinanceCredentialsCommand,
        "bybit" => h.SaveBybitCredentialsCommand,
        "okx" => h.SaveOkxCredentialsCommand,
        _ => h.SaveKucoinCredentialsCommand,
    };

    private ICommand? ExHelpCommandOf(string id) => _host is not { } h ? null : id switch
    {
        "binance" => h.ToggleBinanceHelpCommand,
        "bybit" => h.ToggleBybitHelpCommand,
        "okx" => h.ToggleOkxHelpCommand,
        _ => h.ToggleKucoinHelpCommand,
    };

    private bool ExHelpVisibleOf(string id) => _host is { } h && id switch
    {
        "binance" => h.IsBinanceHelpVisible,
        "bybit" => h.IsBybitHelpVisible,
        "okx" => h.IsOkxHelpVisible,
        _ => h.IsKucoinHelpVisible,
    };

    public bool ExHelp => ExHelpVisibleOf(_exTab);
    public string ExCurName => ExName(_exTab) + " API keys";
    public string ExSrcLabel => ExSourceOf(_exTab) switch { "ENV" => "ENV VARS", "FILE" => "LOCAL FILE", _ => "NOT SET" };
    public string ExSrcColor => ExIsEnv(_exTab) ? SettingsData.Amber : ExHasKey(_exTab) ? SettingsData.Accent : SettingsData.Faint;
    public string ExSrcBg => ExIsEnv(_exTab) ? "#150f04" : ExHasKey(_exTab) ? "#061615" : "transparent";
    public string ExSrcBorder => ExIsEnv(_exTab) ? "#3a2a12" : ExHasKey(_exTab) ? "#14302e" : "#152233";
    public string ExMask => ExHasKey(_exTab) ? ExMaskOf(_exTab) : "no key set";
    public string ExStatus
    {
        get
        {
            if (_host is null) return NotConfigured;
            var s = ExSaveStatusOf(_exTab);
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            return ExHasKey(_exTab) ? OrNotConfigured(ExApiStatusOf(_exTab)) : NotConfigured;
        }
    }
    public string ExStatusColor => ExHasKey(_exTab) ? SettingsData.Green : SettingsData.Faint;

    // No testnet flag exists in this build — the toggle stays off and says so.
    public bool ExTestnet => false;
    public string TnColor => SettingsData.Faint;
    public string TnBorder => "#152233";
    public string TnBg => "transparent";
    public string TnTrack => "#152233";
    public string TnKnob => SettingsData.Dimmer;
    public double TnKnobLeft => 2;
    public string HelpPath => "stored in %LOCALAPPDATA%\\CryptoAITerminal\\api-credentials.json (DPAPI) · env vars take priority";

    private void ToggleExHelp()
    {
        if (_host is null) { Toast("Settings are not connected yet", "warn"); return; }
        Exec(ExHelpCommandOf(_exTab));
        this.RaisePropertyChanged(nameof(ExHelp));
    }

    private void RebuildExFields()
    {
        if (!_exFieldsByTab.TryGetValue(_exTab, out var fields)) return;
        if (ExFields.Count == fields.Count && ExFields.SequenceEqual(fields)) return;
        ExFields.Clear();
        foreach (var f in fields) ExFields.Add(f);
        // Perms stays empty: the terminal cannot read key permissions from an exchange.
    }

    private void RebuildExTabs()
    {
        ExTabs.Clear();
        foreach (var (id, name, _) in SettingsData.Exchanges)
        {
            var key = id;
            var on = _exTab == key;
            var has = ExHasKey(key);
            ExTabs.Add(new ExTabVM
            {
                Label = name,
                Bg = on ? "#0e2a2a" : "transparent",
                Fg = on ? SettingsData.Text : SettingsData.Text3,
                Dot = has ? SettingsData.Green : "#152233",
                State = _host is null ? Dash : has ? ExSourceOf(key).ToLowerInvariant() : "empty",
                StateColor = has ? SettingsData.Green : "#1e3048",
                Command = new RelayCommand(() => { _exTab = key; RebuildExFields(); Refresh(); }),
            });
        }
    }

    private void ExSave()
    {
        if (_host is null) { Toast("Settings are not connected yet", "warn"); return; }
        var fields = _exFieldsByTab[_exTab];
        if (fields[0].Value.Trim().Length == 0 || fields[1].Value.Trim().Length == 0)
        { Toast("Both key and secret are required", "warn"); return; }
        Exec(ExSaveCommandOf(_exTab));
        MarkSaved(); Refresh();
        Toast(OrNotConfigured(ExSaveStatusOf(_exTab)), "ok");
    }

    private void ExClear() => AskConfirm(new ConfirmConfig
    {
        Kind = "clearKeys", BotId = _exTab, Accent = "#3a1620", IconColor = SettingsData.Red, Icon = "✕",
        Title = "Remove " + ExName(_exTab) + " keys",
        Body = "The stored key, secret and passphrase for " + ExName(_exTab) + " are overwritten with empty values. Environment variables are not touched.",
        Cta = "REMOVE KEYS", BtnBg = "#14060a", BtnFg = SettingsData.Red, BtnBorder = SettingsData.Red,
    });

    // ── wallet session ───────────────────────────────────────────────────────
    private WalletWorkspaceViewModel? Wal => _host?.WalletVM;
    private bool WalletConnected => !string.IsNullOrWhiteSpace(Wal?.ConnectedAddress);

    public string WalletBadge => Wal is null ? Dash : WalletConnected ? "CONNECTED" : "NOT CONNECTED";
    public string WalletColor => WalletConnected ? SettingsData.Green : SettingsData.Faint;
    public string WalletBorder => WalletConnected ? "#14302e" : "#152233";
    public string WalletBg => WalletConnected ? "#061615" : "#050f14";
    public string WalletDetail => Wal is null ? NotConfigured
        : WalletConnected ? Wal.SelectedProvider + " · " + Wal.SelectedNetwork
        : "DEX bots cannot sign without a connected wallet";
    public string WalletAddr => string.IsNullOrWhiteSpace(Wal?.ConnectedAddress) ? Dash : Wal!.ConnectedAddress;
    public string WalletAddrColor => WalletConnected ? SettingsData.Text : SettingsData.Text3;
    // No session expiry is tracked by the wallet workspace — never invent one.
    public string WalletExpires => Dash;
    public string WalletExpiresColor => SettingsData.Faint;
    public string WalletBtnLabel => WalletConnected ? "DISCONNECT WALLET" : "REFRESH WALLET";
    public string WalletBtnColor => WalletConnected ? SettingsData.Red : SettingsData.Accent;
    public string WalletBtnBorder => WalletConnected ? "#3a1620" : "#14302e";
    public string WalletBtnBg => WalletConnected ? "#14060a" : "#061615";

    private void WalletToggle()
    {
        if (Wal is not { } w) { Toast("Wallet is not connected yet", "warn"); return; }
        Exec(WalletConnected ? w.DisconnectWalletCommand : w.RefreshWalletCommand);
        Refresh();
    }

    // ── notification channels ────────────────────────────────────────────────
    private AlertsViewModel? Al => _host?.AlertsVM;

    private bool IsChannelConfigured(string id)
    {
        if (Al is not { } a) return false;
        return id switch
        {
            "telegram" => a.TelegramBotToken.Trim().Length > 0 && a.TelegramChatId.Trim().Length > 0,
            "discord" => a.DiscordWebhookUrl.Trim().Length > 0,
            "ntfy" => a.NtfyTopic.Trim().Length > 0,
            "email" => a.EmailHost.Trim().Length > 0 && a.EmailUsername.Trim().Length > 0
                       && a.EmailFrom.Trim().Length > 0 && a.EmailTo.Trim().Length > 0,
            _ => false,
        };
    }

    private string ChannelStatus(string id) => Al is not { } a ? NotConfigured : id switch
    {
        "telegram" => OrNotConfigured(a.TelegramStatus),
        "discord" => OrNotConfigured(a.DiscordStatus),
        "ntfy" => OrNotConfigured(a.NtfyStatus),
        "email" => OrNotConfigured(a.EmailStatus),
        _ => NotConfigured,
    };

    private ICommand? ChannelTest(string id) => Al is not { } a ? null : id switch
    {
        "telegram" => a.TestTelegramCommand,
        "discord" => a.TestDiscordCommand,
        "ntfy" => a.TestNtfyCommand,
        _ => a.TestEmailCommand,
    };

    private void RefreshNtDisplay() { foreach (var ch in NtChannels) RefreshNtChannel(ch.Id); }

    private void RefreshNtChannel(string id)
    {
        var ch = NtChannels.FirstOrDefault(c => c.Id == id);
        if (ch is null) return;

        var configured = IsChannelConfigured(id);
        ch.Open = _ntOpen == id;
        ch.Border = ch.Open ? "#152233" : "#0d1b27";
        ch.Dot = configured ? SettingsData.Green : "#152233";
        ch.Status = ChannelStatus(id);
        ch.StatusColor = configured ? SettingsData.Green : SettingsData.Faint;
        ch.Summary = configured ? "configured" : NotConfigured;
        // Channel credentials come from env vars / the secrets file; the desk never writes them to disk.
        ch.Last = "applies to this session · not saved to disk";
        ch.EnLabel = "APPLY TO SESSION";
        ch.EnColor = SettingsData.Accent;
        ch.EnBorder = "#14302e";
        ch.EnBg = "#061615";
        if (ch.HasSsl && Al is { } a)
        {
            ch.SslMark = a.EmailUseSsl ? "✓" : "";
            ch.SslBorder = a.EmailUseSsl ? SettingsData.Accent : "#152233";
            ch.SslBg = a.EmailUseSsl ? SettingsData.Accent : "transparent";
        }
        ch.Refresh();
    }

    private void NtTest(string id)
    {
        if (Al is null) { Toast("Alerts are not connected yet", "warn"); return; }
        if (!IsChannelConfigured(id)) { Toast("Fill the fields for this channel first", "warn"); return; }
        Exec(ChannelTest(id));
        RefreshNtChannel(id);
        Toast("Test sent — the channel status line shows the result", "info");
    }

    /// <summary>Re-applies the typed values to the live notification services for this
    /// session. Nothing is persisted — Discord/ntfy/Email are env-var only.</summary>
    private void NtApply(string id)
    {
        if (Al is not { } a) { Toast("Alerts are not connected yet", "warn"); return; }
        // Re-assigning re-runs the service Configure() call in each setter.
        switch (id)
        {
            case "telegram": a.TelegramBotToken = a.TelegramBotToken.Trim(); a.TelegramChatId = a.TelegramChatId.Trim(); break;
            case "discord": a.DiscordWebhookUrl = a.DiscordWebhookUrl.Trim(); break;
            case "ntfy": a.NtfyTopic = a.NtfyTopic.Trim(); break;
            case "email": a.EmailHost = a.EmailHost.Trim(); break;
        }
        RefreshNtChannel(id); Refresh();
        Toast("Applied for this session — channel settings are not written to disk", "info");
    }

    private void NtTestAll()
    {
        if (Al is null) { Toast("Alerts are not connected yet", "warn"); return; }
        var ids = NtChannels.Select(c => c.Id).Where(IsChannelConfigured).ToList();
        if (ids.Count == 0) { Toast("No channel is configured yet", "warn"); return; }
        foreach (var id in ids) Exec(ChannelTest(id));
        RefreshNtDisplay();
        Toast("Test sent to " + ids.Count + " configured channel(s)", "info");
    }

    // ── telegram account & inline signals ────────────────────────────────────
    private TelegramAccountViewModel? Tga => _host?.TelegramAccountVM;
    private TelegramSignalViewModel? Tgs => _host?.TelegramSignalVM;

    public string TgPhone
    {
        get => Tga?.Phone ?? "";
        set { if (Tga is { } t) t.Phone = value ?? ""; }
    }

    /// <summary>Routed to the login code, or to the 2FA password when Telegram asks for one.</summary>
    public string TgCode
    {
        get => Tga is null ? "" : Tga.IsPasswordVisible ? Tga.Password : Tga.Code;
        set { if (Tga is { } t) { if (t.IsPasswordVisible) t.Password = value ?? ""; else t.Code = value ?? ""; } }
    }

    public string TgBadge => Tga is null ? Dash : Tga.IsConnected ? "SIGNED IN" : "SIGNED OUT";
    public string TgColor => Tga?.IsConnected == true ? SettingsData.Green : SettingsData.Faint;
    public string TgBorder => Tga?.IsConnected == true ? "#14302e" : "#152233";
    public string TgBg => Tga?.IsConnected == true ? "#061615" : "#050f14";
    public bool TgNeedCode => Tga is { } t && (t.IsCodeVisible || t.IsPasswordVisible);
    public string TgStatus => Tga is null ? NotConfigured : OrNotConfigured(Tga.StatusText);
    public string TgStatusColor => Tga?.IsConnected == true ? SettingsData.Green : SettingsData.Amber;
    public string TgBtnLabel => Tga is null ? "NOT CONNECTED"
        : Tga.IsConnected ? "DISCONNECT"
        : Tga.IsPasswordVisible ? "CONFIRM PASSWORD"
        : Tga.IsCodeVisible ? "CONFIRM CODE" : "CONNECT";
    public string TgBtnColor => Tga?.IsConnected == true ? SettingsData.Red : SettingsData.Accent;
    public string TgBtnBorder => Tga?.IsConnected == true ? "#3a1620" : "#14302e";
    public string TgBtnBg => Tga?.IsConnected == true ? "#14060a" : "#061615";

    private bool TgInline => Tgs?.InlineButtonsEnabled ?? false;
    public string TgInlineTrack => TgInline ? "#14302e" : "#152233";
    public string TgInlineKnob => TgInline ? SettingsData.Accent : SettingsData.Dimmer;
    public double TgInlineKnobLeft => TgInline ? 15 : 2;
    public string TgInlineBorder => TgInline ? "#14302e" : "#152233";
    public string TgInlineBg => TgInline ? "#061615" : "#050f14";
    public bool TgHasPending => TgInline && (Tgs?.HasPendingSignals ?? false);
    public bool TgNoPending => !TgHasPending;

    private void TgConnect()
    {
        if (Tga is not { } t) { Toast("Telegram is not connected yet", "warn"); return; }
        Exec(t.IsConnected ? t.DisconnectCommand : t.ConnectCommand);
        Refresh(); RaiseInputs();
    }

    private void TgToggleInline()
    {
        if (Tgs is not { } s) { Toast("Telegram signals are not connected yet", "warn"); return; }
        if (!s.InlineButtonsEnabled)
            AskConfirm(new ConfirmConfig
            {
                Kind = "inline", Accent = "#3a2a12", IconColor = SettingsData.Amber, Icon = "!",
                Title = "Enable inline-button signals",
                Body = "Pressing Accept in Telegram places a market order through the terminal. Risk caps still apply, but you are approving from your phone.",
                HasType = true, TypeWord = "ACCEPT", Cta = "ENABLE",
                BtnBg = "#150f04", BtnFg = SettingsData.Amber, BtnBorder = SettingsData.Amber,
            });
        else { s.InlineButtonsEnabled = false; Refresh(); Toast("Inline signals off — alerts are notification-only", "info"); }
    }

    private void RebuildTgPending()
    {
        Pending.Clear();
        if (Tgs is not { } s || !s.InlineButtonsEnabled) return;
        foreach (var row in s.PendingRows)
        {
            var r = row;
            Pending.Add(new PendingSignalVM
            {
                Label = r.Label, Desc = r.Description, SideColor = r.SideBrush,
                AcceptCommand = new RelayCommand(() => { Exec(r.AcceptCommand); Refresh(); }),
                SkipCommand = new RelayCommand(() => { Exec(r.SkipCommand); Refresh(); }),
            });
        }
    }

    // ── alerts ───────────────────────────────────────────────────────────────
    public string NlPrompt
    {
        get => Al?.NlPrompt ?? "";
        set { if (Al is { } a) a.NlPrompt = value ?? ""; }
    }

    public string NaSym
    {
        get => Al?.NewAlertSymbol ?? "";
        set { if (Al is { } a) { a.NewAlertSymbol = value ?? ""; Touch(); } }
    }

    public string NaCond
    {
        get => Al?.SelectedCondition ?? "";
        set { if (Al is { } a && !string.IsNullOrEmpty(value)) { a.SelectedCondition = value; Touch(); } }
    }

    public string NaThr
    {
        get => _naThrText;
        set
        {
            var v = BotsDeskData.Decimalish(value);
            this.RaiseAndSetIfChanged(ref _naThrText, v);
            if (Al is { } a && decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            { a.NewAlertThreshold = d; Touch(); }
        }
    }

    public string NlStatus => Al is null ? NotConfigured : Al.NlRunning ? "parsing…" : OrNotConfigured(Al.NlStatus);
    public string NlBtn => Al?.NlRunning == true ? "GENERATING…" : "GENERATE";

    private bool NaRepeat => Al?.NewAlertRepeat ?? false;
    public string RepeatLabel => NaRepeat ? "repeating" : "once";
    public string RepeatColor => NaRepeat ? SettingsData.Accent : SettingsData.Text3;
    public string RepeatBorder => NaRepeat ? "#14302e" : "#152233";
    public string RepeatBg => NaRepeat ? "#061615" : "#050f14";
    public string RepeatMark => NaRepeat ? "✓" : "";
    public string RepeatBoxBorder => NaRepeat ? SettingsData.Accent : "#152233";
    public string RepeatBoxBg => NaRepeat ? SettingsData.Accent : "transparent";

    private bool Sound => Al?.SoundEnabled ?? false;
    public string SoundMark => Sound ? "✓" : "";
    public string SoundBorder => Sound ? SettingsData.Accent : "#152233";
    public string SoundBg => Sound ? SettingsData.Accent : "transparent";

    public string AlNote => "Conditions come from the live alert engine — anything not listed is not evaluated.";
    public string ActiveMeta => _host is null ? NotConfigured : ActiveAlertCount + " watching · " + FiredAlertCount + " fired";
    public bool NoActive => (Al?.Alerts.Count ?? 0) == 0;

    private void RebuildNewChannels()
    {
        NewChannels.Clear();
        if (Al is not { } a) return;
        void Chip(string label, Func<bool> get, Action<bool> set)
        {
            var on = get();
            NewChannels.Add(new ChannelChip
            {
                Label = label,
                Border = on ? "#14302e" : "#152233",
                Bg = on ? "#061615" : "transparent",
                Fg = on ? SettingsData.Accent : SettingsData.Faint,
                Command = new RelayCommand(() => { set(!get()); RebuildNewChannels(); }),
            });
        }
        Chip("TELEGRAM", () => a.NewAlertSendTelegram, v => a.NewAlertSendTelegram = v);
        Chip("DISCORD", () => a.NewAlertSendDiscord, v => a.NewAlertSendDiscord = v);
        Chip("NTFY", () => a.NewAlertSendNtfy, v => a.NewAlertSendNtfy = v);
        Chip("EMAIL", () => a.NewAlertSendEmail, v => a.NewAlertSendEmail = v);
    }

    private void RebuildExamples()
    {
        if (Examples.Count > 0) return;
        foreach (var t in new[] { "ping me when BTC breaks 75k", "tell me if ETH drops 10% in 1h", "alert when BTC 24h change is above 5%" })
        {
            var s = t;
            Examples.Add(new ExampleChip { Label = t, Command = new RelayCommand(() => { NlPrompt = s; this.RaisePropertyChanged(nameof(NlPrompt)); }) });
        }
    }

    private void AlAdd()
    {
        if (Al is not { } a) { Toast("Alerts are not connected yet", "warn"); return; }
        if (a.NewAlertSymbol.Trim().Length == 0) { Toast("Symbol is required", "warn"); return; }
        if (_naThrText.Trim().Length == 0) { Toast("Threshold is required", "warn"); return; }
        if (!(a.NewAlertSendTelegram || a.NewAlertSendDiscord || a.NewAlertSendNtfy || a.NewAlertSendEmail))
        { Toast("Pick at least one channel", "warn"); return; }
        Exec(a.AddAlertCommand);
        Refresh();
        Toast("Alert added · " + a.NewAlertSymbol + " " + a.SelectedCondition + " " + _naThrText, "ok");
    }

    private void RebuildAlerts()
    {
        Alerts.Clear();
        if (Al is not { } a) return;
        foreach (var item in a.Alerts)
        {
            var it = item;
            var channels = new List<string>();
            if (it.SendTelegram) channels.Add("TE");
            if (it.SendDiscord) channels.Add("DI");
            if (it.SendNtfy) channels.Add("NT");
            if (it.SendEmail) channels.Add("EM");
            Alerts.Add(new AlertRowVM
            {
                Symbol = it.Symbol,
                Rule = it.ConditionLabel,
                Dot = it.StatusColor,
                Distance = "",              // no live distance-to-trigger is published
                DistColor = SettingsData.Faint,
                Channels = channels,
                Repeat = "",                // repeat flag is not exposed per alert row
                Bg = it.HasFired ? "rgba(4,16,26,.6)" : "transparent",
                ToggleLabel = it.StatusLabel,   // read-only state chip: no pause API exists
                ToggleColor = it.StatusColor,
                ToggleCommand = null,
                DeleteCommand = new RelayCommand(() => { Exec(a.DeleteAlertCommand, it.Id); Refresh(); Toast("Alert deleted", "info"); }),
            });
        }
    }

    private void RebuildHistory()
    {
        History.Clear();
        if (Al is not { } a) return;
        foreach (var h in a.AlertHistory)
            History.Add(new HistoryRow
            {
                Time = h.FiredAtFormatted,
                Channel = h.Symbol,
                Color = SettingsData.Accent,
                Msg = h.ConditionLabel + " → " + h.TriggerValueFormatted,
            });
    }

    // ── execution defaults & hotkeys ─────────────────────────────────────────
    private void RebuildExecFlags()
    {
        ExecFlags.Clear();
        if (Wal is not { } w) return;

        var paper = w.GlobalPaperOnlyMode;
        ExecFlags.Add(new GuardToggle
        {
            Label = "Paper-only mode",
            Hint = paper ? "Every order is simulated" : "Live orders are allowed",
            TrackBg = paper ? "#14302e" : "#152233",
            Knob = paper ? SettingsData.Accent : SettingsData.Dimmer,
            KnobLeft = paper ? 15 : 2,
            Command = new RelayCommand(() =>
            {
                Exec(w.ApplyGlobalExecutionModeCommand, w.GlobalPaperOnlyMode ? "LIVE" : "PAPER");
                Touch(); Refresh();
                if (w.GlobalPaperOnlyMode && !w.LicenseAllowsLive) Toast("Live mode needs a valid license — pinned to paper", "warn");
            }),
        });

        var allows = w.LicenseAllowsLive;
        ExecFlags.Add(new GuardToggle
        {
            Label = "License allows live trading",
            Hint = allows ? "A valid license is active" : "Activate a license to unlock live orders",
            TrackBg = allows ? "#14302e" : "#152233",
            Knob = allows ? SettingsData.Accent : SettingsData.Dimmer,
            KnobLeft = allows ? 15 : 2,
            Command = null,   // read-only: driven by the license, not by this desk
        });
    }

    private void HkReset()
    {
        if (_host?.HotkeySettings is not { } hk) { Toast("Hotkeys are not connected yet", "warn"); return; }
        var d = new HotkeySettings();
        hk.BuyMarket = d.BuyMarket; hk.SellMarket = d.SellMarket;
        hk.Allocation25 = d.Allocation25; hk.Allocation50 = d.Allocation50; hk.Allocation100 = d.Allocation100;
        hk.CancelOrders = d.CancelOrders; hk.FocusPair = d.FocusPair;
        hk.Save();
        _capturing = null;
        foreach (var r in Hotkeys) { r.Bg = "transparent"; r.CaptureLabel = "CAPTURE"; }
        MarkSaved(); Refresh();
        Toast("Hotkeys reset to defaults and saved", "info");
    }

    private void HkCapture(string k, HotkeyRowVM row)
    {
        if (_capturing == k) { _capturing = null; row.Bg = "transparent"; row.CaptureLabel = "CAPTURE"; return; }
        foreach (var r in Hotkeys) { r.Bg = "transparent"; r.CaptureLabel = "CAPTURE"; }
        _capturing = k; row.Bg = "#0e2a2a"; row.CaptureLabel = "TYPE A KEY NAME";
        Toast("Type the key name for “" + row.Label + "” in the box — e.g. B, D1, Escape", "info");
    }

    // ── stable input instances ───────────────────────────────────────────────
    private void BuildStableFields()
    {
        // affiliate links
        AffFields.Clear();
        AffField("BINANCE REFERRAL", () => _host?.BinanceAffiliateUrl ?? "", (h, v) => h.BinanceAffiliateUrl = v);
        AffField("BYBIT REFERRAL", () => _host?.BybitAffiliateUrl ?? "", (h, v) => h.BybitAffiliateUrl = v);
        AffField("OKX REFERRAL", () => _host?.OkxAffiliateUrl ?? "", (h, v) => h.OkxAffiliateUrl = v);
        AffField("KUCOIN REFERRAL", () => _host?.KucoinAffiliateUrl ?? "", (h, v) => h.KucoinAffiliateUrl = v);

        // exchange key fields — one stable set per exchange, secrets always masked
        ExTab("binance",
            () => _host?.BinanceKeyInput ?? "", (h, v) => h.BinanceKeyInput = v,
            () => _host?.BinanceSecretInput ?? "", (h, v) => h.BinanceSecretInput = v, null, null);
        ExTab("bybit",
            () => _host?.BybitKeyInput ?? "", (h, v) => h.BybitKeyInput = v,
            () => _host?.BybitSecretInput ?? "", (h, v) => h.BybitSecretInput = v, null, null);
        ExTab("okx",
            () => _host?.OkxKeyInput ?? "", (h, v) => h.OkxKeyInput = v,
            () => _host?.OkxSecretInput ?? "", (h, v) => h.OkxSecretInput = v,
            () => _host?.OkxPassphraseInput ?? "", (h, v) => h.OkxPassphraseInput = v);
        ExTab("kucoin",
            () => _host?.KucoinKeyInput ?? "", (h, v) => h.KucoinKeyInput = v,
            () => _host?.KucoinSecretInput ?? "", (h, v) => h.KucoinSecretInput = v,
            () => _host?.KucoinPassphraseInput ?? "", (h, v) => h.KucoinPassphraseInput = v);

        // notification channels
        NtChannels.Clear();
        var telegram = Channel("telegram", "Telegram", 2, false,
            "Create a bot with @BotFather, then send it any message and read the chat id.");
        AddChannelField(telegram, "BOT TOKEN", true, null, () => Al?.TelegramBotToken ?? "", (a, v) => a.TelegramBotToken = v);
        AddChannelField(telegram, "CHAT ID", false, null, () => Al?.TelegramChatId ?? "", (a, v) => a.TelegramChatId = v);

        var discord = Channel("discord", "Discord", 1, false,
            "Channel webhook URL. Read from DISCORD_WEBHOOK_URL at start-up and kept in memory only.");
        AddChannelField(discord, "WEBHOOK URL", true, null, () => Al?.DiscordWebhookUrl ?? "", (a, v) => a.DiscordWebhookUrl = v);

        var ntfy = Channel("ntfy", "ntfy.sh push", 1, false,
            "Free anonymous push. Anyone with the topic can publish — use a long random string. Read from NTFY_TOPIC.");
        AddChannelField(ntfy, "TOPIC OR FULL URL", true, null, () => Al?.NtfyTopic ?? "", (a, v) => a.NtfyTopic = v);

        var email = Channel("email", "Email (SMTP)", 3, true,
            "For Gmail use smtp.gmail.com / 587 / STARTTLS with an App Password. Read from the EMAIL_SMTP_* variables.");
        AddChannelField(email, "SMTP HOST", false, null, () => Al?.EmailHost ?? "", (a, v) => a.EmailHost = v);
        AddChannelField(email, "PORT", false, BotsDeskData.Digits,
            () => Al?.EmailPort.ToString(CultureInfo.InvariantCulture) ?? "",
            (a, v) => { if (int.TryParse(v, out var p) && p > 0) a.EmailPort = p; });
        AddChannelField(email, "USERNAME", false, null, () => Al?.EmailUsername ?? "", (a, v) => a.EmailUsername = v);
        AddChannelField(email, "PASSWORD", true, null, () => Al?.EmailPassword ?? "", (a, v) => a.EmailPassword = v);
        AddChannelField(email, "FROM", false, null, () => Al?.EmailFrom ?? "", (a, v) => a.EmailFrom = v);
        AddChannelField(email, "TO", false, null, () => Al?.EmailTo ?? "", (a, v) => a.EmailTo = v);

        // hotkeys
        Hotkeys.Clear();
        foreach (var (k, label, color, placeholder) in SettingsData.Hotkeys)
        {
            var key = k;
            var row = new HotkeyRowVM { Label = label, KeyColor = color, Placeholder = placeholder };
            row.Changed = v =>
            {
                if (_host?.HotkeySettings is not { } hk) return;
                SetHotkey(hk, key, v);
                hk.Save();
                _lastSaved = DateTime.Now;
                RaiseDirty();
                this.RaisePropertyChanged(nameof(NavGroups));
            };
            row.DisplayText = () => _host?.HotkeySettings is { } hk ? HotkeyDisplay(hk, key) : "";
            row.CaptureCommand = new RelayCommand(() => HkCapture(key, row));
            Hotkeys.Add(row);
            _boundHotkeys.Add((row, () => _host?.HotkeySettings is { } hk ? GetHotkey(hk, key) : ""));
        }
    }

    /// <summary>Execution defaults — global risk &amp; mode off the wallet workspace.
    /// Built at attach time because the option lists come from the live VM.</summary>
    private void BuildExecFields()
    {
        if (Wal is not { } wal) return;
        ExecFields.Clear();

        ExecSelect("EXECUTION MODE", new List<string> { "Paper only", "Live allowed" },
            () => Wal is null ? "" : Wal.GlobalPaperOnlyMode ? "Paper only" : "Live allowed",
            (w, v) => Exec(w.ApplyGlobalExecutionModeCommand, v == "Live allowed" ? "LIVE" : "PAPER"));
        ExecSelect("QUOTE ASSET", wal.GlobalQuoteAssetOptions.ToList(),
            () => Wal?.GlobalQuoteAssetSymbol ?? "", (w, v) => w.GlobalQuoteAssetSymbol = v);
        ExecNumber("POSITION SIZE", "%", () => Wal?.GlobalPositionSizingPercent,
            (w, d) => Exec(w.ApplyGlobalPositionSizingCommand, d.ToString(CultureInfo.InvariantCulture)));
        ExecNumber("MAX SPEND / TRADE", "USDT", () => Wal?.GlobalMaxSpendPerTradeUsdt, (w, d) => w.GlobalMaxSpendPerTradeUsdt = d);
        ExecNumber("MAX DAILY LOSS", "USDT", () => Wal?.GlobalMaxDailyLossUsdt, (w, d) => w.GlobalMaxDailyLossUsdt = d);
        ExecNumber("MAX OPEN EXPOSURE", "USDT", () => Wal?.GlobalMaxOpenExposureUsdt, (w, d) => w.GlobalMaxOpenExposureUsdt = d);
    }

    private void AffField(string label, Func<string> read, Action<MainWindowViewModel, string> write)
    {
        var f = new SetField { Label = label, Placeholder = "https://…", IsText = true };
        f.Changed = v => { if (_host is { } h) { write(h, v); Touch(); } };
        AffFields.Add(f);
        _boundFields.Add((f, read));
    }

    private void ExTab(string id,
        Func<string> readKey, Action<MainWindowViewModel, string> writeKey,
        Func<string> readSecret, Action<MainWindowViewModel, string> writeSecret,
        Func<string>? readPass, Action<MainWindowViewModel, string>? writePass)
    {
        var list = new List<SetField>();

        SetField Make(string label, string placeholder, Func<string> read, Action<MainWindowViewModel, string> write)
        {
            var f = new SetField { Label = label, Placeholder = placeholder, IsPassword = true, IsText = true };
            f.Changed = v => { if (_host is { } h) { write(h, v); Touch(); } };
            _boundFields.Add((f, read));
            return f;
        }

        list.Add(Make("API KEY", "paste the API key", readKey, writeKey));
        list.Add(Make("API SECRET", "shown only once by the exchange", readSecret, writeSecret));
        if (readPass is not null && writePass is not null)
            list.Add(Make("PASSPHRASE", "API passphrase", readPass, writePass));

        _exFieldsByTab[id] = list;
    }

    private NtChannelVM Channel(string id, string name, int cols, bool ssl, string hint)
    {
        var ch = new NtChannelVM { Id = id, Name = name, Hint = hint, Cols = cols, HasSsl = ssl, Open = _ntOpen == id };
        ch.ToggleOpenCommand = new RelayCommand(() =>
        {
            _ntOpen = _ntOpen == id ? null : id;
            foreach (var o in NtChannels) o.Open = _ntOpen == o.Id;
        });
        ch.TestCommand = new RelayCommand(() => NtTest(id));
        ch.EnableCommand = new RelayCommand(() => NtApply(id));
        ch.SslCommand = new RelayCommand(() =>
        {
            if (Al is not { } a) { Toast("Alerts are not connected yet", "warn"); return; }
            a.EmailUseSsl = !a.EmailUseSsl; RefreshNtChannel("email"); Touch();
        });
        NtChannels.Add(ch);
        return ch;
    }

    private void AddChannelField(NtChannelVM ch, string label, bool pass, Func<string, string>? clean,
        Func<string> read, Action<AlertsViewModel, string> write)
    {
        var f = new SetField { Label = label, IsPassword = pass, IsText = true, Clean = clean };
        f.Changed = v => { if (Al is { } a) { write(a, v); RefreshNtChannel(ch.Id); Touch(); } };
        ch.Fields.Add(f);
        _boundFields.Add((f, read));
    }

    private void ExecSelect(string label, List<string> options, Func<string> read, Action<WalletWorkspaceViewModel, string> write)
    {
        var f = new SetField { Label = label, IsSelect = true, IsText = false, Options = options };
        f.Changed = v => { if (Wal is { } w && v.Length > 0) { write(w, v); Touch(); } };
        ExecFields.Add(f);
        _boundFields.Add((f, read));
    }

    private void ExecNumber(string label, string unit, Func<decimal?> read, Action<WalletWorkspaceViewModel, decimal> write)
    {
        var f = new SetField { Label = label, Unit = unit, IsText = true, Clean = BotsDeskData.Decimalish };
        f.Changed = v =>
        {
            if (Wal is { } w && decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            { write(w, d); Touch(); }
        };
        ExecFields.Add(f);
        _boundFields.Add((f, () => read() is { } d ? d.ToString("0.########", CultureInfo.InvariantCulture) : ""));
    }

    private static string GetHotkey(HotkeySettings hk, string key) => key switch
    {
        "BuyMarket" => hk.BuyMarket,
        "SellMarket" => hk.SellMarket,
        "Allocation25" => hk.Allocation25,
        "Allocation50" => hk.Allocation50,
        "Allocation100" => hk.Allocation100,
        "CancelOrders" => hk.CancelOrders,
        _ => hk.FocusPair,
    };

    private static void SetHotkey(HotkeySettings hk, string key, string v)
    {
        switch (key)
        {
            case "BuyMarket": hk.BuyMarket = v; break;
            case "SellMarket": hk.SellMarket = v; break;
            case "Allocation25": hk.Allocation25 = v; break;
            case "Allocation50": hk.Allocation50 = v; break;
            case "Allocation100": hk.Allocation100 = v; break;
            case "CancelOrders": hk.CancelOrders = v; break;
            default: hk.FocusPair = v; break;
        }
    }

    private static string HotkeyDisplay(HotkeySettings hk, string key) => key switch
    {
        "BuyMarket" => hk.BuyMarketDisplay,
        "SellMarket" => hk.SellMarketDisplay,
        "Allocation25" => hk.Allocation25Display,
        "Allocation50" => hk.Allocation50Display,
        "Allocation100" => hk.Allocation100Display,
        "CancelOrders" => hk.CancelOrdersDisplay,
        _ => hk.FocusPairDisplay,
    };

    // ── save / revert ────────────────────────────────────────────────────────
    private void SaveAll()
    {
        if (_host is not { } h) { Toast("Settings are not connected yet", "warn"); return; }

        var done = new List<string>();
        Exec(h.SaveAiSettingsCommand); done.Add("AI provider");

        // Only save an exchange whose key field actually holds something — saving an
        // empty field would wipe a stored credential.
        foreach (var (id, name, _) in SettingsData.Exchanges)
        {
            var fields = _exFieldsByTab[id];
            if (fields[0].Value.Trim().Length == 0) continue;
            Exec(ExSaveCommandOf(id)); done.Add(name + " keys");
        }

        Exec(h.SaveAffiliateLinksCommand); done.Add("affiliate links");
        if (h.HotkeySettings is { } hk) { hk.Save(); done.Add("hotkeys"); }

        MarkSaved(); Refresh();
        Toast("Saved: " + string.Join(", ", done) + " · notification channels are session-only", "ok");
    }

    private void OnRevert()
    {
        if (_host is null) { Toast("Settings are not connected yet", "warn"); return; }
        if (!_dirty) { Toast("Nothing to revert", "info"); return; }
        AskConfirm(new ConfirmConfig
        {
            Kind = "revert", Accent = "#3a1620", IconColor = SettingsData.Red, Icon = "↻",
            Title = "Reload settings from disk",
            Body = "Every field edited in this session is re-read from the stored configuration. Saved settings are untouched.",
            Cta = "RELOAD", BtnBg = "#14060a", BtnFg = SettingsData.Red, BtnBorder = SettingsData.Red,
        });
    }

    // ── confirm modal ────────────────────────────────────────────────────────
    private string? _modal;
    private ConfirmConfig _confirm = new();
    private string _confirmTyped = "";
    public bool ModalConfirm => _modal == "confirm";
    public string ConfirmAccent => _confirm.Accent;
    public string ConfirmIcon => _confirm.Icon;
    public string ConfirmIconColor => _confirm.IconColor;
    public string ConfirmTitle => _confirm.Title;
    public string ConfirmBody => _confirm.Body;
    public bool ConfirmHasType => _confirm.HasType;
    public string ConfirmTypeWord => _confirm.TypeWord;
    public string ConfirmCta => _confirm.Cta;
    public string ConfirmTyped { get => _confirmTyped; set { this.RaiseAndSetIfChanged(ref _confirmTyped, value ?? ""); RaiseConfirmBtn(); } }
    private bool ConfirmReady => !_confirm.HasType || string.Equals((_confirmTyped ?? "").Trim(), _confirm.TypeWord, StringComparison.OrdinalIgnoreCase);
    public string ConfirmBtnBg => ConfirmReady ? _confirm.BtnBg : "#0a1520";
    public string ConfirmBtnFg => ConfirmReady ? _confirm.BtnFg : "#3d5a72";
    public string ConfirmBtnBorder => ConfirmReady ? _confirm.BtnBorder : "#152233";
    public double ConfirmBtnOpacity => ConfirmReady ? 1 : .7;
    private void RaiseConfirmBtn() { foreach (var n in new[] { nameof(ConfirmBtnBg), nameof(ConfirmBtnFg), nameof(ConfirmBtnBorder), nameof(ConfirmBtnOpacity) }) this.RaisePropertyChanged(n); }

    private void AskConfirm(ConfirmConfig cfg)
    {
        _confirm = cfg; _confirmTyped = ""; _modal = "confirm";
        foreach (var n in new[] { nameof(ConfirmAccent), nameof(ConfirmIcon), nameof(ConfirmIconColor), nameof(ConfirmTitle), nameof(ConfirmBody),
            nameof(ConfirmHasType), nameof(ConfirmTypeWord), nameof(ConfirmCta), nameof(ConfirmTyped), nameof(ModalConfirm) })
            this.RaisePropertyChanged(n);
        RaiseConfirmBtn();
    }

    private void RunConfirm()
    {
        if (!ConfirmReady) { Toast("Type " + _confirm.TypeWord + " to confirm", "warn"); return; }
        switch (_confirm.Kind)
        {
            case "revert":
                SyncFields();
                _dirty = false; RaiseDirty(); RaiseInputs();
                Toast("Fields reloaded from the stored configuration", "info");
                break;

            case "delProfile":
                if (_host is { } dh && _confirm.BotId is { } pn)
                {
                    dh.ProfileName = pn;
                    Exec(dh.DeleteProfileCommand);
                    Toast(OrNotConfigured(dh.ProfileStatus), "info");
                }
                break;

            case "clearKeys":
                if (_confirm.BotId is { } exId && _exFieldsByTab.TryGetValue(exId, out var fields))
                {
                    foreach (var f in fields) f.Value = "";
                    Exec(ExSaveCommandOf(exId));
                    Toast(ExName(exId) + " keys removed from the local store", "warn");
                }
                break;

            case "inline":
                if (Tgs is { } s) { s.InlineButtonsEnabled = true; Toast("Inline signals enabled", "warn"); }
                break;
        }
        _modal = null; this.RaisePropertyChanged(nameof(ModalConfirm));
        Refresh();
    }

    // ── toast ────────────────────────────────────────────────────────────────
    private bool _hasToast;
    public bool HasToast { get => _hasToast; private set => this.RaiseAndSetIfChanged(ref _hasToast, value); }
    public string ToastMsg { get; private set; } = "";
    public string ToastColor { get; private set; } = SettingsData.Accent;
    public string ToastIcon { get; private set; } = "";
    public string ToastBorder { get; private set; } = "#0d1b27";
    public string ToastMeta { get; private set; } = "";

    private void Toast(string msg, string kind = "ok")
    {
        (string color, string icon) = kind switch
        {
            "ok" => (SettingsData.Green, "✓"), "warn" => (SettingsData.Amber, "!"), "bad" => (SettingsData.Red, "✕"),
            "ai" => (SettingsData.Accent, "✦"), _ => (SettingsData.Accent, "›"),
        };
        ToastMsg = msg; ToastColor = color; ToastIcon = icon; ToastBorder = SettingsData.Alpha(color, "55");
        ToastMeta = DateTime.Now.ToString("HH:mm:ss");
        foreach (var n in new[] { nameof(ToastMsg), nameof(ToastColor), nameof(ToastIcon), nameof(ToastBorder), nameof(ToastMeta) })
            this.RaisePropertyChanged(n);
        HasToast = true; _toastTimer.Stop(); _toastTimer.Start();
    }
}
