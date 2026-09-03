#!/usr/bin/env bash
#
# Starts a Zazi stack for testing: PostgreSQL, the API and the web dashboard.
#
# Intended for a pilot or a demo on one machine. It is not a deployment: migrations are
# applied here for convenience, whereas a real deployment runs them as a separate, reviewable
# step (see docs/MIGRATION_SAFETY.md).
#
# Usage: scripts/start-stack.sh [--port-db 55433] [--port-api 5055] [--port-web 5080]

set -euo pipefail

DB_PORT=55433
API_PORT=5055
WEB_PORT=5080
DB_NAME="${ZAZI_DB_NAME:-zazi}"
PG_ROOT="${ZAZI_PG_ROOT:-$HOME/.zazi-testdb}"

while [ $# -gt 0 ]; do
    case "$1" in
        --port-db)  DB_PORT="$2"; shift 2 ;;
        --port-api) API_PORT="$2"; shift 2 ;;
        --port-web) WEB_PORT="$2"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 1 ;;
    esac
done

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# `dotnet ef` spawns further `dotnet` processes resolved from PATH, so pointing at the right
# binary is not enough: the directory has to lead. Without this, a system-wide SDK shadows
# the one global.json pins and the migration step fails with an SDK-not-found error that
# looks nothing like the actual problem.
PATH="$(dirname "$DOTNET"):$PATH"
export PATH

# The signing key must be stable across the API and the web application, or a session issued
# by one is meaningless to the other. Generated once and kept beside the database.
KEY_FILE="$PG_ROOT/signing.key"

echo "Zazi — starting a test stack"
echo

# ─── PostgreSQL ──────────────────────────────────────────────────────────────
# The port is checked rather than assumed. Another project's cluster answers a connection
# happily and then rejects the role, which reads like a broken application.
if lsof -nP -iTCP:"$DB_PORT" -sTCP:LISTEN >/dev/null 2>&1; then
    owner_pid="$(lsof -nP -iTCP:"$DB_PORT" -sTCP:LISTEN | awk 'NR==2 {print $2}')"
    owner_cmd="$(ps -o command= -p "$owner_pid" 2>/dev/null || true)"

    case "$owner_cmd" in
        *"$PG_ROOT"*) echo "  postgres  already running on $DB_PORT" ;;
        *)
            echo "  postgres  port $DB_PORT is held by something else:" >&2
            echo "            $owner_cmd" >&2
            echo "            Choose another port with --port-db." >&2
            exit 1 ;;
    esac
else
    if [ ! -d "$PG_ROOT/pgdata" ]; then
        echo "  postgres  no cluster at $PG_ROOT/pgdata." >&2
        echo "            See docs/TESTING.md for one-time setup." >&2
        exit 1
    fi

    mkdir -p /tmp/zazipg
    "$PG_ROOT/pgdist/bin/pg_ctl" -D "$PG_ROOT/pgdata" -l "$PG_ROOT/pg.log" \
        -o "-p $DB_PORT -c listen_addresses=127.0.0.1 -c unix_socket_directories=/tmp/zazipg -c max_connections=400" \
        start >/dev/null
    sleep 3
    echo "  postgres  started on $DB_PORT"
fi

if [ ! -f "$KEY_FILE" ]; then
    umask 077
    head -c 48 /dev/urandom | base64 > "$KEY_FILE"
    echo "  key       generated at $KEY_FILE"
fi

export ZAZI_JWT_KEY="$(cat "$KEY_FILE")"
export ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=$DB_PORT;Database=$DB_NAME;Username=carl;Password=carl-test-password"

# ─── Schema ──────────────────────────────────────────────────────────────────
echo "  schema    applying migrations"
"$DOTNET" ef database update \
    --project "$ROOT/src/Zazi.Infrastructure" \
    --startup-project "$ROOT/src/Zazi.Api" >/dev/null 2>&1 \
    || {
        echo "  schema    migration failed. Re-run without the redirect to see why:" >&2
        echo "            $DOTNET ef database update --project src/Zazi.Infrastructure --startup-project src/Zazi.Api" >&2
        exit 1
    }

# ─── Services ────────────────────────────────────────────────────────────────
export ASPNETCORE_ENVIRONMENT=Development

ASPNETCORE_URLS="http://0.0.0.0:$API_PORT" \
    nohup "$DOTNET" run --project "$ROOT/src/Zazi.Api" --no-launch-profile > /tmp/zazi-api.log 2>&1 &

ASPNETCORE_URLS="http://0.0.0.0:$WEB_PORT" \
    nohup "$DOTNET" run --project "$ROOT/src/Zazi.Web" --no-launch-profile > /tmp/zazi-web.log 2>&1 &

wait_for() {
    local url="$1" name="$2"
    for _ in $(seq 1 60); do
        if curl -s -o /dev/null --max-time 2 "$url"; then
            echo "  $name"
            return 0
        fi
        sleep 2
    done
    echo "  $name failed to become ready — see the log" >&2
    return 1
}

wait_for "http://127.0.0.1:$API_PORT/ready" "api       ready on $API_PORT   (log: /tmp/zazi-api.log)"
wait_for "http://127.0.0.1:$WEB_PORT/ready" "web       ready on $WEB_PORT   (log: /tmp/zazi-web.log)"

echo
echo "Dashboard   http://127.0.0.1:$WEB_PORT"
echo "API         http://127.0.0.1:$API_PORT"
echo
echo "If this is a new database, create the first owner:"
echo
echo "  ConnectionStrings__DefaultConnection=\"\$ConnectionStrings__DefaultConnection\" \\"
echo "  ZAZI_JWT_KEY=\"\$(cat $KEY_FILE)\" \\"
echo "  $DOTNET run --project src/Zazi.Bootstrap -- \\"
echo "    --organization \"Your Business\" --branch \"Main\" \\"
echo "    --owner-email owner@example.test --owner-password '…'"
echo
echo "Stop with: scripts/stop-stack.sh"
