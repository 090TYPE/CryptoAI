-- ============================================================================
--  CryptoAI server DB — migration 016: bot-order idempotency (Track 4 Phase 4.5).
--  Apply AFTER 015. Idempotent. Adds client_order_id so a node restart between
--  placing an order and stamping last_run can't create a duplicate: the same
--  logical tick derives the same id, and the unique index rejects the second row.
-- ============================================================================

ALTER TABLE bot_orders ADD COLUMN IF NOT EXISTS client_order_id TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS ux_bot_orders_client
  ON bot_orders (client_order_id) WHERE client_order_id IS NOT NULL;
