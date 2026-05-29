using Avalonia.Media;
using Avalonia.Threading;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

// ════════════════════════════════════════════════════════════════════════════
//  Row VM — one symbol with prices from all exchanges
// ════════════════════════════════════════════════════════════════════════════

public sealed class CrossExchangeSpreadRowVM : ReactiveObject
{
    // Mutable source data (updated in-place by RefreshFrom)
    private CrossExchangePriceRow _row;

    public CrossExchangeSpreadRowVM(CrossExchangePriceRow row) => _row = row;

    public string Symbol => _row.Symbol;

    // ── Per-exchange prices ───────────────────────────────────────────────────

    public string BinanceAskLabel => FmtAsk(_row.Prices, "Binance");
    public string BybitAskLabel   => FmtAsk(_row.Prices, "Bybit");
    public string OkxAskLabel     => FmtAsk(_row.Prices, "OKX");

    public string BinanceBidLabel => FmtBid(_row.Prices, "Binance");
    public string BybitBidLabel   => FmtBid(_row.Prices, "Bybit");
    public string OkxBidLabel     => FmtBid(_row.Prices, "OKX");

    // Highlight: which exchange has the lowest ask (best buy)
    public bool IsBinanceBestBuy => IsBestBuy("Binance");
    public bool IsBybitBestBuy   => IsBestBuy("Bybit");
    public bool IsOkxBestBuy     => IsBestBuy("OKX");

    // Highlight: which exchange has the highest bid (best sell)
    public bool IsBinanceBestSell => IsBestSell("Binance");
    public bool IsBybitBestSell   => IsBestSell("Bybit");
    public bool IsOkxBestSell     => IsBestSell("OKX");

    // Brush properties — teal when best, neutral otherwise (Foreground-safe)
    public IBrush BinanceAskBrush  => IsBinanceBestBuy  ? Teal : DefaultFg;
    public IBrush BybitAskBrush    => IsBybitBestBuy    ? Teal : DefaultFg;
    public IBrush OkxAskBrush      => IsOkxBestBuy      ? Teal : DefaultFg;
    public IBrush BinanceBidBrush  => IsBinanceBestSell ? Amber : DefaultFg;
    public IBrush BybitBidBrush    => IsBybitBestSell   ? Amber : DefaultFg;
    public IBrush OkxBidBrush      => IsOkxBestSell     ? Amber : DefaultFg;

    // Staleness indicators
    public string BinanceAgeLabel => FmtAge(_row.Prices, "Binance");
    public string BybitAgeLabel   => FmtAge(_row.Prices, "Bybit");
    public string OkxAgeLabel     => FmtAge(_row.Prices, "OKX");

    // ── Spread summary ────────────────────────────────────────────────────────

    private CrossExchangeOpportunity? Opp => _row.BestOpportunity;

    public string BestBuyLabel  => Opp is null ? "—" : $"{Opp.BuyExchange}  ${Opp.BuyAsk:N2}";
    public string BestSellLabel => Opp is null ? "—" : $"{Opp.SellExchange}  ${Opp.SellBid:N2}";

    public string GrossSpreadLabel =>
        Opp is null ? "—" : $"{Opp.GrossSpreadPct:+0.000;-0.000}%";

    public string NetSpreadLabel =>
        Opp is null ? "—" : $"{Opp.NetSpreadPct:+0.000;-0.000}%";

    public string ProfitLabel
    {
        get
        {
            if (Opp is null) return "—";
            // Estimated for $500 notional (display only — actual notional from config)
            var profit = 500m * Opp.NetSpreadPct / 100m;
            return profit >= 0 ? $"+${profit:N2}" : $"-${Math.Abs(profit):N2}";
        }
    }

    public bool IsProfitable => Opp?.IsProfitable == true;

    public IBrush NetSpreadBrush =>
        Opp is null ? Gray :
        Opp.NetSpreadPct >= 0.3m  ? Teal :
        Opp.NetSpreadPct >= 0.1m  ? Amber :
        Opp.NetSpreadPct >= 0     ? new SolidColorBrush(Color.Parse("#8FA3B8")) :
                                     Red;

    public IBrush ProfitBrush =>
        IsProfitable ? Teal : Gray;

    // Raw opportunity for the Execute command
    public CrossExchangeOpportunity? Opportunity => _row.BestOpportunity;

    // ── In-place update ───────────────────────────────────────────────────────

    public void RefreshFrom(CrossExchangePriceRow newRow)
    {
        _row = newRow;
        this.RaisePropertyChanged(string.Empty);   // raise all at once
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private bool IsBestBuy(string exch)
    {
        if (!_row.Prices.TryGetValue(exch, out var p) || p.IsStale) return false;
        return _row.Prices.Where(kv => !kv.Value.IsStale)
                          .All(kv => kv.Key == exch || kv.Value.EffAsk >= p.EffAsk);
    }

    private bool IsBestSell(string exch)
    {
        if (!_row.Prices.TryGetValue(exch, out var p) || p.IsStale) return false;
        return _row.Prices.Where(kv => !kv.Value.IsStale)
                          .All(kv => kv.Key == exch || kv.Value.EffBid <= p.EffBid);
    }

    private static string FmtAsk(Dictionary<string, ExchangePriceData> prices, string exch)
    {
        if (!prices.TryGetValue(exch, out var p) || p.IsStale) return "—";
        return FmtPrice(p.EffAsk);
    }

    private static string FmtBid(Dictionary<string, ExchangePriceData> prices, string exch)
    {
        if (!prices.TryGetValue(exch, out var p) || p.IsStale) return "—";
        return FmtPrice(p.EffBid);
    }

    private static string FmtAge(Dictionary<string, ExchangePriceData> prices, string exch)
    {
        if (!prices.TryGetValue(exch, out var p)) return "×";
        if (p.IsStale) return "stale";
        var age = (DateTime.UtcNow - p.UpdatedAt).TotalSeconds;
        return age < 1 ? "<1s" : $"{age:F0}s";
    }

    private static string FmtPrice(decimal p) =>
        p <= 0    ? "—"     :
        p >= 1000 ? $"{p:N0}" :
        p >= 1    ? $"{p:N3}" :
                    $"{p:G5}";

    private static readonly IBrush Teal      = new SolidColorBrush(Color.Parse("#21E6C1"));
    private static readonly IBrush Amber     = new SolidColorBrush(Color.Parse("#F4B860"));
    private static readonly IBrush Red       = new SolidColorBrush(Color.Parse("#FF5555"));
    private static readonly IBrush Gray      = new SolidColorBrush(Color.Parse("#3A4F63"));
    private static readonly IBrush DefaultFg = new SolidColorBrush(Color.Parse("#8FA3B8"));
}

// ════════════════════════════════════════════════════════════════════════════
//  Row VM — execution history
// ════════════════════════════════════════════════════════════════════════════

public sealed class ArbExecutionRowVM
{
    public ArbExecutionRowVM(CrossExchangeArbExecution exec) => _exec = exec;
    private readonly CrossExchangeArbExecution _exec;

    public string TimeLabel     => _exec.ExecutedAt.ToLocalTime().ToString("HH:mm:ss");
    public string Symbol        => _exec.Symbol;
    public string RouteLabel    => $"{_exec.BuyExchange} → {_exec.SellExchange}";
    public string BuyLabel      => $"${_exec.BuyPrice:N2}";
    public string SellLabel     => $"${_exec.SellPrice:N2}";
    public string SpreadLabel   => $"{_exec.GrossSpreadPct:+0.000}%";
    public string ProfitLabel   =>
        _exec.EstimatedProfitUsd >= 0
            ? $"+${_exec.EstimatedProfitUsd:N2}"
            : $"-${Math.Abs(_exec.EstimatedProfitUsd):N2}";
    public string StatusLabel   => _exec.Status;

    public IBrush ProfitBrush =>
        _exec.IsSuccess
            ? new SolidColorBrush(Color.Parse("#21E6C1"))
            : new SolidColorBrush(Color.Parse("#FF5555"));

    public IBrush StatusBrush =>
        _exec.IsSuccess
            ? new SolidColorBrush(Color.Parse("#21E6C1"))
            : new SolidColorBrush(Color.Parse("#FF5555"));
}

// ════════════════════════════════════════════════════════════════════════════
//  Main ViewModel
// ════════════════════════════════════════════════════════════════════════════

public sealed class CrossExchangeArbitrageViewModel : ReactiveObject, IDisposable
{
    private readonly CrossExchangeArbitrageService _svc;
    private readonly DispatcherTimer               _scanTimer;
    private CancellationTokenSource                _execCts = new();
    private bool _disposed;

    // Lookup for in-place row updates (avoids flicker on rapid redraws)
    private readonly Dictionary<string, CrossExchangeSpreadRowVM> _rowMap = new();

    // ── Collections ───────────────────────────────────────────────────────────

    public ObservableCollection<CrossExchangeSpreadRowVM> SpreadRows  { get; } = [];
    public ObservableCollection<ArbExecutionRowVM>        History     { get; } = [];

    // ── Configuration ─────────────────────────────────────────────────────────

    private decimal _minNetSpreadPct;
    private decimal _notionalUsd;
    private decimal _takerFeePct;
    private bool    _autoExecute;

    public decimal MinNetSpreadPct
    {
        get => _minNetSpreadPct;
        set { this.RaiseAndSetIfChanged(ref _minNetSpreadPct, Math.Max(0.01m, value)); _svc.MinNetSpreadPct = _minNetSpreadPct; }
    }

    public decimal NotionalUsd
    {
        get => _notionalUsd;
        set { this.RaiseAndSetIfChanged(ref _notionalUsd, Math.Max(10m, value)); _svc.TradeNotionalUsd = _notionalUsd; }
    }

    public decimal TakerFeePct
    {
        get => _takerFeePct;
        set { this.RaiseAndSetIfChanged(ref _takerFeePct, Math.Max(0.01m, value)); _svc.TakerFeePct = _takerFeePct; }
    }

    public bool AutoExecute
    {
        get => _autoExecute;
        set { this.RaiseAndSetIfChanged(ref _autoExecute, value); _svc.AutoExecute = _autoExecute; }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private bool   _isExecuting;
    private string _statusLabel = "Connecting to price feeds…";

    public bool IsExecuting
    {
        get => _isExecuting;
        private set => this.RaiseAndSetIfChanged(ref _isExecuting, value);
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => this.RaiseAndSetIfChanged(ref _statusLabel, value);
    }

    // ── KPIs ──────────────────────────────────────────────────────────────────

    public int    ProfitableCount => SpreadRows.Count(r => r.IsProfitable);
    public int    TotalSymbols    => SpreadRows.Count;
    public decimal TotalProfitUsd =>
        History.Where(h => h.StatusLabel == "Executed").Sum(h =>
        {
            var exec = _svc.History.FirstOrDefault(e =>
                e.ExecutedAt == DateTime.Parse(h.TimeLabel + " " + DateTime.Today.ToString("yyyy-MM-dd")));
            return exec?.EstimatedProfitUsd ?? 0m;
        });

    public string TotalProfitLabel
    {
        get
        {
            var sum = _svc.History.Where(e => e.IsSuccess).Sum(e => e.EstimatedProfitUsd);
            return sum >= 0 ? $"+${sum:N2}" : $"-${Math.Abs(sum):N2}";
        }
    }

    public string BestSpreadLabel
    {
        get
        {
            var best = SpreadRows
                .Where(r => r.Opportunity is not null)
                .OrderByDescending(r => r.Opportunity!.NetSpreadPct)
                .FirstOrDefault();
            if (best?.Opportunity is null) return "—";
            return $"{best.Symbol} {best.Opportunity.NetSpreadPct:+0.000}% ({best.Opportunity.BuyExchange}→{best.Opportunity.SellExchange})";
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<CrossExchangeSpreadRowVM, Unit> ExecuteCommand { get; }
    public ReactiveCommand<Unit, Unit>                     ToggleAutoCommand { get; }

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<string>? ToastRequested;

    /// <summary>
    /// Fired after a successful arb execution. Args: symbol, buyExchange, sellExchange, profitUsd.
    /// Set by MainWindowViewModel to record into P&L dashboard.
    /// </summary>
    public Action<string, string, string, decimal>? OnArbExecuted;

    // ── Constructor ───────────────────────────────────────────────────────────

    public CrossExchangeArbitrageViewModel(CrossExchangeArbitrageService svc)
    {
        _svc = svc;

        _minNetSpreadPct = svc.MinNetSpreadPct;
        _notionalUsd     = svc.TradeNotionalUsd;
        _takerFeePct     = svc.TakerFeePct;
        _autoExecute     = svc.AutoExecute;

        ExecuteCommand = ReactiveCommand.CreateFromTask<CrossExchangeSpreadRowVM>(
            row => DoExecuteAsync(row), outputScheduler: App.UiScheduler);

        ToggleAutoCommand = ReactiveCommand.Create(
            () => { AutoExecute = !_autoExecute; },
            outputScheduler: App.UiScheduler);

        // Connect price feeds
        _svc.StartMonitoring();

        // Scan for opportunities every 1 second
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _scanTimer.Tick += (_, _) => DoScanTick();
        _scanTimer.Start();
    }

    // ── Scan tick (1 s) ───────────────────────────────────────────────────────

    private void DoScanTick()
    {
        var matrix = _svc.GetPriceMatrix();
        if (matrix.Count == 0) return;

        var newSymbols    = matrix.Select(r => r.Symbol).ToHashSet();
        var removedSymbols = _rowMap.Keys.Except(newSymbols).ToList();

        // Remove stale symbols
        foreach (var sym in removedSymbols)
        {
            if (_rowMap.TryGetValue(sym, out var staleRow))
            {
                SpreadRows.Remove(staleRow);
                _rowMap.Remove(sym);
            }
        }

        // Update or add
        for (int i = 0; i < matrix.Count; i++)
        {
            var row = matrix[i];

            if (_rowMap.TryGetValue(row.Symbol, out var existingVm))
            {
                // In-place update to avoid collection thrash
                existingVm.RefreshFrom(row);

                // Keep sort order stable by repositioning if necessary
                var currentIdx = SpreadRows.IndexOf(existingVm);
                if (currentIdx != i && i < SpreadRows.Count)
                    SpreadRows.Move(currentIdx, i);
            }
            else
            {
                var newVm = new CrossExchangeSpreadRowVM(row);
                _rowMap[row.Symbol] = newVm;
                if (i < SpreadRows.Count)
                    SpreadRows.Insert(i, newVm);
                else
                    SpreadRows.Add(newVm);
            }

            // Auto-execute check
            if (row.BestOpportunity is { } opp && _svc.ShouldAutoExecute(opp) && !_isExecuting)
                _ = DoExecuteAsync(null, opp);
        }

        RaiseKpis();
        UpdateStatus(matrix);
    }

    private void UpdateStatus(IReadOnlyList<CrossExchangePriceRow> matrix)
    {
        var profitable = matrix.Count(r => r.BestOpportunity?.IsProfitable == true);
        var exchangeCount = CrossExchangeArbitrageService.ExchangeNames
            .Count(e => matrix.Any(r => r.Prices.ContainsKey(e)));

        StatusLabel = profitable > 0
            ? $"⚡ {profitable} profitable spread{(profitable > 1 ? "s" : "")} · {matrix.Count} symbols · {exchangeCount}/3 exchanges live"
            : $"Scanning · {matrix.Count} symbols · {exchangeCount}/3 exchanges live";
    }

    // ── Execute ───────────────────────────────────────────────────────────────

    private async Task DoExecuteAsync(CrossExchangeSpreadRowVM? row,
                                      CrossExchangeOpportunity? opp = null)
    {
        opp ??= row?.Opportunity;
        if (opp is null) return;

        IsExecuting = true;
        StatusLabel = $"Executing arb: {opp.BuyExchange} → {opp.SellExchange} {opp.Symbol}…";

        _execCts.Cancel();
        _execCts = new CancellationTokenSource();

        try
        {
            var (ok, error, profit) = await _svc.ExecuteArbAsync(opp, _execCts.Token);

            Dispatcher.UIThread.Post(() =>
            {
                // Prepend to history
                if (_svc.History.Count > 0)
                {
                    var latest = _svc.History[^1];
                    History.Insert(0, new ArbExecutionRowVM(latest));
                    if (History.Count > 200) History.RemoveAt(History.Count - 1);
                }

                RaiseKpis();

                if (ok)
                {
                    StatusLabel = $"✓ Executed {opp.Symbol}  {opp.BuyExchange}→{opp.SellExchange}  profit ≈ ${profit:N2}";
                    ToastRequested?.Invoke(
                        $"⚡ Arb executed\n{opp.Symbol}: {opp.BuyExchange}→{opp.SellExchange}\nProfit ≈ ${profit:N2}");
                    OnArbExecuted?.Invoke(opp.Symbol, opp.BuyExchange, opp.SellExchange, profit);
                }
                else
                {
                    StatusLabel = $"✗ {error}";
                    ToastRequested?.Invoke($"Arb failed: {error}");
                }

                IsExecuting = false;
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => IsExecuting = false);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusLabel = $"Error: {ex.Message}";
                IsExecuting = false;
            });
        }
    }

    private void RaiseKpis()
    {
        this.RaisePropertyChanged(nameof(ProfitableCount));
        this.RaisePropertyChanged(nameof(TotalSymbols));
        this.RaisePropertyChanged(nameof(TotalProfitLabel));
        this.RaisePropertyChanged(nameof(BestSpreadLabel));
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanTimer.Stop();
        _execCts.Cancel();
        _execCts.Dispose();
        _svc.Dispose();
    }
}
