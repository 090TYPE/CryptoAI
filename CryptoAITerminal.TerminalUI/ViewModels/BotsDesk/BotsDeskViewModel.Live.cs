using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

/// <summary>
/// Live wiring for the Bots desk: it holds no data of its own, it mirrors the six
/// bot engines the shell owns. There is no bot registry in the app, so the row
/// list is synthesised — exactly one row per engine singleton — and refreshed
/// from the engine view-models on a timer.
/// </summary>
public partial class BotsDeskViewModel
{
    // engine row ids (stable, one per engine singleton)
    private const string IdGrid = "grid", IdDca = "dca", IdRule = "rule",
                         IdAi = "ai", IdAgent = "agent", IdTrail = "trail";

    private MainWindowViewModel? _host;
    private DispatcherTimer? _liveTimer;
    private string _rowSignature = "";

    /// <summary>Reader over the shared realized-trade store (%AppData%\CryptoAITerminal\pnl-history.json).</summary>
    private readonly PnlDashboardService _pnlStore = new();
    private DateTime _pnlLoadedAt = DateTime.MinValue;
    private IReadOnlyList<TradeRecord> _records = Array.Empty<TradeRecord>();
    private Dictionary<string, SourceRow> _byBotTotal = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SourceRow> _byBot24 = new(StringComparer.OrdinalIgnoreCase);

    // EQUITY 7D / MAX DD are derived from the same store; they are recomputed only when the
    // store snapshot actually changed (at most every 15s), never on every one-second tick.
    private const int SparkDays = 7;
    private const int SparkPoints = 32;
    private int _recordsVersion;
    private int _seriesVersion = -1;

    public bool IsAttached => _host is not null;

    // ── engine accessors ────────────────────────────────────────────────────
    private GridBotViewModel? Grid => _host?.GridBotVM;
    private DcaBotViewModel? Dca => _host?.DcaBotVM;
    private AIBotViewModel? Rule => _host?.AIBotVM;
    private AiTraderViewModel? Trader => _host?.AiTraderVM;
    private AutonomousAgentViewModel? AgentVm => _host?.AutonomousVM;
    private AdvancedTrailingStopViewModel? Trail => _host?.AdvancedTrailingVM;
    private WalletWorkspaceViewModel? Wallet => _host?.WalletVM;

    /// <summary>Bot label used by <see cref="PnlDashboardService"/> records, or null when the engine records nothing.</summary>
    private static string? PnlKey(string id) => id switch
    {
        IdGrid => "Grid Bot",
        IdDca => "DCA Bot",
        IdRule => "AI Bot",
        _ => null            // ai trader / autonomous agent / trailing stop do not write trade records
    };

    // ═════════════════════════ attach ═══════════════════════════════════════
    /// <summary>
    /// Hands the desk its live sources. Called once from the shell; before it runs
    /// the desk is empty (it never shows sample rows).
    /// </summary>
    public void Attach(MainWindowViewModel host)
    {
        if (host is null || _host is not null) return;
        _host = host;

        BuildEngineRows();
        ReloadPnl(force: true);
        SyncRows();
        RefreshSeries(force: true);
        SeedTpSl();
        SyncCoins();
        RebuildGuards();
        RebuildAgentCaps();
        RebuildVenueOptions();
        SyncRail();

        _liveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _liveTimer.Tick += (_, _) => Tick();
        _liveTimer.Start();

        RebuildVisible();
        RebuildDetail();
        Recompute();
    }

    private void BuildEngineRows()
    {
        Bots.Clear();
        if (_host is null) return;
        foreach (var t in BotsDeskData.Templates)
            Bots.Add(new BotRowViewModel { Desk = this, Id = t.id, Type = t.type, Name = t.name });
    }

    // ═════════════════════════ periodic sync ════════════════════════════════
    private void Tick()
    {
        if (_host is null) return;
        // Nothing on this desk is observable from another section, yet every second the hidden
        // desk still re-read the trade store, re-projected six rows and raised the header.
        // The timer keeps running so the first tick after navigating back (≤1 s) refills
        // everything — ReloadPnl's own staleness window has long expired by then.
        if (_host.IsBotsSectionVisible == false) return;

        RefreshPositionsIfStale();
        ReloadPnl(force: false);
        SyncRows();
        RefreshSeries(force: false);
        RefreshAllRows();
        RaiseLiveHeader();
        SyncRail();

        var sig = RowSignature();
        if (sig == _rowSignature) return;
        _rowSignature = sig;

        RebuildVisible();
        if (_expandedId is not null) RebuildDetail();
        RebuildTypeChips();
        RebuildTicker();
        RebuildBullets();
    }

    /// <summary>
    /// Open-position exposure feeds the AI trader's budget-used bar, but nothing else
    /// refreshes the positions table on a schedule. Pull it once a minute while the desk
    /// is on screen — one venue read per minute stays well inside exchange rate limits.
    /// </summary>
    private void RefreshPositionsIfStale()
    {
        if (_host is null || _host.IsBotsSectionVisible == false) return;
        if (_positionsRefreshing) return;
        if (DateTime.UtcNow - _lastPositionsPull < TimeSpan.FromMinutes(1)) return;

        var positions = _host.AllPositionsVM;
        if (positions is null) return;

        _lastPositionsPull = DateTime.UtcNow;
        _positionsRefreshing = true;
        _ = Task.Run(async () =>
        {
            try { await positions.RefreshAsync().ConfigureAwait(false); }
            catch { /* a venue read failure must never disturb the desk */ }
            finally { _positionsRefreshing = false; }
        });
    }

    private DateTime _lastPositionsPull = DateTime.MinValue;
    private bool _positionsRefreshing;

    /// <summary>Structural state only — metric values refresh every tick without a rebuild.</summary>
    private string RowSignature()
        => string.Join("|", Bots.Select(b => b.Id + b.Status + b.Mode + b.Market + b.Venue))
           + "|" + _records.Count
           + "|" + (_host?.BriefingVM?.GeneratedAt ?? "");

    /// <summary>
    /// Re-reads the realized-trade store (same file the P&amp;L dashboard uses). The timer path
    /// does the file read and the two groupings on a worker thread and publishes the finished
    /// snapshot back on the UI thread; only the callers that need the data immediately
    /// (<see cref="Attach"/>, the desk REFRESH button) pass <paramref name="force"/> and pay
    /// the synchronous read.
    /// </summary>
    private void ReloadPnl(bool force)
    {
        if (!force && DateTime.UtcNow - _pnlLoadedAt < TimeSpan.FromSeconds(15)) return;
        if (!force && _pnlReloading) return;
        _pnlLoadedAt = DateTime.UtcNow;

        if (force)
        {
            try { ApplyPnlSnapshot(ReadPnlSnapshot()); }
            catch { /* store missing or being rewritten — keep the previous snapshot */ }
            return;
        }

        _pnlReloading = true;
        _ = Task.Run(() =>
        {
            PnlSnapshot snapshot;
            try
            {
                snapshot = ReadPnlSnapshot();
            }
            catch
            {
                _pnlReloading = false;   // store missing or being rewritten — keep the previous snapshot
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                ApplyPnlSnapshot(snapshot);
                _pnlReloading = false;
            });
        });
    }

    private bool _pnlReloading;

    private readonly record struct PnlSnapshot(
        IReadOnlyList<TradeRecord> Records,
        Dictionary<string, SourceRow> ByBotTotal,
        Dictionary<string, SourceRow> ByBot24);

    private PnlSnapshot ReadPnlSnapshot()
    {
        _pnlStore.Load();                      // de-duplicating append, safe to repeat
        var records = _pnlStore.GetAll();
        var cutoff = DateTime.UtcNow.AddHours(-24);
        return new PnlSnapshot(
            records,
            _pnlStore.ComputeByBot(records).ToDictionary(r => r.Label, StringComparer.OrdinalIgnoreCase),
            _pnlStore.ComputeByBot(records.Where(r => r.ClosedAtUtc >= cutoff).ToList())
                     .ToDictionary(r => r.Label, StringComparer.OrdinalIgnoreCase));
    }

    private void ApplyPnlSnapshot(PnlSnapshot snapshot)
    {
        _records = snapshot.Records;
        _byBotTotal = snapshot.ByBotTotal;
        _byBot24 = snapshot.ByBot24;
        _recordsVersion++;   // invalidates the cached equity series / drawdown
    }

    /// <summary>Manual resync (desk REFRESH button) — also nudges the P&amp;L dashboard.</summary>
    private void ResyncNow()
    {
        if (_host is null) { Toast("Desk is not attached to the trading shell", "warn"); return; }
        ReloadPnl(force: true);
        try { _host.PnlDashboardVM?.Refresh(); } catch { /* dashboard owns its own errors */ }
        SyncRows();
        RefreshSeries(force: true);
        RefreshAllRows();
        RebuildVisible();
        RebuildDetail();
        Recompute();
        Toast(Bots.Count(b => b.Status == "running") + "/" + Bots.Count + " engines live · state re-read", "info");
    }

    // ═════════════════════════ row state from engines ═══════════════════════
    private void SyncRows()
    {
        if (_host is null) return;
        foreach (var row in Bots) SyncRow(row);
    }

    private void SyncRow(BotRowViewModel row)
    {
        switch (row.Id)
        {
            case IdGrid when Grid is { } g:
                row.Venue = "Binance " + (g.IsFuturesMode ? "Futures" : "Spot");
                row.Market = g.Symbol + (g.IsFuturesMode ? " · " + g.Leverage + "x" : "");
                row.Status = !g.IsRunning ? "stopped" : g.IsPaused ? "paused" : "running";
                row.Mode = "live";                       // the grid engine always routes to the exchange gateway
                row.BudgetUsd = null;                    // no budget concept on the grid engine
                row.BudgetUsedUsd = null;
                break;

            case IdDca when Dca is { } d:
                row.Venue = d.SelectedExchange + " Spot";
                row.Market = d.Coins.Count == 0 ? "" : string.Join(" · ", d.Coins.Take(3).Select(c => c.Symbol));
                row.Status = d.IsRunning ? "running" : "stopped";
                row.Mode = "live";
                row.BudgetUsd = (double)d.TotalBudget;
                row.BudgetUsedUsd = DcaDeployedUsd(d);   // executed buys of the latest cycle
                break;

            case IdRule when Rule is { } r:
                row.Venue = r.SelectedExchange + " " + (r.IsFuturesMode ? "Futures" : "Spot");
                row.Market = r.Symbol + (r.IsFuturesMode ? " · " + r.FuturesLeverage + "x" : "");
                row.Status = r.IsRunning ? "running" : "stopped";
                row.Mode = "live";
                row.BudgetUsd = (double)r.MaxRiskPerTrade;
                row.BudgetUsedUsd = null;
                break;

            case IdAi when Trader is { } a:
                row.Venue = a.IsDexVenue ? "DEX" : a.SelectedExchange + " " + (a.IsFuturesMode ? "Futures" : "Spot");
                row.Market = a.IsDexVenue ? "on-chain swaps" : "agent-picked";
                row.Status = a.IsRunning ? "running" : "stopped";
                row.Mode = a.LiveEnabled ? "live" : "paper";
                row.BudgetUsd = (double)a.MaxTotalExposureUsd;
                // MaxTotalExposureUsd caps total open exposure; nothing in the app attributes a
                // position to an engine, so this is the wallet-wide open notional the cap applies to.
                row.BudgetUsedUsd = OpenExposureUsd();
                break;

            case IdAgent when AgentVm is { } ag:
                row.Venue = "app actions";
                row.Market = string.IsNullOrWhiteSpace(ag.AllowlistText) ? "no allowlist" : ag.AllowlistText.Trim();
                row.Status = ag.IsRunning ? "running" : "stopped";
                row.Mode = ag.LiveEnabled ? "live" : "paper";
                row.BudgetUsd = (double)ag.SessionBudgetUsd;
                // Real session spend straight off the agent's guardrails.
                row.BudgetUsedUsd = (double)ag.SessionSpentUsd;
                break;

            case IdTrail when Trail is { } t:
                row.Venue = "in-process";
                row.Market = t.TypeLabels[t.SelectedTypeIndex];
                row.Status = t.IsArmed ? "running" : "stopped";
                row.Mode = "live";
                row.BudgetUsd = null;
                row.BudgetUsedUsd = null;
                break;
        }

        // uptime: measured by the desk from the observed start (engines expose no start stamp)
        if (row.Status == "stopped") row.RunningSince = null;
        else row.RunningSince ??= DateTime.Now;

        // realized PnL / fills / win-rate — only for engines that record trades
        var key = PnlKey(row.Id);
        if (key is not null && _byBotTotal.TryGetValue(key, out var total))
        {
            row.PnlTotalUsd = (double)total.PnlUsd;
            row.Fills = total.Trades;
            row.WinRatePct = total.Trades == 0 ? null : (double)total.WinRate;
        }
        else
        {
            row.PnlTotalUsd = null;
            row.Fills = null;
            row.WinRatePct = null;
        }

        if (key is not null && _byBot24.TryGetValue(key, out var day))
        {
            row.Pnl24Usd = (double)day.PnlUsd;
            row.Fills24 = day.Trades;
        }
        else
        {
            row.Pnl24Usd = null;
            row.Fills24 = null;
        }
    }

    // ═════════════════════════ equity series / drawdown ═════════════════════
    /// <summary>
    /// Recomputes the per-engine equity sparkline and max drawdown from the realized-trade
    /// store: the cumulative P&amp;L curve of the last <see cref="SparkDays"/> days resampled to
    /// <see cref="SparkPoints"/> samples, and the deepest fall from that curve's rolling peak.
    /// Skipped unless the store snapshot changed, so a one-second tick costs nothing.
    /// Engines that record no trades keep an empty sparkline and a null drawdown ("—").
    /// </summary>
    private void RefreshSeries(bool force)
    {
        if (!force && _seriesVersion == _recordsVersion) return;
        _seriesVersion = _recordsVersion;

        var to = DateTime.UtcNow;
        var from = to.AddDays(-SparkDays);

        foreach (var row in Bots)
        {
            var key = PnlKey(row.Id);
            if (key is null)
            {
                row.SetSpark(null);
                row.MaxDdUsd = null;
                row.MaxDdPct = null;
                continue;
            }

            row.SetSpark(_pnlStore.ComputeEquitySeries(_records, key, from, to, SparkPoints));

            var m = _pnlStore.ComputeBotMetrics(_records, key, from, to);
            row.MaxDdUsd = m is { } v ? (double?)v.MaxDrawdownUsd : null;
            row.MaxDdPct = m is { } v2 ? (double?)v2.MaxDrawdownPct : null;
        }
    }

    // ═════════════════════════ budget deployed ══════════════════════════════
    private int _dcaExecCount = -1;
    private double? _dcaDeployed;

    /// <summary>
    /// USD the DCA engine actually bought on its most recent cycle — <see cref="DcaBotViewModel.TotalBudget"/>
    /// is a per-cycle budget, so only the newest execution batch counts. The engine's history rows
    /// expose formatted text only, so the printed total is parsed back; rows it skipped are ignored.
    /// Null when it has never executed a buy. Recomputed only when the history grew.
    /// </summary>
    private double? DcaDeployedUsd(DcaBotViewModel d)
    {
        if (d.Executions.Count == 0) { _dcaExecCount = 0; _dcaDeployed = null; return null; }
        if (d.Executions.Count == _dcaExecCount) return _dcaDeployed;
        _dcaExecCount = d.Executions.Count;

        // Newest first (both live inserts and the loaded history are ordered that way);
        // one cycle shares one timestamp label.
        var cycle = d.Executions[0].TimeLabel;
        double sum = 0;
        bool any = false;
        foreach (var e in d.Executions)
        {
            if (!string.Equals(e.TimeLabel, cycle, StringComparison.Ordinal)) break;
            if (!string.Equals(e.StatusLabel, "Executed", StringComparison.Ordinal)) continue;
            if (double.TryParse(e.TotalLabel, NumberStyles.Any, Inv, out var v)) { sum += v; any = true; }
        }

        _dcaDeployed = any ? (double?)sum : null;
        return _dcaDeployed;
    }

    /// <summary>
    /// Notional of every open exchange position — the same figure the unified positions view
    /// totals (size × entry). Null until that view has fetched once: an empty table before the
    /// first refresh is not the same measurement as no exposure.
    /// </summary>
    private double? OpenExposureUsd()
    {
        if (_host?.AllPositionsVM is not { } p) return null;
        if (p.Rows.Count == 0 && p.StatusLabel.Contains("No data yet", StringComparison.Ordinal)) return null;
        return (double)p.Rows.Sum(r => r.Size * r.EntryPrice);
    }

    // ═════════════════════════ footer health ════════════════════════════════
    /// <summary>
    /// Age of the freshest market-data update the shell holds — the same
    /// <c>CexMarketItemViewModel.LastUpdated</c> stamp the top bar's connection state is driven
    /// from. "—" while no market has ever ticked.
    /// </summary>
    private string FeedAgeLabel()
    {
        if (_host is null) return BotsDeskData.Dash;

        DateTime newest = default;
        foreach (var m in _host.MarketFeedVM.Markets)
            if (m.LastUpdated > newest) newest = m.LastUpdated;
        if (newest == default) return BotsDeskData.Dash;

        var age = DateTime.Now - newest;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalSeconds < 1
            ? "tick " + (int)age.TotalMilliseconds + "ms"
            : "tick " + age.TotalSeconds.ToString("0.0", Inv) + "s";
    }

    /// <summary>
    /// Orders actually broadcast in the last minute once an execution path feeds
    /// <see cref="GatewayHealthService.RecordOrderSent"/>. The app has no single order-send point
    /// this desk owns, so until then it reports the only order-flow it can measure itself —
    /// trades that closed into the realized-trade store in the last minute — and says so.
    /// </summary>
    private string OrderRateLabel()
    {
        if (GatewayHealthService.Instance.OrdersLastMinute is { } sent)
            return sent.ToString(Inv);

        var since = DateTime.UtcNow.AddMinutes(-1);
        return _records.Count(r => r.ClosedAtUtc >= since) + " closed fills";
    }

    // ═════════════════════════ engine control ═══════════════════════════════
    private static void Run(ReactiveCommand<Unit, Unit>? cmd)
    {
        if (cmd is null) return;
        try { cmd.Execute().Subscribe(_ => { }, _ => { }); }
        catch { /* command refused (CanExecute) — the caller already gated on the engine flags */ }
    }

    internal bool CanStart(string id) => id switch
    {
        IdGrid => Grid?.CanStart ?? false,
        IdDca => Dca?.CanStart ?? false,
        IdRule => Rule is { IsRunning: false },
        IdAi => Trader is { IsRunning: false },
        IdAgent => AgentVm is { IsRunning: false },
        IdTrail => Trail is { IsArmed: false },
        _ => false
    };

    internal bool CanStop(string id) => id switch
    {
        IdGrid => Grid?.CanStop ?? false,
        IdDca => Dca?.CanStop ?? false,
        IdRule => Rule is { IsRunning: true },
        IdAi => Trader is { IsRunning: true },
        IdAgent => AgentVm is { IsRunning: true },
        IdTrail => Trail is { IsArmed: true },
        _ => false
    };

    /// <summary>Only the grid engine implements pause/resume.</summary>
    internal bool CanPause(string id) => id == IdGrid && (Grid?.CanPause ?? false);
    internal bool CanResume(string id) => id == IdGrid && (Grid?.CanResume ?? false);

    /// <summary>Manual single turn — only the AI trader and the DCA engine support it.</summary>
    internal bool CanRunOnce(string id) => id switch
    {
        IdAi => Trader is not null,
        IdDca => Dca is { IsRunning: true },
        _ => false
    };

    private bool StartEngine(string id)
    {
        if (!CanStart(id)) return false;
        switch (id)
        {
            case IdGrid: Run(Grid?.StartCommand); return true;
            case IdDca: Run(Dca?.StartCommand); return true;
            case IdRule: Run(Rule?.StartBotCommand); return true;
            case IdAi: Run(Trader?.StartLoopCommand); return true;
            case IdAgent: Run(AgentVm?.ArmCommand); return true;
            case IdTrail: Run(Trail?.ToggleArmCommand); return true;
        }
        return false;
    }

    private bool StopEngine(string id)
    {
        if (!CanStop(id)) return false;
        switch (id)
        {
            case IdGrid: Run(Grid?.StopCommand); return true;
            case IdDca: Run(Dca?.StopCommand); return true;
            case IdRule: Rule?.StopBot(); return true;
            case IdAi: Run(Trader?.StopCommand); return true;
            case IdAgent: Run(AgentVm?.StopCommand); return true;
            case IdTrail: Run(Trail?.ToggleArmCommand); return true;
        }
        return false;
    }

    private bool PauseEngine(string id)
    {
        if (CanResume(id)) { Run(Grid?.ResumeCommand); return true; }
        if (!CanPause(id)) return false;
        Run(Grid?.PauseCommand);
        return true;
    }

    /// <summary>Hard stop. Only the AI trader has a dedicated kill-switch; the rest stop.</summary>
    private bool KillEngine(string id)
    {
        if (id == IdAi && Trader is not null) { Run(Trader.KillCommand); return true; }
        return StopEngine(id);
    }

    private bool RunOnceEngine(string id)
    {
        if (!CanRunOnce(id)) return false;
        if (id == IdAi) { Run(Trader?.RunOnceCommand); return true; }
        if (id == IdDca) { Run(Dca?.RunNowCommand); return true; }
        return false;
    }

    // ═════════════════════════ engine logs ══════════════════════════════════
    /// <summary>The raw log buffer of an engine, or null when it keeps none.</summary>
    internal string? EngineLog(string id) => id switch
    {
        IdGrid => Grid?.BotLog,
        IdDca => Dca?.BotLog,
        IdRule => Rule?.BotLog,
        IdAi => Trader?.AgentLog,
        IdAgent => AgentVm?.StatusLog,
        IdTrail => null,                 // the trailing engine keeps a status label, not a log
        _ => null
    };

    /// <summary>
    /// Splits a real engine log buffer into rows. The time is taken from the line
    /// when the engine stamped one; the level is classified from the line's own
    /// markers — nothing is added to the text.
    /// </summary>
    internal static List<LogRow> ParseLog(string? raw, int max = 200)
    {
        var rows = new List<LogRow>();
        if (string.IsNullOrWhiteSpace(raw)) return rows;

        var lines = raw.Split('\n');
        for (int i = lines.Length - 1; i >= 0 && rows.Count < max; i--)
        {
            var line = lines[i].TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var time = "";
            if (line.Length > 10 && line[0] == '[' && line[9] == ']' && line[3] == ':')
            {
                time = line.Substring(1, 8);
                line = line[10..].Trim();
            }
            else if (line.Length > 8 && line[2] == ':' && line[5] == ':' && char.IsDigit(line[0]))
            {
                time = line[..8];
                line = line[8..].Trim();
            }

            var (level, color) = Classify(line);
            rows.Add(new LogRow { Time = time, Level = level, Color = color, Msg = line });
        }
        return rows;
    }

    private static (string level, string color) Classify(string line)
    {
        var u = line.ToUpperInvariant();
        if (u.Contains("[ERROR]") || u.Contains("ERROR") || u.Contains("KILL-SWITCH") || u.Contains("⛔"))
            return ("ERR", BotsDeskData.Red);
        if (u.Contains("WARN") || u.Contains("[GUARD]") || u.Contains("BLOCKED") || u.Contains("REJECT"))
            return ("WARN", BotsDeskData.Amber);
        // note: these emoji are surrogate pairs — they must be matched as strings, not chars
        if (u.Contains("[CLOSED]") || u.Contains("FILL") || u.Contains("BUY ") || u.Contains("SELL ") || line.Contains("💰", StringComparison.Ordinal))
            return ("FILL", BotsDeskData.Green);
        if (u.Contains("CLAUDE") || u.Contains("[STRATEGY]") || u.Contains("AI") || line.Contains("🧠", StringComparison.Ordinal))
            return ("AI", "#b48cff");
        return ("INFO", BotsDeskData.Faint);
    }

    // ═════════════════════════ realized fills ═══════════════════════════════
    /// <summary>Closed trades this engine recorded, newest first. Empty when it records none.</summary>
    internal List<TradeRecord> EngineTrades(string id, int max = 50)
    {
        var key = PnlKey(id);
        if (key is null) return new List<TradeRecord>();
        return _records
            .Where(r => string.Equals(r.BotName ?? r.SourceLabel, key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.ClosedAtUtc)
            .Take(max)
            .ToList();
    }

    /// <summary>Realized PnL across every engine in the trailing 24h, or null when nothing is recorded.</summary>
    private double? Sum24 => _byBot24.Count == 0 ? null : (double)_byBot24.Values.Sum(r => r.PnlUsd);

    /// <summary>Realized loss booked today, used against the wallet's daily-loss cap.</summary>
    private double LossToday
    {
        get
        {
            var since = DateTime.UtcNow.Date;
            var loss = _records.Where(r => r.ClosedAtUtc >= since && r.PnlUsd < 0m).Sum(r => r.PnlUsd);
            return (double)Math.Abs(loss);
        }
    }
}
