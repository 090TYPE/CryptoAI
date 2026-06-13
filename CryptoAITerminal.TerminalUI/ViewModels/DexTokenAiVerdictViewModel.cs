using CryptoAITerminal.Core.Models;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Card-state wrapper around a <see cref="TokenAiVerdict"/> for the DEX trading
/// page. Holds busy / deep-scan flags and exposes binding-friendly pass-throughs.
/// </summary>
public sealed class DexTokenAiVerdictViewModel : ReactiveObject
{
    private TokenAiVerdict _verdict = TokenAiVerdict.Pending();
    private bool _hasVerdict;
    private bool _isBusy;
    private bool _deepScanBusy;
    private string? _deepScanNote;

    public bool HasVerdict
    {
        get => _hasVerdict;
        private set => this.RaiseAndSetIfChanged(ref _hasVerdict, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public bool DeepScanBusy
    {
        get => _deepScanBusy;
        set => this.RaiseAndSetIfChanged(ref _deepScanBusy, value);
    }

    public string? DeepScanNote
    {
        get => _deepScanNote;
        set
        {
            this.RaiseAndSetIfChanged(ref _deepScanNote, value);
            this.RaisePropertyChanged(nameof(HasDeepScanNote));
        }
    }

    public bool HasDeepScanNote => !string.IsNullOrWhiteSpace(_deepScanNote);

    public string Badge => _verdict.Verdict;
    public string AccentHex => _verdict.AccentHex;
    public string ScoreLabel => $"{_verdict.RiskScore}/100";
    public string Reason => _verdict.Reason;
    public string RedFlagsText => _verdict.RedFlagsText;
    public string SourceLabel => _verdict.Source;

    public void ApplyVerdict(TokenAiVerdict verdict)
    {
        _verdict = verdict ?? TokenAiVerdict.Pending();
        HasVerdict = true;
        RaisePassThroughs();
    }

    public void Reset()
    {
        _verdict = TokenAiVerdict.Pending();
        HasVerdict = false;
        DeepScanNote = null;
        RaisePassThroughs();
    }

    private void RaisePassThroughs()
    {
        this.RaisePropertyChanged(nameof(Badge));
        this.RaisePropertyChanged(nameof(AccentHex));
        this.RaisePropertyChanged(nameof(ScoreLabel));
        this.RaisePropertyChanged(nameof(Reason));
        this.RaisePropertyChanged(nameof(RedFlagsText));
        this.RaisePropertyChanged(nameof(SourceLabel));
    }
}
