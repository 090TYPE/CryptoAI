-- ============================================================================
--  CryptoAI server DB — migration 022: durable per-licence daily AI spend.
--  Apply AFTER 021. Idempotent.
--
--  Why: AiBudget counted tokens in process memory, and its own comment said so.
--  Every deploy, crash or restart reset every counter to zero, which means the
--  daily cap was not a cap — it was a cap between restarts. On a box that gets
--  redeployed during the working day the ceiling effectively did not exist.
--
--  One row per licence per UTC day. The hot path still reads and writes memory;
--  this table is loaded once at startup and written behind by a flusher, so the
--  cost of durability is a periodic upsert rather than a query per AI call.
-- ============================================================================

CREATE TABLE IF NOT EXISTS ai_budget_daily (
    license     TEXT   NOT NULL,
    day         DATE   NOT NULL,
    tokens      BIGINT NOT NULL DEFAULT 0,
    updated_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (license, day)
);

COMMENT ON TABLE ai_budget_daily IS 'Tokens charged per licence per UTC day. Written behind by the API; the live counter is in memory.';

-- Reporting reads "who is spending today", not "what did this licence do";
-- the primary key already covers the per-licence lookup.
CREATE INDEX IF NOT EXISTS ix_ai_budget_daily_day ON ai_budget_daily (day DESC, tokens DESC);
