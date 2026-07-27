-- ============================================================================
--  CryptoAI server DB — migration 021: runtime-editable server settings.
--  Apply AFTER 020. Idempotent.
--
--  Why: every operational knob the server has today is an environment variable
--  read once at startup — AI models for the background jobs, poll cadence,
--  anomaly thresholds, cache TTLs, the daily token cap. Changing any of them
--  means editing .env and restarting the contour, and for anything the CLIENT
--  sends (the model on every AI call) even that does not help: the value ships
--  inside installed terminals.
--
--  provider_keys already proved the pattern — collectors read it on every run,
--  so an admin edit takes effect immediately. This generalises it to plain
--  settings, which need no encryption but do need an audit trail: when spend
--  jumps, the first question is what was changed just before.
--
--  Values are TEXT on purpose. Typing lives in the reader (SettingsStore.GetInt
--  and friends) so adding a knob never needs a migration.
-- ============================================================================

CREATE TABLE IF NOT EXISTS server_settings (
    key         TEXT PRIMARY KEY,
    value       TEXT        NOT NULL,
    updated_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by  TEXT
);

COMMENT ON TABLE  server_settings IS 'Runtime-editable configuration. Absent key = use the in-code default.';
COMMENT ON COLUMN server_settings.updated_by IS 'Free-form actor label from the admin API; null for values written by migration.';

-- Append-only history. Kept separate from the live table so a rollback is a
-- read of this one, and so deleting a setting (back to the code default) still
-- leaves a record that it existed.
CREATE TABLE IF NOT EXISTS server_settings_audit (
    id          BIGSERIAL PRIMARY KEY,
    key         TEXT        NOT NULL,
    old_value   TEXT,
    new_value   TEXT,                       -- null = the setting was removed
    changed_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
    changed_by  TEXT
);

CREATE INDEX IF NOT EXISTS ix_server_settings_audit_key_time
    ON server_settings_audit (key, changed_utc DESC);

CREATE INDEX IF NOT EXISTS ix_server_settings_audit_time
    ON server_settings_audit (changed_utc DESC);
