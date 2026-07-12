#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
image="${1:-luthn:local}"
test_id="${LUTHN_TEST_ID:-$$}"
test_root="$(mktemp -d "${TMPDIR:-/tmp}/luthn-lifecycle.XXXXXX")"
port="$((18000 + test_id % 1000))"
original_home="$HOME"

export HOME="$test_root/home"
export DOCKER_CONFIG="${DOCKER_CONFIG:-$original_home/.docker}"
export LUTHN_DATA_DIR="$test_root/data"
export LUTHN_CONFIG_DIR="$test_root/config"
export LUTHN_STATE_DIR="$test_root/state"
export LUTHN_BIN_DIR="$test_root/bin"
export LUTHN_CLI_PATH="$test_root/bin/luthn"
export LUTHN_SERVICE_TOKEN_FILE="$test_root/config/service-token"
export LUTHN_PROJECT_NAME="luthn-test-$test_id"
export LUTHN_POSTGRES_VOLUME="luthn-test-$test_id-postgres"
export LUTHN_OPERATOR_VOLUME="luthn-test-$test_id-operator"
export LUTHN_PORT="$port"
export LUTHN_IMAGE="$image"
export LUTHN_SKIP_PULL=true
export LUTHN_SOURCE_BASE_URL="file://$repo_root"

mkdir -p "$HOME"
mkdir -p "$test_root/forbidden-bin"
for forbidden_command in git dotnet; do
  printf '#!/usr/bin/env bash\necho "host %s must not be invoked" >&2\nexit 99\n' \
    "$forbidden_command" >"$test_root/forbidden-bin/$forbidden_command"
  chmod 0755 "$test_root/forbidden-bin/$forbidden_command"
done
export PATH="$test_root/forbidden-bin:$PATH"

cleanup() {
  if [[ -f "$LUTHN_DATA_DIR/compose.yaml" && -f "$LUTHN_CONFIG_DIR/luthn.env" ]]; then
    docker compose \
      --project-name "$LUTHN_PROJECT_NAME" \
      --env-file "$LUTHN_CONFIG_DIR/luthn.env" \
      -f "$LUTHN_DATA_DIR/compose.yaml" \
      down --volumes --remove-orphans >/dev/null 2>&1 || true
  fi
  docker volume rm "$LUTHN_POSTGRES_VOLUME" "$LUTHN_OPERATOR_VOLUME" >/dev/null 2>&1 || true
  rm -rf "$test_root"
}
trap cleanup EXIT HUP INT TERM

cli="$LUTHN_CLI_PATH"
base_url="http://127.0.0.1:$port"

echo "[1/8] install"
"$repo_root/scripts/install.sh"
token_before="$(cat "$LUTHN_SERVICE_TOKEN_FILE")"
test -n "$token_before"
test "$(stat -f '%Lp' "$LUTHN_SERVICE_TOKEN_FILE" 2>/dev/null || stat -c '%a' "$LUTHN_SERVICE_TOKEN_FILE")" = "600"
test -x "$LUTHN_DATA_DIR/runtime/luthn-codex-connector.py"
if grep -Fq "$token_before" "$LUTHN_CONFIG_DIR/luthn.env"; then
  echo "service token leaked into Compose environment config" >&2
  exit 1
fi
curl -fsS "$base_url/healthz" >/dev/null
curl -fsS "$base_url/readyz" >/dev/null
console_html="$(curl -fsS "$base_url/")"
grep -q '<title>Luthn Operator Console</title>' <<<"$console_html"
preview_output="$(curl -fsS -X POST "$base_url/api/classification/preview" \
  -H 'content-type: application/json' \
  -H "Authorization: Bearer $token_before" \
  --data '{"sourceId":"lifecycle-preview","content":"Public lifecycle preview.","sourceType":"note"}')"
grep -q '"sourceId":"lifecycle-preview"' <<<"$preview_output"
connection_output="$(curl -fsS -X POST "$base_url/api/agent-connections/codex/observations" \
  -H 'content-type: application/json' \
  -H "Authorization: Bearer $token_before" \
  --data '{"agentName":"Codex","integrationKind":"host-hook-mcp","connectorVersion":"lifecycle","channels":[{"channel":"mcp","configured":true,"verificationState":"Verified","activityState":"Succeeded","failureCode":null}]}')"
grep -q '"agentId":"codex"' <<<"$connection_output"
connection_list="$(curl -fsS "$base_url/api/agent-connections" \
  -H "Authorization: Bearer $token_before")"
grep -q '"state":"Active"' <<<"$connection_list"

echo "[2/8] status and MCP"
status_output="$("$cli" status)"
grep -q 'Readiness: ready' <<<"$status_output"
mcp_output="$("$cli" mcp --list-tools)"
grep -q '^get_context_pack$' <<<"$mcp_output"

echo "[3/8] adapter write"
printf '%s\n' '{"sessionId":"lifecycle","turnId":"before-update","sourceAgent":"codex","summary":"Lifecycle sentinel remains after update.","coreTags":["lifecycle"],"idempotencyKey":"lifecycle-before-update"}' \
  | "$cli" adapter
context_output="$(curl -fsS -X POST "$base_url/api/agent/context-packs" \
  -H 'content-type: application/json' \
  -H "Authorization: Bearer $token_before" \
  --data '{"query":"Lifecycle sentinel","coreTags":["lifecycle"],"maxItems":20}')"
grep -q 'Lifecycle sentinel' <<<"$context_output"

echo "[4/8] update"
update_write_probe="$test_root/update-write-probe.sh"
cat >"$update_write_probe" <<EOF
#!/usr/bin/env bash
set -euo pipefail
if curl -fsS -X POST "$base_url/api/agent/turn-summaries" \\
  -H 'content-type: application/json' \\
  -H "Authorization: Bearer $token_before" \\
  --data '{"sessionId":"lifecycle","turnId":"during-update","sourceAgent":"codex","summary":"This write must not be accepted during update.","coreTags":["lifecycle"],"idempotencyKey":"lifecycle-during-update"}' >/dev/null 2>&1; then
  echo "write path accepted a turn summary during update backup/migration window" >&2
  exit 1
fi
EOF
chmod 0755 "$update_write_probe"
LUTHN_UPDATE_AFTER_STOP_HOOK="$update_write_probe" "$cli" update "$image"
token_after="$(cat "$LUTHN_SERVICE_TOKEN_FILE")"
test "$token_before" = "$token_after"
backup_path="$(awk -F= '$1 == "BACKUP_PATH" { print $2 }' "$LUTHN_STATE_DIR/install-state.env")"
test -s "$backup_path"
grep -q '^Luthn__Auth__Tokens__0__Scopes__4=classification.preview$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__Scopes__5=agent.connection.read$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__Scopes__6=agent.connection.write$' "$LUTHN_CONFIG_DIR/luthn.env"
context_output="$(curl -fsS -X POST "$base_url/api/agent/context-packs" \
  -H 'content-type: application/json' \
  -H "Authorization: Bearer $token_after" \
  --data '{"query":"Lifecycle sentinel","coreTags":["lifecycle"],"maxItems":20}')"
grep -q 'Lifecycle sentinel' <<<"$context_output"

echo "[5/8] reset guard"
if "$cli" reset >/dev/null 2>&1; then
  echo "reset unexpectedly succeeded without --yes" >&2
  exit 1
fi

echo "[6/8] reset"
"$cli" reset --yes
curl -fsS "$base_url/readyz" >/dev/null

echo "[7/8] uninstall preserves state"
"$cli" uninstall
test -f "$LUTHN_CONFIG_DIR/luthn.env"
test -f "$LUTHN_SERVICE_TOKEN_FILE"
test -d "$LUTHN_STATE_DIR/backups"
test ! -e "$LUTHN_CLI_PATH"
docker volume inspect "$LUTHN_POSTGRES_VOLUME" >/dev/null
docker volume inspect "$LUTHN_OPERATOR_VOLUME" >/dev/null

echo "[8/8] reinstall and purge"
"$repo_root/scripts/install.sh"
if "$cli" uninstall --purge-data >/dev/null 2>&1; then
  echo "purge unexpectedly succeeded without --yes" >&2
  exit 1
fi
"$cli" uninstall --purge-data --yes
test ! -e "$LUTHN_CONFIG_DIR"
test ! -e "$LUTHN_STATE_DIR"
test ! -e "$LUTHN_CLI_PATH"
if docker volume inspect "$LUTHN_POSTGRES_VOLUME" >/dev/null 2>&1; then
  echo "PostgreSQL volume still exists after purge" >&2
  exit 1
fi
if docker volume inspect "$LUTHN_OPERATOR_VOLUME" >/dev/null 2>&1; then
  echo "operator volume still exists after purge" >&2
  exit 1
fi

echo "distribution lifecycle passed"
