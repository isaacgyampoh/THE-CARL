#!/usr/bin/env bash
# Stops the API, the web dashboard and the test PostgreSQL started by start-stack.sh.
set -uo pipefail

PG_ROOT="${ZAZI_PG_ROOT:-$HOME/.zazi-testdb}"
DB_PORT="${1:-55433}"

pkill -f "Zazi.Api" 2>/dev/null && echo "  api       stopped"
pkill -f "Zazi.Web" 2>/dev/null && echo "  web       stopped"

if [ -d "$PG_ROOT/pgdata" ]; then
    "$PG_ROOT/pgdist/bin/pg_ctl" -D "$PG_ROOT/pgdata" -o "-p $DB_PORT" stop >/dev/null 2>&1 \
        && echo "  postgres  stopped"
fi

exit 0
