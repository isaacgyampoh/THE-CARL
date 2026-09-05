#!/usr/bin/env bash
#
# Takes a restorable backup of the pilot database.
#
# The embedded PostgreSQL distribution this project uses ships only initdb, pg_ctl and
# postgres — there is no pg_dump, no pg_basebackup and no psql. So this takes a cold
# filesystem copy instead: stop the server, copy the data directory, start it again.
#
# A cold copy of a cleanly stopped cluster is a complete, restorable backup. The cost is a
# short outage, which for a pilot of this size is a fair trade for not adding a dependency
# just to take a backup. Copying a *running* data directory would produce a file that looks
# fine and fails to start, so the stop is not optional.
#
# Usage: scripts/backup-database.sh [destination-directory]

set -euo pipefail

PG_ROOT="${ZAZI_PG_ROOT:-$HOME/.zazi-testdb}"
DB_PORT="${ZAZI_DB_PORT:-55433}"
DEST="${1:-$HOME/zazi-backups}"
SOCKET_DIR="${ZAZI_SOCKET_DIR:-/tmp/zazipg}"

if [ ! -d "$PG_ROOT/pgdata" ]; then
    echo "No cluster at $PG_ROOT/pgdata. Set ZAZI_PG_ROOT to the right location." >&2
    exit 1
fi

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
TARGET="$DEST/zazi-$STAMP"
mkdir -p "$DEST"

was_running=0
if "$PG_ROOT/pgdist/bin/pg_ctl" -D "$PG_ROOT/pgdata" status >/dev/null 2>&1; then
    was_running=1
fi

restart_if_needed() {
    if [ "$was_running" -eq 1 ]; then
        # The socket directory is not guaranteed to survive a reboot or a /tmp sweep, and
        # postgres will not start without it.
        mkdir -p "$SOCKET_DIR"
        "$PG_ROOT/pgdist/bin/pg_ctl" -D "$PG_ROOT/pgdata" \
            -l "$PG_ROOT/pg.log" \
            -o "-p $DB_PORT -c listen_addresses=127.0.0.1 -c unix_socket_directories=$SOCKET_DIR -c max_connections=400" \
            start >/dev/null 2>&1 || echo "  WARNING: the server did not restart — start it manually" >&2
    fi
}

# Whatever happens below, do not leave the pilot with a stopped database.
trap restart_if_needed EXIT

if [ "$was_running" -eq 1 ]; then
    echo "  stopping postgres for a consistent copy"
    "$PG_ROOT/pgdist/bin/pg_ctl" -D "$PG_ROOT/pgdata" -o "-p $DB_PORT" -m fast stop >/dev/null
    sleep 2
fi

echo "  copying data directory"
cp -R "$PG_ROOT/pgdata" "$TARGET"

# Written after the copy: a marker present means the copy finished. A half-copied directory
# has no marker and is obviously unusable rather than deceptively restorable.
cat > "$TARGET/BACKUP_INFO.txt" <<INFO
Zazi database backup
taken            $STAMP
source           $PG_ROOT/pgdata
postgres version $("$PG_ROOT/pgdist/bin/postgres" --version 2>/dev/null || echo unknown)

To restore:
  scripts/stop-stack.sh
  mv "$PG_ROOT/pgdata" "$PG_ROOT/pgdata.replaced-$STAMP"
  cp -R "$TARGET" "$PG_ROOT/pgdata"
  scripts/start-stack.sh

Restore onto the same PostgreSQL major version. This is a physical copy, not a dump, so it
is not portable across versions or architectures.
INFO

SIZE="$(du -sh "$TARGET" | cut -f1)"
echo "  backup complete: $TARGET ($SIZE)"

# Keep the last 7. Unbounded backups fill the disk that the database is running on, which
# turns a safety measure into an outage.
KEEP=7
ls -1dt "$DEST"/zazi-* 2>/dev/null | tail -n +$((KEEP + 1)) | while read -r old; do
    echo "  pruning $old"
    rm -rf "$old"
done
