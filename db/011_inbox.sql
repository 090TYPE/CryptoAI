-- ============================================================================
--  CryptoAI server DB — migration 011: in-terminal inbox.
--  Apply AFTER 010. Idempotent. Every server event (alert, AI anomaly, bot) lands
--  here for the user and the desktop terminal reads it via /api/inbox — so all
--  users get notified in-app without configuring any phone channel.
-- ============================================================================

CREATE TABLE IF NOT EXISTS inbox (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  kind        TEXT NOT NULL DEFAULT 'system',  -- alert | anomaly | bot | system
  title       TEXT NOT NULL,
  message     TEXT,
  created_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
  read_utc    TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS ix_inbox_user ON inbox (user_id, created_utc DESC);
CREATE INDEX IF NOT EXISTS ix_inbox_unread ON inbox (user_id) WHERE read_utc IS NULL;
