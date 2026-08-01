using Avalonia.Controls;

namespace CryptoAITerminal.TerminalUI.Controls;

/// <summary>
/// Вид вердикта AI. Данные берёт из <see cref="ViewModels.AiVerdictVM"/> в DataContext:
/// <c>&lt;ctrl:AiVerdictCard DataContext="{Binding InsightVerdict}" /&gt;</c>.
/// </summary>
public partial class AiVerdictCard : UserControl
{
    public AiVerdictCard()
    {
        InitializeComponent();
    }
}
