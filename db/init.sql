-- ============================================================================
--  CryptoAI server DB — favorites-driven 24/7 DEX candle tracking
--  Target: PostgreSQL 16 + TimescaleDB
--  Mount this into the Timescale container at:
--    /docker-entrypoint-initdb.d/init.sql   (runs once on first boot)
--
--  Design:
--   * user_favorites  — who favorited what (private, M:N)
--   * tracked_tokens  — the UNIQUE set of tokens polled 24/7 (ref-counted).
--                       One poll per token regardless of how many users hold it.
--   * dex_candles     — raw 1m OHLCV hypertable; higher timeframes are derived
--                       automatically via continuous aggregates (not re-fetched).
--   * token_snapshot  — latest metrics for fast list reads.
--  Retention: raw candles kept 30 days, then dropped.
-- ============================================================================

CREATE EXTENSION IF NOT EXISTS timescaledb;
CREATE EXTENSION IF NOT EXISTS pgcrypto;   -- gen_random_uuid + column crypto later

-- ─────────────────────────────────────────────────────────────────────────────
-- Users (minimal; custodial secrets/withdrawals live in a separate migration)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS users (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  email           TEXT UNIQUE NOT NULL,
  license_name    TEXT,
  pw_hash         TEXT NOT NULL,
  totp_secret_enc BYTEA,
  created_utc     TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ─────────────────────────────────────────────────────────────────────────────
-- 1. tracked_tokens — the poll registry the worker drives 24/7
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS tracked_tokens (
  chain            TEXT    NOT NULL,
  token_address    TEXT    NOT NULL,
  pool_address     TEXT,                                  -- main pool, resolved once
  symbol           TEXT,
  name             TEXT,
  source           TEXT    NOT NULL DEFAULT 'favorite',   -- favorite | trending | both
  fav_count        INT     NOT NULL DEFAULT 0,            -- ref-count across users
  is_active        BOOLEAN NOT NULL DEFAULT true,
  poll_interval_s  INT     NOT NULL DEFAULT 60,           -- 60s = 1-minute candles
  last_polled_utc  TIMESTAMPTZ,
  next_poll_utc    TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_trade_utc   TIMESTAMPTZ,                           -- liveness / dead-token pruning
  added_utc        TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (chain, token_address)
);

-- Fast "what is due to poll now" scan for the worker
CREATE INDEX IF NOT EXISTS ix_tracked_due
  ON tracked_tokens (next_poll_utc) WHERE is_active;

-- Tokens still needing a pool resolved
CREATE INDEX IF NOT EXISTS ix_tracked_needs_pool
  ON tracked_tokens (chain, token_address) WHERE pool_address IS NULL AND is_active;

-- ─────────────────────────────────────────────────────────────────────────────
-- 2. user_favorites — source of truth for fav_count (private per user)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS user_favorites (
  user_id       UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  chain         TEXT NOT NULL,
  token_address TEXT NOT NULL,
  symbol        TEXT,
  added_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (user_id, chain, token_address)
);
CREATE INDEX IF NOT EXISTS ix_fav_token ON user_favorites (chain, token_address);

-- Keep tracked_tokens.fav_count in sync automatically.
-- INSERT: upsert token, bump count, (re)activate.
-- DELETE: drop count; the worker deactivates when it hits 0 (see housekeeping).
CREATE OR REPLACE FUNCTION sync_tracked() RETURNS TRIGGER AS $$
BEGIN
  IF (TG_OP = 'INSERT') THEN
    INSERT INTO tracked_tokens (chain, token_address, symbol, source, fav_count, is_active)
    VALUES (NEW.chain, NEW.token_address, NEW.symbol, 'favorite', 1, true)
    ON CONFLICT (chain, token_address) DO UPDATE
      SET fav_count = tracked_tokens.fav_count + 1,
          is_active = true,
          symbol    = COALESCE(tracked_tokens.symbol, EXCLUDED.symbol),
          source    = CASE WHEN tracked_tokens.source = 'trending'
                           THEN 'both' ELSE tracked_tokens.source END;
    RETURN NEW;
  ELSIF (TG_OP = 'DELETE') THEN
    UPDATE tracked_tokens
       SET fav_count = GREATEST(fav_count - 1, 0)
     WHERE chain = OLD.chain AND token_address = OLD.token_address;
    RETURN OLD;
  END IF;
  RETURN NULL;
END $$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_fav ON user_favorites;
CREATE TRIGGER trg_fav
  AFTER INSERT OR DELETE ON user_favorites
  FOR EACH ROW EXECUTE FUNCTION sync_tracked();

-- ─────────────────────────────────────────────────────────────────────────────
-- 3. dex_candles — raw 1-minute OHLCV hypertable
--    Worker UPSERTs the most recent candles each poll; only recent (uncompressed)
--    chunks receive writes, so ON CONFLICT stays valid.
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS dex_candles (
  chain         TEXT        NOT NULL,
  token_address TEXT        NOT NULL,
  ts            TIMESTAMPTZ NOT NULL,
  o NUMERIC, h NUMERIC, l NUMERIC, c NUMERIC, v NUMERIC,
  PRIMARY KEY (chain, token_address, ts)
);

SELECT create_hypertable('dex_candles', 'ts', if_not_exists => TRUE);

ALTER TABLE dex_candles SET (
  timescaledb.compress,
  timescaledb.compress_segmentby = 'chain, token_address',
  timescaledb.compress_orderby   = 'ts DESC'
);
SELECT add_compression_policy('dex_candles', INTERVAL '2 days', if_not_exists => TRUE);
SELECT add_retention_policy ('dex_candles', INTERVAL '30 days', if_not_exists => TRUE);

-- ─────────────────────────────────────────────────────────────────────────────
-- 4. Higher timeframes — derived from raw 1m via continuous aggregates.
--    Never re-fetched from upstream; TimescaleDB rolls them up on a schedule
--    and serves recent buckets in real time.
-- ─────────────────────────────────────────────────────────────────────────────
CREATE MATERIALIZED VIEW IF NOT EXISTS dex_candles_5m
WITH (timescaledb.continuous) AS
SELECT chain, token_address,
       time_bucket('5 minutes', ts) AS ts,
       first(o, ts) AS o, max(h) AS h, min(l) AS l, last(c, ts) AS c, sum(v) AS v
FROM dex_candles
GROUP BY chain, token_address, time_bucket('5 minutes', ts)
WITH NO DATA;

CREATE MATERIALIZED VIEW IF NOT EXISTS dex_candles_15m
WITH (timescaledb.continuous) AS
SELECT chain, token_address,
       time_bucket('15 minutes', ts) AS ts,
       first(o, ts) AS o, max(h) AS h, min(l) AS l, last(c, ts) AS c, sum(v) AS v
FROM dex_candles
GROUP BY chain, token_address, time_bucket('15 minutes', ts)
WITH NO DATA;

CREATE MATERIALIZED VIEW IF NOT EXISTS dex_candles_1h
WITH (timescaledb.continuous) AS
SELECT chain, token_address,
       time_bucket('1 hour', ts) AS ts,
       first(o, ts) AS o, max(h) AS h, min(l) AS l, last(c, ts) AS c, sum(v) AS v
FROM dex_candles
GROUP BY chain, token_address, time_bucket('1 hour', ts)
WITH NO DATA;

CREATE MATERIALIZED VIEW IF NOT EXISTS dex_candles_4h
WITH (timescaledb.continuous) AS
SELECT chain, token_address,
       time_bucket('4 hours', ts) AS ts,
       first(o, ts) AS o, max(h) AS h, min(l) AS l, last(c, ts) AS c, sum(v) AS v
FROM dex_candles
GROUP BY chain, token_address, time_bucket('4 hours', ts)
WITH NO DATA;

CREATE MATERIALIZED VIEW IF NOT EXISTS dex_candles_1d
WITH (timescaledb.continuous) AS
SELECT chain, token_address,
       time_bucket('1 day', ts) AS ts,
       first(o, ts) AS o, max(h) AS h, min(l) AS l, last(c, ts) AS c, sum(v) AS v
FROM dex_candles
GROUP BY chain, token_address, time_bucket('1 day', ts)
WITH NO DATA;

-- Refresh schedules (each derived TF recomputes only its trailing window)
SELECT add_continuous_aggregate_policy('dex_candles_5m',
  start_offset => INTERVAL '2 hours',  end_offset => INTERVAL '5 minutes',
  schedule_interval => INTERVAL '5 minutes', if_not_exists => TRUE);
SELECT add_continuous_aggregate_policy('dex_candles_15m',
  start_offset => INTERVAL '6 hours',  end_offset => INTERVAL '15 minutes',
  schedule_interval => INTERVAL '15 minutes', if_not_exists => TRUE);
SELECT add_continuous_aggregate_policy('dex_candles_1h',
  start_offset => INTERVAL '1 day',    end_offset => INTERVAL '1 hour',
  schedule_interval => INTERVAL '1 hour', if_not_exists => TRUE);
SELECT add_continuous_aggregate_policy('dex_candles_4h',
  start_offset => INTERVAL '4 days',   end_offset => INTERVAL '4 hours',
  schedule_interval => INTERVAL '1 hour', if_not_exists => TRUE);
SELECT add_continuous_aggregate_policy('dex_candles_1d',
  start_offset => INTERVAL '30 days',  end_offset => INTERVAL '1 hour',
  schedule_interval => INTERVAL '1 hour', if_not_exists => TRUE);

-- ─────────────────────────────────────────────────────────────────────────────
-- 5. token_snapshot — latest metrics (updated every poll) for fast list reads
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS token_snapshot (
  chain         TEXT NOT NULL,
  token_address TEXT NOT NULL,
  price_usd  NUMERIC,
  liq_usd    NUMERIC,
  vol24h     NUMERIC,
  chg_5m     NUMERIC,
  chg_1h     NUMERIC,
  chg_24h    NUMERIC,
  updated_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (chain, token_address)
);

-- ============================================================================
--  Reference queries (used by the API / worker)
-- ============================================================================
--
--  Worker: claim the next batch of due tokens (skip-locked so multiple workers
--  don't grab the same rows):
--     SELECT chain, token_address, pool_address, poll_interval_s
--       FROM tracked_tokens
--      WHERE is_active AND next_poll_utc <= now()
--      ORDER BY next_poll_utc
--      LIMIT 50
--      FOR UPDATE SKIP LOCKED;
--
--  Worker: after fetching candles, reschedule:
--     UPDATE tracked_tokens
--        SET last_polled_utc = now(),
--            next_poll_utc   = now() + make_interval(secs => poll_interval_s),
--            last_trade_utc  = :lastTradeUtc
--      WHERE chain = :chain AND token_address = :token;
--
--  Worker: UPSERT candles (recent, uncompressed chunks only):
--     INSERT INTO dex_candles (chain, token_address, ts, o,h,l,c,v)
--     VALUES (...)
--     ON CONFLICT (chain, token_address, ts)
--       DO UPDATE SET h=GREATEST(dex_candles.h, EXCLUDED.h),
--                     l=LEAST   (dex_candles.l, EXCLUDED.l),
--                     c=EXCLUDED.c, v=EXCLUDED.v;
--
--  Worker housekeeping: drop tokens nobody favorites and that aren't trending:
--     UPDATE tracked_tokens
--        SET is_active = false
--      WHERE fav_count = 0 AND source <> 'trending';
--
--  API: a user's favorites with latest prices:
--     SELECT f.chain, f.token_address, f.symbol,
--            s.price_usd, s.chg_1h, s.chg_24h, s.vol24h
--       FROM user_favorites f
--       LEFT JOIN token_snapshot s USING (chain, token_address)
--      WHERE f.user_id = :userId
--      ORDER BY f.added_utc DESC;
--
--  API: candles for a chart (tf ∈ dex_candles / _5m / _15m / _1h / _4h / _1d):
--     SELECT ts, o,h,l,c,v FROM dex_candles_1h
--      WHERE chain = :chain AND token_address = :token
--        AND ts >= :from AND ts < :to
--      ORDER BY ts;
-- ============================================================================
