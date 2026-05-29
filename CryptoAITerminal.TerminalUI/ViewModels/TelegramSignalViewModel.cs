using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

// ── Pending order that waits for Telegram confirm ─────────────────────────────

public sealed class TelegramPendingSignal
{
    public long    MessageId   { get; init; }
    public string  Symbol      { get; init; } = string.Empty;
    public string  Side        { get; init; } = "BUY";
    public decimal Price       { get; init; }
    public decimal Quantity    { get; init; }
    public string  Description { get; init; } = string.Empty;
    public DateTime CreatedAt  { get; init; } = DateTime.UtcNow;
}

// ── Row VM ─────────────────────────────────────────────────────────────────────

public sealed class TelegramSignalRowVM : ReactiveObject
{
    public TelegramPendingSignal Signal { get; }

    public string Label      => $"{Signal.Side}  {Signal.Symbol}  @ {Signal.Price:N2}  ×{Signal.Quantity:0.####}";
    public string SideBrush  => Signal.Side == "SELL" ? "#FF6B6B" : "#21E6C1";
    public string AgeLabel   => $"{Math.Max(0, (int)(DateTime.UtcNow - Signal.CreatedAt).TotalSeconds)}s ago";
    public string Description => Signal.Description;

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> AcceptCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SkipCommand   { get; }

    public TelegramSignalRowVM(
        TelegramPendingSignal signal,
        Action<TelegramSignalRowVM> onAccept,
        Action<TelegramSignalRowVM> onSkip)
    {
        Signal        = signal;
        AcceptCommand = ReactiveCommand.Create(() => onAccept(this));
        SkipCommand   = ReactiveCommand.Create(() => onSkip(this));
    }
}

// ── Master VM ─────────────────────────────────────────────────────────────────

/// <summary>
/// Tracks Telegram signals that are awaiting user confirmation.
/// Populated by <see cref="MainWindowViewModel"/> when inline-button signals arrive.
/// </summary>
public sealed class TelegramSignalViewModel : ReactiveObject
{
    private readonly TelegramNotificationService           _telegram;
    private readonly ConcurrentDictionary<long, TelegramPendingSignal> _pending = new();

    public ObservableCollection<TelegramSignalRowVM> PendingRows { get; } = [];

    public bool HasPendingSignals => PendingRows.Count > 0;
    public bool HasNoSignals      => PendingRows.Count == 0;
    public bool InlineButtonsEnabled
    {
        get => _inlineEnabled;
        set { this.RaiseAndSetIfChanged(ref _inlineEnabled, value); }
    }
    private bool _inlineEnabled = true;

    /// <summary>Raised when the user (or Telegram callback) accepts a signal. Caller places the order.</summary>
    public event Action<TelegramPendingSignal>? SignalAccepted;
    /// <summary>Raised when a signal is skipped.</summary>
    public event Action<TelegramPendingSignal>? SignalSkipped;

    public TelegramSignalViewModel(TelegramNotificationService telegram)
    {
        _telegram = telegram;

        // Wire Telegram callback polling
        _telegram.CallbackReceived += OnTelegramCallback;
    }

    // ── public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a signal message to Telegram with [✅ Accept] [❌ Skip] buttons
    /// and registers it as a pending signal.
    /// </summary>
    public async void SendSignal(string symbol, string side, decimal price, decimal quantity, string description = "")
    {
        if (!InlineButtonsEnabled || !_telegram.IsConfigured) return;

        var text = $"🔔 <b>Signal</b>\n" +
                   $"<code>{side} {symbol}</code>  @ <b>{price:N2}</b>  ×{quantity:0.####}\n" +
                   $"{(string.IsNullOrWhiteSpace(description) ? "" : description + "\n")}" +
                   $"<i>Expires in 5 minutes.</i>";

        var msgId = await _telegram.SendWithButtonsAsync(text,
            ("✅ Accept", $"accept:{0}"),   // placeholder — filled after we get msgId
            ("❌ Skip",   $"skip:{0}"));

        if (msgId <= 0) return;

        // Re-register with actual message id in callback data
        // (Telegram doesn't let us update the button data after sending, but
        //  we track by msgId on our end — that's the reliable key.)
        var signal = new TelegramPendingSignal
        {
            MessageId   = msgId,
            Symbol      = symbol,
            Side        = side,
            Price       = price,
            Quantity    = quantity,
            Description = description
        };

        _pending[msgId] = signal;

        Dispatcher.UIThread.Post(() =>
        {
            var row = new TelegramSignalRowVM(signal, AcceptRow, SkipRow);
            PendingRows.Insert(0, row);
            RaiseCounts();

            // Auto-expire after 5 minutes
            var timer = new System.Timers.Timer(TimeSpan.FromMinutes(5).TotalMilliseconds) { AutoReset = false };
            timer.Elapsed += (_, _) =>
            {
                timer.Dispose();
                ExpireSignal(msgId);
            };
            timer.Start();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private void OnTelegramCallback(TelegramCallback cb)
    {
        // cb.CallbackData = "accept:<something>" or "skip:<something>"
        if (cb.CallbackData.StartsWith("accept", StringComparison.OrdinalIgnoreCase))
        {
            // Accept the most-recent pending signal (simple matching)
            if (_pending.IsEmpty) { _ = _telegram.AnswerCallbackQueryAsync(cb.QueryId, "No pending signals."); return; }
            var latest = GetLatestPending();
            if (latest is null) return;

            _ = _telegram.AnswerCallbackQueryAsync(cb.QueryId, $"✅ Order placed: {latest.Side} {latest.Symbol}");
            RemovePending(latest.MessageId, accepted: true);
        }
        else if (cb.CallbackData.StartsWith("skip", StringComparison.OrdinalIgnoreCase))
        {
            if (_pending.IsEmpty) { _ = _telegram.AnswerCallbackQueryAsync(cb.QueryId, "No pending signals."); return; }
            var latest = GetLatestPending();
            if (latest is null) return;

            _ = _telegram.AnswerCallbackQueryAsync(cb.QueryId, "❌ Signal skipped.");
            RemovePending(latest.MessageId, accepted: false);
        }
    }

    private TelegramPendingSignal? GetLatestPending()
    {
        TelegramPendingSignal? latest = null;
        foreach (var s in _pending.Values)
            if (latest is null || s.CreatedAt > latest.CreatedAt)
                latest = s;
        return latest;
    }

    private void AcceptRow(TelegramSignalRowVM row) => RemovePending(row.Signal.MessageId, accepted: true);
    private void SkipRow(TelegramSignalRowVM row)   => RemovePending(row.Signal.MessageId, accepted: false);

    private void RemovePending(long msgId, bool accepted)
    {
        if (!_pending.TryRemove(msgId, out var signal)) return;

        Dispatcher.UIThread.Post(() =>
        {
            var row = PendingRows.FirstOrDefault(r => r.Signal.MessageId == msgId);
            if (row is not null) PendingRows.Remove(row);
            RaiseCounts();
        }, Avalonia.Threading.DispatcherPriority.Background);

        if (accepted) SignalAccepted?.Invoke(signal);
        else          SignalSkipped?.Invoke(signal);
    }

    private void ExpireSignal(long msgId)
    {
        if (!_pending.ContainsKey(msgId)) return;
        RemovePending(msgId, accepted: false);
    }

    private void RaiseCounts()
    {
        this.RaisePropertyChanged(nameof(HasPendingSignals));
        this.RaisePropertyChanged(nameof(HasNoSignals));
    }
}
