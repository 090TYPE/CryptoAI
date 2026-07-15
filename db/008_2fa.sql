-- ============================================================================
--  CryptoAI server DB — migration 008: TOTP 2FA. Apply AFTER 007. Idempotent.
--  Secret is envelope-encrypted (same cipher as custodial secrets).
-- ============================================================================

CREATE TABLE IF NOT EXISTS user_2fa (
  user_id            UUID PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
  secret_ciphertext  BYTEA NOT NULL,
  secret_wrapped_dek BYTEA NOT NULL,
  enabled            BOOLEAN NOT NULL DEFAULT false,
  created_utc        TIMESTAMPTZ NOT NULL DEFAULT now()
);
