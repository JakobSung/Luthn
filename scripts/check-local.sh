#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

env_file="$repo_root/.env"
base_url="${LUTHN_LOCAL_BASE_URL:-http://localhost:8080}"

if [[ -f "$env_file" ]]; then
  set -a
  # shellcheck disable=SC1090
  source "$env_file"
  set +a
fi

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "missing required command: $1" >&2
    exit 1
  fi
}

require_docker_daemon() {
  if docker info >/dev/null 2>&1; then
    return 0
  fi

  cat >&2 <<'ERROR'
Docker is installed, but the Docker daemon is not reachable.

If you use OrbStack, start OrbStack and wait until Docker is running.
If you use Docker Desktop, start Docker Desktop.
If your Docker context points at a stale socket, switch to a working context:
  docker context ls
  docker context use <context-name>
ERROR
  exit 1
}

require_command docker
require_command curl
require_docker_daemon

echo "Docker Compose services:"
docker compose ps

echo
echo "Luthn health:"
curl -fsS "$base_url/healthz"

echo
echo
echo "Luthn readiness:"
classification_ready=true
ready_body="$(mktemp)"
ready_status="$(curl -sS -o "$ready_body" -w '%{http_code}' "$base_url/readyz")"
cat "$ready_body"
if [[ "$ready_status" != "200" ]]; then
  if grep -q 'classification-provider' "$ready_body" &&
    grep -Eq 'No classification provider is configured|The mock classification provider is disabled|Production classification requires an operator-configured non-mock provider' "$ready_body"; then
    classification_ready=false
    echo
    echo "warning: readiness is not_ready until an operator classification provider is configured."
    echo "continuing smoke tests that do not require classification."
  else
    rm -f "$ready_body"
    exit 1
  fi
fi
rm -f "$ready_body"

echo
echo
echo "Agent context-pack smoke:"
if [[ -n "${LUTHN_SERVICE_VALUE:-}" ]]; then
  curl -fsS -X POST "$base_url/api/agent/context-packs" \
    -H 'content-type: application/json' \
    -H "Authorization: Bearer $LUTHN_SERVICE_VALUE" \
    --data '{"query":"demo runbook","coreTags":["demo"],"maxItems":20}'
else
  echo "skipped: .env does not contain LUTHN_SERVICE_VALUE"
fi

echo
echo
echo "Agent turn-summary intake smoke:"
if [[ "$classification_ready" != "true" ]]; then
  echo "skipped: classification provider is not ready"
elif [[ -n "${LUTHN_SERVICE_VALUE:-}" ]]; then
  curl -fsS -X POST "$base_url/api/agent/turn-summaries" \
    -H 'content-type: application/json' \
    -H "Authorization: Bearer $LUTHN_SERVICE_VALUE" \
    --data '{"sessionId":"local-check","turnId":"turn-1","sourceAgent":"codex","summary":"Public-safe local check summary.","coreTags":["demo"],"idempotencyKey":"local-check-turn-1"}'
else
  echo "skipped: .env does not contain LUTHN_SERVICE_VALUE"
fi

echo
echo
echo "Agent connection status read smoke:"
if [[ -n "${LUTHN_SERVICE_VALUE:-}" ]]; then
  curl -fsS "$base_url/api/agent-connections" \
    -H "Authorization: Bearer $LUTHN_SERVICE_VALUE"
else
  echo "skipped: .env does not contain LUTHN_SERVICE_VALUE"
fi

echo
echo
echo "Operator console:"
echo "$base_url/"
