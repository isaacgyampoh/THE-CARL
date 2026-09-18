#!/usr/bin/env bash
#
# Checks that Zazi is actually serving, and exits non-zero when it is not.
#
# This is not an alerting system. It is the part an alerting system needs: something that
# knows what "healthy" means here and says so in an exit code. Point an uptime monitor at
# the health endpoints, or run this from a systemd timer with OnFailure= pointing at
# whatever notifies you — see deploy/zazi-healthcheck.{service,timer}.
#
# /health says the process is up. /ready says it can reach its database, which is the
# distinction that matters: a process answering /health while its database is gone is the
# failure people actually hit, and it looks like health.
#
# Usage:
#   scripts/healthcheck.sh                       # defaults to the local pilot stack
#   ZAZI_API_URL=https://api.example \
#   ZAZI_WEB_URL=https://app.example scripts/healthcheck.sh

set -uo pipefail

API="${ZAZI_API_URL:-http://127.0.0.1:5055}"
WEB="${ZAZI_WEB_URL:-http://127.0.0.1:5080}"
TIMEOUT="${ZAZI_HEALTH_TIMEOUT:-10}"

FAILED=0

check() {
    local label="$1" url="$2"
    local code

    # --fail is deliberately not used: the status code is the information, and curl's own
    # failure modes (DNS, refused, TLS, timeout) need to be distinguishable from a 503.
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time "$TIMEOUT" "$url" 2>/dev/null)"

    if [ "$code" = "200" ]; then
        printf '  ok    %-28s %s\n' "$label" "$url"
    elif [ -z "$code" ] || [ "$code" = "000" ]; then
        printf '  FAIL  %-28s %s (unreachable)\n' "$label" "$url"
        FAILED=1
    else
        printf '  FAIL  %-28s %s (HTTP %s)\n' "$label" "$url" "$code"
        FAILED=1
    fi
}

echo "Checking Zazi"
check "api process"        "$API/health"
check "api and database"   "$API/ready"
check "dashboard process"  "$WEB/health"
check "dashboard database" "$WEB/ready"

if [ "$FAILED" -ne 0 ]; then
    echo ""
    echo "Zazi is not fully healthy." >&2
    exit 1
fi

echo ""
echo "Zazi is healthy."
