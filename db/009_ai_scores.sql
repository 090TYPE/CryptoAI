-- ============================================================================
--  CryptoAI server DB — migration 009: cached AI token verdicts.
--  Apply AFTER 008. Idempotent. The AiScoreCollector scores each tracked token
--  ONCE with the server's Claude key and caches it for every user.
-- ============================================================================

CREATE TABLE IF NOT EXISTS token_ai_score (
  chain         TEXT NOT NULL,
  token_address TEXT NOT NULL,
  score         INT,      -- 0-100, higher = safer
  verdict       TEXT,     -- short label, e.g. "High rug risk"
  summary       TEXT,     -- 1-2 sentence rationale
  model         TEXT,
  updated_utc   TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (chain, token_address)
);
