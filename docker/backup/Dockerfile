# pg_dump must match the server major version, so this is built on the same Postgres 16 base the
# Timescale image derives from rather than on a bare alpine with whatever client apk ships today.
FROM postgres:16-alpine

# restic gives client-side encryption and deduplicated, incremental snapshots. The point of the
# encryption is that the S3 bucket alone is not enough to read the licence database.
# sqlite is here for the licence bot's order history, which lives in SQLite rather than in Postgres.
RUN apk add --no-cache restic ca-certificates tzdata sqlite

COPY backup.sh /usr/local/bin/backup.sh
RUN chmod +x /usr/local/bin/backup.sh

ENTRYPOINT ["/usr/local/bin/backup.sh"]
