using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

// ── Row displayed in the template list ────────────────────────────────────────

public sealed class OrderTemplateRowVM : ReactiveObject
{
    public OrderTemplate Model { get; }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> EditCommand   { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> DeleteCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ApplyCommand  { get; }

    public string SlotLabel    => $"Shift+{Model.Slot}";
    public string NameLabel    => Model.Name;
    public string SummaryLabel =>
        $"{Model.Side}  {Model.Symbol}  {Model.OrderType}" +
        $"  Lmt {Model.LimitOffsetPct:+0.00;-0.00}%  TP +{Model.TakeProfitPct:0.##}%  SL -{Model.StopLossPct:0.##}%";
    public string SideBrush    => Model.Side == "SELL" ? "#FF6B6B" : "#21E6C1";

    public OrderTemplateRowVM(
        OrderTemplate model,
        Action<OrderTemplateRowVM> onEdit,
        Action<OrderTemplateRowVM> onDelete,
        Action<OrderTemplateRowVM> onApply)
    {
        Model         = model;
        EditCommand   = ReactiveCommand.Create(() => onEdit(this));
        DeleteCommand = ReactiveCommand.Create(() => onDelete(this));
        ApplyCommand  = ReactiveCommand.Create(() => onApply(this));
    }

    public void Refresh()
    {
        this.RaisePropertyChanged(nameof(SlotLabel));
        this.RaisePropertyChanged(nameof(NameLabel));
        this.RaisePropertyChanged(nameof(SummaryLabel));
        this.RaisePropertyChanged(nameof(SideBrush));
    }
}

// ── Master view-model ──────────────────────────────────────────────────────────

public sealed class OrderTemplatesViewModel : ReactiveObject
{
    // ── domain list ─────────────────────────────────────────────────────────
    private readonly List<OrderTemplate> _models = [];

    public ObservableCollection<OrderTemplateRowVM> Rows { get; } = [];

    // ── editor state ─────────────────────────────────────────────────────────
    private bool    _isEditing;
    private int     _editSlot           = 1;
    private string  _editName           = "My Template";
    private string  _editSymbol         = "BTCUSDT";
    private string  _editSide           = "BUY";
    private string  _editOrderType      = "Limit";
    private decimal _editQty            = 0.001m;
    private decimal _editLimitOffsetPct = -0.10m;
    private decimal _editTpPct          = 3.0m;
    private decimal _editSlPct          = 1.5m;
    private OrderTemplate? _editingModel;

    public bool IsEditing
    {
        get => _isEditing;
        set => this.RaiseAndSetIfChanged(ref _isEditing, value);
    }

    public bool HasNoTemplates => Rows.Count == 0;

    // editor fields
    public int     EditSlot           { get => _editSlot;           set => this.RaiseAndSetIfChanged(ref _editSlot, value); }
    public string  EditName           { get => _editName;           set => this.RaiseAndSetIfChanged(ref _editName, value); }
    public string  EditSymbol         { get => _editSymbol;         set => this.RaiseAndSetIfChanged(ref _editSymbol, value); }
    public string  EditSide           { get => _editSide;           set => this.RaiseAndSetIfChanged(ref _editSide, value); }
    public string  EditOrderType      { get => _editOrderType;      set => this.RaiseAndSetIfChanged(ref _editOrderType, value); }
    public decimal EditQty            { get => _editQty;            set => this.RaiseAndSetIfChanged(ref _editQty, value); }
    public decimal EditLimitOffsetPct { get => _editLimitOffsetPct; set => this.RaiseAndSetIfChanged(ref _editLimitOffsetPct, value); }
    public decimal EditTpPct          { get => _editTpPct;          set => this.RaiseAndSetIfChanged(ref _editTpPct, value); }
    public decimal EditSlPct          { get => _editSlPct;          set => this.RaiseAndSetIfChanged(ref _editSlPct, value); }

    // combo options
    public IReadOnlyList<string> SideOptions      { get; } = ["BUY", "SELL"];
    public IReadOnlyList<string> OrderTypeOptions { get; } = ["Limit", "Market"];
    public IReadOnlyList<int>    SlotOptions      { get; } = Enumerable.Range(1, 9).ToList<int>();

    // ── commands ─────────────────────────────────────────────────────────────
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> NewTemplateCommand  { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SaveTemplateCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CancelEditCommand   { get; }

    // ── external event ────────────────────────────────────────────────────────
    /// <summary>Raised when the user clicks "Apply" on a row or triggers a hotkey slot.</summary>
    public event Action<OrderTemplate>? TemplateApplied;

    // ── ctor ──────────────────────────────────────────────────────────────────

    public OrderTemplatesViewModel()
    {
        NewTemplateCommand  = ReactiveCommand.Create(StartNew,              outputScheduler: App.UiScheduler);
        SaveTemplateCommand = ReactiveCommand.Create(Save,                  outputScheduler: App.UiScheduler);
        CancelEditCommand   = ReactiveCommand.Create(() => { IsEditing = false; }, outputScheduler: App.UiScheduler);

        SeedExamples();
    }

    // ── public API ───────────────────────────────────────────────────────────

    /// <summary>Apply the template bound to hotkey slot N (1–9). No-op if slot is empty.</summary>
    public void ApplySlot(int slot)
    {
        var t = _models.FirstOrDefault(m => m.Slot == slot);
        if (t is not null)
            TemplateApplied?.Invoke(t);
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private void SeedExamples()
    {
        AddModel(new OrderTemplate
        {
            Slot = 1, Name = "BTC Long Setup",
            Symbol = "BTCUSDT", Side = "BUY", OrderType = "Limit",
            Quantity = 0.001m, LimitOffsetPct = -0.10m, TakeProfitPct = 3.0m, StopLossPct = 1.5m
        });
        AddModel(new OrderTemplate
        {
            Slot = 2, Name = "ETH Quick Scalp",
            Symbol = "ETHUSDT", Side = "BUY", OrderType = "Limit",
            Quantity = 0.01m, LimitOffsetPct = -0.05m, TakeProfitPct = 1.2m, StopLossPct = 0.6m
        });
        AddModel(new OrderTemplate
        {
            Slot = 3, Name = "BTC Short",
            Symbol = "BTCUSDT", Side = "SELL", OrderType = "Market",
            Quantity = 0.001m, LimitOffsetPct = 0m, TakeProfitPct = 2.0m, StopLossPct = 1.0m
        });
    }

    private void StartNew()
    {
        _editingModel       = null;
        EditSlot            = NextAvailableSlot();
        EditName            = "New Template";
        EditSymbol          = "BTCUSDT";
        EditSide            = "BUY";
        EditOrderType       = "Limit";
        EditQty             = 0.001m;
        EditLimitOffsetPct  = -0.10m;
        EditTpPct           = 3.0m;
        EditSlPct           = 1.5m;
        IsEditing           = true;
    }

    private void StartEdit(OrderTemplateRowVM row)
    {
        _editingModel       = row.Model;
        EditSlot            = row.Model.Slot;
        EditName            = row.Model.Name;
        EditSymbol          = row.Model.Symbol;
        EditSide            = row.Model.Side;
        EditOrderType       = row.Model.OrderType;
        EditQty             = row.Model.Quantity;
        EditLimitOffsetPct  = row.Model.LimitOffsetPct;
        EditTpPct           = row.Model.TakeProfitPct;
        EditSlPct           = row.Model.StopLossPct;
        IsEditing           = true;
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(EditName)) return;

        if (_editingModel is not null)
        {
            // Update existing
            _editingModel.Slot           = EditSlot;
            _editingModel.Name           = EditName;
            _editingModel.Symbol         = EditSymbol;
            _editingModel.Side           = EditSide;
            _editingModel.OrderType      = EditOrderType;
            _editingModel.Quantity       = EditQty;
            _editingModel.LimitOffsetPct = EditLimitOffsetPct;
            _editingModel.TakeProfitPct  = EditTpPct;
            _editingModel.StopLossPct    = EditSlPct;

            var row = Rows.FirstOrDefault(r => r.Model == _editingModel);
            row?.Refresh();
        }
        else
        {
            AddModel(new OrderTemplate
            {
                Slot           = EditSlot,
                Name           = EditName,
                Symbol         = EditSymbol,
                Side           = EditSide,
                OrderType      = EditOrderType,
                Quantity       = EditQty,
                LimitOffsetPct = EditLimitOffsetPct,
                TakeProfitPct  = EditTpPct,
                StopLossPct    = EditSlPct,
            });
        }

        IsEditing = false;
        this.RaisePropertyChanged(nameof(HasNoTemplates));
    }

    private void DeleteRow(OrderTemplateRowVM row)
    {
        _models.Remove(row.Model);
        Rows.Remove(row);
        this.RaisePropertyChanged(nameof(HasNoTemplates));
    }

    private void AddModel(OrderTemplate t)
    {
        _models.Add(t);
        Rows.Add(new OrderTemplateRowVM(t, StartEdit, DeleteRow, r => TemplateApplied?.Invoke(r.Model)));
    }

    private int NextAvailableSlot()
    {
        var used = _models.Select(m => m.Slot).ToHashSet();
        for (int i = 1; i <= 9; i++)
            if (!used.Contains(i)) return i;
        return 1;
    }
}
