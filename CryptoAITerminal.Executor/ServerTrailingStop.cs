using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.Executor;

/// <summary>
/// Server-side TP / SL / trailing-stop decision core, ported from the desktop <c>TpSlManager</c>.
/// Pure functions: given the config, the carried state, and a price tick, return the single action
/// to take (matching the desktop's TP-then-SL-then-trail ordering) plus the next state. Placement,
/// exchange-native orders and the price stream are the runner's job (Phase 4.3 wiring).
///
/// Preserves the desktop fixes: BUG-07 (partial TP works on spot — close a slice, re-arm at TP2)
/// and the trailing anti-spam guard (only move the SL on >0.1% improvement).
/// </summary>
public static class ServerTrailingStop
{
    /// <summary>What a price tick decided.</summary>
    public enum TslAction { None, CloseAll, PartialClose, MoveSl }

    /// <summary>Carried position state between ticks.</summary>
    public readonly record struct TslState(
        decimal Peak, decimal Sl, decimal RemainingQty, decimal TpPercent, bool Closed, bool PartialDone = false);

    /// <summary>The action for this tick and the state to carry forward.</summary>
    public readonly record struct TslDecision(
        TslAction Action, decimal Qty, decimal NewSlPrice, string Reason, TslState Next);

    /// <summary>Initial SL price for a fresh position (percent offset from entry).</summary>
    public static decimal InitialSl(bool isLong, decimal entry, decimal slPercent)
        => isLong ? entry * (1m - slPercent / 100m) : entry * (1m + slPercent / 100m);

    /// <summary>Seed state at entry: peak = entry, SL at the configured offset, TP at the first target.</summary>
    public static TslState Init(bool isLong, decimal entry, TpSlConfig cfg)
        => new(Peak: entry, Sl: InitialSl(isLong, entry, cfg.SlPercent),
               RemainingQty: 0m, TpPercent: cfg.TpPercent, Closed: false);

    /// <summary>Evaluate one price tick. TP is checked first, then SL, then trailing movement.</summary>
    public static TslDecision Evaluate(TpSlConfig cfg, TslState state, bool isLong, decimal entry, decimal price)
    {
        if (state.Closed) return new(TslAction.None, 0m, state.Sl, "", state);

        // Non-trailing SL always sits at the fixed offset; trailing SL is whatever we've ratcheted to.
        decimal effectiveSl = cfg.TrailingStop ? state.Sl : InitialSl(isLong, entry, cfg.SlPercent);

        // --- Take profit ---
        if (cfg.TpEnabled)
        {
            bool tpHit = isLong
                ? price >= entry * (1m + state.TpPercent / 100m)
                : price <= entry * (1m - state.TpPercent / 100m);

            if (tpHit)
            {
                // BUG-07: partial TP works on spot too — TP1 closes a slice and re-arms the target at
                // TP2; the next TP hit (PartialDone) closes the remainder rather than halving forever.
                if (cfg.PartialTp && !state.PartialDone && state.RemainingQty > 0m)
                {
                    var tp1Qty = Math.Round(state.RemainingQty * cfg.PartialTpClosePercent / 100m, 3, MidpointRounding.ToZero);
                    if (tp1Qty > 0m)
                    {
                        var next = state with
                        {
                            RemainingQty = state.RemainingQty - tp1Qty,
                            TpPercent = cfg.PartialTp2Percent,
                            PartialDone = true,
                        };
                        return new(TslAction.PartialClose, tp1Qty, effectiveSl, "TP1", next);
                    }
                }
                return new(TslAction.CloseAll, state.RemainingQty, effectiveSl, "TP", state with { Closed = true });
            }
        }

        // --- Stop loss ---
        if (cfg.SlEnabled)
        {
            bool slHit = isLong ? price <= effectiveSl : price >= effectiveSl;
            if (slHit)
                return new(TslAction.CloseAll, state.RemainingQty, effectiveSl, "SL", state with { Closed = true });
        }

        // --- Trailing movement ---
        if (cfg.TrailingStop && cfg.SlEnabled)
        {
            if (isLong && price > state.Peak)
            {
                var newSl = price * (1m - cfg.SlPercent / 100m);
                var next = state with { Peak = price };
                if (newSl > state.Sl * 1.001m) // anti-spam: only on >0.1% improvement
                    return new(TslAction.MoveSl, 0m, newSl, "trail", next with { Sl = newSl });
                return new(TslAction.None, 0m, state.Sl, "", next);
            }
            if (!isLong && price < state.Peak)
            {
                var newSl = price * (1m + cfg.SlPercent / 100m);
                var next = state with { Peak = price };
                if (newSl < state.Sl * 0.999m)
                    return new(TslAction.MoveSl, 0m, newSl, "trail", next with { Sl = newSl });
                return new(TslAction.None, 0m, state.Sl, "", next);
            }
        }

        return new(TslAction.None, 0m, state.Sl, "", state);
    }
}
