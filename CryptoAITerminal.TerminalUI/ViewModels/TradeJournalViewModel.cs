using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

// ── Row VM ─────────────────────────────────────────────────────────────────────

public sealed class JournalEntryRowVM : ReactiveObject
{
    private readonly PnlDashboardService _svc;
    private string    _notes;
    private JournalTag _tag;
    private bool      _isEditing;

    public TradeRecord Model { get; }

    public string  DateLabel      => Model.ClosedAtUtc.ToLocalTime().ToString("dd MMM  HH:mm");
    public string  SymbolLabel    => Model.Symbol;
    public string  DirectionLabel => Model.Direction == TradeDirection.Long ? "▲ L" : "▼ S";
    public string  DirectionBrush => Model.Direction == TradeDirection.Long ? "#21E6C1" : "#FF6B6B";
    public string  PnlLabel       => $"{(Model.PnlUsd >= 0 ? "+" : "")}{Model.PnlUsd:0.00} USD";
    public string  PnlBrush       => Model.PnlUsd >= 0 ? "#3DDC84" : "#FF6B6B";
    public string  PnlPctLabel    => $"{(Model.PnlPercent >= 0 ? "+" : "")}{Model.PnlPercent:0.00}%";
    public string  ExchangeLabel  => string.IsNullOrWhiteSpace(Model.Exchange) ? Model.Source.ToString() : Model.Exchange;

    public string Notes
    {
        get => _notes;
        set
        {
            this.RaiseAndSetIfChanged(ref _notes, value);
            Model.Notes = value;
            _svc.Save();
        }
    }

    public JournalTag Tag
    {
        get => _tag;
        set
        {
            this.RaiseAndSetIfChanged(ref _tag, value);
            Model.Tag = value;
            this.RaisePropertyChanged(nameof(TagLabel));
            this.RaisePropertyChanged(nameof(TagBrush));
            _svc.Save();
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        set => this.RaiseAndSetIfChanged(ref _isEditing, value);
    }

    public string TagLabel => _tag == JournalTag.None ? "—" : _tag.ToString();
    public string TagBrush => _tag switch
    {
        JournalTag.BotSignal => "#21E6C1",
        JournalTag.Manual    => "#F4B860",
        JournalTag.Sniper    => "#A855F7",
        JournalTag.Scalp     => "#3B82F6",
        JournalTag.Swing     => "#10B981",
        JournalTag.Breakout  => "#EF4444",
        JournalTag.Breakdown => "#F97316",
        JournalTag.Reversal  => "#EC4899",
        _                    => "#4A6880"
    };

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> EditCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> DoneCommand { get; }

    public JournalEntryRowVM(TradeRecord model, PnlDashboardService svc)
    {
        Model  = model;
        _svc   = svc;
        _notes = model.Notes;
        _tag   = model.Tag;

        EditCommand = ReactiveCommand.Create(() => { IsEditing = true; });
        DoneCommand = ReactiveCommand.Create(() => { IsEditing = false; });
    }
}

// ── Tag statistics row ──────────────────────────────────────────────────────────

public sealed record TagStatRow(
    string  TagLabel,
    string  TagBrush,
    int     Trades,
    int     Wins,
    string  WinRateLabel,
    string  PnlLabel,
    string  PnlBrush);

// ── Master VM ─────────────────────────────────────────────────────────────────

public sealed class TradeJournalViewModel : ReactiveObject
{
    private readonly PnlDashboardService _svc;

    // ── collections ──────────────────────────────────────────────────────────
    public ObservableCollection<JournalEntryRowVM> Rows    { get; } = [];
    public ObservableCollection<TagStatRow>        TagStats { get; } = [];

    // ── filter state ──────────────────────────────────────────────────────────
    private string    _filterTag    = "All";
    private string    _filterPeriod = "All";
    private string    _searchText   = string.Empty;

    public string FilterTag
    {
        get => _filterTag;
        set { this.RaiseAndSetIfChanged(ref _filterTag, value); Refresh(); }
    }

    public string FilterPeriod
    {
        get => _filterPeriod;
        set { this.RaiseAndSetIfChanged(ref _filterPeriod, value); Refresh(); }
    }

    public string SearchText
    {
        get => _searchText;
        set { this.RaiseAndSetIfChanged(ref _searchText, value); Refresh(); }
    }

    // combo options
    public IReadOnlyList<string> TagOptions    { get; } = ["All", "Untagged", "BotSignal", "Manual", "Sniper", "Scalp", "Swing", "Breakout", "Breakdown", "Reversal"];
    public IReadOnlyList<string> PeriodOptions { get; } = ["All", "Today", "Week", "Month", "Year"];
    public IReadOnlyList<JournalTag> AllTags   { get; } = Enum.GetValues<JournalTag>().ToList<JournalTag>();

    // ── summary labels ────────────────────────────────────────────────────────
    private string _summaryLabel = string.Empty;
    public  string SummaryLabel { get => _summaryLabel; private set => this.RaiseAndSetIfChanged(ref _summaryLabel, value); }

    public bool HasNoRows => Rows.Count == 0;

    // ── export status ─────────────────────────────────────────────────────────
    private string _exportStatus = string.Empty;
    public  string ExportStatus { get => _exportStatus; private set => this.RaiseAndSetIfChanged(ref _exportStatus, value); }

    // ── commands ──────────────────────────────────────────────────────────────
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand         { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ExportCsvCommand       { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ExportTaxReportCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ReviewWithAiCommand    { get; }

    // ── AI journal coach (#4) ───────────────────────────────────────────────────
    private readonly TradeJournalCoachAiService _coach = new();
    private bool _coachRunning, _hasCoach;
    private string _coachSummary = "", _coachStrengths = "", _coachLeaks = "", _coachSuggestions = "", _coachSource = "";

    public bool CoachRunning { get => _coachRunning; private set => this.RaiseAndSetIfChanged(ref _coachRunning, value); }
    public bool HasCoachReview { get => _hasCoach; private set => this.RaiseAndSetIfChanged(ref _hasCoach, value); }
    public string CoachSummary { get => _coachSummary; private set => this.RaiseAndSetIfChanged(ref _coachSummary, value); }
    public string CoachStrengths { get => _coachStrengths; private set => this.RaiseAndSetIfChanged(ref _coachStrengths, value); }
    public string CoachLeaks { get => _coachLeaks; private set => this.RaiseAndSetIfChanged(ref _coachLeaks, value); }
    public string CoachSuggestions { get => _coachSuggestions; private set => this.RaiseAndSetIfChanged(ref _coachSuggestions, value); }
    public string CoachSource { get => _coachSource; private set => this.RaiseAndSetIfChanged(ref _coachSource, value); }

    public void ConfigureAi(string apiKey, string model)
    {
        _coach.ApiKey = apiKey ?? "";
        if (!string.IsNullOrWhiteSpace(model)) _coach.Model = model;
    }

    private async System.Threading.Tasks.Task ReviewWithAiAsync()
    {
        if (CoachRunning) return;
        var trades = Rows.Select(r => r.Model).ToList();
        CoachRunning = true;
        try
        {
            var review = await _coach.ReviewAsync(trades).ConfigureAwait(true);
            CoachSummary = review.Summary;
            CoachStrengths = review.Strengths.Length > 0 ? "✔ " + string.Join("\n✔ ", review.Strengths) : "";
            CoachLeaks = review.Leaks.Length > 0 ? "⚠ " + string.Join("\n⚠ ", review.Leaks) : "";
            CoachSuggestions = review.Suggestions.Length > 0 ? "→ " + string.Join("\n→ ", review.Suggestions) : "";
            CoachSource = review.Source;
            HasCoachReview = true;
        }
        catch (System.Exception ex) { ExportStatus = $"AI coach failed: {ex.Message}"; }
        finally { CoachRunning = false; }
    }

    // ── ctor ──────────────────────────────────────────────────────────────────

    public TradeJournalViewModel(PnlDashboardService svc)
    {
        _svc = svc;

        RefreshCommand         = ReactiveCommand.Create(Refresh,          outputScheduler: App.UiScheduler);
        ExportCsvCommand       = ReactiveCommand.Create(ExportCsv,        outputScheduler: App.UiScheduler);
        ExportTaxReportCommand = ReactiveCommand.Create(ExportTaxReport,  outputScheduler: App.UiScheduler);
        ReviewWithAiCommand    = ReactiveCommand.CreateFromTask(ReviewWithAiAsync, outputScheduler: App.UiScheduler);

        Refresh();
    }

    // ── public API ────────────────────────────────────────────────────────────

    /// <summary>Call after a new trade is recorded to update the journal view.</summary>
    public void Refresh()
    {
        Dispatcher.UIThread.Post(DoRefresh, DispatcherPriority.Background);
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private void DoRefresh()
    {
        var all = _svc.GetAll();

        // Period filter
        var now = DateTime.UtcNow;
        IEnumerable<TradeRecord> q = _filterPeriod switch
        {
            "Today" => all.Where(r => r.ClosedAtUtc.Date == now.Date),
            "Week"  => all.Where(r => (now - r.ClosedAtUtc).TotalDays <= 7),
            "Month" => all.Where(r => r.ClosedAtUtc.Year == now.Year && r.ClosedAtUtc.Month == now.Month),
            "Year"  => all.Where(r => r.ClosedAtUtc.Year == now.Year),
            _       => all
        };

        // Tag filter
        if (_filterTag != "All" && _filterTag != "Untagged")
        {
            if (Enum.TryParse<JournalTag>(_filterTag, out var filterEnum))
                q = q.Where(r => r.Tag == filterEnum);
        }
        else if (_filterTag == "Untagged")
        {
            q = q.Where(r => r.Tag == JournalTag.None);
        }

        // Search
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var s = _searchText.Trim();
            q = q.Where(r =>
                r.Symbol.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                r.Notes.Contains(s, StringComparison.OrdinalIgnoreCase)  ||
                r.Exchange.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        var records = q.OrderByDescending(r => r.ClosedAtUtc).ToList();

        Rows.Clear();
        foreach (var r in records)
            Rows.Add(new JournalEntryRowVM(r, _svc));

        this.RaisePropertyChanged(nameof(HasNoRows));
        BuildTagStats(records);
        BuildSummary(records);
    }

    private void BuildTagStats(List<TradeRecord> records)
    {
        TagStats.Clear();
        var rows = _svc.ComputeByTag(records);
        foreach (var row in rows)
        {
            var tagBrush = GetTagBrush(row.Label);
            TagStats.Add(new TagStatRow(
                row.Label,
                tagBrush,
                row.Trades,
                row.Wins,
                $"{row.WinRate:0.0}%",
                $"{(row.PnlUsd >= 0 ? "+" : "")}{row.PnlUsd:0.00}",
                row.PnlUsd >= 0 ? "#3DDC84" : "#FF6B6B"));
        }
    }

    private void BuildSummary(List<TradeRecord> records)
    {
        if (records.Count == 0) { SummaryLabel = "No trades match the current filter."; return; }
        var m = _svc.ComputeMetrics(records);
        SummaryLabel = $"{m.TotalTrades} trades  •  Win {m.WinRate:0.0}%  •  " +
                       $"P&L {(m.TotalPnlUsd >= 0 ? "+" : "")}{m.TotalPnlUsd:0.00} USD  •  " +
                       $"PF {m.ProfitFactor:0.00}";
    }

    private void ExportCsv()
    {
        try
        {
            var records = Rows.Select(r => r.Model).ToList();
            var csv     = _svc.BuildCsv(records);

            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            var path = Path.Combine(downloads,
                $"CryptoAI-Journal-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

            File.WriteAllText(path, csv, System.Text.Encoding.UTF8);
            ExportStatus = $"✓ Saved to {path}";
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export error: {ex.Message}";
        }
    }

    private void ExportTaxReport()
    {
        try
        {
            var records = _svc.GetAll();
            var year    = DateTime.Now.Year;
            var service = new TaxReportService();
            var result  = service.Generate(records, year);
            ExportStatus = $"✓ {result.Summary}  →  {result.TradesPath}";
        }
        catch (Exception ex)
        {
            ExportStatus = $"Tax report error: {ex.Message}";
        }
    }

    private static string GetTagBrush(string tagLabel) => tagLabel switch
    {
        "BotSignal" => "#21E6C1",
        "Manual"    => "#F4B860",
        "Sniper"    => "#A855F7",
        "Scalp"     => "#3B82F6",
        "Swing"     => "#10B981",
        "Breakout"  => "#EF4444",
        "Breakdown" => "#F97316",
        "Reversal"  => "#EC4899",
        _           => "#4A6880"
    };
}
