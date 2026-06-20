using System;
using Avalonia.Controls;
using CryptoAITerminal.TerminalUI.ViewModels;
using CryptoAITerminal.TerminalUI.ViewModels.Dashboard;

namespace CryptoAITerminal.TerminalUI.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    private void OnAddWidgetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo
            && combo.SelectedItem is WidgetCatalogEntry entry
            && DataContext is MainWindowViewModel vm)
        {
            vm.DashboardLayoutVM.AddWidgetCommand.Execute(entry.Key).Subscribe();
            combo.SelectedItem = null;
        }
    }
}
