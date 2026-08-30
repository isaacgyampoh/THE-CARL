#!/usr/bin/env bash
#
# Confirms a running API is genuinely backed by PostgreSQL before end-to-end verification.
#
# This exists because of two real misdiagnoses. Once the connection string was supplied as
# ConnectionStrings__Default instead of ConnectionStrings__DefaultConnection, so the API
# silently started on its in-memory store and every login failed with "0-candidate email" —
# which looks exactly like a broken client. Once another project's PostgreSQL held the port,
# so the API connected to a cluster that had never heard of this tenant.
#
# A green result here means: the API is up, it is talking to PostgreSQL, and that database
# contains the schema. It deliberately makes no claim about test data.
#
# Usage: scripts/verify-e2e-env.sh [base-url] [pg-host] [pg-port]

set -uo pipefail

BASE_URL="${1:-http://127.0.0.1:5055}"
PG_HOST="${2:-127.0.0.1}"
PG_PORT="${3:-55433}"

failures=0

fail() { printf '  FAIL  %s\n' "$1"; failures=$((failures + 1)); }
pass() { printf '  ok    %s\n' "$1"; }

echo "Checking end-to-end environment"
echo

# 1. The configuration key. The wrong one is not an error anywhere — it just yields null,
#    and the API quietly uses an in-memory store.
if [ -n "${ConnectionStrings__DefaultConnection:-}" ]; then
    pass "ConnectionStrings__DefaultConnection is set"
elif [ -n "${ConnectionStrings__Default:-}" ]; then
    fail "ConnectionStrings__Default is set, but the API reads DefaultConnection. It will run in memory."
else
    printf '  note  ConnectionStrings__DefaultConnection not set in this shell (may be set in the API process)\n'
fi

# 2. PostgreSQL is actually listening.
if nc -z -w 3 "$PG_HOST" "$PG_PORT" 2>/dev/null; then
    pass "PostgreSQL reachable at $PG_HOST:$PG_PORT"
else
    fail "No PostgreSQL at $PG_HOST:$PG_PORT"
fi

# 3. And it is the cluster this project expects, not another project's server on the same
#    port. A foreign cluster answers TCP happily and then rejects the role.
owner="$(lsof -nP -iTCP:"$PG_PORT" -sTCP:LISTEN 2>/dev/null | awk 'NR==2 {print $2}')"
if [ -n "$owner" ]; then
    datadir="$(ps -o command= -p "$owner" 2>/dev/null | tr ' ' '\n' | grep -A0 'pgdata' | head -1)"
    case "$datadir" in
        *thecarl*|*zazi*) pass "port $PG_PORT is served by Zazi's cluster" ;;
        "")        printf '  note  could not determine the data directory for pid %s\n' "$owner" ;;
        *)         fail "port $PG_PORT is served by another project's cluster: $datadir" ;;
    esac
fi

# 4. The API is up.
health="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$BASE_URL/health" 2>/dev/null)"
if [ "$health" = "200" ]; then
    pass "API healthy at $BASE_URL"
else
    fail "API not healthy at $BASE_URL (HTTP ${health:-none})"
fi

# 5. The decisive check: an anonymous login must be refused by a real user table. An
#    in-memory store answers 401 too, so this is necessary but not sufficient on its own —
#    it is the combination with the checks above that makes the environment trustworthy.
login="$(curl -s -o /dev/null -w '%{http_code}' --max-time 8 \
    -X POST "$BASE_URL/api/v1/auth/login" \
    -H 'Content-Type: application/json' \
    -d '{"email":"nobody@invalid.test","password":"not-a-real-password"}' 2>/dev/null)"
if [ "$login" = "401" ]; then
    pass "authentication endpoint reachable and refusing unknown users"
else
    fail "unexpected login status ${login:-none} (expected 401)"
fi

echo
if [ "$failures" -eq 0 ]; then
    echo "Environment looks correct for end-to-end verification."
    exit 0
fi

echo "$failures check(s) failed. Do not trust end-to-end results from this environment."
exit 1
