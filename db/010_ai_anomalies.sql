-- ============================================================================
--  CryptoAI server DB — migration 010: AI anomaly alerts on favorites.
--  Apply AFTER 009. Idempotent. AiAnomalyCollector pre-filters big movers
--  deterministically, asks Claude if it's worth waking the trader, then pushes
--  to every user who favorited the token.
-- ============================================================================

CREATE TABLE IF NOT EXISTS ai_anomalies (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  chain         TEXT NOT NULL,
  token_address TEXT NOT NULL,
  symbol        TEXT,
  severity      TEXT,      -- low | medium | high
  headline      TEXT,
  detail        TEXT,
  created_utc   TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_anom_token ON ai_anomalies (chain, token_address, created_utc DESC);
