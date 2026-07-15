-- ============================================================================
--  CryptoAI server DB — migration 005: server-side price alerts (24/7).
--  Apply AFTER 004. Idempotent. The AlertCollector watches token_snapshot and
--  flips matching alerts to 'triggered' (+ audit), so users get hit even with
--  their PC off.
-- ============================================================================

CREATE TABLE IF NOT EXISTS price_alerts (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id         UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  chain           TEXT NOT NULL,
  token_address   TEXT NOT NULL,
  symbol          TEXT,
  condition       TEXT NOT NULL,                 -- 'above' | 'below'
  threshold       NUMERIC NOT NULL,
  status          TEXT NOT NULL DEFAULT 'active', -- active | triggered | disabled
  created_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
  triggered_utc   TIMESTAMPTZ,
  triggered_price NUMERIC
);

CREATE INDEX IF NOT EXISTS ix_alerts_active ON price_alerts (chain, token_address) WHERE status = 'active';
CREATE INDEX IF NOT EXISTS ix_alerts_user   ON price_alerts (user_id);
