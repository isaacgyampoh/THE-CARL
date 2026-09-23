#!/usr/bin/env bash
#
# Takes a logical backup of the production database on Render, and proves it exists.
#
# Render keeps point-in-time recovery for the database itself, which covers "restore the
# database as it was at 14:05". This covers the other case: a copy that can be downloaded,
# kept elsewhere, and restored into a database somewhere else — including after somebody
# deletes the Render service entirely.
#
# Render retains each logical backup for seven days, so a copy that must outlive that has to
# be downloaded and stored somewhere of your own. Pass a directory to do that here.
#
# Usage:
#   RENDER_API_KEY=... scripts/render-backup.sh [download-directory]
#
# The restore procedure is in docs/PRODUCTION.md. A backup that has never been restored is a
# hope, not a backup.

set -euo pipefail

: "${RENDER_API_KEY:?RENDER_API_KEY is empty or unset.}"
DATABASE_ID="${ZAZI_DATABASE_ID:-dpg-dao1pjo473hc73b4e7vg-a}"
DOWNLOAD_TO="${1:-}"
API="https://api.render.com/v1/postgres/$DATABASE_ID"
AUTH=(-H "Authorization: Bearer $RENDER_API_KEY")

echo "==> asking Render for a backup of $DATABASE_ID"
before="$(curl -sf "${AUTH[@]}" "$API/export" | python3 -c 'import sys,json; print(len(json.load(sys.stdin)))')"
curl -sf -X POST "${AUTH[@]}" "$API/export" >/dev/null
echo "    requested; waiting for it to appear"

for _ in $(seq 1 40); do
    listing="$(curl -sf "${AUTH[@]}" "$API/export")"
    count="$(printf '%s' "$listing" | python3 -c 'import sys,json; print(len(json.load(sys.stdin)))')"
    if [ "$count" -gt "$before" ]; then
        created="$(printf '%s' "$listing" | python3 -c 'import sys,json; print(json.load(sys.stdin)[0]["createdAt"])')"
        echo "    backup created: $created"

        if [ -n "$DOWNLOAD_TO" ]; then
            mkdir -p "$DOWNLOAD_TO"
            url="$(printf '%s' "$listing" | python3 -c 'import sys,json; print(json.load(sys.stdin)[0]["url"])')"
            file="$DOWNLOAD_TO/zazi-$(date -u +%Y%m%dT%H%M%SZ).tar.gz"
            # The download link carries its own short-lived token; it is not a secret to keep.
            curl -sf -o "$file" "$url"
            size="$(wc -c < "$file" | tr -d ' ')"
            if [ "$size" -lt 1000 ]; then
                echo "    the downloaded file is suspiciously small ($size bytes)" >&2
                exit 1
            fi
            echo "    downloaded to $file ($size bytes)"
            echo "    this copy contains real business data — keep it somewhere private."
        fi

        exit 0
    fi
    sleep 15
done

echo "No backup appeared within ten minutes. Check the Render dashboard." >&2
exit 1
