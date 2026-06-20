namespace CryptoAITerminal.TerminalUI.ViewModels.Dashboard;

/// <summary>Plain placement of one widget on the dashboard grid. Used for math and JSON persistence.</summary>
public sealed record WidgetPlacement(string Key, int Col, int Row, int ColSpan, int RowSpan);
