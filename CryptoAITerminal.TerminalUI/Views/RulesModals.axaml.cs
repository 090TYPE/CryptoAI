using Avalonia.Controls;

namespace CryptoAITerminal.TerminalUI.Views;

/// <summary>Overlay layer for the Rules desk (eval side-panel, delete confirm,
/// toast). Inherits the <c>RulesDeskViewModel</c> DataContext from its host.</summary>
public partial class RulesModals : UserControl
{
    public RulesModals()
    {
        InitializeComponent();
    }
}
