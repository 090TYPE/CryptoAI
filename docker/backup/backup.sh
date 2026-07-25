#!/bin/sh
# Encrypted, incremental backups of the CryptoAI database to S3.
#
# The database holds licences - money already taken from customers - and until now the pgdata
# volume was the only copy. A dump is streamed straight into restic, so the plaintext never touches
# disk, and restic encrypts client-side: the bucket alone cannot read it.
#
# Deliberately a plain loop rather than cron. There is no second process to supervise, the container
# restart policy is the only liveness mechanism needed, and the schedule is visible in one variable.
set -eu

: "${DB_HOST:=db}"
: "${DB_NAME:=cryptoai}"
: "${DB_USER:=postgres}"
: "${BACKUP_INTERVAL_SECONDS:=21600}"   # 6h
: "${BACKUP_KEEP_DAILY:=7}"
: "${BACKUP_KEEP_WEEKLY:=4}"
: "${BACKUP_KEEP_MONTHLY:=6}"

log() { echo "[backup] $(date -u '+%Y-%m-%dT%H:%M:%SZ') $*"; }

if [ -z "${RESTIC_REPOSITORY:-}" ] || [ -z "${RESTIC_PASSWORD:-}" ]; then
    log "RESTIC_REPOSITORY or RESTIC_PASSWORD is unset - backups are OFF."
    log "This is a real risk, not a warning to skip: pgdata is then the only copy of the licence"
    log "database. See .env.example for the three values needed."
    # Sleeping rather than exiting keeps the container from a crash-restart loop that would bury
    # the message in noise. The state is still visible in 'docker compose ps'.
    while true; do sleep 3600; done
fi

log "repository=${RESTIC_REPOSITORY} interval=${BACKUP_INTERVAL_SECONDS}s"

# init is idempotent in effect: an existing repository simply fails this check and is left alone.
if ! restic snapshots >/dev/null 2>&1; then
    log "initialising repository"
    restic init || { log "init failed - check credentials and bucket"; }
fi

while true; do
    started=$(date -u '+%s')

    # --clean lets the dump restore into an empty database; the stream never lands on disk.
    if pg_dump --host="$DB_HOST" --username="$DB_USER" --dbname="$DB_NAME" --format=plain --clean --if-exists \
        | restic backup --stdin --stdin-filename "${DB_NAME}.sql" --tag cryptoai-db --host cryptoai
    then
        log "snapshot written"

        if restic forget --tag cryptoai-db \
            --keep-daily "$BACKUP_KEEP_DAILY" \
            --keep-weekly "$BACKUP_KEEP_WEEKLY" \
            --keep-monthly "$BACKUP_KEEP_MONTHLY" \
            --prune
        then
            log "retention applied"
        else
            log "WARNING retention failed - snapshots kept, storage will grow"
        fi
    else
        # Never exit on a failed run: a transient database restart or S3 blip must not stop all
        # future backups until somebody notices the container is gone.
        log "ERROR backup failed - will retry at the next interval"
    fi

    elapsed=$(( $(date -u '+%s') - started ))
    sleep_for=$(( BACKUP_INTERVAL_SECONDS - elapsed ))
    [ "$sleep_for" -lt 60 ] && sleep_for=60
    sleep "$sleep_for"
done
