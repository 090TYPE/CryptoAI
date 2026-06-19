using Avalonia.Threading;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public enum CopyTradingMode { Off, Leader, Follower }

public sealed class CopyTradeLogEntry
{
    public string Time    { get; init; } = "";
    public string Message { get; init; } = "";
    public bool   IsError { get; init; }
}

/// <summary>
/// ViewModel for the Copy Trading workspace.
/// Leader mode: publishes every bot trade to /api/copy-trades.
/// Follower mode: polls a remote leader URL and mirrors trades.
/// </summary>
public sealed class CopyTradingViewModel : ReactiveObject, IDisposable
{
    private readonly CopyTradingLeaderService   _leader;
    private readonly CopyTradingFollowerService _follower;

    // ── Mode ──────────────────────────────────────────────────────────────────

    private CopyTradingMode _mode = CopyTradingMode.Off;
    public CopyTradingMode Mode
    {
        get => _mode;
        set
        {
            this.RaiseAndSetIfChanged(ref _mode, value);
            this.RaisePropertyChanged(nameof(IsLeader));
            this.RaisePropertyChanged(nameof(IsFollower));
            this.RaisePropertyChanged(nameof(ModeLabel));
            ApplyMode();
        }
    }

    public bool IsLeader   => _mode == CopyTradingMode.Leader;
    public bool IsFollower => _mode == CopyTradingMode.Follower;
    public string ModeLabel => _mode switch
    {
        CopyTradingMode.Leader   => "Leader — publishing trades",
        CopyTradingMode.Follower => "Follower — mirroring leader",
        _                        => "Off",
    };

    // ── Follower config ───────────────────────────────────────────────────────

    private string  _leaderUrl    = "http://localhost:5180";
    private decimal _scaleRatio   = 1.0m;
    private decimal _perfFeePct   = 20m;

    public string LeaderUrl
    {
        get => _leaderUrl;
        set
        {
            this.RaiseAndSetIfChanged(ref _leaderUrl, value);
            _follower.LeaderUrl = value;
        }
    }

    public decimal ScaleRatio
    {
        get => _scaleRatio;
        set
        {
            this.RaiseAndSetIfChanged(ref _scaleRatio, Math.Clamp(Math.Round(value, 2), 0.01m, 10m));
            _follower.ScaleRatio = _scaleRatio;
        }
    }

    public decimal PerformanceFeePct
    {
        get => _perfFeePct;
        set
        {
            this.RaiseAndSetIfChanged(ref _perfFeePct, Math.Clamp(value, 0m, 100m));
            _follower.PerformanceFeePct = _perfFeePct;
        }
    }

    // ── KPI ───────────────────────────────────────────────────────────────────

    private int     _totalPublished;
    private int     _totalCopied;
    private decimal _estimatedFeeEarned;

    public int     TotalPublished      { get => _totalPublished;     private set => this.RaiseAndSetIfChanged(ref _totalPublished, value); }
    public int     TotalCopied         { get => _totalCopied;        private set => this.RaiseAndSetIfChanged(ref _totalCopied, value); }
    public decimal EstimatedFeeEarned  { get => _estimatedFeeEarned; private set => this.RaiseAndSetIfChanged(ref _estimatedFeeEarned, value); }

    public string FeeLabel => $"${EstimatedFeeEarned:N2}";

    // ── Log ───────────────────────────────────────────────────────────────────

    public ObservableCollection<CopyTradeLogEntry> Log { get; } = [];

    // ── Wallet watch (external wallets, on-chain) ───────────────────────────────

    private static readonly string WatchedWalletsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal", "watched-wallets.json");

    private sealed record WatchedWalletDto(string Address, string Label, string Chain, bool Copy);

    private readonly WalletWatchService _walletWatch = new();
    private bool _isWatching;
    private string _newWalletAddress = "";
    private string _newWalletLabel = "";
    private string _selectedWatchChain = "eth";

    /// <summary>Raised so the host can show a toast + add to the notification registry.</summary>
    public event Action<string>? ToastRequested;

    public ObservableCollection<WatchedWalletItemViewModel> WatchedWallets { get; } = [];
    public IReadOnlyList<string> ChainOptions => WalletWatchService.SupportedChains;

    public string NewWalletAddress { get => _newWalletAddress; set => this.RaiseAndSetIfChanged(ref _newWalletAddress, value); }
    public string NewWalletLabel   { get => _newWalletLabel;   set => this.RaiseAndSetIfChanged(ref _newWalletLabel, value); }
    public string SelectedWatchChain { get => _selectedWatchChain; set => this.RaiseAndSetIfChanged(ref _selectedWatchChain, value); }

    public bool IsWatching
    {
        get => _isWatching;
        private set { this.RaiseAndSetIfChanged(ref _isWatching, value); this.RaisePropertyChanged(nameof(WatchStatus)); }
    }

    public string WatchStatus => IsWatching
        ? $"Слежка активна · {WatchedWallets.Count} кошельков"
        : "Слежка выключена";

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> SetLeaderCommand   { get; }
    public ReactiveCommand<Unit, Unit> SetFollowerCommand { get; }
    public ReactiveCommand<Unit, Unit> SetOffCommand      { get; }
    public ReactiveCommand<Unit, Unit> AddWatchedWalletCommand { get; }
    public ReactiveCommand<WatchedWalletItemViewModel, Unit> RemoveWatchedWalletCommand { get; }
    public ReactiveCommand<Unit, Unit> StartWatchCommand { get; }
    public ReactiveCommand<Unit, Unit> StopWatchCommand  { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public CopyTradingViewModel(
        CopyTradingLeaderService leader,
        CopyTradingFollowerService follower)
    {
        _leader   = leader;
        _follower = follower;

        _follower.LeaderUrl        = _leaderUrl;
        _follower.ScaleRatio       = _scaleRatio;
        _follower.PerformanceFeePct = _perfFeePct;

        _leader.TradePublished += OnTradePublished;
        _follower.ExecutionCompleted += OnExecutionCompleted;
        _follower.LogMessage += msg => AddLog(msg);

        SetLeaderCommand   = ReactiveCommand.Create(() => { Mode = CopyTradingMode.Leader; });
        SetFollowerCommand = ReactiveCommand.Create(() => { Mode = CopyTradingMode.Follower; });
        SetOffCommand      = ReactiveCommand.Create(() => { Mode = CopyTradingMode.Off; });

        AddWatchedWalletCommand    = ReactiveCommand.Create(AddWatchedWallet);
        RemoveWatchedWalletCommand = ReactiveCommand.Create<WatchedWalletItemViewModel>(RemoveWatchedWallet);
        StartWatchCommand          = ReactiveCommand.Create(StartWatch);
        StopWatchCommand           = ReactiveCommand.Create(StopWatch);

        _walletWatch.SignalDetected += OnWalletSignal;
        LoadWatchedWallets();
    }

    // ── Wallet watch logic ──────────────────────────────────────────────────────

    private void AddWatchedWallet()
    {
        var addr = (NewWalletAddress ?? "").Trim();
        if (addr.Length < 8 || !WalletWatchService.IsChainSupported(SelectedWatchChain))
        {
            AddLog("Укажите корректный адрес кошелька и сеть.");
            return;
        }
        if (WatchedWallets.Any(w => string.Equals(w.Address, addr, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(w.Chain, SelectedWatchChain, StringComparison.OrdinalIgnoreCase)))
        {
            AddLog("Этот кошелёк уже отслеживается.");
            return;
        }

        var item = new WatchedWalletItemViewModel(addr, (NewWalletLabel ?? "").Trim(), SelectedWatchChain);
        item.WhenAnyValue(x => x.Copy).Skip(1).Subscribe(_ => SaveWatchedWallets());
        WatchedWallets.Add(item);
        NewWalletAddress = "";
        NewWalletLabel = "";
        SaveWatchedWallets();
        this.RaisePropertyChanged(nameof(WatchStatus));
        AddLog($"Добавлен кошелёк для слежки: {item.Summary}");
        if (IsWatching) StartWatch(); // restart with the new wallet included
    }

    private void RemoveWatchedWallet(WatchedWalletItemViewModel item)
    {
        if (item is null) return;
        WatchedWallets.Remove(item);
        SaveWatchedWallets();
        this.RaisePropertyChanged(nameof(WatchStatus));
        AddLog($"Кошелёк снят со слежки: {item.Summary}");
        if (IsWatching) StartWatch();
    }

    private void StartWatch()
    {
        if (WatchedWallets.Count == 0)
        {
            AddLog("Сначала добавьте хотя бы один кошелёк.");
            return;
        }
        _walletWatch.Start(WatchedWallets.Select(w => (w.Address, w.Chain)));
        IsWatching = true;
        AddLog($"Слежка запущена за {WatchedWallets.Count} кошельками.");
    }

    private void StopWatch()
    {
        _walletWatch.Stop();
        IsWatching = false;
        AddLog("Слежка остановлена.");
    }

    private void OnWalletSignal(CopyTradeSignal s)
    {
        var item = WatchedWallets.FirstOrDefault(w =>
            string.Equals(w.Address, s.WalletAddress, StringComparison.OrdinalIgnoreCase));
        var label = string.IsNullOrWhiteSpace(item?.Label) ? s.WalletAddress[..Math.Min(10, s.WalletAddress.Length)] : item!.Label;
        var action = s.Direction == CopyTradeDirection.Buy ? "КУПИЛ" : "ПРОДАЛ";
        var token = s.PairContractAddress.Length > 12
            ? $"{s.PairContractAddress[..6]}…{s.PairContractAddress[^4..]}"
            : s.PairContractAddress;
        var tx = s.TxHash.Length > 12 ? $"{s.TxHash[..8]}…" : s.TxHash;

        var prefix = (item?.Copy == true && s.Direction == CopyTradeDirection.Buy) ? "⭐ СКОПИРУЙ — " : "👛 ";
        var msg = $"{prefix}{label}: {action} ({s.ChainId})\n" +
                  $"потратил {s.SpentAmountNative:0.###} / получил {s.ReceivedAmountTokens:0.##}\n" +
                  $"токен {token} · tx {tx}";

        ToastRequested?.Invoke(msg);
        AddLog(msg.Replace("\n", "  "));
    }

    private void SaveWatchedWallets()
    {
        try
        {
            var dto = WatchedWallets
                .Select(w => new WatchedWalletDto(w.Address, w.Label, w.Chain, w.Copy))
                .ToList();
            AtomicJsonFile.Write(WatchedWalletsPath, dto);
        }
        catch { /* best-effort */ }
    }

    private void LoadWatchedWallets()
    {
        try
        {
            if (!File.Exists(WatchedWalletsPath)) return;
            var dto = AtomicJsonFile.Read<List<WatchedWalletDto>>(WatchedWalletsPath);
            if (dto is null) return;
            foreach (var d in dto)
            {
                var item = new WatchedWalletItemViewModel(d.Address, d.Label, d.Chain, d.Copy);
                item.WhenAnyValue(x => x.Copy).Skip(1).Subscribe(_ => SaveWatchedWallets());
                WatchedWallets.Add(item);
            }
            this.RaisePropertyChanged(nameof(WatchStatus));
        }
        catch { /* ignore corrupt cache */ }
    }

    public void SetFollowerGateway(IExchangeGateway? gw) => _follower.Gateway = gw;

    // ── Internal ──────────────────────────────────────────────────────────────

    private void ApplyMode()
    {
        switch (_mode)
        {
            case CopyTradingMode.Leader:
                _follower.Stop();
                AddLog("Leader mode active — all bot trades will be published.");
                break;
            case CopyTradingMode.Follower:
                _follower.Start();
                AddLog($"Follower started — polling {_leaderUrl}");
                break;
            case CopyTradingMode.Off:
                _follower.Stop();
                AddLog("Copy trading disabled.");
                break;
        }
    }

    private void OnTradePublished(CopyTrade t)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TotalPublished++;
            AddLog($"Published: {t.Side} {t.Quantity} {t.Symbol} @ {t.Price:N4} [{t.Source}]");
        });
    }

    private void OnExecutionCompleted(CopyExecution e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TotalCopied = _follower.TotalCopied;
            EstimatedFeeEarned = _follower.EstimatedFeesEarned;
            this.RaisePropertyChanged(nameof(FeeLabel));
        });
    }

    private void AddLog(string msg)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var entry = new CopyTradeLogEntry
            {
                Time    = DateTime.Now.ToString("HH:mm:ss"),
                Message = msg,
            };
            Log.Insert(0, entry);
            while (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
        });
    }

    public void Dispose()
    {
        _leader.TradePublished       -= OnTradePublished;
        _follower.ExecutionCompleted -= OnExecutionCompleted;
        _follower.Stop();
        _walletWatch.SignalDetected -= OnWalletSignal;
        _walletWatch.Stop();
    }
}
