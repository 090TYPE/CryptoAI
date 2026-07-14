-- ============================================================================
--  CryptoAI server DB — migration 003: deployer, holders, whales, on-chain,
--  liquidations. Apply AFTER 002_dex_data.sql. Idempotent.
-- ============================================================================

-- Token contract deployer reputation (Etherscan-family analysis)
CREATE TABLE IF NOT EXISTS token_deployer (
  chain            TEXT NOT NULL,
  token_address    TEXT NOT NULL,
  deployer_address TEXT,
  tokens_deployed  INT,
  est_rugpulls     INT,
  wallet_age_months INT,
  updated_utc      TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (chain, token_address)
);

-- Holder distribution (from GeckoTerminal token info)
CREATE TABLE IF NOT EXISTS token_holders (
  chain         TEXT NOT NULL,
  token_address TEXT NOT NULL,
  holder_count  BIGINT,
  top10_pct     NUMERIC,
  top11_30_pct  NUMERIC,
  top31_50_pct  NUMERIC,
  rest_pct      NUMERIC,
  updated_utc   TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (chain, token_address)
);

-- Large ("whale") stablecoin transfers
CREATE TABLE IF NOT EXISTS whale_txs (
  tx_hash       TEXT PRIMARY KEY,
  chain         TEXT NOT NULL,
  token_symbol  TEXT,
  from_address  TEXT,
  to_address    TEXT,
  amount        NUMERIC,
  usd_value     NUMERIC,
  ts            TIMESTAMPTZ,
  fetched_utc   TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_whale_ts ON whale_txs (ts DESC);

-- On-chain market metrics (CoinMetrics community), time series
CREATE TABLE IF NOT EXISTS onchain_metrics (
  asset   TEXT NOT NULL,
  metric  TEXT NOT NULL,
  ts      TIMESTAMPTZ NOT NULL,
  value   NUMERIC,
  PRIMARY KEY (asset, metric, ts)
);

-- Derivatives liquidations (CoinGlass)
CREATE TABLE IF NOT EXISTS liquidations (
  id          BIGSERIAL PRIMARY KEY,
  symbol      TEXT NOT NULL,
  side        TEXT,
  usd_value   NUMERIC,
  ts          TIMESTAMPTZ NOT NULL,
  fetched_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (symbol, side, ts)
);
CREATE INDEX IF NOT EXISTS ix_liq_ts ON liquidations (ts DESC);
