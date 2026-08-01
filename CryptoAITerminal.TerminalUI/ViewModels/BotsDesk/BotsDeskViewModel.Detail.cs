using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

public partial class BotsDeskViewModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static decimal Dec(string s, decimal fallback)
        => decimal.TryParse(BotsDeskData.Decimalish(s), NumberStyles.Any, Inv, out var d) ? d : fallback;
    private static int Int(string s, int fallback)
        => int.TryParse(BotsDeskData.Digits(s), NumberStyles.Any, Inv, out var i) ? i : fallback;

    // ── risk caps — bound to the app's real limits ───────────────────────────
    // order size / open positions live on the AI trader; daily loss and total
    // exposure are the wallet-wide guards every route is checked against.
    // The sliders in the view have fixed ranges, so the getters report a clamped
    // slider position while the labels show the engine's true value. Writes are
    // ignored unless the value actually moved, so simply binding the slider can
    // never rewrite a setting that sits outside the slider's range.
    private const double OrderMin = 100, OrderMax = 10000;
    private const double ExpMin = 1000, ExpMax = 100000;
    private const double PosMin = 1, PosMax = 20;
    private const double DailyMin = 50, DailyMax = 5000;

    public double RiskOrder
    {
        get => Trader is { } t ? Math.Clamp((double)t.MaxOrderUsd, OrderMin, OrderMax) : OrderMin;
        set { if (Trader is { } t && Math.Abs(value - RiskOrder) > 0.0001) { t.MaxOrderUsd = (decimal)value; RaiseRiskLabels(); } }
    }

    public double RiskExposure
    {
        get => Wallet is { } w ? Math.Clamp((double)w.GlobalMaxOpenExposureUsdt, ExpMin, ExpMax) : ExpMin;
        set { if (Wallet is { } w && Math.Abs(value - RiskExposure) > 0.0001) { w.GlobalMaxOpenExposureUsdt = (decimal)value; RaiseRiskLabels(); } }
    }

    public double RiskPositions
    {
        get => Trader is { } t ? Math.Clamp(t.MaxOpenPositions, PosMin, PosMax) : PosMin;
        set { if (Trader is { } t && Math.Abs(value - RiskPositions) > 0.0001) { t.MaxOpenPositions = (int)value; RaiseRiskLabels(); } }
    }

    public double RiskDaily
    {
        get => Wallet is { } w ? Math.Clamp((double)w.GlobalMaxDailyLossUsdt, DailyMin, DailyMax) : DailyMin;
        set { if (Wallet is { } w && Math.Abs(value - RiskDaily) > 0.0001) { w.GlobalMaxDailyLossUsdt = (decimal)value; RaiseRiskLabels(); RaiseLiveHeader(); } }
    }

    // labels show the engine's real value, not the clamped slider position
    public string RiskOrderLabel => Trader is { } t ? "$" + ((double)t.MaxOrderUsd).ToString("#,##0", Inv) : BotsDeskData.Dash;
    public string RiskExposureLabel => Wallet is { } w ? "$" + ((double)w.GlobalMaxOpenExposureUsdt).ToString("#,##0", Inv) : BotsDeskData.Dash;
    public string RiskPositionsLabel => Trader is { } t ? t.MaxOpenPositions.ToString("0", Inv) : BotsDeskData.Dash;
    public string RiskDailyLabel => Wallet is { } w ? "−$" + ((double)w.GlobalMaxDailyLossUsdt).ToString("#,##0", Inv) : BotsDeskData.Dash;

    private void RaiseRiskLabels()
    {
        foreach (var n in new[] { nameof(RiskOrder), nameof(RiskExposure), nameof(RiskPositions), nameof(RiskDaily),
            nameof(RiskOrderLabel), nameof(RiskExposureLabel), nameof(RiskPositionsLabel), nameof(RiskDailyLabel) })
            this.RaisePropertyChanged(n);
    }

    // ── guards (only the two the app really enforces) ────────────────────────
    public ObservableCollection<GuardToggle> GuardsRisk { get; } = new();

    private void RebuildGuards()
    {
        GuardsRisk.Clear();
        if (_host is null) return;

        Add("Live execution allowed",
            "Wallet-wide guard: off means no order is ever broadcast",
            !PaperOnly,
            () => { PaperOnly = !PaperOnly; RebuildGuards(); RebuildWizardGuards(); RaiseLiveHeader(); RefreshAllRows(); });

        Add("Symbol allowlist",
            "Autonomous agent rejects anything outside the list",
            !string.IsNullOrWhiteSpace(Allowlist),
            () =>
            {
                if (AgentVm is null) return;
                _allowlistCache = string.IsNullOrWhiteSpace(AgentVm.AllowlistText) ? _allowlistCache : AgentVm.AllowlistText;
                AgentVm.AllowlistText = string.IsNullOrWhiteSpace(AgentVm.AllowlistText) ? _allowlistCache : "";
                this.RaisePropertyChanged(nameof(Allowlist));
                RebuildGuards();
                RebuildWizardGuards();
            });

        void Add(string label, string hint, bool on, Action toggle)
            => GuardsRisk.Add(new GuardToggle
            {
                Label = label, Hint = hint,
                TrackBg = on ? "#14302e" : SemanticColor.Stroke,
                Knob = on ? BotsDeskData.Accent : "#3d5a72",
                KnobLeft = on ? 15 : 2,
                Command = new RelayCommand(toggle)
            });
    }

    private string _allowlistCache = "";

    /// <summary>The autonomous agent's symbol allowlist — the app's only allowlist.</summary>
    public string Allowlist
    {
        get => AgentVm?.AllowlistText ?? "";
        set
        {
            if (AgentVm is null) return;
            AgentVm.AllowlistText = value ?? "";
            this.RaisePropertyChanged();
        }
    }

    // ── DCA basket (mirrors DcaBotViewModel.Coins) ───────────────────────────
    public ObservableCollection<CoinRowViewModel> Coins { get; } = new();

    public string NewCoin
    {
        get => Dca?.NewCoinSymbol ?? "";
        set { if (Dca is not null) { Dca.NewCoinSymbol = (value ?? "").ToUpperInvariant(); this.RaisePropertyChanged(); } }
    }

    public string WeightSum => Dca?.WeightSumLabel ?? "";
    public string WeightColor => Dca?.WeightSumColor ?? BotsDeskData.Faint;

    /// <summary>Rebuilds the basket mirror only when the engine's own list changed, so edits in progress survive.</summary>
    private void SyncCoins()
    {
        if (Dca is null) { Coins.Clear(); return; }
        if (Coins.Count == Dca.Coins.Count &&
            Coins.Select(c => c.Id).SequenceEqual(Dca.Coins.Select(c => c.Id), StringComparer.Ordinal))
        {
            foreach (var row in Coins)
                if (row.Source is { } src) row.Stats = src.StatsLabel;
            OnCoinWeightChanged();
            return;
        }

        Coins.Clear();
        foreach (var c in Dca.Coins)
            Coins.Add(new CoinRowViewModel
            {
                Desk = this,
                Source = c,
                Id = c.Id,
                Symbol = c.Symbol,
                Weight = c.WeightPercent.ToString(Inv),
                Ma = c.ConditionalBuy,
                Stats = c.StatsLabel
            });
        OnCoinWeightChanged();
    }

    public void OnCoinWeightChanged()
    {
        this.RaisePropertyChanged(nameof(WeightSum));
        this.RaisePropertyChanged(nameof(WeightColor));
    }

    public void RemoveCoin(CoinRowViewModel c)
    {
        if (Dca is null) return;
        try { Dca.RemoveCoinCommand.Execute(c.Id).Subscribe(_ => { }, _ => { }); } catch { }
        SyncCoins();
        Toast(c.Symbol + " removed from the DCA basket", "info");
    }

    private void AddCoin()
    {
        if (Dca is null) { Toast("DCA engine is not available", "warn"); return; }
        var sym = (Dca.NewCoinSymbol ?? "").Trim();
        if (sym.Length == 0) { Toast("Type a symbol first", "warn"); return; }
        try { Dca.AddCoinCommand.Execute().Subscribe(_ => { }, _ => { }); } catch { }
        SyncCoins();
        this.RaisePropertyChanged(nameof(NewCoin));
        Toast(sym + " added to the DCA basket", "ok");
    }

    // ── TP / SL (mirrors AIBotViewModel — the only engine with a TP/SL config) ─
    public ObservableCollection<TpSlRowViewModel> Tpsl { get; } = new();

    private void SeedTpSl()
    {
        Tpsl.Clear();
        if (Rule is null) return;
        var r = Rule;

        Tpsl.Add(new TpSlRowViewModel
        {
            Desk = this, Key = "tp", ValueKey = "tpVal", Label = "Take profit", Hint = "reduce-only TP on the exchange",
            ApplyOn = v => r.TpEnabled = v, ApplyValue = v => r.TpPercent = Dec(v, r.TpPercent)
        });
        Tpsl.Add(new TpSlRowViewModel
        {
            Desk = this, Key = "partial", ValueKey = "tp1", Label = "Partial TP1 close", Hint = "share of the position closed at TP1",
            ApplyOn = v => r.PartialTp = v, ApplyValue = v => r.PartialTpClosePercent = Dec(v, r.PartialTpClosePercent)
        });
        Tpsl.Add(new TpSlRowViewModel
        {
            Desk = this, Key = "partial", ValueKey = "tp2", Label = "TP2 target", Hint = "the remainder exits here",
            ApplyOn = v => r.PartialTp = v, ApplyValue = v => r.PartialTp2Percent = Dec(v, r.PartialTp2Percent)
        });
        Tpsl.Add(new TpSlRowViewModel
        {
            Desk = this, Key = "sl", ValueKey = "slVal", Label = "Stop loss", Hint = "StopMarket, reduce-only",
            ApplyOn = v => r.SlEnabled = v, ApplyValue = v => r.SlPercent = Dec(v, r.SlPercent)
        });
        Tpsl.Add(new TpSlRowViewModel
        {
            Desk = this, Key = "trail", ValueKey = "trailVal", Label = "Trailing stop", Hint = "the stop follows every new peak",
            ApplyOn = v => r.TrailingStop = v, ApplyValue = _ => { /* the rule bot trails at SlPercent */ }
        });

        SyncTpSl();
    }

    private void SyncTpSl()
    {
        if (Rule is not { } r) return;
        foreach (var row in Tpsl)
        {
            switch (row.ValueKey)
            {
                case "tpVal": row.Sync(r.TpEnabled, r.TpPercent.ToString("0.##", Inv)); break;
                case "tp1": row.Sync(r.PartialTp, r.PartialTpClosePercent.ToString("0.##", Inv)); break;
                case "tp2": row.Sync(r.PartialTp, r.PartialTp2Percent.ToString("0.##", Inv)); break;
                case "slVal": row.Sync(r.SlEnabled, r.SlPercent.ToString("0.##", Inv)); break;
                case "trailVal": row.Sync(r.TrailingStop, r.SlPercent.ToString("0.##", Inv)); break;
            }
        }
    }

    public void OnTpSlChanged(TpSlRowViewModel row)
    {
        if (row.Key == "partial")
            foreach (var r in Tpsl.Where(r => r.Key == "partial" && !ReferenceEquals(r, row))) r.On = row.On;
        this.RaisePropertyChanged(nameof(TpSummary));
        RebuildTpPreview();
    }

    public string TpSummary => Rule?.TpSlSummary ?? BotsDeskData.Dash;

    public string TpNote => Rule is null
        ? "TP/SL is configured on the rule bot only — the other engines manage exits themselves."
        : "Futures: TP/SL are placed as exchange orders (TakeProfitMarket / StopMarket) with ReduceOnly. " +
          "Trailing SL replaces the stop order on each new peak. Spot is monitored in-process.";

    public ObservableCollection<KvRow> TpPreview { get; } = new();

    private void RebuildTpPreview()
    {
        TpPreview.Clear();
        if (Rule is not { } r) return;
        // Percent-based only: the desk has no mark-price feed to turn these into prices.
        TpPreview.Add(new KvRow { Label = "TP1", Value = r.TpEnabled ? "+" + r.TpPercent.ToString("0.##", Inv) + "%" : "off", Color = r.TpEnabled ? BotsDeskData.Green : BotsDeskData.Faint });
        TpPreview.Add(new KvRow { Label = "TP2", Value = r.PartialTp ? "+" + r.PartialTp2Percent.ToString("0.##", Inv) + "%" : "off", Color = r.PartialTp ? BotsDeskData.Green : BotsDeskData.Faint });
        TpPreview.Add(new KvRow { Label = "SL", Value = r.SlEnabled ? "−" + r.SlPercent.ToString("0.##", Inv) + "%" : "off", Color = r.SlEnabled ? BotsDeskData.Red : BotsDeskData.Faint });
        var rr = r.SlPercent > 0 ? (double)(r.TpPercent / r.SlPercent) : 0;
        TpPreview.Add(new KvRow { Label = "R multiple", Value = rr > 0 ? rr.ToString("0.00", Inv) + "R" : BotsDeskData.Dash, Color = BotsDeskData.Text });
    }

    // ── engine parameters (two-way onto the engine view-models) ──────────────
    public ObservableCollection<ParamRowViewModel> DetailParams { get; } = new();
    private bool _dirty;
    public string DirtyLabel => _dirty ? "applied — restart the engine to take effect" : "in sync with engine";
    public string DirtyColor => _dirty ? BotsDeskData.Amber : BotsDeskData.Faint;
    public void MarkDirty() { _dirty = true; RaiseDirty(); }
    private void RaiseDirty()
    {
        this.RaisePropertyChanged(nameof(DirtyLabel));
        this.RaisePropertyChanged(nameof(DirtyColor));
    }

    private void P(string label, string unit, string value, Action<string>? apply)
        => DetailParams.Add(Seeded(new ParamRowViewModel { Desk = this, Label = label, Unit = unit, IsText = true, Apply = apply }, value));

    private void PSel(string label, IEnumerable<string> options, string value, Action<string>? apply)
        => DetailParams.Add(Seeded(new ParamRowViewModel
        {
            Desk = this, Label = label, IsSelect = true, Options = options.ToList(), Apply = apply
        }, value));

    private static ParamRowViewModel Seeded(ParamRowViewModel row, string value) { row.Seed(value); return row; }

    private static readonly string[] Bools = { "true", "false" };

    private void RebuildParams(BotRowViewModel b)
    {
        DetailParams.Clear();

        switch (b.Id)
        {
            case IdGrid when Grid is { } g:
                P("SYMBOL", "", g.Symbol, v => { if (!string.IsNullOrWhiteSpace(v)) g.Symbol = v.ToUpperInvariant(); });
                P("LOWER PRICE", "USDT", g.LowerPrice.ToString("0.####", Inv), v => g.LowerPrice = Dec(v, g.LowerPrice));
                P("UPPER PRICE", "USDT", g.UpperPrice.ToString("0.####", Inv), v => g.UpperPrice = Dec(v, g.UpperPrice));
                P("GRID LEVELS", "", g.GridLevels.ToString(Inv), v => g.GridLevels = Int(v, g.GridLevels));
                P("QTY / GRID", "", g.QuantityPerGrid.ToString("0.########", Inv), v => g.QuantityPerGrid = Dec(v, g.QuantityPerGrid));
                PSel("MARKET MODE", g.AvailableMarketModes, g.SelectedMarketMode, v => g.SelectedMarketMode = v);
                PSel("MARGIN", g.AvailableMarginModes, g.SelectedMarginMode, v => g.SelectedMarginMode = v);
                P("LEVERAGE", "x", g.Leverage.ToString(Inv), v => g.Leverage = Int(v, g.Leverage));
                break;

            case IdDca when Dca is { } d:
                P("BUDGET / CYCLE", "USDT", d.TotalBudget.ToString("0.##", Inv), v => d.TotalBudget = Dec(v, d.TotalBudget));
                P("INTERVAL", "", d.IntervalValue.ToString(Inv), v => d.IntervalValue = Int(v, d.IntervalValue));
                PSel("PERIOD", d.IntervalTypes, d.SelectedIntervalType, v => d.SelectedIntervalType = v);
                PSel("EXCHANGE", d.AvailableExchanges, d.SelectedExchange, v => d.SelectedExchange = v);
                PSel("RUN ON START", Bools, d.ExecuteImmediately ? "true" : "false", v => d.ExecuteImmediately = v == "true");
                P("NEXT RUN", "", d.NextExecutionLabel, null);
                P("COUNTDOWN", "", d.CountdownLabel, null);
                break;

            case IdRule when Rule is { } r:
                P("SYMBOL", "", r.Symbol, v => r.Symbol = v.ToUpperInvariant());
                PSel("STRATEGY", r.AvailableStrategies, r.SelectedStrategy, v => r.SelectedStrategy = v);
                P("QUANTITY", "", r.Quantity.ToString("0.########", Inv), v => r.Quantity = Dec(v, r.Quantity));
                P("MAX RISK / TRADE", "$", r.MaxRiskPerTrade.ToString("0.##", Inv), v => r.MaxRiskPerTrade = Dec(v, r.MaxRiskPerTrade));
                PSel("EXCHANGE", r.AvailableExchanges, r.SelectedExchange, v => r.SelectedExchange = v);
                PSel("MARKET MODE", r.AvailableMarketModes, r.SelectedMarketMode, v => r.SelectedMarketMode = v);
                PSel("MARGIN", r.AvailableFuturesMarginModes, r.SelectedFuturesMarginMode, v => r.SelectedFuturesMarginMode = v);
                PSel("BIAS", r.AvailableFuturesBiasModes, r.SelectedFuturesBias, v => r.SelectedFuturesBias = v);
                P("LEVERAGE", "x", r.FuturesLeverage.ToString(Inv), v => r.FuturesLeverage = Int(v, r.FuturesLeverage));
                P("MA FAST / SLOW", "", r.MaFastPeriod + " / " + r.MaSlowPeriod, null);
                break;

            case IdAi when Trader is { } a:
                PSel("VENUE", a.AvailableVenues, a.SelectedVenue, v => a.SelectedVenue = v);
                PSel("EXCHANGE", a.AvailableExchanges, a.SelectedExchange, v => a.SelectedExchange = v);
                PSel("MARKET MODE", a.AvailableMarketModes, a.SelectedMarketMode, v => a.SelectedMarketMode = v);
                PSel("MARGIN", a.AvailableMarginModes, a.SelectedMarginMode, v => a.SelectedMarginMode = v);
                P("LEVERAGE", "x", a.Leverage.ToString(Inv), v => a.Leverage = Int(v, a.Leverage));
                P("TURN INTERVAL", "s", a.IntervalSeconds.ToString(Inv), v => a.IntervalSeconds = Int(v, a.IntervalSeconds));
                P("MAX ORDER", "$", a.MaxOrderUsd.ToString("0.##", Inv), v => a.MaxOrderUsd = Dec(v, a.MaxOrderUsd));
                P("MAX EXPOSURE", "$", a.MaxTotalExposureUsd.ToString("0.##", Inv), v => a.MaxTotalExposureUsd = Dec(v, a.MaxTotalExposureUsd));
                P("MAX POSITIONS", "", a.MaxOpenPositions.ToString(Inv), v => a.MaxOpenPositions = Int(v, a.MaxOpenPositions));
                P("MAX DAILY LOSS", "$", a.MaxDailyLossUsd.ToString("0.##", Inv), v => a.MaxDailyLossUsd = Dec(v, a.MaxDailyLossUsd));
                P("PAPER START", "$", a.VirtualStartUsd.ToString("0.##", Inv), v => a.VirtualStartUsd = Dec(v, a.VirtualStartUsd));
                P("DEX SLIPPAGE", "%", a.DexSlippagePercent.ToString("0.##", Inv), v => a.DexSlippagePercent = Dec(v, a.DexSlippagePercent));
                break;

            case IdAgent when AgentVm is { } ag:
                P("TURN INTERVAL", "s", ag.IntervalSeconds.ToString(Inv), v => ag.IntervalSeconds = Int(v, ag.IntervalSeconds));
                P("SESSION BUDGET", "$", ag.SessionBudgetUsd.ToString("0.##", Inv), v => ag.SessionBudgetUsd = Dec(v, ag.SessionBudgetUsd));
                P("MAX TRADES", "", ag.MaxTrades.ToString(Inv), v => ag.MaxTrades = Int(v, ag.MaxTrades));
                PSel("EXECUTION", Bools, ag.LiveEnabled ? "true" : "false", v => ag.LiveEnabled = v == "true");
                P("ALLOWLIST", "", ag.AllowlistText, v => ag.AllowlistText = v.ToUpperInvariant());
                break;

            case IdTrail when Trail is { } t:
                PSel("TYPE", t.TypeLabels, t.TypeLabels[t.SelectedTypeIndex],
                    v => t.SelectedTypeIndex = Math.Max(0, t.TypeLabels.ToList().IndexOf(v)));
                P("TRAIL DISTANCE", "%", t.PctDistance.ToString("0.##", Inv), v => t.PctDistance = Dec(v, t.PctDistance));
                P("ATR PERIOD", "", t.AtrPeriod.ToString(Inv), v => t.AtrPeriod = Int(v, t.AtrPeriod));
                P("ATR MULTIPLIER", "x", t.AtrMultiplier.ToString("0.##", Inv), v => t.AtrMultiplier = Dec(v, t.AtrMultiplier));
                P("CHANDELIER PERIOD", "", t.ChanPeriod.ToString(Inv), v => t.ChanPeriod = Int(v, t.ChanPeriod));
                P("CHANDELIER MULT", "x", t.ChanMultiplier.ToString("0.##", Inv), v => t.ChanMultiplier = Dec(v, t.ChanMultiplier));
                P("BREAK-EVEN TRIGGER", "%", t.BreakEvenTrigPct.ToString("0.##", Inv), v => t.BreakEvenTrigPct = Dec(v, t.BreakEvenTrigPct));
                P("BREAK-EVEN BUFFER", "%", t.BreakEvenBufferPct.ToString("0.##", Inv), v => t.BreakEvenBufferPct = Dec(v, t.BreakEvenBufferPct));
                P("SWING LOOKBACK", "", t.SwingLookback.ToString(Inv), v => t.SwingLookback = Int(v, t.SwingLookback));
                break;
        }

        _dirty = false;
        RaiseDirty();
    }

    // ── tabs ─────────────────────────────────────────────────────────────────
    public ObservableCollection<DetailTab> DetailTabs { get; } = new();
    public bool IsOverview => _detailTab == "overview";
    public bool IsParams => _detailTab == "params";
    public bool IsTpSl => _detailTab == "tpsl";
    public bool IsTrades => _detailTab == "trades";
    public bool IsLogs => _detailTab == "logs";
    public bool IsRisk => _detailTab == "risk";

    private void RebuildTabs()
    {
        DetailTabs.Clear();
        void T(string id, string label)
        {
            var active = _detailTab == id; var key = id;
            DetailTabs.Add(new DetailTab
            {
                Label = label, Border = active ? SemanticColor.Stroke : "transparent",
                Bg = active ? "#08131d" : "transparent", Fg = active ? BotsDeskData.Accent : BotsDeskData.Faint,
                Command = new RelayCommand(() => SetDetailTab(key))
            });
        }
        T("overview", "OVERVIEW"); T("params", "PARAMETERS"); T("tpsl", "TP / SL");
        T("trades", "FILLS"); T("logs", "LOGS"); T("risk", "RISK");
    }

    private void SetDetailTab(string tab)
    {
        _detailTab = tab;
        RebuildDetail();
    }

    // ── detail display collections / scalars ─────────────────────────────────
    public ObservableCollection<StatCell> DetailStats { get; } = new();
    public ObservableCollection<GridLevel> DetailLevels { get; } = new();
    public ObservableCollection<KvRow> DetailPosition { get; } = new();
    public ObservableCollection<TradeRow> DetailTrades { get; } = new();
    public ObservableCollection<LogRow> DetailLogs { get; } = new();
    public ObservableCollection<LogFilter> DetailLogFilters { get; } = new();

    public string DetailEngineLine { get; private set; } = "";
    public string GridTitle { get; private set; } = "";
    public string GridMeta { get; private set; } = "";
    public string PosMeta { get; private set; } = "";
    public string HedgeVenue { get; private set; } = BotsDeskData.Dash;
    public string AiSource { get; private set; } = "";
    public string AiNote { get; private set; } = "";
    public bool HasCoins { get; private set; }
    public string TradesMeta { get; private set; } = "";

    private BotRowViewModel? Expanded() => Bots.FirstOrDefault(b => b.Id == _expandedId);

    private string _logLevel = "all";
    private string? _paramsBotId;

    public void RebuildDetail()
    {
        RebuildTabs();
        RaiseDetailFlags();
        RefreshSelectionHeader();

        var b = Expanded();
        if (b is null)
        {
            _paramsBotId = null;
            DetailStats.Clear(); DetailLevels.Clear(); DetailPosition.Clear();
            DetailTrades.Clear(); DetailLogs.Clear(); DetailParams.Clear();
            TpPreview.Clear();
            RaiseDetailScalars();
            return;
        }

        DetailEngineLine = b.Type + " engine · " + (string.IsNullOrEmpty(b.Venue) ? BotsDeskData.Dash : b.Venue)
                           + " · " + (b.Mode == "live" && !PaperOnly ? "live gate open" : "paper guard on");

        BuildStats(b);
        BuildLadder(b);
        BuildPosition(b);

        HasCoins = b.Id == IdDca;
        if (HasCoins) SyncCoins();

        if (_paramsBotId != b.Id) { RebuildParams(b); _paramsBotId = b.Id; }
        SyncTpSl();
        RebuildTpPreview();
        BuildTradeRows(b);
        BuildLogRows(b);
        RebuildGuards();
        BuildDetailCommands(b);
        RaiseRiskLabels();
        RaiseDetailScalars();
    }

    // ── overview: performance stats ──────────────────────────────────────────
    private void BuildStats(BotRowViewModel b)
    {
        DetailStats.Clear();
        void S(string l, string v, string c) => DetailStats.Add(new StatCell { Label = l, Value = v, Color = c });

        S("PnL total", b.PnlTotalAbs, b.PnlTotalColor);
        S("PnL 24h", b.Pnl24Pct, b.Pnl24Color);
        S("Fills", b.Trades, BotsDeskData.Text);
        S("Winrate", b.Winrate, BotsDeskData.Text);
        S("Max DD 7d", b.MaxDdLabel, b.MaxDdUsd is { } dd && dd > 0 ? BotsDeskData.Red : BotsDeskData.Text3);

        switch (b.Id)
        {
            case IdGrid when Grid is { } g:
                S("Cycles closed", g.CyclesCompleted.ToString(Inv), BotsDeskData.Text);
                S("Session PnL", g.GridPnLLabel, g.GridPnLColor);
                S("Spacing", g.GridSpacing > 0 ? g.GridSpacing.ToString("N2", Inv) : BotsDeskData.Dash, BotsDeskData.Text3);
                S("Levels", g.GridLevels.ToString(Inv), BotsDeskData.Text3);
                break;
            case IdDca when Dca is { } d:
                S("Next run", d.NextExecutionLabel, BotsDeskData.Text);
                S("Countdown", d.CountdownLabel, BotsDeskData.Text);
                S("Executions", d.Executions.Count.ToString(Inv), BotsDeskData.Text3);
                S("Basket", d.WeightSumLabel, d.WeightSumColor);
                break;
            case IdRule when Rule is { } r:
                S("Strategy", r.StrategySummary, BotsDeskData.Text);
                S("Market", r.BotModeSummary, BotsDeskData.Text3);
                S("TP / SL", r.TpSlSummary, BotsDeskData.Text3);
                S("Max risk", "$" + r.MaxRiskPerTrade.ToString("0.##", Inv), BotsDeskData.Text3);
                break;
            case IdAi when Trader is { } a:
                S("Mode", a.ModeLabel, a.ModeBrush);
                S("Agent", a.StatusLabel, a.StatusBrush);
                S("Max order", "$" + a.MaxOrderUsd.ToString("0.##", Inv), BotsDeskData.Text3);
                S("Daily loss cap", "−$" + a.MaxDailyLossUsd.ToString("0.##", Inv), BotsDeskData.Red);
                break;
            case IdAgent when AgentVm is { } ag:
                S("Mode", ag.ModeBadge, ag.LiveEnabled ? BotsDeskData.Amber : BotsDeskData.Accent);
                S("Session budget", "$" + ag.SessionBudgetUsd.ToString("0.##", Inv), BotsDeskData.Text);
                S("Max trades", ag.MaxTrades.ToString(Inv), BotsDeskData.Text3);
                S("Turn interval", ag.IntervalSeconds + "s", BotsDeskData.Text3);
                break;
            case IdTrail when Trail is { } t:
                S("State", t.StatusLabel, t.IsArmed ? BotsDeskData.Green : BotsDeskData.Text3);
                S("Current stop", t.StopLabel, BotsDeskData.Text);
                S("Entry", string.IsNullOrEmpty(t.EntryLabel) ? BotsDeskData.Dash : t.EntryLabel, BotsDeskData.Text3);
                S("Type", t.TypeLabels[t.SelectedTypeIndex], BotsDeskData.Text3);
                break;
        }
    }

    // ── overview: ladder (real configuration, no simulated fill state) ───────
    private void BuildLadder(BotRowViewModel b)
    {
        DetailLevels.Clear();
        GridTitle = "";
        GridMeta = "";

        if (b.Id == IdGrid && Grid is { } g && g.UpperPrice > g.LowerPrice && g.GridLevels >= 2)
        {
            GridTitle = "GRID LEVELS";
            GridMeta = g.GridSummary;
            var step = (g.UpperPrice - g.LowerPrice) / g.GridLevels;
            int n = Math.Min(g.GridLevels + 1, 24);
            for (int i = 0; i < n; i++)
            {
                var price = g.LowerPrice + step * i;
                DetailLevels.Add(new GridLevel
                {
                    Price = price.ToString("N2", Inv),
                    Ratio = n == 1 ? 1 : (i + 1) / (double)n,
                    Color = BotsDeskData.Accent,
                    Opacity = .55,
                    Tag = "",
                    TagColor = BotsDeskData.Faint
                });
            }
            return;
        }

        if (b.Id == IdDca && Dca is { } d && d.Coins.Count > 0)
        {
            GridTitle = "DCA BASKET";
            GridMeta = d.WeightSumLabel;
            foreach (var c in d.Coins)
                DetailLevels.Add(new GridLevel
                {
                    Price = c.Symbol,
                    Ratio = Math.Clamp(c.WeightPercent / 100.0, 0, 1),
                    Color = BotsDeskData.Accent,
                    Opacity = .75,
                    Tag = c.WeightPercent + "%",
                    TagColor = BotsDeskData.Text3
                });
        }
    }

    // ── overview: open exchange position on this engine's symbol ─────────────
    private string? EngineSymbol(string id) => id switch
    {
        IdGrid => Grid?.Symbol,
        IdRule => Rule?.Symbol,
        _ => null
    };

    private UnifiedPositionRowVM? _detailPosition;

    private void BuildPosition(BotRowViewModel b)
    {
        DetailPosition.Clear();
        _detailPosition = null;

        var sym = EngineSymbol(b.Id);
        if (sym is null || _host?.AllPositionsVM is not { } positions)
        {
            PosMeta = "no symbol-level position feed for this engine";
            return;
        }

        _detailPosition = positions.Rows
            .FirstOrDefault(r => string.Equals(r.Symbol, sym, StringComparison.OrdinalIgnoreCase));

        if (_detailPosition is null)
        {
            PosMeta = "no open position on " + sym;
            return;
        }

        var p = _detailPosition;
        PosMeta = p.Exchange + " · exchange position";
        void K(string l, string v, string c) => DetailPosition.Add(new KvRow { Label = l, Value = v, Color = c });
        K("Side", p.Side.ToUpperInvariant(), p.SideBrush);
        K("Size", p.SizeLabel, BotsDeskData.Text);
        K("Entry", p.EntryPriceLabel, BotsDeskData.Text);
        K("Mark", p.MarkPriceLabel, BotsDeskData.Text);
        K("uPnL", p.PnlLabel, p.PnlBrush);
        K("Liq. price", p.LiqLabel, BotsDeskData.Amber);
    }

    // ── fills: realized trades / DCA executions ──────────────────────────────
    private void BuildTradeRows(BotRowViewModel b)
    {
        DetailTrades.Clear();

        if (b.Id == IdDca && Dca is { } d)
        {
            foreach (var e in d.Executions.Take(50))
                DetailTrades.Add(new TradeRow
                {
                    Time = e.TimeLabel,
                    Symbol = e.Symbol,
                    Side = "BUY",
                    SideColor = BotsDeskData.Green,
                    Price = e.PriceLabel,
                    Qty = e.QtyLabel,
                    Total = e.TotalLabel,
                    Pnl = BotsDeskData.Dash,     // DCA accumulates — no per-execution realized PnL
                    PnlColor = BotsDeskData.Text3,
                    Route = e.StatusLabel
                });
            TradesMeta = d.Executions.Count + " executions recorded";
            return;
        }

        var trades = EngineTrades(b.Id);
        foreach (var t in trades)
        {
            var pnl = (double)t.PnlUsd;
            var isLong = t.Direction.ToString().Equals("Long", StringComparison.OrdinalIgnoreCase);
            DetailTrades.Add(new TradeRow
            {
                Time = t.ClosedAtUtc.ToLocalTime().ToString("dd.MM HH:mm"),
                Symbol = t.Symbol,
                Side = isLong ? "LONG" : "SHORT",
                SideColor = isLong ? BotsDeskData.Green : BotsDeskData.Red,
                Price = t.ExitPrice > 0 ? t.ExitPrice.ToString("N4", Inv) : BotsDeskData.Dash,
                Qty = t.Quantity > 0 ? t.Quantity.ToString("N6", Inv) : BotsDeskData.Dash,
                Total = t.ExitPrice > 0 && t.Quantity > 0 ? (t.ExitPrice * t.Quantity).ToString("N2", Inv) : BotsDeskData.Dash,
                Pnl = Money(pnl, true),
                PnlColor = Sgn(pnl),
                Route = string.IsNullOrWhiteSpace(t.ExitReason) ? "" : t.ExitReason
            });
        }
        TradesMeta = b.Fills is { } n
            ? n.ToString("N0", Inv) + " closed trades · showing " + trades.Count
            : "this engine records no closed trades";
    }

    // ── logs: the engine's own buffer ────────────────────────────────────────
    private void BuildLogRows(BotRowViewModel b)
    {
        var all = ParseLog(EngineLog(b.Id));
        DetailLogs.Clear();
        foreach (var l in _logLevel == "all" ? all : all.Where(l => l.Level == _logLevel)) DetailLogs.Add(l);

        DetailLogFilters.Clear();
        foreach (var lv in new[] { "all", "FILL", "WARN", "ERR", "AI" })
        {
            var key = lv; var active = _logLevel == lv;
            DetailLogFilters.Add(new LogFilter
            {
                Label = lv == "all" ? "ALL" : lv,
                Border = active ? SemanticColor.Stroke : "#0d1b27", Bg = active ? "#08131d" : "transparent",
                Fg = active ? BotsDeskData.Accent : BotsDeskData.Faint,
                Command = new RelayCommand(() => { _logLevel = key; RebuildDetail(); })
            });
        }
    }

    private void RaiseDetailFlags()
    {
        foreach (var n in new[] { nameof(IsOverview), nameof(IsParams), nameof(IsTpSl), nameof(IsTrades), nameof(IsLogs), nameof(IsRisk) })
            this.RaisePropertyChanged(n);
    }

    private void RaiseDetailScalars()
    {
        foreach (var n in new[]
        {
            nameof(DetailEngineLine), nameof(GridTitle), nameof(GridMeta), nameof(PosMeta), nameof(HedgeVenue),
            nameof(AiSource), nameof(AiNote), nameof(HasCoins), nameof(TradesMeta), nameof(TpSummary), nameof(TpNote),
            nameof(WeightSum), nameof(WeightColor), nameof(DirtyLabel), nameof(DirtyColor), nameof(NewCoin), nameof(Allowlist)
        })
            this.RaisePropertyChanged(n);
    }

    // ── detail commands (rebuilt per expanded engine) ────────────────────────
    public ICommand? ClosePositionCommand { get; private set; }
    public ICommand? HedgeCommand { get; private set; }
    public ICommand? ApplyAiCommand { get; private set; }
    public ICommand? AskAiCommand { get; private set; }
    public ICommand? SaveParamsCommand { get; private set; }
    public ICommand? AiSuggestParamsCommand { get; private set; }
    public ICommand? DryRunCommand { get; private set; }
    public ICommand? ResetParamsCommand { get; private set; }
    public ICommand? SaveTpSlCommand { get; private set; }
    public ICommand? AiTpSlCommand { get; private set; }
    public ICommand? ExportCsvCommand { get; private set; }
    public ICommand? TaxReportCommand { get; private set; }
    public ICommand? JournalCommand { get; private set; }
    public ICommand? CopyLogsCommand { get; private set; }
    public ICommand AddCoinCommand { get; private set; } = null!;

    private void BuildDetailCommands(BotRowViewModel b)
    {
        // AI parameter review — only engines with a real AI parameter service.
        AiSource = "";
        AiNote = "";
        ApplyAiCommand = null;
        AiSuggestParamsCommand = null;

        if (b.Id == IdGrid && Grid is { } g)
        {
            AiSource = string.IsNullOrWhiteSpace(g.AiParamsSource) ? "BotParameterAi" : g.AiParamsSource;
            AiNote = g.AiParamsRunning ? "Analysing the book…" : g.AiParamsRationale;
            ApplyAiCommand = new RelayCommand(() => { Run(g.SuggestParamsCommand); Toast("BotParameterAi is sizing the grid from live book data", "ai"); });
            AiSuggestParamsCommand = ApplyAiCommand;
        }
        else if (b.Id == IdRule && Rule is { } r)
        {
            AiSource = "DynamicTpSlAi";
            AiNote = r.TpSlSuggestRunning ? "Analysing volatility…" : r.TpSlSuggestNote;
            ApplyAiCommand = new RelayCommand(() => { Run(r.SuggestTpSlCommand); Toast("DynamicTpSlAi is sizing TP/SL from volatility", "ai"); });
            AiSuggestParamsCommand = ApplyAiCommand;
        }

        // No reasoning-trace viewer exists for these engines.
        AskAiCommand = null;

        ClosePositionCommand = _detailPosition is { } pos && _host?.AllPositionsVM is { } ap
            ? new RelayCommand(() => AskConfirm(new ConfirmConfig
            {
                Kind = "closePos", BotId = b.Id, Accent = "#3a1620", IconColor = BotsDeskData.Red, Icon = "⛔",
                Title = "Close position · " + pos.Symbol,
                // Этот путь СОЗНАТЕЛЬНО не закрыт заслоном живой торговли: закрытие снижает риск,
                // и отнимать аварийный выход опаснее, чем оставить его открытым. Но молчать о том,
                // что при выключенной живой торговле отсюда всё равно уйдёт настоящий ордер, —
                // нельзя: человек читает бейдж PAPER ONLY и делает вывод обо всём экране.
                Body = "Market-closes the open " + pos.Side.ToLowerInvariant() + " on " + pos.Exchange
                       + ". The engine keeps running."
                       + (Wallet is { } w && !w.TryApproveLiveExecution("close position", out _)
                           ? "\n\n⚠ Это НАСТОЯЩИЙ ордер на бирже вашим ключом — закрытие позиции работает даже при выключенной живой торговле."
                           : ""),
                Cta = "CLOSE AT MARKET", BtnBg = "#14060a", BtnFg = BotsDeskData.Red, BtnBorder = BotsDeskData.Red
            }))
            : null;

        // No hedge router is wired into this desk.
        HedgeCommand = null;
        HedgeVenue = BotsDeskData.Dash;

        SaveParamsCommand = new RelayCommand(() => _ = RestartEngineAsync(b));
        DryRunCommand = null;                       // no dry-run/backtest runner is wired here
        ResetParamsCommand = new RelayCommand(() =>
        {
            RebuildParams(b);
            Toast("Parameters re-read from the engine", "info");
        });

        SaveTpSlCommand = Rule is null ? null : new RelayCommand(() =>
        {
            SyncTpSl();
            Toast("TP/SL stored on the rule bot · applied on its next start", "ok");
        });
        AiTpSlCommand = Rule is null ? null : new RelayCommand(() =>
        {
            Run(Rule.SuggestTpSlCommand);
            Toast("DynamicTpSlAi requested", "ai");
        });

        ExportCsvCommand = _host?.PnlDashboardVM is { } pnl
            ? new RelayCommand(() => { Run(pnl.ExportCsvCommand); Toast("Realized-trade CSV exported", "ok"); })
            : null;
        TaxReportCommand = null;                    // the FIFO report lives on its own desk
        JournalCommand = null;                      // no journal-coach command is exposed to this desk
        CopyLogsCommand = null;                     // clipboard access is not available from this view-model

        foreach (var n in new[] { nameof(ClosePositionCommand), nameof(HedgeCommand), nameof(ApplyAiCommand), nameof(AskAiCommand),
            nameof(SaveParamsCommand), nameof(AiSuggestParamsCommand), nameof(DryRunCommand), nameof(ResetParamsCommand),
            nameof(SaveTpSlCommand), nameof(AiTpSlCommand), nameof(ExportCsvCommand), nameof(TaxReportCommand),
            nameof(JournalCommand), nameof(CopyLogsCommand) })
            this.RaisePropertyChanged(n);
    }

    /// <summary>Parameter edits are already on the engine — a restart makes a running engine pick them up.</summary>
    /// <summary>
    /// Перезапуск движка под новые параметры.
    ///
    /// Остановку ОБЯЗАТЕЛЬНО ждать. Раньше здесь стояли два синхронных вызова подряд, а движки
    /// снимают флаг «работаю» только в конце своей асинхронной остановки: GridBotViewModel.StopAsync
    /// сначала отменяет на бирже все выставленные лимитки и лишь потом ставит IsRunning = false.
    /// То есть StartEngine вызывался, когда CanStart ещё false, молча возвращал false — и грид
    /// оставался остановленным: вся сетка снята с биржи, набранная позиция без встречных ордеров,
    /// а пользователю показывалось «перезапущен с новыми параметрами». Он нажал «сохранить
    /// параметры», а получил тихую ликвидацию сетки.
    /// </summary>
    private async Task RestartEngineAsync(BotRowViewModel b)
    {
        if (!CanStop(b.Id)) { Toast("Parameters stored · " + b.Name + " will use them on start", "ok"); _dirty = false; RaiseDirty(); return; }

        await StopEngineAsync(b.Id);
        var restarted = StartEngine(b.Id);

        _dirty = false;
        RaiseDirty();
        AfterEngineAction(b.Id);
        Toast(restarted
                ? b.Name + " restarted with the new parameters"
                : b.Name + " остановлен, но не запустился обратно — параметры сохранены, запустите вручную",
            restarted ? "ok" : "warn");
    }

    private void InitDetail()
    {
        AddCoinCommand = new RelayCommand(AddCoin);
        RebuildGuards();
    }
}
