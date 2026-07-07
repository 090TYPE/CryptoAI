namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// One live line in the "Ask AI" activity trace — a glyph (per event kind), its colour,
/// and the human text. Populated as the agent streams thinking / tool-call / result steps.
/// </summary>
public sealed class AgentStepViewModel
{
    public AgentStepViewModel(string glyph, string glyphBrush, string text)
    {
        Glyph = glyph;
        GlyphBrush = glyphBrush;
        Text = text;
    }

    public string Glyph { get; }
    public string GlyphBrush { get; }
    public string Text { get; }
}
