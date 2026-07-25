using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CryptoAITerminal.TerminalUI.ViewModels.BotsDesk;

public partial class BotsDeskViewModel
{
    // ── after any engine command: re-read state instead of patching locally ──
    private void AfterEngineAction(string? id = null)
    {
        SyncRows();
        RefreshAllRows();
        RebuildVisible();
        if (_expandedId is not null && (id is null || _expandedId == id)) RebuildDetail();
        RaiseLiveHeader();
        Recompute();
    }

    // ── expand / select / menu ───────────────────────────────────────────────
    /// <summary>
    /// The row checkbox is a button nested inside the row's expand button. Avalonia
    /// normally stops the inner click, but if it ever bubbles we must not toggle the
    /// detail panel as a side effect of selecting — so an expand that lands in the
    /// same click as a select is ignored.
    /// </summary>
    private DateTime _lastSelectAt = DateTime.MinValue;

    public void ToggleExpand(BotRowViewModel row)
    {
        if (DateTime.UtcNow - _lastSelectAt < TimeSpan.FromMilliseconds(200)) return;
        _menuForId = null;
        _expandedId = _expandedId == row.Id ? null : row.Id;
        _detailTab = "overview";
        foreach (var b in Bots)
        {
            b.Expanded = b.Id == _expandedId;
            b.MenuOpen = false;
            b.RefreshAll();
        }
        RebuildDetail();
    }

    public void ToggleSelect(BotRowViewModel row)
    {
        _lastSelectAt = DateTime.UtcNow;
        if (!Selected.Remove(row.Id)) Selected.Add(row.Id);
        AfterSelectionChanged();
    }

    public void ToggleMenu(BotRowViewModel row)
    {
        _menuForId = _menuForId == row.Id ? null : row.Id;
        foreach (var b in Bots) b.MenuOpen = b.Id == _menuForId;
    }

    private void AfterSelectionChanged()
    {
        foreach (var b in Bots) b.Selected = Selected.Contains(b.Id);
        RefreshSelectionHeader();
    }

    private void ToggleSelectAll()
    {
        _lastSelectAt = DateTime.UtcNow;
        var all = VisibleBots.Count > 0 && VisibleBots.All(b => Selected.Contains(b.Id));
        Selected.Clear();
        if (!all) foreach (var b in VisibleBots) Selected.Add(b.Id);
        AfterSelectionChanged();
    }

    // ── selection header ─────────────────────────────────────────────────────
    public bool HasSelection => Selected.Count > 0;
    public string SelectionLabel => Selected.Count + " selected";
    public string AllBoxBorder => AllSelected ? BotsDeskData.Accent : "#152233";
    public string AllBoxBg => AllSelected ? BotsDeskData.Accent : "transparent";
    public string AllBoxMark => AllSelected ? "✓" : "";
    private bool AllSelected => VisibleBots.Count > 0 && VisibleBots.All(b => Selected.Contains(b.Id));

    private void RefreshSelectionHeader()
    {
        this.RaisePropertyChanged(nameof(HasSelection));
        this.RaisePropertyChanged(nameof(SelectionLabel));
        this.RaisePropertyChanged(nameof(AllBoxBorder));
        this.RaisePropertyChanged(nameof(AllBoxBg));
        this.RaisePropertyChanged(nameof(AllBoxMark));
    }

    // ── bulk actions (real engine commands) ──────────────────────────────────
    private void RebuildBulkActions()
    {
        BulkActions.Clear();
        BulkActions.Add(new BulkAction { Label = "▶ START", Color = BotsDeskData.Green, Command = new RelayCommand(() => Bulk("start")) });
        BulkActions.Add(new BulkAction { Label = "❙❙ PAUSE", Color = BotsDeskData.Amber, Command = new RelayCommand(() => Bulk("pause")) });
        BulkActions.Add(new BulkAction { Label = "■ STOP", Color = BotsDeskData.Text3, Command = new RelayCommand(() => Bulk("stop")) });
        BulkActions.Add(new BulkAction { Label = "⛔ KILL", Color = BotsDeskData.Red, Command = new RelayCommand(BulkKill) });
    }

    private void Bulk(string action)
    {
        var ids = Bots.Where(b => Selected.Contains(b.Id)).Select(b => b.Id).ToList();
        int done = 0;
        foreach (var id in ids)
        {
            var ok = action switch
            {
                "start" => StartEngine(id),
                "pause" => PauseEngine(id),
                "stop" => StopEngine(id),
                _ => false
            };
            if (ok) done++;
        }
        AfterEngineAction();
        var verb = action switch { "start" => "started", "pause" => "paused", _ => "stopped" };
        Toast(done == 0
            ? "No selected engine accepts " + action + " right now"
            : done + " engine" + (done == 1 ? "" : "s") + " " + verb,
            done == 0 ? "warn" : "ok");
    }

    private void BulkKill()
        => AskConfirm(new ConfirmConfig
        {
            Kind = "killSel", Accent = "#3a1620", IconColor = BotsDeskData.Red, Icon = "⛔",
            Title = "Kill " + Selected.Count + " engines",
            Body = "Selected engines are stopped immediately. The AI trader also runs its own kill-switch.",
            Cta = "KILL SELECTED", BtnBg = "#14060a", BtnFg = BotsDeskData.Red, BtnBorder = BotsDeskData.Red
        });

    // ── per-row inline actions + context menu ────────────────────────────────
    private void BuildRowActionsAndMenu(BotRowViewModel b)
    {
        b.Actions.Clear();
        foreach (var key in _quick.Take(4))
        {
            var d = BotsDeskData.ActionDefs.FirstOrDefault(x => x.key == key);
            if (d.key is null) d = BotsDeskData.ActionDefs[0];
            var k = key;
            var enabled = ActionEnabled(k, b.Id);
            var icon = d.icon;
            var label = d.label;

            // start doubles as resume while the engine is paused
            if (k == "start" && CanResume(b.Id)) { label = "Resume"; enabled = true; }
            if (k == "pause" && CanResume(b.Id)) { label = "Resume"; icon = "▶"; enabled = true; }

            b.Actions.Add(new RowAction
            {
                Icon = icon,
                Title = enabled ? label : label + " · not available for this engine",
                Color = enabled ? d.color : BotsDeskData.Ghost,
                Command = new RelayCommand(() => RunRowAction(k, b))
            });
        }

        b.MenuItems.Clear();
        b.MenuItems.Add(new RowMenuItem { Label = "Engine parameters", Hint = "✎", Color = BotsDeskData.Text, Command = new RelayCommand(() => OpenDetail(b, "params")) });
        if (b.Id == IdRule)
            b.MenuItems.Add(new RowMenuItem { Label = "TP / SL settings", Hint = "◇", Color = BotsDeskData.Text, Command = new RelayCommand(() => OpenDetail(b, "tpsl")) });
        b.MenuItems.Add(new RowMenuItem { Label = "Realized fills", Hint = "⌁", Color = BotsDeskData.Text, Command = new RelayCommand(() => OpenDetail(b, "trades")) });
        b.MenuItems.Add(new RowMenuItem { Label = "Engine log", Hint = "≡", Color = BotsDeskData.Text, Command = new RelayCommand(() => OpenDetail(b, "logs")) });
        b.MenuItems.Add(new RowMenuItem { Label = "Risk caps", Hint = "🛡", Color = BotsDeskData.Text, Command = new RelayCommand(() => OpenDetail(b, "risk")) });
        if (HasPaperSwitch(b.Id))
        {
            var live = b.Mode == "live";
            b.MenuItems.Add(new RowMenuItem
            {
                Label = live ? "Switch to paper" : "Switch to live",
                Hint = live ? "" : "⚠",
                Color = live ? BotsDeskData.Text : BotsDeskData.Amber,
                Command = new RelayCommand(() => SwitchMode(b))
            });
        }
        b.MenuItems.Add(new RowMenuItem { Label = "Kill-switch", Hint = "⛔", Color = BotsDeskData.Red, Command = new RelayCommand(() => KillOne(b)) });
    }

    /// <summary>Only the AI trader and the autonomous agent have a paper/live switch of their own.</summary>
    private static bool HasPaperSwitch(string id) => id is IdAi or IdAgent;

    private bool ActionEnabled(string key, string id) => key switch
    {
        "start" => CanStart(id),
        "pause" => CanPause(id) || CanResume(id),
        "stop" => CanStop(id),
        "runNow" => CanRunOnce(id),
        "kill" => CanStop(id),
        _ => true
    };

    private void CloseMenus()
    {
        _menuForId = null;
        foreach (var b in Bots) b.MenuOpen = false;
    }

    private void OpenDetail(BotRowViewModel b, string tab)
    {
        _menuForId = null;
        _expandedId = b.Id;
        _detailTab = tab;
        foreach (var x in Bots) { x.Expanded = x.Id == b.Id; x.MenuOpen = false; x.RefreshAll(); }
        RebuildDetail();
    }

    private void RunRowAction(string key, BotRowViewModel b)
    {
        switch (key)
        {
            case "start":
                if (CanResume(b.Id)) { PauseEngine(b.Id); AfterEngineAction(b.Id); Toast(b.Name + " resumed", "ok"); }
                else if (StartEngine(b.Id)) { AfterEngineAction(b.Id); Toast(b.Name + " started", "ok"); }
                else Toast(b.Name + " cannot start right now", "warn");
                break;

            case "pause":
                if (PauseEngine(b.Id)) { AfterEngineAction(b.Id); Toast(b.Name + (CanResume(b.Id) ? " paused" : " resumed"), "warn"); }
                else Toast("This engine has no pause — use stop", "warn");
                break;

            case "stop":
                if (StopEngine(b.Id)) { AfterEngineAction(b.Id); Toast(b.Name + " stopped", "info"); }
                else Toast(b.Name + " is not running", "warn");
                break;

            case "runNow":
                if (RunOnceEngine(b.Id)) { AfterEngineAction(b.Id); Toast(b.Name + " · one turn triggered", "ai"); }
                else Toast("This engine has no manual single run", "warn");
                break;

            case "kill": KillOne(b); break;
            case "edit": OpenDetail(b, "params"); break;
            case "logs": OpenDetail(b, "logs"); break;
            case "trades": OpenDetail(b, "trades"); break;
            case "risk": OpenDetail(b, "risk"); break;
        }
    }

    private void KillOne(BotRowViewModel b)
    {
        CloseMenus();
        if (!CanStop(b.Id)) { Toast(b.Name + " is not running", "warn"); return; }
        AskConfirm(new ConfirmConfig
        {
            Kind = "killOne", BotId = b.Id, Accent = "#3a1620", IconColor = BotsDeskData.Red, Icon = "⛔",
            Title = "Kill-switch · " + b.Name,
            Body = "Stops the engine immediately. Open exchange orders are cancelled by the engine's own stop path.",
            Cta = "KILL ENGINE", BtnBg = "#14060a", BtnFg = BotsDeskData.Red, BtnBorder = BotsDeskData.Red
        });
    }

    private void SwitchMode(BotRowViewModel b)
    {
        CloseMenus();
        if (b.Mode == "live")
        {
            SetEngineLive(b.Id, false);
            AfterEngineAction(b.Id);
            Toast(b.Name + " moved to paper", "info");
            return;
        }

        AskConfirm(new ConfirmConfig
        {
            Kind = "goLive", BotId = b.Id, Accent = "#3a2a12", IconColor = BotsDeskData.Amber, Icon = "⚠",
            Title = "Switch " + b.Name + " to live",
            Body = "The engine will place real orders. The wallet paper guard, the risk caps and the kill-switch still apply.",
            HasType = true, TypeWord = "LIVE", Cta = "GO LIVE", BtnBg = "#150f04", BtnFg = BotsDeskData.Amber, BtnBorder = BotsDeskData.Amber
        });
    }

    /// <summary>Flips the engine's own paper/live flag (AI trader and autonomous agent only).</summary>
    private void SetEngineLive(string id, bool live)
    {
        if (id == IdAi && Trader is not null) Trader.LiveEnabled = live;
        else if (id == IdAgent && AgentVm is not null) AgentVm.LiveEnabled = live;
    }

    // ── engine launch from the wizard / engine cards ─────────────────────────
    /// <summary>Applies the wizard's configuration to the matching engine and starts it.</summary>
    private void CreateFromTemplate(string engineId, WizardResult? o)
    {
        var row = Bots.FirstOrDefault(b => b.Id == engineId);
        if (_host is null || row is null) { Toast("Desk is not attached to the trading shell", "warn"); return; }

        ApplyWizardConfig(engineId, o);

        var started = CanStart(engineId) && StartEngine(engineId);
        _modal = null;
        _wizStep = 1;
        _wizTemplate = null;

        AfterEngineAction(engineId);
        OpenDetail(row, "overview");
        RaiseModalFlags();

        Toast(started
            ? row.Name + " configured and started"
            : row.Name + " configured — it is already running",
            "ok");
    }
}
