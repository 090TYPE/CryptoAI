using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Host view model for the Sentiment dashboard widget. Everything on it belongs to
/// <see cref="SentimentViewModel"/> except the FUNDING AVG cell, which reads the funding monitor —
/// that single foreign cell is why this widget was the last one still compiled against
/// <see cref="MainWindowViewModel"/>.
///
/// Property names match what the widget already bound, so only its <c>x:DataType</c> changes.
/// </summary>
public sealed class SentimentScreenViewModel : ReactiveObject
{
    public SentimentScreenViewModel(SentimentViewModel sentiment, FundingRateViewModel fundingRate)
    {
        SentimentVM   = sentiment;
        FundingRateVM = fundingRate;
    }

    /// <summary>Fear &amp; Greed, long/short split, open interest, Deribit IV and the AI read.</summary>
    public SentimentViewModel SentimentVM { get; }

    /// <summary>Only the 8h funding average cell — the rest of the widget is sentiment.</summary>
    public FundingRateViewModel FundingRateVM { get; }
}
