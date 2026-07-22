-- ============================================================================
--  CryptoAI server DB — migration 015: live-trading kill-switch (Track 4 Phase 4.5 / §4).
--  Apply AFTER 014. Idempotent. Per-user halt of live bot execution; the all-zero
--  UUID row is the GLOBAL halt (stops live for everyone). Paper mode is never gated.
--  The executor checks this before every live order.
-- ============================================================================

CREATE TABLE IF NOT EXISTS user_flags (
  user_id     UUID PRIMARY KEY,               -- 00000000-...-0 = global switch
  live_halted BOOLEAN NOT NULL DEFAULT FALSE,
  reason      TEXT,
  updated_utc TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Global row present by default, off. Flip live_halted=true to halt all live bots.
INSERT INTO user_flags (user_id, live_halted, reason)
VALUES ('00000000-0000-0000-0000-000000000000', FALSE, 'global live kill-switch')
ON CONFLICT (user_id) DO NOTHING;
