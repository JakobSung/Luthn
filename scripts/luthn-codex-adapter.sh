#!/usr/bin/env bash
set -euo pipefail

env_luthn_base_url="${LUTHN_BASE_URL:-}"
env_luthn_local_base_url="${LUTHN_LOCAL_BASE_URL:-}"
env_luthn_service_bearer="${LUTHN_SERVICE_BEARER:-}"
env_luthn_service_value="${LUTHN_SERVICE_VALUE:-}"

if [[ ( -z "$env_luthn_base_url" && -z "$env_luthn_local_base_url" ) || ( -z "$env_luthn_service_bearer" && -z "$env_luthn_service_value" ) ]] && [[ -f ".env" ]]; then
  set -a
  # shellcheck disable=SC1091
  source ".env"
  set +a

  [[ -n "$env_luthn_base_url" ]] && LUTHN_BASE_URL="$env_luthn_base_url"
  [[ -n "$env_luthn_local_base_url" ]] && LUTHN_LOCAL_BASE_URL="$env_luthn_local_base_url"
  [[ -n "$env_luthn_service_bearer" ]] && LUTHN_SERVICE_BEARER="$env_luthn_service_bearer"
  [[ -n "$env_luthn_service_value" ]] && LUTHN_SERVICE_VALUE="$env_luthn_service_value"
fi

base_url="${LUTHN_BASE_URL:-${LUTHN_LOCAL_BASE_URL:-http://localhost:8080}}"
bearer="${LUTHN_SERVICE_BEARER:-${LUTHN_SERVICE_VALUE:-}}"

if [[ -z "$bearer" ]]; then
  echo "missing LUTHN_SERVICE_BEARER or LUTHN_SERVICE_VALUE" >&2
  exit 2
fi

payload="$(cat)"
if [[ -z "${payload//[[:space:]]/}" ]]; then
  echo "expected turn-summary JSON payload on stdin" >&2
  exit 2
fi

curl -fsS -X POST "$base_url/api/agent/turn-summaries" \
  -H 'content-type: application/json' \
  -H "Authorization: Bearer $bearer" \
  --data "$payload" >/dev/null || {
    echo "warning: Luthn turn-summary upload failed" >&2
    exit 0
  }
