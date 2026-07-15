-- ============================================================================
--  CryptoAI server DB — migration 006: server-side bot execution.
--  Apply AFTER 005. Idempotent. The BotExecutorService runs enabled bot_configs
--  on schedule (params.intervalMinutes) and records each action in bot_orders.
-- ============================================================================

ALTER TABLE bot_configs ADD COLUMN IF NOT EXISTS last_run_utc TIMESTAMPTZ;

CREATE TABLE IF NOT EXISTS bot_orders (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  bot_id      UUID NOT NULL REFERENCES bot_configs(id) ON DELETE CASCADE,
  user_id     UUID NOT NULL,
  side        TEXT,                          -- 'buy' | 'sell'
  asset       TEXT,
  amount      NUMERIC,                       -- USD notional (DCA) or base qty
  price       NUMERIC,
  status      TEXT NOT NULL DEFAULT 'paper', -- 'paper' | 'placed' | 'failed'
  ext_ref     TEXT,                          -- exchange order id (real impl)
  created_utc TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_bot_orders_bot ON bot_orders (bot_id, created_utc DESC);
