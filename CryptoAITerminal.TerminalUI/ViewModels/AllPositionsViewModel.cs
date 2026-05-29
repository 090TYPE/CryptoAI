using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.Core.Enums;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>One row in the unified positions table.</summary>
public sealed class UnifiedPositionRowVM : ReactiveObject
{
    private decimal _markPrice;
    private decimal _unrealizedPnl;
    private decimal _pnlPct;

    // ── Static / identity fields ──────────────────────────────────────────────
    public string  Exchange         { get; }
    public string  Symbol           { get; }
    public string  Side             { get; }   // "Long" | "Short"
    public decimal EntryPrice       { get; }
    public decimal Size             { get; }   // absolute quantity
    public int     Leverage         { get; }
    public decimal LiquidationPrice { get; }
    public IExchangeGateway  Gateway        { get; }
    public FuturesPosition?  SourcePosition { get; }

    // ── Live fields ───────────────────────────────────────────────────────────
    public decimal MarkPrice
    {
        get => _markPrice;
        set
        {
            this.RaiseAndSetIfChanged(ref _markPrice, value);
            this.RaisePropertyChanged(nameof(MarkPriceLabel));
        }
    }

    public decimal UnrealizedPnl
    {
        get => _unrealizedPnl;
        set
        {
            this.RaiseAndSetIfChanged(ref _unrealizedPnl, value);
            this.RaisePropertyChanged(nameof(PnlLabel));
            this.RaisePropertyChanged(nameof(PnlBrush));
        }
    }

    public decimal PnlPct
    {
        get => _pnlPct;
        set => this.RaiseAndSetIfChanged(ref _pnlPct, value);
    }

    // ── Computed display labels ───────────────────────────────────────────────
    public string SideBrush       => Side == "Short" ? "#FF6B6B" : "#21E6C1";
    public string ExchangeBrush   => Exchange switch
    {
        "Binance" => "#F0B90B",
        "Bybit"   => "#F7A600",
        "OKX"     => "#3BBFFF",
        "DEX"     => "#9B59B6",
        _         => "#8FA3B8"
    };
    public string EntryPriceLabel => $"{EntryPrice:N2}";
    public string MarkPriceLabel  => _markPrice > 0 ? $"{_markPrice:N2}" : "–";
    public string SizeLabel       => $"{Size:0.####}";
    public string LeverageLabel   => Leverage > 1 ? $"{Leverage}×" : "–";
    public string LiqLabel        => LiquidationPrice > 0 ? $"{LiquidationPrice:N2}" : "–";
    public string NotionalLabel   => $"{Size * EntryPrice:N0} USDT";
    public string PnlLabel        => $"{_unrealizedPnl:+#,##0.00;-#,##0.00;0.00} ({_pnlPct:+0.##;-0.##;0}%)";
    public string PnlBrush        => _unrealizedPnl >= 0 ? "#21E6C1" : "#FF6B6B";

    public UnifiedPositionRowVM(string exchange, FuturesPosition pos, IExchangeGateway gateway)
    {
        Exchange         = exchange;
        Symbol           = pos.Symbol;
        Side             = pos.PositionSide == FuturesPositionSide.Short ? "Short" : "Long";
        EntryPrice       = pos.EntryPrice;
        Size             = Math.Abs(pos.Quantity);
        Leverage         = pos.Leverage;
        LiquidationPrice = pos.LiquidationPrice;
        _markPrice       = pos.MarkPrice;
        _unrealizedPnl   = pos.UnrealizedPnl;
        _pnlPct          = EntryPrice > 0 && Size > 0
            ? Math.Round(pos.UnrealizedPnl / (EntryPrice * Size) * 100m, 2)
            : 0m;
        Gateway          = gateway;
        SourcePosition   = pos;
    }
}

/// <summary>
/// Aggregates all open futures positions from every connected gateway into one
/// sortable table with unified totals and an emergency "Close All" button.
/// </summary>
public sealed class AllPositionsViewModel : ReactiveObject
{
    private readonly IReadOnlyList<(string Name, IExchangeGateway Gateway)> _gateways;

    private string _totalPnlLabel   = "–";
    private string _totalPnlBrush   = "#8FA3B8";
    private string _totalSizeLabel  = "–";
    private string _statusLabel     = "No data yet — press Refresh";
    private string _sortBy          = "PnlPct";
    private bool   _sortDescending  = true;
    private bool   _isRefreshing;
    private bool   _isClosingAll;

    // ── Collections ───────────────────────────────────────────────────────────

    public ObservableCollection<UnifiedPositionRowVM> Rows { get; } = [];

    // ── Display properties ────────────────────────────────────────────────────

    public string TotalPnlLabel  { get => _totalPnlLabel;  private set => this.RaiseAndSetIfChanged(ref _totalPnlLabel, value); }
    public string TotalPnlBrush  { get => _totalPnlBrush;  private set => this.RaiseAndSetIfChanged(ref _totalPnlBrush, value); }
    public string TotalSizeLabel { get => _totalSizeLabel; private set => this.RaiseAndSetIfChanged(ref _totalSizeLabel, value); }
    public string StatusLabel    { get => _statusLabel;    private set => this.RaiseAndSetIfChanged(ref _statusLabel, value); }
    public bool   IsRefreshing   { get => _isRefreshing;   private set => this.RaiseAndSetIfChanged(ref _isRefreshing, value); }
    public bool   IsClosingAll   { get => _isClosingAll;   private set => this.RaiseAndSetIfChanged(ref _isClosingAll, value); }

    public bool HasPositions   => Rows.Count > 0;
    public bool HasNoPositions => Rows.Count == 0;
    public int  PositionCount  => Rows.Count;

    /// <summary>Column to sort by: "PnlPct" | "Size" | "Exchange".</summary>
    public string SortBy
    {
        get => _sortBy;
        set { this.RaiseAndSetIfChanged(ref _sortBy, value); ApplySort(); }
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set { this.RaiseAndSetIfChanged(ref _sortDescending, value); ApplySort(); }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> RefreshCommand        { get; }
    public ReactiveCommand<Unit, Unit> CloseAllCommand       { get; }
    public ReactiveCommand<Unit, Unit> SortByPnlCommand      { get; }
    public ReactiveCommand<Unit, Unit> SortBySizeCommand     { get; }
    public ReactiveCommand<Unit, Unit> SortByExchangeCommand { get; }
    public ReactiveCommand<UnifiedPositionRowVM, Unit> ClosePositionCommand { get; }
    public ReactiveCommand<UnifiedPositionRowVM, Unit> Close25Command       { get; }
    public ReactiveCommand<UnifiedPositionRowVM, Unit> Close50Command       { get; }
    public ReactiveCommand<UnifiedPositionRowVM, Unit> Close75Command       { get; }

    /// <summary>Forwarded errors / status notes for the parent VM to show in the activity log.</summary>
    public event Action<string>? OnLog;

    // ── Two-step CloseAll confirmation ────────────────────────────────────────
    // Первый клик: переводит UI в "armed" состояние с предупреждением (CloseAllConfirmRequested=true).
    // Второй клик в течение 8 секунд: реально закрывает все позиции.
    // Авто-сброс через таймер если второй клик не пришёл — защищает от случайного нажатия.

    private bool _closeAllConfirmRequested;
    public bool CloseAllConfirmRequested
    {
        get => _closeAllConfirmRequested;
        private set => this.RaiseAndSetIfChanged(ref _closeAllConfirmRequested, value);
    }

    private System.Timers.Timer? _closeAllArmTimer;

    // ── Constructor ───────────────────────────────────────────────────────────

    public AllPositionsViewModel(
        IReadOnlyList<(string Name, IExchangeGateway Gateway)> gateways)
    {
        _gateways = gateways;

        RefreshCommand  = ReactiveCommand.CreateFromTask(RefreshAsync,
            outputScheduler: App.UiScheduler);
        CloseAllCommand = ReactiveCommand.CreateFromTask(CloseAllAsync,
            outputScheduler: App.UiScheduler);
        ClosePositionCommand = ReactiveCommand.CreateFromTask<UnifiedPositionRowVM>(
            row => ClosePartialAsync(row, 1.0m), outputScheduler: App.UiScheduler);
        Close25Command = ReactiveCommand.CreateFromTask<UnifiedPositionRowVM>(
            row => ClosePartialAsync(row, 0.25m), outputScheduler: App.UiScheduler);
        Close50Command = ReactiveCommand.CreateFromTask<UnifiedPositionRowVM>(
            row => ClosePartialAsync(row, 0.50m), outputScheduler: App.UiScheduler);
        Close75Command = ReactiveCommand.CreateFromTask<UnifiedPositionRowVM>(
            row => ClosePartialAsync(row, 0.75m), outputScheduler: App.UiScheduler);

        SortByPnlCommand = ReactiveCommand.Create(() =>
        {
            if (SortBy == "PnlPct") SortDescending = !SortDescending;
            else SortBy = "PnlPct";
        }, outputScheduler: App.UiScheduler);

        SortBySizeCommand = ReactiveCommand.Create(() =>
        {
            if (SortBy == "Size") SortDescending = !SortDescending;
            else SortBy = "Size";
        }, outputScheduler: App.UiScheduler);

        SortByExchangeCommand = ReactiveCommand.Create(() =>
        {
            if (SortBy == "Exchange") SortDescending = !SortDescending;
            else SortBy = "Exchange";
        }, outputScheduler: App.UiScheduler);
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        StatusLabel  = "Refreshing positions…";

        var newRows = new List<UnifiedPositionRowVM>();

        foreach (var (name, gw) in _gateways)
        {
            try
            {
                var positions = await gw.GetOpenPositionsAsync();
                foreach (var pos in positions)
                {
                    if (pos.Quantity == 0m) continue;
                    newRows.Add(new UnifiedPositionRowVM(name, pos, gw));
                }
            }
            catch { /* gateway not connected / not implemented → skip silently */ }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Rows.Clear();
            foreach (var row in newRows) Rows.Add(row);
            ApplySortInternal();
            UpdateTotals();

            StatusLabel = Rows.Count > 0
                ? $"{Rows.Count} open position{(Rows.Count == 1 ? "" : "s")} · " +
                  $"{_gateways.Count} exchange{(_gateways.Count == 1 ? "" : "s")} checked"
                : "No open positions found";

            this.RaisePropertyChanged(nameof(HasPositions));
            this.RaisePropertyChanged(nameof(HasNoPositions));
            this.RaisePropertyChanged(nameof(PositionCount));
        });

        IsRefreshing = false;
    }

    // ── Close All (emergency exit) ────────────────────────────────────────────

    private async Task CloseAllAsync()
    {
        if (IsClosingAll || Rows.Count == 0) return;

        // Two-step confirmation: первый клик «armит» команду, второй — реально закрывает.
        if (!CloseAllConfirmRequested)
        {
            CloseAllConfirmRequested = true;
            StatusLabel = $"⚠ Press CLOSE ALL again to close {Rows.Count} position(s).";

            _closeAllArmTimer?.Stop();
            _closeAllArmTimer?.Dispose();
            _closeAllArmTimer = new System.Timers.Timer(8000) { AutoReset = false };
            _closeAllArmTimer.Elapsed += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (CloseAllConfirmRequested)
                {
                    CloseAllConfirmRequested = false;
                    StatusLabel = "Close-all confirmation expired.";
                }
            });
            _closeAllArmTimer.Start();
            return;
        }

        _closeAllArmTimer?.Stop();
        CloseAllConfirmRequested = false;

        IsClosingAll = true;
        StatusLabel  = $"Emergency exit — closing {Rows.Count} position(s)…";

        var tasks = Rows.ToList().Select(row => ClosePartialAsync(row, 1.0m));
        await Task.WhenAll(tasks);

        IsClosingAll = false;
        await RefreshAsync();
    }

    private async Task ClosePartialAsync(UnifiedPositionRowVM row, decimal fraction)
    {
        try
        {
            var pos = row.SourcePosition;
            if (pos is null) return;

            fraction = Math.Clamp(fraction, 0.01m, 1.0m);
            var qtyToClose = Math.Abs(pos.Quantity) * fraction;
            if (qtyToClose <= 0m) return;

            // Opposite side закрывает позицию.
            var closeSide = pos.PositionSide == FuturesPositionSide.Short
                ? OrderSide.Buy
                : OrderSide.Sell;

            var order = new Order
            {
                Symbol       = pos.Symbol,
                Side         = closeSide,
                Type         = OrderType.Market,
                Quantity     = qtyToClose,
                MarketType   = TradingMarketType.FuturesUsdM,
                PositionSide = pos.PositionSide,
                ReduceOnly   = true,
            };

            await row.Gateway.PlaceOrderAsync(order);
            var pctLabel = fraction >= 0.999m ? "100%" : $"{fraction:P0}";
            OnLog?.Invoke($"[{row.Exchange}] {pctLabel} reduce-only close → {pos.Symbol} {closeSide} {qtyToClose:0.####}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[{row.Exchange}] Close failed for {row.Symbol}: {ex.Message}");
        }
    }

    // ── Totals ────────────────────────────────────────────────────────────────

    private void UpdateTotals()
    {
        if (Rows.Count == 0)
        {
            TotalPnlLabel  = "–";
            TotalPnlBrush  = "#8FA3B8";
            TotalSizeLabel = "–";
            return;
        }

        var totalPnl  = Rows.Sum(r => r.UnrealizedPnl);
        var totalNotl = Rows.Sum(r => r.Size * r.EntryPrice);

        TotalPnlLabel  = totalPnl >= 0
            ? $"+{totalPnl:N2} USDT"
            : $"{totalPnl:N2} USDT";
        TotalPnlBrush  = totalPnl >= 0 ? "#21E6C1" : "#FF6B6B";
        TotalSizeLabel = $"{totalNotl:N0} USDT notional";
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    private void ApplySort() =>
        Dispatcher.UIThread.Post(ApplySortInternal, DispatcherPriority.Background);

    private void ApplySortInternal()
    {
        var sorted = _sortBy switch
        {
            "Size" => _sortDescending
                ? Rows.OrderByDescending(r => r.Size * r.EntryPrice).ToList()
                : Rows.OrderBy(r => r.Size * r.EntryPrice).ToList(),
            "Exchange" => _sortDescending
                ? Rows.OrderByDescending(r => r.Exchange).ThenByDescending(r => r.PnlPct).ToList()
                : Rows.OrderBy(r => r.Exchange).ThenByDescending(r => r.PnlPct).ToList(),
            _ /* PnlPct */ => _sortDescending
                ? Rows.OrderByDescending(r => r.PnlPct).ToList()
                : Rows.OrderBy(r => r.PnlPct).ToList()
        };

        for (int i = 0; i < sorted.Count; i++)
        {
            int old = Rows.IndexOf(sorted[i]);
            if (old != i) Rows.Move(old, i);
        }
    }
}
