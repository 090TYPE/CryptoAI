-- ============================================================================
--  CryptoAI server DB — migration 023: per-call AI usage, by feature.
--  Apply AFTER 022. Idempotent.
--
--  Why: the vendor already reports input_tokens and output_tokens on every
--  response, and AiRequestPolicy.CountUsage already parses them — but the only
--  thing done with the number was to decrement a budget. Nothing recorded WHAT
--  the spend was for, so every cost figure about this product is an estimate
--  with a ±30% error bar and no way to close it.
--
--  One row per call. At a few thousand calls a day this is small; the retention
--  policy below keeps it that way without anyone having to remember.
-- ============================================================================

CREATE TABLE IF NOT EXISTS ai_usage (
    id            BIGSERIAL PRIMARY KEY,
    at_utc        TIMESTAMPTZ NOT NULL DEFAULT now(),
    license       TEXT,
    feature       TEXT,            -- null for the server's own jobs
    model         TEXT,
    vendor        TEXT,
    input_tokens  BIGINT NOT NULL DEFAULT 0,
    output_tokens BIGINT NOT NULL DEFAULT 0
);

COMMENT ON TABLE ai_usage IS 'One row per model call. Feature is null for server-side jobs (digests, scoring).';

CREATE INDEX IF NOT EXISTS ix_ai_usage_at      ON ai_usage (at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_ai_usage_feature ON ai_usage (feature, at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_ai_usage_license ON ai_usage (license, at_utc DESC);
