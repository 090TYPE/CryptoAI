-- ============================================================================
--  CryptoAI server DB — migration 013: server-side grid bot state (Track 4 Phase 4.2).
--  Apply AFTER 012. Idempotent. Tracks each active grid limit order so a grid bot
--  is restart-safe: on boot it reloads its open levels instead of re-placing them.
-- ============================================================================

CREATE TABLE IF NOT EXISTS grid_orders (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  bot_id      UUID NOT NULL REFERENCES bot_configs(id) ON DELETE CASCADE,
  user_id     UUID NOT NULL,
  level       INT  NOT NULL,                  -- index into the generated price ladder
  side        TEXT NOT NULL,                  -- 'buy' | 'sell'
  price       NUMERIC NOT NULL,
  qty         NUMERIC NOT NULL,
  ext_ref     TEXT,                           -- exchange order id (null while paper)
  status      TEXT NOT NULL DEFAULT 'open',   -- 'open' | 'filled' | 'cancelled'
  created_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
  filled_utc  TIMESTAMPTZ
);

-- One live order per (bot, level, side) — prevents duplicate placement on races/restart.
CREATE UNIQUE INDEX IF NOT EXISTS ux_grid_orders_open
  ON grid_orders (bot_id, level, side) WHERE status = 'open';
CREATE INDEX IF NOT EXISTS ix_grid_orders_bot ON grid_orders (bot_id, status);
