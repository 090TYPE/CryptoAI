using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using CryptoAITerminal.TerminalUI.Controls;
using CryptoAITerminal.TerminalUI.ViewModels;
using CryptoAITerminal.TerminalUI.ViewModels.Dashboard;
using ReactiveUI;
using System.Reactive;

namespace CryptoAITerminal.TerminalUI.Views.Dashboard;

public partial class WidgetHost : UserControl
{
    public static readonly StyledProperty<bool> IsEditModeProperty =
        AvaloniaProperty.Register<WidgetHost, bool>(nameof(IsEditMode));

    public bool IsEditMode
    {
        get => GetValue(IsEditModeProperty);
        set => SetValue(IsEditModeProperty, value);
    }

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    private bool _dragging;
    private Point _dragStart;
    private int _startCol, _startRow;

    public WidgetHost()
    {
        AvaloniaXamlLoader.Load(this);
        RemoveCommand = ReactiveCommand.Create(RemoveSelf);

        var handle = this.FindControl<TextBlock>("DragHandle")!;
        handle.PointerPressed += OnHandlePressed;
        handle.PointerMoved += OnHandleMoved;
        handle.PointerReleased += OnHandleReleased;

        var thumb = this.FindControl<Thumb>("ResizeThumb")!;
        thumb.DragDelta += OnResizeDelta;
        thumb.DragCompleted += OnResizeCompleted;
    }

    private DashboardWidget? Widget => DataContext as DashboardWidget;
    private WidgetGridPanel? Grid => this.FindAncestorOfType<WidgetGridPanel>();
    private DashboardLayoutViewModel? Layout =>
        (this.FindAncestorOfType<Window>()?.DataContext as MainWindowViewModel)?.DashboardLayoutVM;

    private void RemoveSelf()
    {
        if (Widget is { } w) Layout?.RemoveWidgetCommand.Execute(w).Subscribe();
    }

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Widget is null) return;
        _dragging = true;
        _dragStart = e.GetPosition(Grid);
        _startCol = Widget.Col;
        _startRow = Widget.Row;
        e.Pointer.Capture((IInputElement?)sender);
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || Widget is null || Grid is null) return;
        var pos = e.GetPosition(Grid);
        double cellW = Grid.Bounds.Width / WidgetGridPanel.Columns;
        int dCol = (int)Math.Round((pos.X - _dragStart.X) / cellW);
        int dRow = (int)Math.Round((pos.Y - _dragStart.Y) / WidgetGridPanel.CellHeight);
        Widget.Col = Math.Clamp(_startCol + dCol, 0, WidgetGridPanel.Columns - Widget.ColSpan);
        Widget.Row = Math.Max(0, _startRow + dRow);
    }

    private void OnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging || Widget is null) { _dragging = false; return; }
        _dragging = false;
        e.Pointer.Capture(null);
        Layout?.CommitDrag(Widget, Widget.Col, Widget.Row);
    }

    private void OnResizeDelta(object? sender, VectorEventArgs e)
    {
        if (Widget is null || Grid is null) return;
        double cellW = Grid.Bounds.Width / WidgetGridPanel.Columns;
        int dCol = (int)Math.Round(e.Vector.X / cellW);
        int dRow = (int)Math.Round(e.Vector.Y / WidgetGridPanel.CellHeight);
        if (dCol != 0) Widget.ColSpan = Math.Max(DashboardLayoutMath.MinColSpan, Widget.ColSpan + dCol);
        if (dRow != 0) Widget.RowSpan = Math.Max(DashboardLayoutMath.MinRowSpan, Widget.RowSpan + dRow);
    }

    private void OnResizeCompleted(object? sender, VectorEventArgs e)
    {
        if (Widget is not null) Layout?.CommitResize(Widget, Widget.ColSpan, Widget.RowSpan);
    }
}
