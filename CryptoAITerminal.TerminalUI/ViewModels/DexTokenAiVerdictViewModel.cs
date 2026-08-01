using System.Linq;
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
        set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            this.RaisePropertyChanged(nameof(IsScanning));
        }
    }

    public bool DeepScanBusy
    {
        get => _deepScanBusy;
        set
        {
            this.RaiseAndSetIfChanged(ref _deepScanBusy, value);
            this.RaisePropertyChanged(nameof(IsScanning));
        }
    }

    /// <summary>Either the initial assessment or a deep scan is in flight; the card shows one progress hint for both.</summary>
    public bool IsScanning => _isBusy || _deepScanBusy;

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

    /// <summary>Hidden on the pending verdict, which has no source yet.</summary>
    public bool HasSourceLabel => !string.IsNullOrWhiteSpace(_verdict.Source);

    /// <summary>Amber for the offline heuristic, muted for a live model — a buyer must tell the two apart at a glance.</summary>
    public string SourceHex => _verdict.IsFallback ? SemanticColor.Warning : "#5a7a94";

    /// <summary>
    /// Тот же вердикт в форме общей карточки: плашка, шкала риска и красные флаги отдельными
    /// строками. Раньше флаги склеивались через « · » в одну строку — три разные причины отказа
    /// читались как одна фраза, хотя по ним решают, покупать ли только что созданный токен.
    /// </summary>
    public AiVerdictVM Card { get; } = new();

    public void ApplyVerdict(TokenAiVerdict verdict)
    {
        _verdict = verdict ?? TokenAiVerdict.Pending();
        HasVerdict = true;
        // Красные флаги — всегда тревожным тоном, независимо от вердикта: у FAVORABLE с одним
        // флагом флаг и есть то, ради чего этот список читают.
        Card.Fill(_verdict.Verdict, _verdict.Reason, null, _verdict.Source, _verdict.IsFallback,
            score: _verdict.RiskScore, scoreCaption: "риск");
        Card.SetBullets(_verdict.RedFlags.Select(f => new AiBulletVM
        {
            Text = f,
            Tone = AiVerdictPalette.Tone.Bad,
        }));
        RaisePassThroughs();
    }

    public void Reset()
    {
        _verdict = TokenAiVerdict.Pending();
        HasVerdict = false;
        DeepScanNote = null;
        Card.Clear();
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
        this.RaisePropertyChanged(nameof(HasSourceLabel));
        this.RaisePropertyChanged(nameof(SourceHex));
    }
}
