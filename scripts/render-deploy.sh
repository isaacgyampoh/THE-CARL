#!/usr/bin/env bash
#
# Deploys one Render service and waits for the result.
#
# This replaces a deploy hook. A hook is a URL with a key baked into it, which means one
# secret per service, and it answers 200 the moment Render accepts the request — it says
# nothing about whether the build succeeded. The workflow papered over that by sleeping and
# then polling a health endpoint, which answers from the OLD container until the swap, so a
# failed build could look like a slow one.
#
# Going through the API instead means a single RENDER_API_KEY for any number of services, and
# a deploy id to poll, so "did this build succeed" has a real answer rather than an inferred
# one. Service ids are not secret and live in the workflow.
#
# Usage: render-deploy.sh <service-id> <human name>
# Requires: RENDER_API_KEY in the environment.

set -euo pipefail

SERVICE_ID="${1:?service id required}"
NAME="${2:-$SERVICE_ID}"

: "${RENDER_API_KEY:?RENDER_API_KEY is empty or unset. It is a Render API key (rnd_…),
created under Account Settings → API Keys, and stored as a repository secret.}"

API="https://api.render.com/v1"
AUTH=(-H "Authorization: Bearer ${RENDER_API_KEY}")

# A deploy is a state machine, and only some of its states are endings. Anything not listed
# here is treated as still in flight; a status Render adds later will simply be polled rather
# than mistaken for success.
TERMINAL_OK="live"
TERMINAL_BAD="build_failed update_failed canceled pre_deploy_failed"

field() { python3 -c "import sys,json; print(json.load(sys.stdin).get('$1',''))"; }

echo "==> Deploying ${NAME} (${SERVICE_ID})"

# clearCache=do_not_clear keeps Docker layer caching, which is the difference between a
# three-minute deploy and a twelve-minute one. A build that needs a clean cache is rare
# enough to be worth doing by hand from the dashboard.
response=$(curl -fsS -X POST "${AUTH[@]}" \
  -H "Content-Type: application/json" \
  -d '{"clearCache":"do_not_clear"}' \
  "${API}/services/${SERVICE_ID}/deploys")

deploy_id=$(printf '%s' "$response" | field id)
if [ -z "$deploy_id" ]; then
  echo "Render accepted the request but returned no deploy id:" >&2
  printf '%s\n' "$response" >&2
  exit 1
fi

commit=$(printf '%s' "$response" | python3 -c \
  "import sys,json; print((json.load(sys.stdin).get('commit') or {}).get('id','')[:7])")
echo "    deploy ${deploy_id} at ${commit:-unknown commit}"

# Twenty minutes. A cold build of these images runs about four; a migration on a large table
# is the thing that could legitimately take longer, and hanging forever helps nobody.
deadline=$(( SECONDS + 1200 ))
status=""
while [ "$SECONDS" -lt "$deadline" ]; do
  status=$(curl -fsS "${AUTH[@]}" "${API}/services/${SERVICE_ID}/deploys/${deploy_id}" | field status)

  case " $TERMINAL_BAD " in
    *" $status "*)
      echo "    ${NAME} deploy ${status}." >&2
      echo "    Logs: https://dashboard.render.com/web/${SERVICE_ID}/deploys/${deploy_id}" >&2
      exit 1
      ;;
  esac

  if [ "$status" = "$TERMINAL_OK" ]; then
    echo "    ${NAME} is live."
    exit 0
  fi

  echo "    ${status}…"
  sleep 15
done

echo "    ${NAME} did not finish deploying within 20 minutes (last status: ${status})." >&2
echo "    It may still be building: https://dashboard.render.com/web/${SERVICE_ID}" >&2
exit 1
