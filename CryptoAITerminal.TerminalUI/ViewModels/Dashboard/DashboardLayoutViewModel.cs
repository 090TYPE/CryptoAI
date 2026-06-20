using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using ReactiveUI;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.TerminalUI.ViewModels.Dashboard;

public sealed class DashboardLayoutViewModel : ReactiveObject
{
    private static readonly string LayoutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CryptoAITerminal", "dashboard-layout.json");

    public ObservableCollection<DashboardWidget> Widgets { get; } = new();

    private bool _isEditMode;
    public bool IsEditMode
    {
        get => _isEditMode;
        set => this.RaiseAndSetIfChanged(ref _isEditMode, value);
    }

    public IReadOnlyList<WidgetCatalogEntry> Catalog => WidgetCatalog.All;

    /// <summary>Catalog entries not currently placed — what "+ Add widget" offers.</summary>
    public IEnumerable<WidgetCatalogEntry> AddableCatalog =>
        WidgetCatalog.All.Where(e => Widgets.All(w => w.Key != e.Key));

    public ReactiveCommand<Unit, Unit> ToggleEditCommand { get; }
    public ReactiveCommand<string, Unit> AddWidgetCommand { get; }
    public ReactiveCommand<DashboardWidget, Unit> RemoveWidgetCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetLayoutCommand { get; }
    public ReactiveCommand<DashboardWidget, Unit> WidenCommand { get; }
    public ReactiveCommand<DashboardWidget, Unit> NarrowCommand { get; }
    public ReactiveCommand<DashboardWidget, Unit> TallerCommand { get; }
    public ReactiveCommand<DashboardWidget, Unit> ShorterCommand { get; }

    public DashboardLayoutViewModel()
    {
        ToggleEditCommand   = ReactiveCommand.Create(() => { IsEditMode = !IsEditMode; });
        AddWidgetCommand    = ReactiveCommand.Create<string>(AddWidget);
        RemoveWidgetCommand = ReactiveCommand.Create<DashboardWidget>(RemoveWidget);
        ResetLayoutCommand  = ReactiveCommand.Create(ResetLayout);
        WidenCommand   = ReactiveCommand.Create<DashboardWidget>(w => StepResize(w, +1, 0));
        NarrowCommand  = ReactiveCommand.Create<DashboardWidget>(w => StepResize(w, -1, 0));
        TallerCommand  = ReactiveCommand.Create<DashboardWidget>(w => StepResize(w, 0, +1));
        ShorterCommand = ReactiveCommand.Create<DashboardWidget>(w => StepResize(w, 0, -1));

        Load();
    }

    /// <summary>Grow/shrink a widget by whole cells (used by the header +/- buttons), clamped + persisted.</summary>
    private void StepResize(DashboardWidget widget, int dCol, int dRow)
    {
        if (widget is null) return;
        CommitResize(widget, widget.ColSpan + dCol, widget.RowSpan + dRow);
    }

    private void AddWidget(string key)
    {
        if (Widgets.Any(w => w.Key == key)) return;
        var entry = WidgetCatalog.Find(key);
        if (entry is null) return;

        var placement = DashboardLayoutMath.PlaceNew(
            key, entry.DefaultColSpan, entry.DefaultRowSpan, CurrentPlacements());
        Widgets.Add(new DashboardWidget(placement, entry.Title));
        this.RaisePropertyChanged(nameof(AddableCatalog));
        Save();
    }

    private void RemoveWidget(DashboardWidget widget)
    {
        if (widget is null) return;
        Widgets.Remove(widget);
        this.RaisePropertyChanged(nameof(AddableCatalog));
        Save();
    }

    private void ResetLayout()
    {
        Widgets.Clear();
        foreach (var p in DashboardLayoutMath.DefaultLayout())
            Widgets.Add(new DashboardWidget(p, WidgetCatalog.Find(p.Key)?.Title ?? p.Key));
        this.RaisePropertyChanged(nameof(AddableCatalog));
        Save();
    }

    /// <summary>Re-snap a widget after a drag and persist. Called by WidgetHost on drop.</summary>
    public void CommitDrag(DashboardWidget widget, int newCol, int newRow)
    {
        var others = Widgets.Where(w => !ReferenceEquals(w, widget)).Select(w => w.ToPlacement()).ToList();
        var dropped = DashboardLayoutMath.ResolveDrop(
            widget.ToPlacement() with { Col = newCol, Row = newRow }, others);
        widget.Col = dropped.Col;
        widget.Row = dropped.Row;
        Save();
    }

    /// <summary>Re-clamp a widget after a resize and persist. Called by WidgetHost on resize-end.</summary>
    public void CommitResize(DashboardWidget widget, int newColSpan, int newRowSpan)
    {
        var (cs, rs) = DashboardLayoutMath.ClampSize(widget.Col, newColSpan, newRowSpan);
        widget.ColSpan = cs;
        widget.RowSpan = rs;
        Save();
    }

    private List<WidgetPlacement> CurrentPlacements() => Widgets.Select(w => w.ToPlacement()).ToList();

    private void Load()
    {
        IReadOnlyList<WidgetPlacement> placements;
        try
        {
            placements = File.Exists(LayoutPath)
                ? (AtomicJsonFile.Read<List<WidgetPlacement>>(LayoutPath) ?? new())
                : DashboardLayoutMath.DefaultLayout();
        }
        catch { placements = DashboardLayoutMath.DefaultLayout(); }

        var known = WidgetCatalog.All.Select(e => e.Key).ToHashSet();
        var clean = DashboardLayoutMath.Sanitize(placements, known);
        if (clean.Count == 0) clean = DashboardLayoutMath.DefaultLayout();

        Widgets.Clear();
        foreach (var p in clean)
            Widgets.Add(new DashboardWidget(p, WidgetCatalog.Find(p.Key)?.Title ?? p.Key));
    }

    public void Save()
    {
        try { AtomicJsonFile.Write(LayoutPath, CurrentPlacements()); }
        catch { /* best-effort */ }
    }
}
