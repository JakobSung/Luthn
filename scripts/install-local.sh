#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

mode="${1:-self-host}"
env_file="$repo_root/.env"
env_example="$repo_root/.env.example"

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

append_env_if_missing() {
  local key="$1"
  local value="$2"

  if ! grep -q "^${key}=" "$env_file"; then
    printf '%s=%s\n' "$key" "$value" >> "$env_file"
  fi
}

ensure_local_connector_scopes() {
  local read_present=false
  local write_present=false
  local access_request_present=false
  local wildcard_present=false
  local scope_value
  local scope_index
  local empty_indices=()

  for scope_index in {0..15}; do
    scope_value="$(awk -F= -v key="Luthn__Auth__Tokens__0__Scopes__${scope_index}" \
      '$1 == key { sub(/^[^=]*=/, ""); print; exit }' "$env_file")"
    case "$scope_value" in
      '*') wildcard_present=true ;;
      agent.connection.read) read_present=true ;;
      agent.connection.write) write_present=true ;;
      access.request) access_request_present=true ;;
      '') empty_indices+=("$scope_index") ;;
    esac
  done

  if [[ "$wildcard_present" == "true" \
    || ( "$read_present" == "true" && "$write_present" == "true" && "$access_request_present" == "true" ) ]]; then
    return 0
  fi

  local required_slots=0
  [[ "$read_present" == "true" ]] || required_slots=$((required_slots + 1))
  [[ "$write_present" == "true" ]] || required_slots=$((required_slots + 1))
  [[ "$access_request_present" == "true" ]] || required_slots=$((required_slots + 1))
  if (( ${#empty_indices[@]} < required_slots )); then
    echo "not enough free service-token scope slots are available for agent connector scopes" >&2
    return 1
  fi

  local key1=""
  local value1=""
  local key2=""
  local value2=""
  local key3=""
  local value3=""
  local next_slot=0
  if [[ "$read_present" != "true" ]]; then
    key1="Luthn__Auth__Tokens__0__Scopes__${empty_indices[$next_slot]}"
    value1="agent.connection.read"
    next_slot=$((next_slot + 1))
  fi
  if [[ "$write_present" != "true" ]]; then
    if [[ -z "$key1" ]]; then
      key1="Luthn__Auth__Tokens__0__Scopes__${empty_indices[$next_slot]}"
      value1="agent.connection.write"
    else
      key2="Luthn__Auth__Tokens__0__Scopes__${empty_indices[$next_slot]}"
      value2="agent.connection.write"
    fi
    next_slot=$((next_slot + 1))
  fi
  if [[ "$access_request_present" != "true" ]]; then
    if [[ -z "$key1" ]]; then
      key1="Luthn__Auth__Tokens__0__Scopes__${empty_indices[$next_slot]}"
      value1="access.request"
    elif [[ -z "$key2" ]]; then
      key2="Luthn__Auth__Tokens__0__Scopes__${empty_indices[$next_slot]}"
      value2="access.request"
    else
      key3="Luthn__Auth__Tokens__0__Scopes__${empty_indices[$next_slot]}"
      value3="access.request"
    fi
  fi

  local env_tmp
  env_tmp="$(mktemp "${env_file}.connector-scopes.XXXXXX")" || return 1
  if ! awk -F= -v key1="$key1" -v value1="$value1" -v key2="$key2" -v value2="$value2" -v key3="$key3" -v value3="$value3" '
    BEGIN { updated1 = 0; updated2 = 0; updated3 = 0 }
    key1 != "" && $1 == key1 { print key1 "=" value1; updated1 = 1; next }
    key2 != "" && $1 == key2 { print key2 "=" value2; updated2 = 1; next }
    key3 != "" && $1 == key3 { print key3 "=" value3; updated3 = 1; next }
    { print }
    END {
      if (key1 != "" && !updated1) print key1 "=" value1
      if (key2 != "" && !updated2) print key2 "=" value2
      if (key3 != "" && !updated3) print key3 "=" value3
    }
  ' "$env_file" >"$env_tmp" \
    || ! chmod 0600 "$env_tmp" \
    || ! mv "$env_tmp" "$env_file"; then
    rm -f "$env_tmp"
    return 1
  fi
}

generate_local_token() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex 24
    return 0
  fi

  if command -v uuidgen >/dev/null 2>&1; then
    uuidgen | tr '[:upper:]' '[:lower:]'
    return 0
  fi

  printf 'luthn-local-%s-%s-%s\n' "$(date +%s)" "$RANDOM" "$RANDOM"
}

ensure_local_service_token() {
  local token="${LUTHN_SERVICE_VALUE:-}"

  if [[ -z "$token" ]]; then
    token="$(generate_local_token)"
    append_env_if_missing "LUTHN_SERVICE_VALUE" "$token"
  fi

  local digest="${Luthn__Auth__Tokens__0__Sha256Digest:-}"
  if [[ -z "$digest" ]]; then
    digest="$(printf '%s' "$token" | dotnet run --no-build --project src/Luthn.Tools -- token-digest --stdin)"
    append_env_if_missing "Luthn__Auth__Tokens__0__Sha256Digest" "$digest"
  fi

  append_env_if_missing "Luthn__Auth__RequireServiceToken" "true"
  append_env_if_missing "Luthn__Identity__Mode" "SingleOwner"
  append_env_if_missing "Luthn__Identity__SingleOwnerUserId" "local-owner"
  append_env_if_missing "Luthn__Auth__Tokens__0__Name" "local-agent"
  append_env_if_missing "Luthn__Auth__Tokens__0__UserId" "local-owner"
  append_env_if_missing "Luthn__Auth__Tokens__0__IsOperator" "false"
  append_env_if_missing "Luthn__Auth__Tokens__0__Scopes__0" "agent.read"
  append_env_if_missing "Luthn__Auth__Tokens__0__Scopes__1" "agent.write.summary"
  append_env_if_missing "Luthn__Auth__Tokens__0__Scopes__2" "memory.write"
  append_env_if_missing "Luthn__Auth__Tokens__0__Scopes__3" "memory.read"
  append_env_if_missing "Luthn__Auth__Tokens__0__Scopes__4" "classification.preview"
  ensure_local_connector_scopes
}

print_usage() {
  cat <<'USAGE'
usage: scripts/install-local.sh [self-host|testing]

self-host  Build the solution, start the local PostgreSQL stack, apply
           migrations, seed demo data, and start the API at http://localhost:8080.
testing    Build the solution and print the credential-free in-memory API
           command for http://127.0.0.1:5089.
USAGE
}

wait_for_postgres() {
  local attempts=30
  local delay_seconds=2

  for ((attempt = 1; attempt <= attempts; attempt++)); do
    if docker compose --env-file "$env_file" exec -T postgres \
      pg_isready -U "${POSTGRES_USER:-luthn}" -d "${POSTGRES_DB:-luthn}" >/dev/null 2>&1; then
      return 0
    fi

    sleep "$delay_seconds"
  done

  echo "postgres did not become ready within $((attempts * delay_seconds)) seconds" >&2
  docker compose --env-file "$env_file" ps postgres >&2 || true
  exit 1
}

case "$mode" in
  -h|--help|help)
    print_usage
    exit 0
    ;;
  self-host|testing)
    ;;
  *)
    echo "unknown install mode: $mode" >&2
    print_usage >&2
    exit 2
    ;;
esac

require_command dotnet

if [[ ! -f "$env_file" ]]; then
  if [[ ! -f "$env_example" ]]; then
    echo "missing .env.example; cannot create local .env" >&2
    exit 1
  fi
  cp "$env_example" "$env_file"
  echo "Created .env from .env.example"
fi

set -a
# shellcheck disable=SC1090
source "$env_file"
set +a

connection_string="${LUTHN_LOCAL_CONNECTION_STRING:-Host=localhost;Port=5432;Database=luthn;Username=luthn}"

echo "Restoring .NET packages..."
dotnet restore Luthn.sln

echo "Building Luthn..."
dotnet build Luthn.sln --no-restore

if [[ "$mode" == "testing" ]]; then
  cat <<'NEXT'

Local testing mode is ready.

Run:
  DOTNET_ENVIRONMENT=Testing dotnet run --project src/Luthn.Host.Api/Luthn.Host.Api.csproj --urls http://127.0.0.1:5089

Then open:
  http://127.0.0.1:5089/
NEXT
  exit 0
fi

require_command docker
require_docker_daemon
if ! docker compose version >/dev/null 2>&1; then
  echo "docker compose plugin is required" >&2
  exit 1
fi

echo "Configuring local agent API token..."
ensure_local_service_token

set -a
# shellcheck disable=SC1090
source "$env_file"
set +a

echo "Starting local PostgreSQL..."
docker compose --env-file "$env_file" up -d postgres

echo "Waiting for PostgreSQL to accept connections..."
wait_for_postgres

echo "Applying database migrations..."
ConnectionStrings__LuthnDb="$connection_string" \
  dotnet run --no-build --project src/Luthn.Tools -- migrate-db

echo "Seeding public-safe demo data..."
ConnectionStrings__LuthnDb="$connection_string" \
  dotnet run --no-build --project src/Luthn.Tools -- seed-demo

echo "Starting Luthn API..."
docker compose --env-file "$env_file" up --build -d api

cat <<'NEXT'

Luthn local self-host is ready.

Open:
  http://localhost:8080/

Check:
  ./scripts/check-local.sh
NEXT
