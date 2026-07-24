using CryptoAITerminal.Core.Models;
using CryptoAITerminal.Executor;
using static CryptoAITerminal.Executor.ServerTrailingStop;

namespace CryptoAITerminal.Core.Tests.Executor;

/// <summary>Phase 4.3 — pure TP/SL/trailing decision core, ported from the desktop TpSlManager
/// (BUG-07 partial TP on spot, BUG-08 single-owner SL move). One price tick → one decision plus the
/// next state; ordering matches the desktop: TP first, then SL, then trailing movement.</summary>
public class ServerTrailingStopTests
{
    private static TslState Init(bool isLong, decimal entry, TpSlConfig cfg)
        => ServerTrailingStop.Init(isLong, entry, cfg);

    [Fact]
    public void Long_take_profit_closes_everything()
    {
        var cfg = new TpSlConfig { TpEnabled = true, TpPercent = 2m, SlEnabled = true, SlPercent = 1m };
        var st = Init(true, 100m, cfg);
        var d = Evaluate(cfg, st, isLong: true, entry: 100m, price: 102m);
        Assert.Equal(TslAction.CloseAll, d.Action);
        Assert.Equal("TP", d.Reason);
        Assert.True(d.Next.Closed);
    }

    [Fact]
    public void Long_stop_loss_closes_everything()
    {
        var cfg = new TpSlConfig { SlEnabled = true, SlPercent = 1m };
        var st = Init(true, 100m, cfg);
        var d = Evaluate(cfg, st, true, 100m, price: 98.9m); // below 99 SL
        Assert.Equal(TslAction.CloseAll, d.Action);
        Assert.Equal("SL", d.Reason);
    }

    [Fact]
    public void Below_thresholds_does_nothing()
    {
        var cfg = new TpSlConfig { TpEnabled = true, TpPercent = 2m, SlEnabled = true, SlPercent = 1m };
        var st = Init(true, 100m, cfg);
        var d = Evaluate(cfg, st, true, 100m, price: 100.5m);
        Assert.Equal(TslAction.None, d.Action);
        Assert.False(d.Next.Closed);
    }

    [Fact]
    public void Trailing_moves_sl_up_as_price_rises()
    {
        var cfg = new TpSlConfig { SlEnabled = true, SlPercent = 1m, TrailingStop = true };
        var st = Init(true, 100m, cfg);   // SL starts @ 99
        var d = Evaluate(cfg, st, true, 100m, price: 110m);
        Assert.Equal(TslAction.MoveSl, d.Action);
        Assert.Equal(108.9m, d.NewSlPrice); // 110 * 0.99
        Assert.Equal(110m, d.Next.Peak);
    }

    [Fact]
    public void Trailing_then_pullback_hits_the_trailed_sl()
    {
        var cfg = new TpSlConfig { SlEnabled = true, SlPercent = 1m, TrailingStop = true };
        var st = Init(true, 100m, cfg);
        st = Evaluate(cfg, st, true, 100m, 110m).Next;   // SL trailed to 108.9
        var d = Evaluate(cfg, st, true, 100m, price: 108.5m); // pull back through it
        Assert.Equal(TslAction.CloseAll, d.Action);
        Assert.Equal("SL", d.Reason);
    }

    [Fact]
    public void Partial_tp_closes_half_then_arms_tp2()
    {
        var cfg = new TpSlConfig
        {
            TpEnabled = true, TpPercent = 2m, PartialTp = true,
            PartialTpClosePercent = 50m, PartialTp2Percent = 4m, SlEnabled = true, SlPercent = 1m
        };
        var st = Init(true, 100m, cfg) with { RemainingQty = 1.0m };

        var tp1 = Evaluate(cfg, st, true, 100m, price: 102m); // hits TP1 @ +2%
        Assert.Equal(TslAction.PartialClose, tp1.Action);
        Assert.Equal(0.5m, tp1.Qty);
        Assert.Equal(0.5m, tp1.Next.RemainingQty);
        Assert.False(tp1.Next.Closed);

        // TP2 target is now +4% (104); at 103 nothing, at 104 it closes the rest
        Assert.Equal(TslAction.None, Evaluate(cfg, tp1.Next, true, 100m, 103m).Action);
        var tp2 = Evaluate(cfg, tp1.Next, true, 100m, 104m);
        Assert.Equal(TslAction.CloseAll, tp2.Action);
        Assert.Equal(0.5m, tp2.Qty);
    }

    [Fact]
    public void Short_stop_loss_triggers_above_entry()
    {
        var cfg = new TpSlConfig { SlEnabled = true, SlPercent = 1m };
        var st = Init(false, 100m, cfg); // short → SL @ 101
        var d = Evaluate(cfg, st, false, 100m, price: 101.1m);
        Assert.Equal(TslAction.CloseAll, d.Action);
        Assert.Equal("SL", d.Reason);
    }
}
