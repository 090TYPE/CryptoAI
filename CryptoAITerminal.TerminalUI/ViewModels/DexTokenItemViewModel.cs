using CryptoAITerminal.Core.Models;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public class DexTokenItemViewModel : ReactiveObject
{
    private DexTokenInfo _tokenInfo = new();

    public DexTokenInfo TokenInfo
    {
        get => _tokenInfo;
        private set => this.RaiseAndSetIfChanged(ref _tokenInfo, value);
    }

    public string DisplayName => string.IsNullOrWhiteSpace(TokenInfo.Name)
        ? TokenInfo.Symbol
        : $"{TokenInfo.Name} ({TokenInfo.Symbol})";

    public string PairLabel => $"{TokenInfo.ChainId.ToUpperInvariant()} / {TokenInfo.DexId}";
    public decimal PriceUsd => TokenInfo.PriceUsd;
    public decimal PriceNative => TokenInfo.PriceNative;
    public decimal PriceChange24h => TokenInfo.PriceChange24h;
    public decimal LiquidityUsd => TokenInfo.LiquidityUsd;
    public decimal Volume24h => TokenInfo.Volume24h;
    public string QuoteSymbol => TokenInfo.QuoteSymbol;
    public string TokenAddress => TokenInfo.TokenAddress;
    public string Url => TokenInfo.Url;

    public void Update(DexTokenInfo tokenInfo)
    {
        TokenInfo = tokenInfo;
        this.RaisePropertyChanged(nameof(DisplayName));
        this.RaisePropertyChanged(nameof(PairLabel));
        this.RaisePropertyChanged(nameof(PriceUsd));
        this.RaisePropertyChanged(nameof(PriceNative));
        this.RaisePropertyChanged(nameof(PriceChange24h));
        this.RaisePropertyChanged(nameof(LiquidityUsd));
        this.RaisePropertyChanged(nameof(Volume24h));
        this.RaisePropertyChanged(nameof(QuoteSymbol));
        this.RaisePropertyChanged(nameof(TokenAddress));
        this.RaisePropertyChanged(nameof(Url));
    }
}
