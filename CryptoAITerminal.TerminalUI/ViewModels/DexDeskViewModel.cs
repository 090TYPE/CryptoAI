using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using CryptoAITerminal.Core.Trading;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>DEX terminal sub-mode: real spot AMM swap vs paper perpetuals.</summary>
public enum DexDeskMode
{
    Swap,
    Perp
}

/// <summary>A single gas-priority tier shown in the DEX ticket (real preference,
/// consumed by the paper engine to model network cost).</summary>
public sealed class DexGasTierViewModel : ReactiveObject
{
    private bool _isSelected;

    public string Key { get; }
    public string Label { get; }
    public string GweiLabel { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public DexGasTierViewModel(string key, string label, string gweiLabel)
    {
        Key = key;
        Label = label;
        GweiLabel = gweiLabel;
    }
}

/// <summary>
/// Thin coordinator for the redesigned DEX terminal. Owns the DEX-specific chrome
/// that is shared across order types — venue sub-mode (SWAP/PERP), gas priority and
/// MEV-protection preferences — while the existing <see cref="DexTradingViewModel"/>
/// keeps driving the real spot-swap execution. Kept separate so the already large
/// swap view model stays focused. The paper-perp surface is added in a later phase.
/// </summary>
public sealed class DexDeskViewModel : ReactiveObject, IDisposable
{
    private DexDeskMode _mode = DexDeskMode.Swap;
    private string _selectedGasTierKey = "Standard";
    private bool _mevProtectionEnabled = true;

    public DexTradingViewModel Swap { get; }
    public DexPerpTradingViewModel Perp { get; }
    public AiTradeAssistantViewModel Assistant { get; }

    public DexDeskViewModel(DexTradingViewModel swap)
    {
        Swap = swap ?? throw new ArgumentNullException(nameof(swap));
        Perp = new DexPerpTradingViewModel(
            anchorPrice: () => Swap.SelectedToken?.PriceUsd ?? 0m,
            symbol: () => Swap.SelectedToken?.TokenInfo.Symbol ?? "TOKEN");
        Assistant = new AiTradeAssistantViewModel(
            venueLabel: "DEX",
            candles: () => Swap.ChartCandles,
            price: () => _mode == DexDeskMode.Perp ? Perp.MarkPrice : (Swap.SelectedToken?.PriceUsd ?? 0m),
            apply: ApplyDexSetup,
            equity: () => _mode == DexDeskMode.Perp ? Perp.Equity : 0m);

        GasTiers = new ObservableCollection<DexGasTierViewModel>
        {
            new("Slow",     "SLOW",     "3 gwei"),
            new("Standard", "STANDARD", "5 gwei"),
            new("Fast",     "FAST",     "9 gwei"),
            new("Instant",  "INSTANT",  "15 gwei"),
        };

        SelectModeCommand = ReactiveCommand.Create<string>(SelectMode, outputScheduler: App.UiScheduler);
        SelectGasTierCommand = ReactiveCommand.Create<string>(SelectGasTier, outputScheduler: App.UiScheduler);
        ToggleMevCommand = ReactiveCommand.Create(ToggleMev, outputScheduler: App.UiScheduler);

        SyncGasTierSelection();
    }

    // ── Venue sub-mode ────────────────────────────────────────────────────────
    public DexDeskMode Mode
    {
        get => _mode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _mode, value);
            this.RaisePropertyChanged(nameof(ModeKey));
            this.RaisePropertyChanged(nameof(IsSwapMode));
            this.RaisePropertyChanged(nameof(IsPerpMode));
            this.RaisePropertyChanged(nameof(SymbolTypeLabel));
            Perp.SetActive(_mode == DexDeskMode.Perp);
        }
    }

    /// <summary>Upper-case key for driving <c>active</c> styles via StringEqualsConverter.</summary>
    public string ModeKey => _mode == DexDeskMode.Perp ? "PERP" : "SWAP";
    public bool IsSwapMode => _mode == DexDeskMode.Swap;
    public bool IsPerpMode => _mode == DexDeskMode.Perp;
    public string SymbolTypeLabel => _mode == DexDeskMode.Perp ? "PERP · DEX" : "SWAP · DEX";

    /// <summary>Shown in the PERP zone until the paper engine is wired (Phase 3).</summary>
    public string PerpPlaceholderTitle => "Paper PERP engine warming up";
    public string PerpPlaceholderDetail =>
        "The simulated perpetuals desk (mark price, funding, order book, positions, "
        + "liquidation) is being wired in. Switch to SWAP to trade spot now.";

    public ReactiveCommand<string, Unit> SelectModeCommand { get; }

    private void SelectMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        Mode = string.Equals(mode, "PERP", StringComparison.OrdinalIgnoreCase)
            ? DexDeskMode.Perp
            : DexDeskMode.Swap;
    }

    // ── Gas priority ──────────────────────────────────────────────────────────
    public ObservableCollection<DexGasTierViewModel> GasTiers { get; }

    public string SelectedGasTierKey
    {
        get => _selectedGasTierKey;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedGasTierKey, value);
            this.RaisePropertyChanged(nameof(SelectedGasTierGweiLabel));
        }
    }

    public string SelectedGasTierGweiLabel =>
        GasTiers.FirstOrDefault(t => string.Equals(t.Key, _selectedGasTierKey, StringComparison.OrdinalIgnoreCase))?.GweiLabel
        ?? "—";

    public ReactiveCommand<string, Unit> SelectGasTierCommand { get; }

    private void SelectGasTier(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            SelectedGasTierKey = key;
            SyncGasTierSelection();
        }
    }

    private void SyncGasTierSelection()
    {
        foreach (var tier in GasTiers)
        {
            tier.IsSelected = string.Equals(tier.Key, _selectedGasTierKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── MEV protection ────────────────────────────────────────────────────────
    public bool MevProtectionEnabled
    {
        get => _mevProtectionEnabled;
        private set
        {
            this.RaiseAndSetIfChanged(ref _mevProtectionEnabled, value);
            this.RaisePropertyChanged(nameof(MevLabel));
            this.RaisePropertyChanged(nameof(MevBrush));
        }
    }

    public string MevLabel => _mevProtectionEnabled ? "ON" : "OFF";
    public string MevBrush => _mevProtectionEnabled ? "#a855f7" : "#5a7a94";

    public ReactiveCommand<Unit, Unit> ToggleMevCommand { get; }

    private void ToggleMev() => MevProtectionEnabled = !_mevProtectionEnabled;

    /// <summary>Apply an AI-assistant setup to the active DEX ticket (PERP fully; SWAP
    /// maps the sizing suggestion onto the buy-amount preset).</summary>
    private void ApplyDexSetup(TradeSetup setup)
    {
        if (_mode == DexDeskMode.Perp)
        {
            Perp.Side = setup.Bias == "LONG" ? "Long" : "Short";
            Perp.OrderType = "Limit";
            Perp.TriggerPrice = setup.Entry;
            Perp.TakeProfit = setup.TakeProfit;
            Perp.StopLoss = setup.StopLoss;
            Perp.Leverage = setup.Leverage;

            var price = Perp.MarkPrice > 0m ? Perp.MarkPrice : setup.Entry;
            if (price > 0m)
            {
                var notional = Perp.Equity * (setup.SizePercent / 100m) * setup.Leverage;
                Perp.SizeTokens = Math.Round(notional / price, 4, MidpointRounding.AwayFromZero);
            }
        }
        else
        {
            var preset = setup.SizePercent >= 75m ? "100"
                : setup.SizePercent >= 50m ? "75"
                : setup.SizePercent >= 25m ? "50"
                : "25";
            Swap.ApplyBuyBalancePresetCommand.Execute(preset).Subscribe();
        }
    }

    public void Dispose() => Perp.Dispose();
}
