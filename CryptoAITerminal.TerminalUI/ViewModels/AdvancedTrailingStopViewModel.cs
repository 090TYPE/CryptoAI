using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Models;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

// ─── Strategy enum ────────────────────────────────────────────────────────────

public enum TrailingStopType
{
    Percentage    = 0,
    AtrTrail      = 1,
    ChandelierExit = 2,
    BreakEven     = 3,
    SwingLow      = 4,
}

// ─── View-model ───────────────────────────────────────────────────────────────

/// <summary>
/// Self-contained advanced trailing-stop engine that runs on every price tick.
/// The caller feeds ticks via <see cref="OnPriceTick"/> and subscribes to
/// <see cref="StopTriggered"/> / <see cref="StopLevelChanged"/> events.
/// </summary>
public sealed class AdvancedTrailingStopViewModel : ReactiveObject
{
    // ── parameters ───────────────────────────────────────────────────────────
    private TrailingStopType _type          = TrailingStopType.AtrTrail;
    private int     _atrPeriod              = 14;
    private decimal _atrMultiplier          = 2.0m;
    private int     _chanPeriod             = 22;
    private decimal _chanMultiplier         = 3.0m;
    private decimal _breakEvenTrigPct       = 1.5m;
    private decimal _breakEvenBufferPct     = 0.1m;
    private int     _swingLookback          = 5;
    private decimal _pctDistance            = 1.0m;

    // ── runtime state ─────────────────────────────────────────────────────────
    private bool    _isArmed;
    private decimal _entryPrice;
    private decimal _peakPrice;
    private decimal _currentStop;
    private bool    _breakEvenMoved;
    private string  _statusLabel = "Disarmed";

    // ── events ───────────────────────────────────────────────────────────────
    /// <summary>Raised (on the calling thread) when price hits the trailing stop.</summary>
    public event Action<decimal>? StopTriggered;
    /// <summary>Raised whenever the stop level is ratcheted upward.</summary>
    public event Action<decimal>? StopLevelChanged;
    /// <summary>Raised when the VM requests to be armed — caller must supply entry price.</summary>
    public event Action? ArmRequested;

    // ── bindable properties ───────────────────────────────────────────────────

    public int SelectedTypeIndex
    {
        get => (int)_type;
        set
        {
            if ((int)_type == value) return;
            _type = (TrailingStopType)value;
            this.RaisePropertyChanged();
            RaiseVisibilityFlags();
        }
    }

    public int     AtrPeriod          { get => _atrPeriod;          set => this.RaiseAndSetIfChanged(ref _atrPeriod, value); }
    public decimal AtrMultiplier      { get => _atrMultiplier;      set => this.RaiseAndSetIfChanged(ref _atrMultiplier, value); }
    public int     ChanPeriod         { get => _chanPeriod;         set => this.RaiseAndSetIfChanged(ref _chanPeriod, value); }
    public decimal ChanMultiplier     { get => _chanMultiplier;     set => this.RaiseAndSetIfChanged(ref _chanMultiplier, value); }
    public decimal BreakEvenTrigPct   { get => _breakEvenTrigPct;   set => this.RaiseAndSetIfChanged(ref _breakEvenTrigPct, value); }
    public decimal BreakEvenBufferPct { get => _breakEvenBufferPct; set => this.RaiseAndSetIfChanged(ref _breakEvenBufferPct, value); }
    public int     SwingLookback      { get => _swingLookback;      set => this.RaiseAndSetIfChanged(ref _swingLookback, value); }
    public decimal PctDistance        { get => _pctDistance;        set => this.RaiseAndSetIfChanged(ref _pctDistance, value); }

    public bool    IsArmed      { get => _isArmed;      private set => this.RaiseAndSetIfChanged(ref _isArmed, value); }
    public decimal CurrentStop  { get => _currentStop;  private set => this.RaiseAndSetIfChanged(ref _currentStop, value); }
    public string  StatusLabel  { get => _statusLabel;  private set => this.RaiseAndSetIfChanged(ref _statusLabel, value); }

    // computed display
    public string ArmButtonText => IsArmed ? "■ Disarm" : "▶ Arm";
    public string ArmButtonFg   => IsArmed ? "#FF6B6B"  : "#21E6C1";
    public string StopLabel     => IsArmed && CurrentStop > 0 ? $"{CurrentStop:N2}" : "--";
    public string EntryLabel    => IsArmed && _entryPrice > 0 ? $"Entry {_entryPrice:N2}" : string.Empty;

    // visibility flags for parameter panels
    public bool IsPctMode       => _type == TrailingStopType.Percentage;
    public bool IsAtrMode       => _type == TrailingStopType.AtrTrail;
    public bool IsChanMode      => _type == TrailingStopType.ChandelierExit;
    public bool IsBreakEvenMode => _type == TrailingStopType.BreakEven;
    public bool IsSwingMode     => _type == TrailingStopType.SwingLow;

    // static combo source
    public IReadOnlyList<string> TypeLabels { get; } =
        ["% Trailing", "ATR Trailing", "Chandelier Exit", "Break-Even", "Swing Low"];

    // ── commands ──────────────────────────────────────────────────────────────
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ToggleArmCommand { get; }

    // ── ctor ──────────────────────────────────────────────────────────────────

    public AdvancedTrailingStopViewModel()
    {
        ToggleArmCommand = ReactiveCommand.Create(ToggleArm, outputScheduler: App.UiScheduler);
    }

    // ── public API ───────────────────────────────────────────────────────────

    /// <summary>Arm the trailing stop with a known entry price.</summary>
    public void Arm(decimal entryPrice)
    {
        _entryPrice     = entryPrice;
        _peakPrice      = entryPrice;
        _currentStop    = 0m;
        _breakEvenMoved = false;
        IsArmed         = true;
        StatusLabel     = $"Armed  entry {entryPrice:N2}";
        RaiseArmDisplayProperties();
    }

    /// <summary>Disarm and reset stop level.</summary>
    public void Disarm()
    {
        IsArmed     = false;
        CurrentStop = 0m;
        StatusLabel = "Disarmed";
        RaiseArmDisplayProperties();
    }

    /// <summary>
    /// Feed a new price tick. Returns <c>true</c> if the stop was hit
    /// (caller should execute the close order).
    /// </summary>
    public bool OnPriceTick(decimal price, IReadOnlyList<DexOhlcvPoint> candles)
    {
        if (!IsArmed || price <= 0)
            return false;

        // Ratchet peak upward
        if (price > _peakPrice) _peakPrice = price;

        decimal candidate = ComputeStop(price, candles);

        if (candidate > 0 && candidate > CurrentStop)
        {
            CurrentStop = candidate;
            this.RaisePropertyChanged(nameof(StopLabel));
            StatusLabel = $"Stop → {CurrentStop:N2}  (peak {_peakPrice:N2})";
            StopLevelChanged?.Invoke(CurrentStop);
        }

        if (CurrentStop > 0 && price <= CurrentStop)
        {
            StatusLabel = $"★ TRIGGERED at {price:N2}";
            IsArmed     = false;
            RaiseArmDisplayProperties();
            StopTriggered?.Invoke(price);
            return true;
        }

        return false;
    }

    // ── private logic ─────────────────────────────────────────────────────────

    private void ToggleArm()
    {
        if (IsArmed) { Disarm(); return; }
        ArmRequested?.Invoke();   // MainWindowViewModel will call Arm(currentPrice)
    }

    private decimal ComputeStop(decimal price, IReadOnlyList<DexOhlcvPoint> candles) => _type switch
    {
        TrailingStopType.Percentage     => ComputePctStop(),
        TrailingStopType.AtrTrail       => ComputeAtrStop(candles),
        TrailingStopType.ChandelierExit => ComputeChandelierStop(candles),
        TrailingStopType.BreakEven      => ComputeBreakEvenStop(price),
        TrailingStopType.SwingLow       => ComputeSwingLowStop(candles),
        _                                => 0m
    };

    // ── strategy implementations ──────────────────────────────────────────────

    // Simple percentage distance from the running peak
    private decimal ComputePctStop() =>
        _peakPrice * (1m - _pctDistance / 100m);

    // ATR trailing: stop = peak − (ATR × multiplier)
    private decimal ComputeAtrStop(IReadOnlyList<DexOhlcvPoint> candles)
    {
        var atr = ComputeAtr(candles, _atrPeriod);
        return atr <= 0 ? 0m : _peakPrice - atr * _atrMultiplier;
    }

    // Chandelier Exit (long): HighestHigh(n) − ATR(n) × multiplier
    private decimal ComputeChandelierStop(IReadOnlyList<DexOhlcvPoint> candles)
    {
        if (candles.Count < _chanPeriod) return 0m;
        var slice       = candles.TakeLast(_chanPeriod).ToArray();
        var highestHigh = slice.Max(c => c.High);
        var atr         = ComputeAtr(candles, _chanPeriod);
        return atr <= 0 ? 0m : highestHigh - atr * _chanMultiplier;
    }

    // Break-even: once price reaches entry + trigPct%, lock stop at entry + buffer
    private decimal ComputeBreakEvenStop(decimal price)
    {
        if (_entryPrice <= 0) return 0m;
        if (_breakEvenMoved) return _entryPrice * (1m + _breakEvenBufferPct / 100m);

        var triggerPrice = _entryPrice * (1m + _breakEvenTrigPct / 100m);
        if (price >= triggerPrice)
        {
            _breakEvenMoved = true;
            StatusLabel     = $"Break-even moved → {_entryPrice:N2}";
        }
        return _breakEvenMoved ? _entryPrice * (1m + _breakEvenBufferPct / 100m) : 0m;
    }

    // Swing Low: stop just below the lowest Low in the last N candles
    private decimal ComputeSwingLowStop(IReadOnlyList<DexOhlcvPoint> candles)
    {
        if (candles.Count < _swingLookback) return 0m;
        return candles.TakeLast(_swingLookback).Min(c => c.Low);
    }

    // ── ATR via Wilder's Simple Average (initial seed) ────────────────────────

    private static decimal ComputeAtr(IReadOnlyList<DexOhlcvPoint> candles, int period)
    {
        if (candles.Count < period + 1) return 0m;

        var slice = candles.TakeLast(period + 1).ToArray();
        decimal sumTr = 0m;
        for (int i = 1; i < slice.Length; i++)
        {
            var curr = slice[i];
            var prev = slice[i - 1];
            var tr   = Math.Max(curr.High - curr.Low,
                       Math.Max(Math.Abs(curr.High - prev.Close),
                                Math.Abs(curr.Low  - prev.Close)));
            sumTr += tr;
        }
        return sumTr / period;
    }

    // ── display helpers ───────────────────────────────────────────────────────

    private void RaiseArmDisplayProperties()
    {
        this.RaisePropertyChanged(nameof(ArmButtonText));
        this.RaisePropertyChanged(nameof(ArmButtonFg));
        this.RaisePropertyChanged(nameof(StopLabel));
        this.RaisePropertyChanged(nameof(EntryLabel));
    }

    private void RaiseVisibilityFlags()
    {
        this.RaisePropertyChanged(nameof(IsPctMode));
        this.RaisePropertyChanged(nameof(IsAtrMode));
        this.RaisePropertyChanged(nameof(IsChanMode));
        this.RaisePropertyChanged(nameof(IsBreakEvenMode));
        this.RaisePropertyChanged(nameof(IsSwingMode));
    }
}
