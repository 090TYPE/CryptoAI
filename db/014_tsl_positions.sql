-- ============================================================================
--  CryptoAI server DB — migration 014: server-side trailing / TP-SL bot state
--  (Track 4 Phase 4.3). Apply AFTER 013. Idempotent. One managed position per
--  trailing bot: entry context + live TP/SL/trailing state, so a restart resumes
--  exactly where the runner left off.
-- ============================================================================

CREATE TABLE IF NOT EXISTS tsl_positions (
  bot_id        UUID PRIMARY KEY REFERENCES bot_configs(id) ON DELETE CASCADE,
  user_id       UUID NOT NULL,
  symbol        TEXT NOT NULL,
  is_long       BOOLEAN NOT NULL,
  entry         NUMERIC NOT NULL,
  futures       BOOLEAN NOT NULL DEFAULT FALSE,
  -- live TslState (see ServerTrailingStop.TslState)
  peak          NUMERIC NOT NULL,
  sl            NUMERIC NOT NULL,
  remaining_qty NUMERIC NOT NULL,
  tp_percent    NUMERIC NOT NULL,
  partial_done  BOOLEAN NOT NULL DEFAULT FALSE,
  closed        BOOLEAN NOT NULL DEFAULT FALSE,
  updated_utc   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_tsl_positions_open ON tsl_positions (bot_id) WHERE NOT closed;
