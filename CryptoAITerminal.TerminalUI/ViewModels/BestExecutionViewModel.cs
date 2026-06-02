using Avalonia.Media;
using Avalonia.Threading;
using CryptoAITerminal.Core.Enums;
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
//  Quote row — one exchange's top-of-book snapshot
// ════════════════════════════════════════════════════════════════════════════

public sealed class ExchangeQuoteRowVM : ReactiveObject
{
    private ExchangeQuote _quote;

    public ExchangeQuoteRowVM(ExchangeQuote q) => _quote = q;

    public string  Exchange     => _quote.Exchange;
    public string  RawLabel     => _quote.HasData ? FmtPrice(_quote.RawPrice) : "—";
    public string  FeeLabel     => _quote.HasData ? $"{_quote.FeeRatePct:F2}%" : "—";
    public string  EffLabel     => _quote.HasData ? FmtPrice(_quote.EffectivePrice) : "—";
    public string  LiqLabel     => _quote.HasData ? $"{_quote.AvailableQty:N4}" : "—";
    /// <summary>Top-of-book liquidity in USD — used by the AI slice planner (#12).</summary>
    public decimal LiquidityUsd => _quote.HasData ? _quote.AvailableQty * _quote.EffectivePrice : 0m;

    public bool    IsBest       { get; set; }

    public IBrush  RowBrush     => IsBest
        ? new SolidColorBrush(Color.Parse("#1A2E28"))
        : Brushes.Transparent;
    public IBrush  PriceBrush   => IsBest
        ? new SolidColorBrush(Color.Parse("#21E6C1"))
        : new SolidColorBrush(Color.Parse("#8FA3B8"));
    public string  BestBadge    => IsBest ? "★ BEST" : "";

    public void RefreshFrom(ExchangeQuote q, bool isBest)
    {
        _quote  = q;
        IsBest  = isBest;
        this.RaisePropertyChanged(string.Empty);
    }

    private static string FmtPrice(decimal p) =>
        p <= 0    ? "—"       :
        p >= 1000 ? $"${p:N2}" :
        p >= 1    ? $"${p:N4}" :
                    $"${p:N6}";
}

// ════════════════════════════════════════════════════════════════════════════
//  Leg row — one exchange slice of a split order
// ════════════════════════════════════════════════════════════════════════════

public sealed class RoutingLegRowVM
{
    private readonly RoutingLeg _leg;
    private readonly int        _rank;   // 1 = largest slice

    public RoutingLegRowVM(RoutingLeg leg, int rank) { _leg = leg; _rank = rank; }

    public string Exchange    => _leg.Exchange;
    public string QtyLabel    => $"{_leg.Quantity:N6}";
    public string PriceLabel  => $"${_leg.AvgFillPrice:N2}";
    public string FeeLabel    => $"{_leg.FeeRatePct:F2}%";
    public string ValueLabel  => $"${_leg.ValueUsd:N2}";
    public string ShareLabel
    {
        get
        {
            // Computed lazily — filled by VM
            return _shareLabel;
        }
    }
    internal string _shareLabel = "";

    public IBrush ExchangeBrush => _rank == 1
        ? new SolidColorBrush(Color.Parse("#21E6C1"))
        : _rank == 2
            ? new SolidColorBrush(Color.Parse("#F4B860"))
            : new SolidColorBrush(Color.Parse("#8FA3B8"));
}

// ════════════════════════════════════════════════════════════════════════════
//  Main ViewModel
// ════════════════════════════════════════════════════════════════════════════

public sealed class BestExecutionViewModel : ReactiveObject, IDisposable
{
    private readonly BestExecutionRouterService _svc;

    // ── Inputs ────────────────────────────────────────────────────────────────

    private string  _symbol       = "BTCUSDT";
    private decimal _notionalUsd  = 1_000m;
    private bool    _isBuySide    = true;

    public string Symbol
    {
        get => _symbol;
        set { this.RaiseAndSetIfChanged(ref _symbol, value?.ToUpperInvariant().Trim() ?? ""); }
    }

    public decimal NotionalUsd
    {
        get => _notionalUsd;
        set { this.RaiseAndSetIfChanged(ref _notionalUsd, value); }
    }

    public bool IsBuySide
    {
        get => _isBuySide;
        set { this.RaiseAndSetIfChanged(ref _isBuySide, value); OnSideChanged(); }
    }

    public string SideLabel => _isBuySide ? "BUY" : "SELL";

    // ── State ─────────────────────────────────────────────────────────────────

    private bool   _isBusy;
    private bool   _hasPlan;
    private string _statusText = "Введите параметры и нажмите «Рассчитать маршрут»";

    public bool   IsBusy      { get => _isBusy;  private set { this.RaiseAndSetIfChanged(ref _isBusy,  value); } }
    public bool   HasPlan     { get => _hasPlan; private set { this.RaiseAndSetIfChanged(ref _hasPlan, value); } }
    public string StatusText  { get => _statusText; private set { this.RaiseAndSetIfChanged(ref _statusText, value); } }

    // ── Result ────────────────────────────────────────────────────────────────

    private RoutingPlan? _plan;

    private string  _wavgLabel     = "—";
    private string  _savingsLabel  = "—";
    private string  _totalLabel    = "—";
    private bool    _isSplit;

    public string WavgLabel    { get => _wavgLabel;    private set { this.RaiseAndSetIfChanged(ref _wavgLabel, value); } }
    public string SavingsLabel { get => _savingsLabel; private set { this.RaiseAndSetIfChanged(ref _savingsLabel, value); } }
    public string TotalLabel   { get => _totalLabel;   private set { this.RaiseAndSetIfChanged(ref _totalLabel, value); } }
    public bool   IsSplit      { get => _isSplit;      private set { this.RaiseAndSetIfChanged(ref _isSplit, value); } }

    public IBrush SavingsBrush => _plan is null || _plan.SavingsPct <= 0
        ? Gray
        : _plan.SavingsPct >= 0.1m ? Teal : Amber;

    // ── Collections ───────────────────────────────────────────────────────────

    public ObservableCollection<ExchangeQuoteRowVM> Quotes { get; } = [];
    public ObservableCollection<RoutingLegRowVM>    Legs   { get; } = [];

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ComputeCommand { get; }
    public ReactiveCommand<Unit, Unit> ExecuteCommand { get; }
    public ReactiveCommand<Unit, Unit> PlanSlicesCommand { get; }

    // ── AI slice planner (#12) ──────────────────────────────────────────────────
    private readonly ExecutionScheduleAiService _aiSched = new();
    private bool _planRunning, _hasSlicePlan;
    private string _planRationale = "", _planSource = "", _selectedUrgency = "Medium";
    public bool PlanRunning { get => _planRunning; private set => this.RaiseAndSetIfChanged(ref _planRunning, value); }
    public bool HasSlicePlan { get => _hasSlicePlan; private set => this.RaiseAndSetIfChanged(ref _hasSlicePlan, value); }
    public string PlanRationale { get => _planRationale; private set => this.RaiseAndSetIfChanged(ref _planRationale, value); }
    public string PlanSource { get => _planSource; private set => this.RaiseAndSetIfChanged(ref _planSource, value); }
    public IReadOnlyList<string> UrgencyOptions { get; } = ["Low", "Medium", "High"];
    public string SelectedUrgency { get => _selectedUrgency; set => this.RaiseAndSetIfChanged(ref _selectedUrgency, value); }

    public void ConfigureAi(string apiKey, string model)
    {
        _aiSched.ApiKey = apiKey ?? "";
        if (!string.IsNullOrWhiteSpace(model)) _aiSched.Model = model;
    }

    private async Task PlanSlicesAsync()
    {
        if (PlanRunning) return;
        var depth = Quotes.Sum(q => q.LiquidityUsd);
        var ctx = new CryptoAITerminal.AIEngine.OrderExecutionContext(
            Symbol, IsBuySide ? "Buy" : "Sell", NotionalUsd, 0m, depth, SelectedUrgency);

        PlanRunning = true;
        try
        {
            var plan = await _aiSched.PlanAsync(ctx).ConfigureAwait(true);
            PlanRationale = plan.Rationale;
            PlanSource = plan.Source;
            HasSlicePlan = true;
        }
        catch (Exception ex) { StatusText = $"AI plan failed: {ex.Message}"; }
        finally { PlanRunning = false; }
    }

    // ── Toast ─────────────────────────────────────────────────────────────────

    public event Action<string>? ToastRequested;

    // ── Constructor ───────────────────────────────────────────────────────────

    public BestExecutionViewModel(BestExecutionRouterService svc)
    {
        _svc = svc;
        PlanSlicesCommand = ReactiveCommand.CreateFromTask(PlanSlicesAsync, outputScheduler: App.UiScheduler);

        var canExecute = this.WhenAnyValue(x => x.HasPlan, x => x.IsBusy,
            (hp, busy) => hp && !busy);

        ComputeCommand = ReactiveCommand.CreateFromTask(
            ComputeAsync,
            outputScheduler: App.UiScheduler);

        ExecuteCommand = ReactiveCommand.CreateFromTask(
            ExecuteAsync,
            canExecute,
            outputScheduler: App.UiScheduler);
    }

    // ── Core logic ────────────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;

    private async Task ComputeAsync()
    {
        if (string.IsNullOrWhiteSpace(_symbol)) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy  = true;
        HasPlan = false;
        StatusText = "Получение стаканов с бирж…";

        try
        {
            var side = _isBuySide ? OrderSide.Buy : OrderSide.Sell;
            _plan = await _svc.ComputeRoutingAsync(_symbol, side, _notionalUsd, _cts.Token);

            await Dispatcher.UIThread.InvokeAsync(() => ApplyPlan(_plan));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyPlan(RoutingPlan plan)
    {
        // Quotes table
        Quotes.Clear();
        int i = 0;
        foreach (var q in plan.Quotes)
        {
            bool isBest = plan.Legs.Count > 0
                && plan.Legs[^1].Exchange == q.Exchange  // last leg = worst, first = best
                || (plan.Legs.Count > 0 && plan.Legs[0].Exchange == q.Exchange);

            // Actually: best = lowest effective price for buy, highest for sell
            bool best;
            if (_isBuySide)
                best = q.HasData && q.EffectivePrice == plan.Quotes
                    .Where(x => x.HasData).Select(x => x.EffectivePrice).DefaultIfEmpty().Min();
            else
                best = q.HasData && q.EffectivePrice == plan.Quotes
                    .Where(x => x.HasData).Select(x => x.EffectivePrice).DefaultIfEmpty().Max();

            if (i < Quotes.Count)
                Quotes[i].RefreshFrom(q, best);
            else
                Quotes.Add(new ExchangeQuoteRowVM(q) { IsBest = best });
            i++;
        }
        while (Quotes.Count > i) Quotes.RemoveAt(Quotes.Count - 1);

        // Legs table
        Legs.Clear();
        var totalValue = plan.Legs.Count > 0 ? plan.TotalNotionalUsd : 0m;
        for (int j = 0; j < plan.Legs.Count; j++)
        {
            var leg  = plan.Legs[j];
            var row  = new RoutingLegRowVM(leg, j + 1);
            row._shareLabel = totalValue > 0
                ? $"{leg.ValueUsd / totalValue * 100m:F1}%"
                : "—";
            Legs.Add(row);
        }

        // Summary
        IsSplit      = plan.IsSplit;
        WavgLabel    = plan.WeightedAvgPrice > 0 ? $"${plan.WeightedAvgPrice:N4}" : "—";
        SavingsLabel = plan.SavingsPct >= 0
            ? $"+${plan.SavingsUsd:N4}  (+{plan.SavingsPct:F4}%)"
            : $"-${Math.Abs(plan.SavingsUsd):N4}  ({plan.SavingsPct:F4}%)";
        TotalLabel   = $"${plan.TotalNotionalUsd:N2}";

        HasPlan    = plan.Legs.Count > 0;
        StatusText = HasPlan
            ? (plan.IsSplit
                ? $"Сплит на {plan.Legs.Count} бирж — экономия {plan.SavingsPct:+0.0000;-0.0000}%"
                : $"Лучшая биржа: {plan.Legs[0].Exchange}")
            : "Нет доступных данных — проверьте подключение";

        this.RaisePropertyChanged(nameof(SavingsBrush));
    }

    private async Task ExecuteAsync()
    {
        if (_plan is null) return;
        IsBusy = true;
        StatusText = "Исполнение…";
        try
        {
            var (ok, err) = await _svc.ExecutePlanAsync(_plan);
            var side      = _plan.Side == OrderSide.Buy ? "покупка" : "продажа";
            if (ok)
            {
                ToastRequested?.Invoke(
                    $"✓ {_plan.Symbol} {side} ${_plan.TotalNotionalUsd:N0} — исполнено " +
                    $"({_plan.Legs.Count} {(_plan.IsSplit ? "биржи" : "биржа")})");
                StatusText = "Ордер исполнен";
            }
            else
            {
                ToastRequested?.Invoke($"✗ Ошибка исполнения: {err}");
                StatusText = $"Ошибка: {err}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnSideChanged() => this.RaisePropertyChanged(nameof(SideLabel));

    // ── Brush constants ───────────────────────────────────────────────────────

    private static readonly IBrush Teal  = new SolidColorBrush(Color.Parse("#21E6C1"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F4B860"));
    private static readonly IBrush Gray  = new SolidColorBrush(Color.Parse("#3A4F63"));

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _cts?.Cancel();
}
