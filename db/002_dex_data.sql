-- ============================================================================
--  CryptoAI server DB — migration 002: full DEX data collection + editable keys
--  Apply AFTER init.sql. Idempotent (IF NOT EXISTS everywhere).
--
--  Adds:
--   * provider_keys   — API keys the collectors use, editable at any time.
--                       Change a row -> collectors pick it up on the next run,
--                       no redeploy. Seeded with the known providers (empty).
--   * token_metadata  — static-ish token info (name/symbol/socials/pair/mcap)
--   * token_security  — GoPlus/Honeypot/RugCheck verdicts per token
--   * news            — aggregated crypto news feed
--   * sentiment       — market sentiment metrics (fear&greed, …)
--   * gas_prices      — per-chain gas
--   * collector_runs  — observability: last run/ok/error/items per collector
-- ============================================================================

-- ── Editable API keys ────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS provider_keys (
  provider     TEXT PRIMARY KEY,          -- 'birdeye','coingecko','covalent',…
  api_key      TEXT NOT NULL DEFAULT '',
  enabled      BOOLEAN NOT NULL DEFAULT false,
  note         TEXT,
  updated_utc  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Seed the full provider list (empty). Fill api_key + set enabled=true anytime:
--   UPDATE provider_keys SET api_key='...', enabled=true WHERE provider='birdeye';
INSERT INTO provider_keys (provider, note) VALUES
  ('anthropic',  'Claude AI (server-side proxy)'),
  ('openai',     'OpenAI / ChatGPT (server-side proxy)'),
  ('birdeye',    'Solana OHLCV / token data'),
  ('coingecko',  'CoinGecko Pro on-chain OHLCV & token data'),
  ('covalent',   'Historical prices / balances'),
  ('alchemy',    'Historical token prices'),
  ('moralis',    'EVM token data / holders'),
  ('etherscan',  'ETH explorer (deployer, tx history)'),
  ('bscscan',    'BSC explorer'),
  ('basescan',   'Base explorer'),
  ('arbiscan',   'Arbitrum explorer'),
  ('polygonscan','Polygon explorer'),
  ('cryptopanic','News aggregator (keyed feed)'),
  ('glassnode',  'On-chain metrics'),
  ('coinmetrics','On-chain metrics (community/pro)'),
  ('coinglass',  'Liquidations / derivatives')
ON CONFLICT (provider) DO NOTHING;

-- ── Token metadata (name / symbol / socials / primary pool / market cap) ──────
CREATE TABLE IF NOT EXISTS token_metadata (
  chain         TEXT NOT NULL,
  token_address TEXT NOT NULL,
  name          TEXT,
  symbol        TEXT,
  image_url     TEXT,
  description   TEXT,
  website       TEXT,
  twitter       TEXT,
  telegram      TEXT,
  pair_address  TEXT,
  market_cap    NUMERIC,
  updated_utc   TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (chain, token_address)
);

-- ── Token security (honeypot / taxes / risk) ─────────────────────────────────
CREATE TABLE IF NOT EXISTS token_security (
  chain         TEXT NOT NULL,
  token_address TEXT NOT NULL,
  is_honeypot   BOOLEAN,
  buy_tax       NUMERIC,
  sell_tax      NUMERIC,
  risk_label    TEXT,
  risk_score    INT,
  details       JSONB,
  updated_utc   TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (chain, token_address)
);

-- ── News feed (RSS + keyed aggregators) ──────────────────────────────────────
CREATE TABLE IF NOT EXISTS news (
  id            BIGSERIAL PRIMARY KEY,
  source        TEXT NOT NULL,
  title         TEXT NOT NULL,
  url           TEXT NOT NULL UNIQUE,
  summary       TEXT,
  sentiment     TEXT,
  published_utc TIMESTAMPTZ,
  fetched_utc   TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_news_published ON news (published_utc DESC);

-- ── Market sentiment metrics (fear&greed, …) ─────────────────────────────────
CREATE TABLE IF NOT EXISTS sentiment (
  metric  TEXT NOT NULL,
  ts      TIMESTAMPTZ NOT NULL,
  value   NUMERIC,
  label   TEXT,
  PRIMARY KEY (metric, ts)
);

-- ── Gas prices per chain ─────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS gas_prices (
  chain    TEXT NOT NULL,
  ts       TIMESTAMPTZ NOT NULL,
  fast     NUMERIC,
  standard NUMERIC,
  slow     NUMERIC,
  PRIMARY KEY (chain, ts)
);

-- ── Collector observability ──────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS collector_runs (
  name         TEXT PRIMARY KEY,
  last_run_utc TIMESTAMPTZ,
  last_ok      BOOLEAN,
  last_error   TEXT,
  items        INT
);
