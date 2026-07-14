-- ============================================================================
--  CryptoAI server DB — migration 004: custodial core (secrets, withdrawals,
--  bot configs, audit). Apply AFTER 003. Idempotent.
--
--  SECURITY: `secrets.ciphertext` and `secrets.wrapped_dek` are envelope-encrypted
--  (per-secret AES-256 data key, itself wrapped by the master key held in Vault).
--  The public API never decrypts — only the isolated executor node does.
-- ============================================================================

CREATE TABLE IF NOT EXISTS secrets (
  id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id           UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  kind              TEXT NOT NULL,                 -- 'cex_api' | 'wallet_pk'
  label             TEXT,                          -- user-facing name, e.g. "Binance main"
  exchange_or_chain TEXT NOT NULL,                 -- 'binance' | 'eth' | 'solana' ...
  ciphertext        BYTEA NOT NULL,                -- envelope-encrypted secret
  wrapped_dek       BYTEA NOT NULL,                -- data key wrapped by the master key
  permissions       TEXT,                          -- 'trade' | 'trade,withdraw' ...
  created_utc       TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_utc       TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_secrets_user ON secrets (user_id);

CREATE TABLE IF NOT EXISTS withdrawals (
  id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id           UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  asset             TEXT NOT NULL,
  amount            NUMERIC NOT NULL,
  to_address        TEXT NOT NULL,
  status            TEXT NOT NULL DEFAULT 'pending', -- pending|executing|done|cancelled|held
  requested_utc     TIMESTAMPTZ NOT NULL DEFAULT now(),
  execute_after_utc TIMESTAMPTZ NOT NULL,            -- delay window (cancellable until then)
  audit_ip          INET
);
CREATE INDEX IF NOT EXISTS ix_withdrawals_due ON withdrawals (execute_after_utc) WHERE status = 'pending';

CREATE TABLE IF NOT EXISTS bot_configs (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  strategy    TEXT NOT NULL,
  params_json JSONB,
  enabled     BOOLEAN NOT NULL DEFAULT false,
  updated_utc TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Append-only audit trail. Revoke UPDATE/DELETE from the app role in production.
CREATE TABLE IF NOT EXISTS audit_log (
  id      BIGSERIAL PRIMARY KEY,
  ts      TIMESTAMPTZ NOT NULL DEFAULT now(),
  user_id UUID,
  actor   TEXT,
  action  TEXT NOT NULL,
  detail  JSONB,
  ip      INET
);
CREATE INDEX IF NOT EXISTS ix_audit_user_ts ON audit_log (user_id, ts DESC);
