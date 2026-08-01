using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CryptoAITerminal.AIEngine;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>Строка разбора под вердиктом. Тон задаёт цвет точки слева.</summary>
public sealed class AiBulletVM
{
    public string Text { get; init; } = "";
    public AiVerdictPalette.Tone Tone { get; init; } = AiVerdictPalette.Tone.Neutral;
    public string Dot => AiVerdictPalette.Color(Tone);
}

/// <summary>
/// Готовый к показу вердикт AI: одна форма для всех панелей.
///
/// До этого каждая панель складывала ответ модели в четыре-пять отдельных строк вью-модели, а
/// список доводов склеивала в одну строку через <c>"• " + string.Join("\n• ", …)</c>. Из-за этого
/// разбор выводился абзацем без иерархии: слово вердикта и его обоснование весили в глазах
/// одинаково, счёт риска был обычным текстом, а признак <c>IsFallback</c> — уже посчитанный в
/// каждом ответе — не показывался нигде. Человек не видел ни главного, ни того, что перед ним
/// вообще не ответ модели.
///
/// Здесь всё это становится данными: слово вердикта с тоном, счёт со шкалой, доводы отдельными
/// строками, подпись источника и отдельная полоса «отвечено без модели, вот почему». Рисует их
/// <c>Controls/AiVerdictCard</c> — один вид на все страницы, поэтому вердикт в снайпере и вердикт
/// в бэктесте читаются одинаково.
/// </summary>
public sealed class AiVerdictVM : ReactiveObject
{
    private string _verdict = "";
    private string _summary = "";
    private string _source = "";
    private string? _fallbackReason;
    private bool _isFallback;
    private bool _hasContent;
    private bool _isRunning;
    private int? _score;
    private string _scoreCaption = "";

    /// <summary>Слово вердикта из словаря панели, например <c>BULLISH</c> или <c>AVOID</c>.</summary>
    public string Verdict
    {
        get => _verdict;
        private set
        {
            this.RaiseAndSetIfChanged(ref _verdict, value ?? "");
            foreach (var n in new[] { nameof(HasVerdict), nameof(VerdictText), nameof(VerdictColor),
                                      nameof(VerdictTint), nameof(VerdictStroke), nameof(ScoreColor) })
                this.RaisePropertyChanged(n);
        }
    }

    public bool HasVerdict => _verdict.Length > 0;
    public string VerdictText => AiVerdictPalette.Display(_verdict);
    public string VerdictColor => AiVerdictPalette.ColorOf(_verdict);
    public string VerdictTint => AiVerdictPalette.TintOf(_verdict);
    public string VerdictStroke => AiVerdictPalette.StrokeOf(_verdict);

    /// <summary>Связный текст вердикта.</summary>
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value ?? ""); }

    /// <summary>Доводы: по строке на пункт, а не абзацем.</summary>
    public ObservableCollection<AiBulletVM> Bullets { get; } = [];

    public bool HasBullets => Bullets.Count > 0;

    /// <summary>Счёт 0..100, если панель его считает.</summary>
    public int? Score
    {
        get => _score;
        private set
        {
            this.RaiseAndSetIfChanged(ref _score, value);
            foreach (var n in new[] { nameof(HasScore), nameof(ScoreBarWidth), nameof(ScoreColor), nameof(ScoreText) })
                this.RaisePropertyChanged(n);
        }
    }

    public bool HasScore => _score is not null;
    public string ScoreText => _score is { } s ? $"{s}/100" : "";

    /// <summary>Что означает счёт: «риск», «оценка». Без подписи число двусмысленно.</summary>
    public string ScoreCaption { get => _scoreCaption; private set => this.RaiseAndSetIfChanged(ref _scoreCaption, value ?? ""); }

    /// <summary>Ширина заполненной части шкалы в дорожке 220px, которую рисует карточка.</summary>
    public double ScoreBarWidth => Math.Clamp(_score ?? 0, 0, 100) / 100.0 * 220.0;

    /// <summary>Шкала красится тоном вердикта: два разных цвета рядом читались бы как два разных смысла.</summary>
    public string ScoreColor => AiVerdictPalette.ColorOf(_verdict);

    /// <summary>Чем отвечено: модель, которую назвал сервер, или встроенный расчёт.</summary>
    public string Source { get => _source; private set => this.RaiseAndSetIfChanged(ref _source, value ?? ""); }

    /// <summary>Ответ собран встроенным расчётом, а не моделью.</summary>
    public bool IsFallback { get => _isFallback; private set => this.RaiseAndSetIfChanged(ref _isFallback, value); }

    /// <summary>
    /// Почему модель не участвовала. Отдельно от <see cref="Summary"/> намеренно: причина — это
    /// не часть вердикта, и, вписанная в его текст, она читается как вывод модели о рынке.
    /// </summary>
    public string? FallbackReason
    {
        get => _fallbackReason;
        private set { this.RaiseAndSetIfChanged(ref _fallbackReason, value); this.RaisePropertyChanged(nameof(FallbackText)); }
    }

    /// <summary>
    /// Подложка и рамка полосы отказа. Через палитру, а не литералом в разметке: жёсткий
    /// <c>#14F4B860</c> пережил бы смену темы и остался бы янтарным на светлом фоне.
    /// </summary>
    public string FallbackTint => SemanticColor.Alpha(SemanticColor.Warning, "14");
    public string FallbackStroke => SemanticColor.Alpha(SemanticColor.Warning, "3D");

    /// <summary>Строка полосы внизу карточки. Пустая, когда полосу показывать не нужно.</summary>
    public string FallbackText => !_isFallback
        ? ""
        : _fallbackReason is { Length: > 0 } why
            ? $"Отвечено встроенным расчётом: {why}"
            : "Отвечено встроенным расчётом — модель не участвовала.";

    /// <summary>Есть ли что показывать вообще.</summary>
    public bool HasContent { get => _hasContent; private set => this.RaiseAndSetIfChanged(ref _hasContent, value); }

    /// <summary>Идёт вызов. Карточка на это время притухает, а не исчезает.</summary>
    public bool IsRunning { get => _isRunning; set => this.RaiseAndSetIfChanged(ref _isRunning, value); }

    /// <summary>
    /// Заполнить из ответа обзорного провайдера — общая форма для восьми панелей: настроение,
    /// киты, on-chain, ликвидации, корреляции, брифинг, опционы, пред-торговый риск.
    /// </summary>
    /// <param name="reason">
    /// <c>LastError</c> службы. Ответ уже может быть встроенным — тогда это объясняет, почему.
    /// </param>
    public void Set(InsightResult result, string? reason = null)
    {
        if (result is null) return;
        Fill(result.Signal, result.Summary, result.Bullets, result.Source, result.IsFallback, reason);
    }

    /// <summary>Заполнить произвольным вердиктом — для панелей со своей формой ответа.</summary>
    public void Fill(string? verdict, string? summary, IEnumerable<string>? bullets,
        string? source, bool isFallback, string? reason = null,
        int? score = null, string scoreCaption = "")
    {
        Verdict = verdict ?? "";
        Summary = summary ?? "";
        Source = source ?? "";
        IsFallback = isFallback;
        FallbackReason = reason;
        Score = score;
        ScoreCaption = scoreCaption;

        SetBullets(bullets?.Select(b => new AiBulletVM { Text = b, Tone = AiVerdictPalette.ToneOf(verdict) }));

        HasContent = Summary.Length > 0 || HasVerdict || HasBullets;
    }

    /// <summary>Доводы с разным тоном — для разборов, где есть и сильные стороны, и утечки.</summary>
    public void SetBullets(IEnumerable<AiBulletVM>? bullets)
    {
        Bullets.Clear();
        if (bullets is not null)
            foreach (var b in bullets)
                if (!string.IsNullOrWhiteSpace(b.Text))
                    Bullets.Add(b);
        this.RaisePropertyChanged(nameof(HasBullets));
    }

    /// <summary>Сброс к пустому состоянию — панель закрыта или контекст сменился.</summary>
    public void Clear()
    {
        Verdict = ""; Summary = ""; Source = ""; Score = null; ScoreCaption = "";
        IsFallback = false; FallbackReason = null;
        SetBullets(null);
        HasContent = false;
    }
}
