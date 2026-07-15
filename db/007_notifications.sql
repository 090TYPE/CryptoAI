-- ============================================================================
--  CryptoAI server DB — migration 007: per-user notification channel.
--  Apply AFTER 006. Idempotent. Used to push alert/bot events to the user's
--  phone (ntfy) or Telegram, so the server can reach them with their PC off.
-- ============================================================================

CREATE TABLE IF NOT EXISTS notification_channels (
  user_id     UUID PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
  kind        TEXT NOT NULL,                 -- 'ntfy' | 'telegram'
  target      TEXT NOT NULL,                 -- ntfy topic OR telegram chat_id
  token       TEXT,                          -- telegram bot token (null for ntfy)
  enabled     BOOLEAN NOT NULL DEFAULT true,
  updated_utc TIMESTAMPTZ NOT NULL DEFAULT now()
);
