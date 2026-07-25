-- ============================================================================
--  CryptoAI server DB — migration 020: compression + retention for the derived
--  timeframes. Apply AFTER 019. Idempotent.
--
--  Why: init.sql gives dex_candles a compression policy (2 days) and a retention
--  policy (30 days), so the raw hypertable is bounded. The five continuous
--  aggregates got only a REFRESH policy — no compression, no retention. Dropping a
--  raw chunk does NOT remove buckets already materialised into an aggregate, so
--  dex_candles_5m and _15m were the only unbounded growth left in the schema
--  (order of 20 GB/year at a few hundred tracked tokens).
--
--  Keep the coarse timeframes forever — they are small and back the correlation
--  matrix and long-range backtests. Age out the fine ones.
-- ============================================================================

-- ── 5m / 15m: the volume drivers. Compress soon, drop eventually. ────────────
ALTER MATERIALIZED VIEW dex_candles_5m  SET (timescaledb.compress);
ALTER MATERIALIZED VIEW dex_candles_15m SET (timescaledb.compress);

SELECT add_compression_policy('dex_candles_5m',  INTERVAL '14 days', if_not_exists => TRUE);
SELECT add_compression_policy('dex_candles_15m', INTERVAL '30 days', if_not_exists => TRUE);

SELECT add_retention_policy('dex_candles_5m',  INTERVAL '1 year',  if_not_exists => TRUE);
SELECT add_retention_policy('dex_candles_15m', INTERVAL '2 years', if_not_exists => TRUE);

-- ── 1h / 4h / 1d: kept indefinitely, but compressed once settled. ────────────
ALTER MATERIALIZED VIEW dex_candles_1h SET (timescaledb.compress);
ALTER MATERIALIZED VIEW dex_candles_4h SET (timescaledb.compress);
ALTER MATERIALIZED VIEW dex_candles_1d SET (timescaledb.compress);

SELECT add_compression_policy('dex_candles_1h', INTERVAL '90 days',  if_not_exists => TRUE);
SELECT add_compression_policy('dex_candles_4h', INTERVAL '180 days', if_not_exists => TRUE);
SELECT add_compression_policy('dex_candles_1d', INTERVAL '365 days', if_not_exists => TRUE);
