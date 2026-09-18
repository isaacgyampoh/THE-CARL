#!/usr/bin/env bash
#
# Production backup. Run from cron or a systemd timer, as root.
#
#   sudo bash /opt/zazi/scripts/backup.sh
#
# Backs up three things, and it is three rather than one for a reason:
#
#   1. The database — the transactions themselves.
#   2. /etc/zazi — the signing key and connection string. Restore a database without
#      the JWT key and every agent's session is void; they can sign in again, but the
#      dashboard cannot validate anything issued before.
#   3. /var/lib/zazi/keys — the data-protection key ring. Lose it and every signed-in
#      owner is signed out, and old antiforgery tokens stop validating.
#
# The database alone is the backup people take and the other two are what they wish they
# had taken.
#
# This uses pg_dump, which runs against a live server and needs no outage — unlike the
# pilot script in scripts/, which takes a cold copy because the embedded distribution
# ships no pg_dump at all.

set -euo pipefail

DEST="${1:-/var/backups/zazi}"
KEEP_DAYS="${ZAZI_BACKUP_KEEP_DAYS:-14}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"

install -d -o root -g root -m 700 "$DEST"

# ─── Database ────────────────────────────────────────────────────────────────
# Custom format: compressed, and restorable selectively with pg_restore.
sudo -u postgres pg_dump --format=custom --no-owner --dbname=zazi \
    > "${DEST}/zazi-${STAMP}.dump"

# ─── Configuration and keys ──────────────────────────────────────────────────
# Contains secrets, so the archive is created with a restrictive mode rather than
# fixed afterwards — there is no window where it is world-readable.
( umask 077 && tar -czf "${DEST}/zazi-config-${STAMP}.tar.gz" \
    -C / etc/zazi var/lib/zazi/keys )

chmod 600 "${DEST}/zazi-${STAMP}.dump" "${DEST}/zazi-config-${STAMP}.tar.gz"

# ─── Offsite ─────────────────────────────────────────────────────────────────
# A backup on the same machine as the database protects against a mistake, not against
# losing the machine. Set ZAZI_BACKUP_REMOTE to an rsync destination and each backup is
# copied there too.
if [ -n "${ZAZI_BACKUP_REMOTE:-}" ]; then
    rsync -a --chmod=600 \
        "${DEST}/zazi-${STAMP}.dump" \
        "${DEST}/zazi-config-${STAMP}.tar.gz" \
        "${ZAZI_BACKUP_REMOTE}/"
fi

# ─── Retention ───────────────────────────────────────────────────────────────
find "$DEST" -name 'zazi-*' -type f -mtime "+${KEEP_DAYS}" -delete

echo "Backed up to ${DEST} (kept ${KEEP_DAYS} days)."
echo
echo "A backup nobody has restored is a hope, not a backup. Test one:"
echo "  sudo -u postgres pg_restore --list ${DEST}/zazi-${STAMP}.dump | head"
