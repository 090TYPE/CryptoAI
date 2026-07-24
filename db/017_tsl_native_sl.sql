-- ============================================================================
--  CryptoAI server DB — migration 017: native trailing-stop tracking (Track 4 Phase 4.3).
--  Apply AFTER 016. Idempotent. Lets a futures trailing bot keep one exchange-native
--  reduce-only stop-loss order that trails; sl_order_id is that resting order so a
--  restart cancels/replaces the right one instead of orphaning it.
-- ============================================================================

ALTER TABLE tsl_positions ADD COLUMN IF NOT EXISTS native_sl   BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE tsl_positions ADD COLUMN IF NOT EXISTS sl_order_id TEXT;
