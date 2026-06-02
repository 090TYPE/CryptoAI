using Avalonia.Media;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Gateway.Binance;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.ViewModels;

// ── Comparison table row ──────────────────────────────────────────────────────

public class StrategyComparisonRow
{
    public string Name        { get; init; } = "";
    public string TradeCount  { get; init; } = "--";
    public string WinRate     { get; init; } = "--";
    public string NetReturn   { get; init; } = "--";
    public string MaxDD       { get; init; } = "--";
    public string Sharpe      { get; init; } = "--";
    public string BestTrade   { get; init; } = "--";
    public string ReturnColor { get; init; } = "#8FA3B8";
    public bool   IsSelected  { get; init; }
    /// <summary>Highlights the user-customised row in a distinct colour.</summary>
    public string NameColor   => IsSelected ? "#21E6C1" : "#D7E3EE";
}

// ── Walk-Forward results row ──────────────────────────────────────────────────

/// <summary>
/// Walk-Forward chart overlay band: IS (blue) or OOS (green) segment.
/// Binds to a Rectangle in the chart canvas — X and Width in chart pixel space (ChartW = 700).
/// </summary>
public sealed record WfSegmentViewModel(
    double X,
    double Width,
    string Color,
    string Label,
    bool   IsInSample)
{
    public string ToolTip => IsInSample
        ? $"In-Sample {Label} (training window)"
        : $"Out-of-Sample {Label} (validation window)";
}

public class WfResultRowVM
{
    public WfResult Raw        { get; }
    public bool     IsTopRank  { get; }

    public string Params       { get; }
    public string IsSharpe     { get; }
    public string IsReturn     { get; }
    public string OosSharpe    { get; }
    public string OosReturn    { get; }
    public string OosMaxDD     { get; }
    public string Efficiency   { get; }
    public string WfScore      { get; }
    public string TimesLabel   { get; }

    public string ReturnColor  { get; }
    public string EffColor     { get; }
    public string ScoreColor   { get; }
    /// <summary>Highlights the top-ranked row.</summary>
    public string NameColor    { get; }

    public WfResultRowVM(WfResult r, bool isTop = false)
    {
        Raw       = r;
        IsTopRank = isTop;

        Params     = r.Params.Label;
        IsSharpe   = $"{r.IsSharpe:0.00}";
        IsReturn   = $"{r.IsReturn:+0.##;-0.##;0}%";
        OosSharpe  = $"{r.OosSharpe:0.00}";
        OosReturn  = $"{r.OosReturn:+0.##;-0.##;0}%";
        OosMaxDD   = $"{r.OosMaxDD:0.#}%";
        Efficiency = $"{r.Efficiency:0.00}×";
        WfScore    = $"{r.WfScore:0.000}";
        TimesLabel = r.TimesSelected > 0 ? $"✓{r.TimesSelected}" : "—";

        ReturnColor = r.OosReturn >= 0 ? "#3DDC84" : "#FF5D73";
        EffColor    = r.Efficiency >= 0.70m ? "#3DDC84"
                    : r.Efficiency >= 0.40m ? "#F4B860" : "#FF5D73";
        ScoreColor  = r.WfScore > 0 ? "#21E6C1" : "#8FA3B8";
        NameColor   = isTop ? "#21E6C1" : "#D7E3EE";
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  BacktestViewModel
// ══════════════════════════════════════════════════════════════════════════════

public class BacktestViewModel : ReactiveObject
{
    private readonly BinanceGateway _gateway;

    // ── Data-fetch config ─────────────────────────────────────────────────────
    private string   _symbol            = "BTCUSDT";
    private string   _selectedTimeframe = "1H";
    private int      _candleLimit       = 500;
    private bool     _useCustomDateRange;
    private DateTime _startDate         = DateTime.UtcNow.AddDays(-90);
    private DateTime _endDate           = DateTime.UtcNow;
    private decimal  _commission        = 0.1m;
    private bool     _isRunning;
    private bool     _isOptimizing;

    // ── Strategy selector ─────────────────────────────────────────────────────
    private string _selectedStrategy = "MA Crossover";

    // MA params
    private int _maFastPeriod = 9;
    private int _maSlowPeriod = 21;

    // RSI params
    private int     _rsiPeriod      = 14;
    private decimal _rsiOverbought  = 70m;
    private decimal _rsiOversold    = 30m;

    // Bollinger Bands params
    private int     _bbPeriod       = 20;
    private decimal _bbMultiplier   = 2.0m;

    // Breakout params
    private int _breakoutPeriod = 20;

    // Walk-Forward Optimizer config
    private int _wfFolds       = 3;
    private int _wfInSamplePct = 70;   // 70 % in-sample

    // ── Monte Carlo config ────────────────────────────────────────────────────
    private int     _mcRuns              = 100;
    private decimal _mcSubsamplePercent  = 70m;
    private bool    _isMonteCarloRunning;
    private string  _monteCarloSummary   = "Monte Carlo не запускался.";

    // ── Results ───────────────────────────────────────────────────────────────
    private BacktestResult _mainResult   = BacktestResult.Empty("Выберите инструмент и нажмите Run");
    private string         _statusText   = "Готов";
    private Geometry?      _equityGeometry;
    private Geometry?      _buyHoldGeometry;
    private string         _equityDateRange = "";
    private string         _equityMinLabel  = "";
    private string         _equityMaxLabel  = "";

    // ── Walk-Forward overlay segments ─────────────────────────────────────────
    public ObservableCollection<WfSegmentViewModel> WalkForwardSegments { get; } = new();
    public bool HasWalkForwardSegments => WalkForwardSegments.Count > 0;
    private string         _bestParamsLabel = "";
    private List<decimal>  _buyHoldValues   = [];

    // ═════════════════════ PROPERTIES ═════════════════════

    // ── Data config ───────────────────────────────────────────────────────────

    public string Symbol
    {
        get => _symbol;
        set => this.RaiseAndSetIfChanged(ref _symbol, value.ToUpperInvariant().Trim());
    }

    public string SelectedTimeframe
    {
        get => _selectedTimeframe;
        set => this.RaiseAndSetIfChanged(ref _selectedTimeframe, value);
    }

    public int CandleLimit
    {
        get => _candleLimit;
        set => this.RaiseAndSetIfChanged(ref _candleLimit, Math.Clamp(value, 50, 3000));
    }

    public bool UseCustomDateRange
    {
        get => _useCustomDateRange;
        set => this.RaiseAndSetIfChanged(ref _useCustomDateRange, value);
    }

    public DateTime StartDate
    {
        get => _startDate;
        set => this.RaiseAndSetIfChanged(ref _startDate, value);
    }

    public DateTime EndDate
    {
        get => _endDate;
        set => this.RaiseAndSetIfChanged(ref _endDate, value);
    }

    public decimal Commission
    {
        get => _commission;
        set => this.RaiseAndSetIfChanged(ref _commission, Math.Clamp(value, 0m, 5m));
    }

    // ── Strategy selector ─────────────────────────────────────────────────────

    public IReadOnlyList<string> AvailableStrategies { get; } =
        ["MA Crossover", "RSI", "Bollinger Bands", "Breakout"];

    public string SelectedStrategy
    {
        get => _selectedStrategy;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedStrategy, value);
            this.RaisePropertyChanged(nameof(IsMAStrategy));
            this.RaisePropertyChanged(nameof(IsRsiStrategy));
            this.RaisePropertyChanged(nameof(IsBBStrategy));
            this.RaisePropertyChanged(nameof(IsBreakoutStrategy));
        }
    }

    public bool IsMAStrategy       => _selectedStrategy == "MA Crossover";
    public bool IsRsiStrategy      => _selectedStrategy == "RSI";
    public bool IsBBStrategy       => _selectedStrategy == "Bollinger Bands";
    public bool IsBreakoutStrategy => _selectedStrategy == "Breakout";

    // ── MA params ─────────────────────────────────────────────────────────────

    public int MaFastPeriod
    {
        get => _maFastPeriod;
        set => this.RaiseAndSetIfChanged(ref _maFastPeriod, Math.Max(2, value));
    }

    public int MaSlowPeriod
    {
        get => _maSlowPeriod;
        set => this.RaiseAndSetIfChanged(ref _maSlowPeriod, Math.Max(MaFastPeriod + 1, value));
    }

    // ── RSI params ────────────────────────────────────────────────────────────

    public int RsiPeriod
    {
        get => _rsiPeriod;
        set => this.RaiseAndSetIfChanged(ref _rsiPeriod, Math.Clamp(value, 2, 100));
    }

    public decimal RsiOverbought
    {
        get => _rsiOverbought;
        set => this.RaiseAndSetIfChanged(ref _rsiOverbought, Math.Clamp(value, 51m, 99m));
    }

    public decimal RsiOversold
    {
        get => _rsiOversold;
        set => this.RaiseAndSetIfChanged(ref _rsiOversold, Math.Clamp(value, 1m, 49m));
    }

    // ── BB params ─────────────────────────────────────────────────────────────

    public int BbPeriod
    {
        get => _bbPeriod;
        set => this.RaiseAndSetIfChanged(ref _bbPeriod, Math.Clamp(value, 2, 200));
    }

    public decimal BbMultiplier
    {
        get => _bbMultiplier;
        set => this.RaiseAndSetIfChanged(ref _bbMultiplier, Math.Clamp(value, 0.5m, 5m));
    }

    // ── Breakout params ───────────────────────────────────────────────────────

    public int BreakoutPeriod
    {
        get => _breakoutPeriod;
        set => this.RaiseAndSetIfChanged(ref _breakoutPeriod, Math.Clamp(value, 2, 300));
    }

    // ── Walk-Forward config ───────────────────────────────────────────────────

    public int WfFolds
    {
        get => _wfFolds;
        set => this.RaiseAndSetIfChanged(ref _wfFolds, Math.Clamp(value, 2, 10));
    }

    public int WfInSamplePct
    {
        get => _wfInSamplePct;
        set => this.RaiseAndSetIfChanged(ref _wfInSamplePct, Math.Clamp(value, 40, 90));
    }

    // ── Monte Carlo params ────────────────────────────────────────────────────

    public int MonteCarloRuns
    {
        get => _mcRuns;
        set => this.RaiseAndSetIfChanged(ref _mcRuns, Math.Clamp(value, 10, 5000));
    }

    public decimal MonteCarloSubsamplePercent
    {
        get => _mcSubsamplePercent;
        set => this.RaiseAndSetIfChanged(ref _mcSubsamplePercent, Math.Clamp(value, 10m, 100m));
    }

    public bool IsMonteCarloRunning
    {
        get => _isMonteCarloRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isMonteCarloRunning, value);
            this.RaisePropertyChanged(nameof(CanRun));
            this.RaisePropertyChanged(nameof(CanOptimize));
            this.RaisePropertyChanged(nameof(CanMonteCarlo));
        }
    }

    public bool CanMonteCarlo => !_isRunning && !_isOptimizing && !_isMonteCarloRunning;

    public string MonteCarloSummary
    {
        get => _monteCarloSummary;
        private set => this.RaiseAndSetIfChanged(ref _monteCarloSummary, value);
    }

    // ── Status ────────────────────────────────────────────────────────────────

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isRunning, value);
            this.RaisePropertyChanged(nameof(CanRun));
            this.RaisePropertyChanged(nameof(CanOptimize));
        }
    }

    public bool IsOptimizing
    {
        get => _isOptimizing;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isOptimizing, value);
            this.RaisePropertyChanged(nameof(CanRun));
            this.RaisePropertyChanged(nameof(CanOptimize));
        }
    }

    public bool CanRun      => !_isRunning && !_isOptimizing && !_isMonteCarloRunning;
    public bool CanOptimize => !_isRunning && !_isOptimizing && !_isMonteCarloRunning;

    public string BestParamsLabel
    {
        get => _bestParamsLabel;
        private set => this.RaiseAndSetIfChanged(ref _bestParamsLabel, value);
    }

    // ── Chart geometry ────────────────────────────────────────────────────────

    public Geometry? EquityGeometry
    {
        get => _equityGeometry;
        private set => this.RaiseAndSetIfChanged(ref _equityGeometry, value);
    }

    public Geometry? BuyHoldGeometry
    {
        get => _buyHoldGeometry;
        private set => this.RaiseAndSetIfChanged(ref _buyHoldGeometry, value);
    }

    public string EquityDateRange
    {
        get => _equityDateRange;
        private set => this.RaiseAndSetIfChanged(ref _equityDateRange, value);
    }

    public string EquityMinLabel
    {
        get => _equityMinLabel;
        private set => this.RaiseAndSetIfChanged(ref _equityMinLabel, value);
    }

    public string EquityMaxLabel
    {
        get => _equityMaxLabel;
        private set => this.RaiseAndSetIfChanged(ref _equityMaxLabel, value);
    }

    // ── KPI labels ────────────────────────────────────────────────────────────

    public string StatusLabel    => _isRunning ? "Загрузка…" : (_mainResult.IsReady ? "Готов" : _statusText);
    public string StatusBrush    => _mainResult.IsReady ? "#3DDC84" : (_isRunning ? "#F4B860" : "#8FA3B8");
    public string WindowLabel    => _mainResult.IsReady
        ? $"{_mainResult.Message} | {CurrentStrategyName}"
        : _statusText;

    public string TradeCountLabel  => _mainResult.IsReady ? _mainResult.TradeCount.ToString()             : "--";
    public string WinRateLabel     => _mainResult.IsReady ? $"{_mainResult.WinRatePercent:0.#}%"          : "--";
    public string NetReturnLabel   => _mainResult.IsReady ? $"{_mainResult.NetReturnPercent:+0.##;-0.##;0}%" : "--";
    public string DrawdownLabel    => _mainResult.IsReady ? $"{_mainResult.MaxDrawdownPercent:0.##}%"     : "--";
    public string SharpeLabel      => _mainResult.IsReady ? $"{_mainResult.SharpeRatio:0.##}"             : "--";
    public string BestTradeLabel   => _mainResult.IsReady ? $"{_mainResult.BestTradePercent:+0.##;-0.##;0}%" : "--";
    public string WorstTradeLabel  => _mainResult.IsReady ? $"{_mainResult.WorstTradePercent:+0.##;-0.##;0}%" : "--";
    public string LastSignalLabel  => _mainResult.LastSignal;
    public string BiasLabel        => _mainResult.IsReady
        ? (_mainResult.WinRatePercent >= 50 ? "Momentum позитивный" : "Momentum защитный")
        : "--";
    public string NetReturnColor   => _mainResult.IsReady
        ? (_mainResult.NetReturnPercent >= 0 ? "#3DDC84" : "#FF5D73")
        : "#8FA3B8";

    public string Narrative => _mainResult.IsReady
        ? $"{CurrentStrategyName} на {Symbol} ({SelectedTimeframe}): " +
          $"Win rate {_mainResult.WinRatePercent:0.#}%, " +
          $"доходность {_mainResult.NetReturnPercent:+0.##;-0.##;0}%, " +
          $"макс. просадка {_mainResult.MaxDrawdownPercent:0.##}%, " +
          $"Sharpe {_mainResult.SharpeRatio:0.##}. " +
          $"Комиссия {Commission}% (round-trip {Commission * 2}%)."
        : _statusText;

    private string CurrentStrategyName => _selectedStrategy switch
    {
        "RSI"            => $"RSI({_rsiPeriod}, OB={_rsiOverbought}, OS={_rsiOversold})",
        "Bollinger Bands"=> $"BB({_bbPeriod}, {_bbMultiplier:0.#}σ)",
        "Breakout"       => $"Breakout({_breakoutPeriod})",
        _                => $"SMA({_maFastPeriod}/{_maSlowPeriod})"
    };

    // ── Collections ───────────────────────────────────────────────────────────

    public IReadOnlyList<string> AvailableTimeframes { get; } = ["1M", "5M", "15M", "1H", "4H", "1D"];

    public ObservableCollection<StrategyComparisonRow> ComparisonRows { get; } = [];
    public ObservableCollection<BacktestTradeRow>      TradeRows      { get; } = [];
    public ObservableCollection<WfResultRowVM>         OptimizerRows  { get; } = [];

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit>          RunCommand        { get; }
    public ReactiveCommand<Unit, Unit>          OptimizeCommand   { get; }
    public ReactiveCommand<Unit, Unit>          MonteCarloCommand { get; }
    public ReactiveCommand<Unit, Unit>          ExportCsvCommand  { get; }
    public ReactiveCommand<Unit, Unit>          ExportReportCommand { get; }
    public ReactiveCommand<WfResultRowVM, Unit> ApplyCommand      { get; }
    public ReactiveCommand<Unit, Unit>          ReviewWithAiCommand { get; }

    // ── AI backtest review (#2) ─────────────────────────────────────────────────
    private readonly BacktestReviewAiService _aiReview = new();
    private bool _aiReviewRunning;
    private bool _hasAiReview;
    private string _aiReviewVerdict = "";
    private string _aiReviewSummary = "";
    private string _aiReviewRisks = "";
    private string _aiReviewSource = "";

    public bool AiReviewRunning { get => _aiReviewRunning; private set => this.RaiseAndSetIfChanged(ref _aiReviewRunning, value); }
    public bool HasAiReview { get => _hasAiReview; private set => this.RaiseAndSetIfChanged(ref _hasAiReview, value); }
    public string AiReviewVerdict { get => _aiReviewVerdict; private set => this.RaiseAndSetIfChanged(ref _aiReviewVerdict, value); }
    public string AiReviewSummary { get => _aiReviewSummary; private set => this.RaiseAndSetIfChanged(ref _aiReviewSummary, value); }
    public string AiReviewRisks { get => _aiReviewRisks; private set => this.RaiseAndSetIfChanged(ref _aiReviewRisks, value); }
    public string AiReviewSource { get => _aiReviewSource; private set => this.RaiseAndSetIfChanged(ref _aiReviewSource, value); }
    public string AiVerdictBrush => _aiReviewVerdict switch
    {
        "ROBUST" => "#3DDC84", "PROMISING" => "#21E6C1", "OVERFIT" => "#FF6B6B", "WEAK" => "#F4B860", _ => "#8FA3B8"
    };

    public void ConfigureAi(string apiKey, string model)
    {
        _aiReview.ApiKey = apiKey ?? "";
        if (!string.IsNullOrWhiteSpace(model)) _aiReview.Model = model;
    }

    private async Task ReviewWithAiAsync()
    {
        if (AiReviewRunning) return;
        if (!_mainResult.IsReady) { ExportStatus = "Сначала запустите бэктест."; return; }

        AiReviewRunning = true;
        try
        {
            decimal buyHold = _buyHoldValues.Count >= 2 && _buyHoldValues[0] != 0m
                ? (_buyHoldValues[^1] / _buyHoldValues[0] - 1m) * 100m : 0m;

            var mc = _monteCarloSummary is { Length: > 0 } and not "Monte Carlo не запускался." ? _monteCarloSummary : null;
            var metrics = new CryptoAITerminal.AIEngine.BacktestMetrics(
                CurrentStrategyName,
                _mainResult.NetReturnPercent, buyHold, _mainResult.WinRatePercent,
                _mainResult.TradeCount, _mainResult.MaxDrawdownPercent, _mainResult.SharpeRatio,
                _mainResult.BestTradePercent, _mainResult.WorstTradePercent, mc);

            var review = await _aiReview.ReviewAsync(metrics).ConfigureAwait(true);
            AiReviewVerdict = review.Verdict;
            AiReviewSummary = review.Summary;
            AiReviewRisks = review.Risks.Length > 0 ? "• " + string.Join("\n• ", review.Risks) : "";
            AiReviewSource = review.Source;
            HasAiReview = true;
            this.RaisePropertyChanged(nameof(AiVerdictBrush));
        }
        catch (Exception ex) { ExportStatus = $"AI review failed: {ex.Message}"; }
        finally { AiReviewRunning = false; }
    }

    private string _exportStatus = string.Empty;
    public string ExportStatus
    {
        get => _exportStatus;
        private set => this.RaiseAndSetIfChanged(ref _exportStatus, value);
    }

    // ═════════════════════ CONSTRUCTOR ═════════════════════

    public BacktestViewModel(BinanceGateway gateway)
    {
        _gateway          = gateway;
        RunCommand        = ReactiveCommand.CreateFromTask(RunAsync,        outputScheduler: App.UiScheduler);
        OptimizeCommand   = ReactiveCommand.CreateFromTask(OptimizeAsync,   outputScheduler: App.UiScheduler);
        MonteCarloCommand = ReactiveCommand.CreateFromTask(MonteCarloAsync, outputScheduler: App.UiScheduler);
        ExportCsvCommand  = ReactiveCommand.Create(ExportCsv,               outputScheduler: App.UiScheduler);
        ExportReportCommand = ReactiveCommand.Create(ExportReport,          outputScheduler: App.UiScheduler);
        ApplyCommand      = ReactiveCommand.Create<WfResultRowVM>(ApplyOptResult, outputScheduler: App.UiScheduler);
        ReviewWithAiCommand = ReactiveCommand.CreateFromTask(ReviewWithAiAsync, outputScheduler: App.UiScheduler);
    }

    // ═════════════════════ CSV EXPORT ═════════════════════

    private void ExportCsv()
    {
        if (!_mainResult.IsReady)
        {
            ExportStatus = "Сначала запустите бэктест.";
            return;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "CryptoAITerminal", "Backtests");
            Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var prefix = $"{stamp}_{SanitizeForFileName(Symbol)}_{SanitizeForFileName(SelectedTimeframe)}_{SanitizeForFileName(CurrentStrategyName)}";

            var tradesPath     = Path.Combine(dir, $"{prefix}_trades.csv");
            var equityPath     = Path.Combine(dir, $"{prefix}_equity.csv");
            var comparisonPath = Path.Combine(dir, $"{prefix}_comparison.csv");
            var summaryPath    = Path.Combine(dir, $"{prefix}_summary.csv");

            File.WriteAllText(tradesPath,     BuildTradesCsv(_mainResult),  Encoding.UTF8);
            File.WriteAllText(equityPath,     BuildEquityCsv(_mainResult),  Encoding.UTF8);
            File.WriteAllText(comparisonPath, BuildComparisonCsv(),         Encoding.UTF8);
            File.WriteAllText(summaryPath,    BuildSummaryCsv(),            Encoding.UTF8);

            ExportStatus = $"Экспортировано в {dir} ({Path.GetFileName(prefix)}_*.csv)";
        }
        catch (Exception ex)
        {
            ExportStatus = $"Ошибка экспорта: {ex.Message}";
        }
    }

    // ═════════════════════ HTML REPORT EXPORT ═════════════════════

    private void ExportReport()
    {
        if (!_mainResult.IsReady)
        {
            ExportStatus = "Сначала запустите бэктест.";
            return;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "CryptoAITerminal", "Backtests");
            Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var prefix = $"{stamp}_{SanitizeForFileName(Symbol)}_{SanitizeForFileName(SelectedTimeframe)}_{SanitizeForFileName(CurrentStrategyName)}";
            var path = Path.Combine(dir, $"{prefix}_report.html");

            var model = new BacktestReportExporter.ReportModel
            {
                Symbol             = Symbol,
                Timeframe          = SelectedTimeframe,
                StrategyName       = CurrentStrategyName,
                Commission         = Commission,
                GeneratedAt        = DateTime.Now,
                DateRange          = EquityDateRange,
                TradeCount         = _mainResult.TradeCount,
                WinRatePercent     = _mainResult.WinRatePercent,
                NetReturnPercent   = _mainResult.NetReturnPercent,
                MaxDrawdownPercent = _mainResult.MaxDrawdownPercent,
                SharpeRatio        = _mainResult.SharpeRatio,
                BestTradePercent   = _mainResult.BestTradePercent,
                WorstTradePercent  = _mainResult.WorstTradePercent,
                Equity             = _mainResult.EquityCurve
                    .Select(p => new BacktestReportExporter.EquityPoint(p.Time, p.Value)).ToList(),
                BuyHold            = _buyHoldValues,
                Comparison         = ComparisonRows
                    .Select(r => new BacktestReportExporter.ComparisonRow(
                        r.Name, r.TradeCount, r.WinRate, r.NetReturn, r.MaxDD, r.Sharpe, r.BestTrade, r.IsSelected))
                    .ToList(),
                MonteCarloSummary  = _monteCarloSummary.StartsWith("Monte Carlo не запускался", StringComparison.Ordinal)
                    ? "" : _monteCarloSummary
            };

            File.WriteAllText(path, BacktestReportExporter.BuildHtml(model), Encoding.UTF8);

            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { /* file is written; opening is best-effort */ }

            ExportStatus = $"Отчёт сохранён: {Path.GetFileName(path)} (открыт в браузере · Ctrl+P → PDF)";
        }
        catch (Exception ex)
        {
            ExportStatus = $"Ошибка экспорта отчёта: {ex.Message}";
        }
    }

    private string BuildTradesCsv(BacktestResult res)
    {
        var sb = new StringBuilder();
        sb.AppendLine("OpenTime,CloseTime,EntryPrice,ExitPrice,ReturnPercent,DurationMinutes");
        foreach (var t in res.Trades)
        {
            var duration = (t.CloseTime - t.OpenTime).TotalMinutes;
            sb.AppendLine(string.Join(",", new[]
            {
                t.OpenTime.ToString("o", CultureInfo.InvariantCulture),
                t.CloseTime.ToString("o", CultureInfo.InvariantCulture),
                t.EntryPrice.ToString(CultureInfo.InvariantCulture),
                t.ExitPrice.ToString(CultureInfo.InvariantCulture),
                t.ReturnPercent.ToString(CultureInfo.InvariantCulture),
                duration.ToString("0.##", CultureInfo.InvariantCulture)
            }));
        }
        return sb.ToString();
    }

    private string BuildEquityCsv(BacktestResult res)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Time,Equity");
        foreach (var p in res.EquityCurve)
        {
            sb.AppendLine(
                p.Time.ToString("o", CultureInfo.InvariantCulture) + "," +
                p.Value.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private string BuildComparisonCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Strategy,Trades,WinRate,NetReturn,MaxDD,Sharpe,BestTrade,IsSelected");
        foreach (var row in ComparisonRows)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                CsvEscape(row.Name),
                CsvEscape(row.TradeCount),
                CsvEscape(row.WinRate),
                CsvEscape(row.NetReturn),
                CsvEscape(row.MaxDD),
                CsvEscape(row.Sharpe),
                CsvEscape(row.BestTrade),
                row.IsSelected ? "1" : "0"
            }));
        }
        return sb.ToString();
    }

    private string BuildSummaryCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Field,Value");
        sb.AppendLine($"Symbol,{CsvEscape(Symbol)}");
        sb.AppendLine($"Timeframe,{CsvEscape(SelectedTimeframe)}");
        sb.AppendLine($"Strategy,{CsvEscape(CurrentStrategyName)}");
        sb.AppendLine($"Commission,{Commission.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Trades,{_mainResult.TradeCount}");
        sb.AppendLine($"WinRatePercent,{_mainResult.WinRatePercent.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"NetReturnPercent,{_mainResult.NetReturnPercent.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"MaxDrawdownPercent,{_mainResult.MaxDrawdownPercent.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"SharpeRatio,{_mainResult.SharpeRatio.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"BestTradePercent,{_mainResult.BestTradePercent.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"WorstTradePercent,{_mainResult.WorstTradePercent.ToString(CultureInfo.InvariantCulture)}");
        return sb.ToString();
    }

    private static string SanitizeForFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new StringBuilder(s.Length);
        foreach (var c in s)
            clean.Append(invalid.Contains(c) || c == ' ' || c == ',' ? '_' : c);
        return clean.ToString();
    }

    private static string CsvEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    // ═════════════════════ RUN ═════════════════════

    private async Task RunAsync()
    {
        IsRunning   = true;
        _statusText = UseCustomDateRange
            ? $"Загружаю {Symbol} {SelectedTimeframe} c {StartDate:dd.MM.yy} по {EndDate:dd.MM.yy}…"
            : $"Загружаю {CandleLimit} × {SelectedTimeframe} свечей {Symbol}…";
        RaiseAllLabels();

        IReadOnlyList<DexOhlcvPoint> candles;
        try
        {
            candles = UseCustomDateRange
                ? await _gateway.GetCandlesByDateRangeAsync(Symbol, SelectedTimeframe, StartDate, EndDate)
                : await _gateway.GetCandlesAsync(Symbol, SelectedTimeframe, CandleLimit);

            if (candles.Count == 0)
            {
                _statusText = "Binance вернул 0 свечей — проверьте символ и период.";
                _mainResult = BacktestResult.Empty(_statusText);
                IsRunning   = false;
                RaiseAllLabels();
                return;
            }

            _statusText = $"Прогоняю стратегии на {candles.Count} свечах…";
            RaiseAllLabels();
        }
        catch (Exception ex)
        {
            _statusText = $"Ошибка загрузки: {ex.Message}";
            _mainResult = BacktestResult.Empty(_statusText);
            IsRunning   = false;
            RaiseAllLabels();
            return;
        }

        try
        {
            // ── Main strategy ──────────────────────────────────────────────
            var mainStrategy = BuildSelectedStrategy();
            _mainResult      = BacktestEngine.Run(mainStrategy, candles, Commission);

            BuildEquityGeometry(candles);

            TradeRows.Clear();
            foreach (var trade in _mainResult.Trades)
                TradeRows.Add(new BacktestTradeRow(trade));

            // ── Cross-strategy comparison ──────────────────────────────────
            RunComparisons(candles);

            _statusText = _mainResult.IsReady ? "Бэктест завершён." : _mainResult.Message;
        }
        catch (Exception ex)
        {
            _statusText = $"Ошибка прогона: {ex.Message}";
            _mainResult = BacktestResult.Empty(_statusText);
        }

        IsRunning = false;
        RaiseAllLabels();
    }

    // ═════════════════════ MONTE CARLO ═════════════════════

    private async Task MonteCarloAsync()
    {
        IsMonteCarloRunning = true;
        MonteCarloSummary   = $"Monte Carlo: загрузка {Symbol} {SelectedTimeframe}…";

        IReadOnlyList<DexOhlcvPoint> candles;
        try
        {
            candles = UseCustomDateRange
                ? await _gateway.GetCandlesByDateRangeAsync(Symbol, SelectedTimeframe, StartDate, EndDate)
                : await _gateway.GetCandlesAsync(Symbol, SelectedTimeframe, CandleLimit);

            if (candles.Count < 30)
            {
                MonteCarloSummary   = "Monte Carlo: нужно минимум 30 свечей.";
                IsMonteCarloRunning = false;
                return;
            }
        }
        catch (Exception ex)
        {
            MonteCarloSummary   = $"Monte Carlo: ошибка загрузки — {ex.Message}";
            IsMonteCarloRunning = false;
            return;
        }

        try
        {
            MonteCarloSummary = $"Monte Carlo: прогон {MonteCarloRuns}× на подвыборках {MonteCarloSubsamplePercent:0}%…";
            var fraction = (double)(MonteCarloSubsamplePercent / 100m);
            var commission = Commission;
            var runs       = MonteCarloRuns;
            var stratName  = CurrentStrategyName;

            var result = await Task.Run(() => MonteCarloSimulator.Run(
                BuildSelectedStrategy,
                candles,
                runs,
                fraction,
                commission));

            if (!result.IsReady)
            {
                MonteCarloSummary = $"Monte Carlo: {result.Message}";
            }
            else
            {
                MonteCarloSummary =
                    $"Monte Carlo {stratName} | {result.Message}\n" +
                    $"Средняя доходность: {result.MeanReturnPercent:+0.##;-0.##;0}% | Медиана: {result.MedianReturnPercent:+0.##;-0.##;0}% | σ: {result.StdDevPercent:0.##}%\n" +
                    $"90% доверительный интервал: [{result.Percentile5:+0.##;-0.##;0}% … {result.Percentile95:+0.##;-0.##;0}%]\n" +
                    $"Лучший: {result.BestReturnPercent:+0.##;-0.##;0}% | Худший: {result.WorstReturnPercent:+0.##;-0.##;0}% | Средняя просадка: {result.MeanDrawdownPercent:0.##}%\n" +
                    $"Прибыльных прогонов: {result.ProfitableRunsPercent:0.#}%";
            }
        }
        catch (Exception ex)
        {
            MonteCarloSummary = $"Monte Carlo: ошибка прогона — {ex.Message}";
        }

        IsMonteCarloRunning = false;
    }

    // ═════════════════════ WALK-FORWARD OPTIMISER ═════════════════════

    private async Task OptimizeAsync()
    {
        IsOptimizing = true;
        OptimizerRows.Clear();
        BestParamsLabel = "Оптимизация…";
        RaiseAllLabels();

        IReadOnlyList<DexOhlcvPoint> candles;
        try
        {
            candles = UseCustomDateRange
                ? await _gateway.GetCandlesByDateRangeAsync(Symbol, SelectedTimeframe, StartDate, EndDate)
                : await _gateway.GetCandlesAsync(Symbol, SelectedTimeframe, CandleLimit);

            if (candles.Count < 60)
            {
                BestParamsLabel = "Нужно минимум 60 свечей для оптимизации.";
                IsOptimizing = false;
                RaiseAllLabels();
                return;
            }
        }
        catch (Exception ex)
        {
            BestParamsLabel = $"Ошибка загрузки: {ex.Message}";
            IsOptimizing = false;
            RaiseAllLabels();
            return;
        }

        try
        {
            var stratType = SelectedStrategyType();
            var inSample  = _wfInSamplePct / 100.0;

            // Run on thread pool — can be slow for large spaces
            var results = await Task.Run(() =>
                WalkForwardOptimizer.Optimize(stratType, candles, Commission, _wfFolds, inSample));

            OptimizerRows.Clear();
            for (int i = 0; i < Math.Min(results.Count, 20); i++)
                OptimizerRows.Add(new WfResultRowVM(results[i], i == 0));

            if (results.Count > 0)
            {
                BestParamsLabel = $"Лучшие параметры: {results[0].Params.Label}  " +
                                  $"WF Score {results[0].WfScore:0.000}  " +
                                  $"OOS Return {results[0].OosReturn:+0.##;-0.##;0}%  " +
                                  $"Eff {results[0].Efficiency:0.00}×";
            }
            else
            {
                BestParamsLabel = "Нет результатов — недостаточно данных.";
            }
        }
        catch (Exception ex)
        {
            BestParamsLabel = $"Ошибка оптимизации: {ex.Message}";
        }

        IsOptimizing = false;
        RaiseAllLabels();
    }

    /// <summary>Applies the best params from an optimizer row to the current strategy settings.</summary>
    private void ApplyOptResult(WfResultRowVM row)
    {
        var v = row.Raw.Params.Values;
        switch (SelectedStrategyType())
        {
            case BacktestStrategyType.MACrossover:
                MaFastPeriod = (int)v["Fast"];
                MaSlowPeriod = (int)v["Slow"];
                break;
            case BacktestStrategyType.RSI:
                RsiPeriod     = (int)v["Period"];
                RsiOverbought = v["OB"];
                RsiOversold   = v["OS"];
                break;
            case BacktestStrategyType.BollingerBands:
                BbPeriod     = (int)v["Period"];
                BbMultiplier = v["Mult"];
                break;
            case BacktestStrategyType.Breakout:
                BreakoutPeriod = (int)v["Period"];
                break;
        }
        BestParamsLabel = $"Применено: {row.Raw.Params.Label}";
    }

    // ═════════════════════ STRATEGY COMPARISON ═════════════════════

    private void RunComparisons(IReadOnlyList<DexOhlcvPoint> candles)
    {
        ComparisonRows.Clear();

        var entries = new List<(string Label, IStrategy Strategy, bool IsSelected)>();

        // ── MA Crossover presets ──────────────────────────────────────────────
        var maPresets = new[]
        {
            ("Scalp SMA(5/15)",    new SimpleMaStrategy(5,   15)),
            ("Swing SMA(9/21)",    new SimpleMaStrategy(9,   21)),
            ("Medium SMA(20/50)",  new SimpleMaStrategy(20,  50)),
            ("Trend SMA(50/200)",  new SimpleMaStrategy(50, 200)),
        };
        foreach (var (lbl, s) in maPresets)
        {
            bool isUser = IsMAStrategy
                && s is SimpleMaStrategy ma
                && ma.Name == $"SMA({_maFastPeriod}/{_maSlowPeriod})";
            entries.Add((lbl, s, isUser));
        }
        if (IsMAStrategy)
        {
            var custom = new SimpleMaStrategy(_maFastPeriod, _maSlowPeriod);
            if (!maPresets.Any(x => x.Item2.Name == custom.Name))
                entries.Add(($"Custom SMA({_maFastPeriod}/{_maSlowPeriod})", custom, true));
        }

        // ── RSI presets ───────────────────────────────────────────────────────
        var rsiPresets = new[]
        {
            ("RSI(7, OS=30, OB=70)",  new RsiStrategy(7,  70m, 30m)),
            ("RSI(14, OS=30, OB=70)", new RsiStrategy(14, 70m, 30m)),
            ("RSI(14, OS=35, OB=65)", new RsiStrategy(14, 65m, 35m)),
            ("RSI(21, OS=25, OB=75)", new RsiStrategy(21, 75m, 25m)),
        };
        foreach (var (lbl, s) in rsiPresets)
        {
            bool isUser = IsRsiStrategy
                && s is RsiStrategy r
                && r.Name == $"RSI({_rsiPeriod}, OS={_rsiOversold:0}, OB={_rsiOverbought:0})";
            entries.Add((lbl, s, isUser));
        }
        if (IsRsiStrategy)
        {
            var custom = new RsiStrategy(_rsiPeriod, _rsiOverbought, _rsiOversold);
            if (!rsiPresets.Any(x => x.Item2.Name == custom.Name))
                entries.Add(($"Custom {custom.Name}", custom, true));
        }

        // ── Bollinger Bands presets ───────────────────────────────────────────
        var bbPresets = new[]
        {
            ("BB(10, 1.5σ)",  new BollingerBandsStrategy(10, 1.5m)),
            ("BB(20, 2.0σ)",  new BollingerBandsStrategy(20, 2.0m)),
            ("BB(20, 2.5σ)",  new BollingerBandsStrategy(20, 2.5m)),
            ("BB(30, 2.0σ)",  new BollingerBandsStrategy(30, 2.0m)),
        };
        foreach (var (lbl, s) in bbPresets)
        {
            bool isUser = IsBBStrategy
                && s is BollingerBandsStrategy bb
                && bb.Name == $"BB({_bbPeriod}, {_bbMultiplier:0.#}σ)";
            entries.Add((lbl, s, isUser));
        }
        if (IsBBStrategy)
        {
            var custom = new BollingerBandsStrategy(_bbPeriod, _bbMultiplier);
            if (!bbPresets.Any(x => x.Item2.Name == custom.Name))
                entries.Add(($"Custom {custom.Name}", custom, true));
        }

        // ── Breakout presets ──────────────────────────────────────────────────
        var brPresets = new[]
        {
            ("Breakout(15)",  new BreakoutStrategy(15)),
            ("Breakout(20)",  new BreakoutStrategy(20)),
            ("Breakout(30)",  new BreakoutStrategy(30)),
            ("Breakout(50)",  new BreakoutStrategy(50)),
        };
        foreach (var (lbl, s) in brPresets)
        {
            bool isUser = IsBreakoutStrategy
                && s is BreakoutStrategy br
                && br.Name == $"Breakout({_breakoutPeriod})";
            entries.Add((lbl, s, isUser));
        }
        if (IsBreakoutStrategy)
        {
            var custom = new BreakoutStrategy(_breakoutPeriod);
            if (!brPresets.Any(x => x.Item2.Name == custom.Name))
                entries.Add(($"Custom {custom.Name}", custom, true));
        }

        // ── Run & rank ────────────────────────────────────────────────────────
        var results = new List<(string Label, BacktestResult Res, bool IsUser)>();
        foreach (var (label, strat, isUser) in entries)
        {
            if (candles.Count <= 2) continue;
            var res = BacktestEngine.Run(strat, candles, Commission);
            results.Add((label, res, isUser));
        }

        results.Sort((a, b) => b.Res.NetReturnPercent.CompareTo(a.Res.NetReturnPercent));

        foreach (var (label, res, isUser) in results)
        {
            ComparisonRows.Add(new StrategyComparisonRow
            {
                Name        = label,
                TradeCount  = res.IsReady ? res.TradeCount.ToString()                        : "--",
                WinRate     = res.IsReady ? $"{res.WinRatePercent:0.#}%"                     : "--",
                NetReturn   = res.IsReady ? $"{res.NetReturnPercent:+0.##;-0.##;0}%"         : "--",
                MaxDD       = res.IsReady ? $"{res.MaxDrawdownPercent:0.##}%"                : "--",
                Sharpe      = res.IsReady ? $"{res.SharpeRatio:0.##}"                        : "--",
                BestTrade   = res.IsReady ? $"{res.BestTradePercent:+0.##;-0.##;0}%"         : "--",
                ReturnColor = res.IsReady
                    ? (res.NetReturnPercent >= 0 ? "#3DDC84" : "#FF5D73")
                    : "#8FA3B8",
                IsSelected  = isUser
            });
        }
    }

    // ═════════════════════ EQUITY CHART ═════════════════════

    private const double ChartW = 700d;
    private const double ChartH = 130d;

    private void BuildEquityGeometry(IReadOnlyList<DexOhlcvPoint> candles)
    {
        EquityGeometry  = null;
        BuyHoldGeometry = null;

        var curve = _mainResult.EquityCurve;
        if (curve.Count < 2) return;

        var minVal = (double)curve.Min(p => p.Value);
        var maxVal = (double)curve.Max(p => p.Value);
        var padding = (maxVal - minVal) * 0.05;
        minVal -= padding;
        maxVal += padding;
        var valRange = Math.Max(maxVal - minVal, 0.001);

        EquityGeometry = BuildPath(curve.Select(p => p.Value).ToList(), minVal, valRange);
        EquityMinLabel = $"{minVal + padding:+0.##;-0.##;0}%";
        EquityMaxLabel = $"{maxVal - padding:+0.##;-0.##;0}%";

        if (candles.Count >= 2)
        {
            var first = candles[0].Close;
            var bh    = candles.Select(c => (c.Close - first) / first * 100m + 100m).ToList();
            _buyHoldValues = bh;
            BuyHoldGeometry = BuildPath(bh, minVal, valRange);
        }
        else
        {
            _buyHoldValues = [];
        }

        if (curve.Count >= 2)
            EquityDateRange = $"{curve[0].Time:dd.MM.yy} — {curve[^1].Time:dd.MM.yy}";

        // ── Walk-Forward overlay ──────────────────────────────────────────────
        RebuildWalkForwardSegments(candles.Count);
    }

    private void RebuildWalkForwardSegments(int totalCandles)
    {
        WalkForwardSegments.Clear();
        if (totalCandles < 10) return;

        var foldSegs = WalkForwardOptimizer.GetFoldSegments(totalCandles, _wfFolds, _wfInSamplePct / 100.0);
        foreach (var seg in foldSegs)
        {
            // IS band
            WalkForwardSegments.Add(new WfSegmentViewModel(
                X:      seg.XStart  * ChartW,
                Width:  seg.IsWidth  * ChartW,
                Color:  "#1A2A5E",  // dark blue — in-sample
                Label:  $"IS {seg.FoldNumber}",
                IsInSample: true));

            // OOS band
            WalkForwardSegments.Add(new WfSegmentViewModel(
                X:      seg.XSplit   * ChartW,
                Width:  seg.OosWidth * ChartW,
                Color:  "#0D3020",  // dark green — out-of-sample
                Label:  $"OOS {seg.FoldNumber}",
                IsInSample: false));
        }

        this.RaisePropertyChanged(nameof(HasWalkForwardSegments));
    }

    private static Geometry BuildPath(List<decimal> values, double minVal, double valRange)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < values.Count; i++)
        {
            double x = i / (double)(values.Count - 1) * ChartW;
            double y = Math.Clamp(ChartH - ((double)values[i] - minVal) / valRange * ChartH, 0, ChartH);
            sb.Append(i == 0 ? $"M {x.ToString("F1", CultureInfo.InvariantCulture)},{y.ToString("F1", CultureInfo.InvariantCulture)}"
                             : $" L {x.ToString("F1", CultureInfo.InvariantCulture)},{y.ToString("F1", CultureInfo.InvariantCulture)}");
        }
        return StreamGeometry.Parse(sb.ToString());
    }

    // ═════════════════════ HELPERS ═════════════════════

    private IStrategy BuildSelectedStrategy() => _selectedStrategy switch
    {
        "RSI"             => new RsiStrategy(_rsiPeriod, _rsiOverbought, _rsiOversold),
        "Bollinger Bands" => new BollingerBandsStrategy(_bbPeriod, _bbMultiplier),
        "Breakout"        => new BreakoutStrategy(_breakoutPeriod),
        _                 => new SimpleMaStrategy(_maFastPeriod, _maSlowPeriod)
    };

    private BacktestStrategyType SelectedStrategyType() => _selectedStrategy switch
    {
        "RSI"             => BacktestStrategyType.RSI,
        "Bollinger Bands" => BacktestStrategyType.BollingerBands,
        "Breakout"        => BacktestStrategyType.Breakout,
        _                 => BacktestStrategyType.MACrossover
    };

    private void RaiseAllLabels()
    {
        this.RaisePropertyChanged(nameof(StatusLabel));
        this.RaisePropertyChanged(nameof(StatusBrush));
        this.RaisePropertyChanged(nameof(WindowLabel));
        this.RaisePropertyChanged(nameof(TradeCountLabel));
        this.RaisePropertyChanged(nameof(WinRateLabel));
        this.RaisePropertyChanged(nameof(NetReturnLabel));
        this.RaisePropertyChanged(nameof(DrawdownLabel));
        this.RaisePropertyChanged(nameof(SharpeLabel));
        this.RaisePropertyChanged(nameof(BestTradeLabel));
        this.RaisePropertyChanged(nameof(WorstTradeLabel));
        this.RaisePropertyChanged(nameof(LastSignalLabel));
        this.RaisePropertyChanged(nameof(BiasLabel));
        this.RaisePropertyChanged(nameof(NetReturnColor));
        this.RaisePropertyChanged(nameof(Narrative));
        this.RaisePropertyChanged(nameof(CanRun));
        this.RaisePropertyChanged(nameof(CanOptimize));
        this.RaisePropertyChanged(nameof(BestParamsLabel));
    }
}

// ── Trade history row ─────────────────────────────────────────────────────────

public class BacktestTradeRow
{
    public string OpenTime    { get; }
    public string CloseTime   { get; }
    public string EntryPrice  { get; }
    public string ExitPrice   { get; }
    public string ReturnLabel { get; }
    public string ReturnColor { get; }
    public string Duration    { get; }

    public BacktestTradeRow(BacktestTrade trade)
    {
        OpenTime    = trade.OpenTime.ToString("dd.MM.yy HH:mm");
        CloseTime   = trade.CloseTime.ToString("dd.MM.yy HH:mm");
        EntryPrice  = trade.EntryPrice.ToString("N4");
        ExitPrice   = trade.ExitPrice.ToString("N4");
        ReturnLabel = $"{trade.ReturnPercent:+0.##;-0.##;0}%";
        ReturnColor = trade.ReturnPercent >= 0 ? "#3DDC84" : "#FF5D73";
        var span    = trade.CloseTime - trade.OpenTime;
        Duration    = span.TotalDays  >= 1 ? $"{span.TotalDays:0}д"
                    : span.TotalHours >= 1 ? $"{span.TotalHours:0}ч"
                    :                        $"{span.TotalMinutes:0}м";
    }
}
