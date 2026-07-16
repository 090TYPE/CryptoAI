-- ============================================================================
--  CryptoAI server DB — migration 012: shared AI digests.
--  Apply AFTER 011. Idempotent.
--
--  Broadcast AI content: computed ONCE by the server with our Claude key and read
--  by every user (/api/digests). One model call serves the whole user base — this
--  is why the data collection + AI live on the server.
-- ============================================================================

CREATE TABLE IF NOT EXISTS ai_digests (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  kind        TEXT NOT NULL,   -- daily | movers | narratives | news_impact | ...
  title       TEXT NOT NULL,
  body        TEXT,
  model       TEXT,
  created_utc TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_digests_kind ON ai_digests (kind, created_utc DESC);
