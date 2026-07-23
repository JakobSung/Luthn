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
export LUTHN_OPERATOR_TOKEN_FILE="$test_root/config/operator-token"
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

operator_key_manifest() {
  docker run --rm \
    --volume "$LUTHN_OPERATOR_VOLUME:/operator:ro" \
    --entrypoint sh \
    "$image" \
    -c 'find /operator/keys -maxdepth 1 -type f -name "key-*.xml" -exec sha256sum {} \; | sort'
}

echo "[1/8] install"
"$repo_root/scripts/install.sh"
token_before="$(cat "$LUTHN_SERVICE_TOKEN_FILE")"
operator_token_before="$(cat "$LUTHN_OPERATOR_TOKEN_FILE")"
test -n "$token_before"
test -n "$operator_token_before"
test "$token_before" != "$operator_token_before"
test "$(stat -f '%Lp' "$LUTHN_SERVICE_TOKEN_FILE" 2>/dev/null || stat -c '%a' "$LUTHN_SERVICE_TOKEN_FILE")" = "600"
test "$(stat -f '%Lp' "$LUTHN_OPERATOR_TOKEN_FILE" 2>/dev/null || stat -c '%a' "$LUTHN_OPERATOR_TOKEN_FILE")" = "600"
test -x "$LUTHN_DATA_DIR/runtime/luthn-codex-connector.py"
if grep -Fq "$token_before" "$LUTHN_CONFIG_DIR/luthn.env"; then
  echo "service token leaked into Compose environment config" >&2
  exit 1
fi
if grep -Fq "$operator_token_before" "$LUTHN_CONFIG_DIR/luthn.env"; then
  echo "operator token leaked into Compose environment config" >&2
  exit 1
fi
grep -q '^Luthn__Auth__Tokens__1__Name=local-operator$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__1__IsOperator=true$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__1__Scopes__0=access.decide$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__1__Scopes__1=config.write$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Identity__Mode=SingleOwner$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Identity__SingleOwnerUserId=local-owner$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__UserId=local-owner$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__IsOperator=false$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__Scopes__8=metrics.write$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Identity__Mode=SingleOwner$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Identity__SingleOwnerUserId=local-owner$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__UserId=local-owner$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__IsOperator=false$' "$LUTHN_CONFIG_DIR/luthn.env"
operator_key_manifest_before="$(operator_key_manifest)"
test -n "$operator_key_manifest_before"
grep -q '^LUTHN_ENVIRONMENT=Production$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Classification__Provider=mock$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Classification__AllowMock=true$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Memory__AutomaticTurnRetentionDays=30$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Memory__AutomaticTurnCleanupEnabled=true$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Memory__AutomaticTurnCleanupIntervalMinutes=60$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Memory__AutomaticTurnCleanupBatchSize=100$' "$LUTHN_CONFIG_DIR/luthn.env"
evaluation_output="$(docker run --rm "$image" classification-eval)"
grep -q '"datasetVersion": 1' <<<"$evaluation_output"
grep -q '"provider": "mock"' <<<"$evaluation_output"
curl -fsS "$base_url/healthz" >/dev/null
operator_config_body="$test_root/operator-config.json"
operator_config_status="$(curl -sS -o "$operator_config_body" -w '%{http_code}' "$base_url/api/operator/classification-provider" \
  -H "Authorization: Bearer $operator_token_before")"
test "$operator_config_status" = "200"
grep -q '"provider"' "$operator_config_body"
agent_config_body="$test_root/agent-config.json"
agent_config_status="$(curl -sS -o "$agent_config_body" -w '%{http_code}' "$base_url/api/operator/classification-provider" \
  -H "Authorization: Bearer $token_before")"
test "$agent_config_status" = "403"
grep -q '"status":403' "$agent_config_body"
curl -fsS "$base_url/readyz" >/dev/null
console_html="$(curl -fsS "$base_url/")"
grep -q '<title>Luthn Operator Console</title>' <<<"$console_html"
fresh_preview_body="$test_root/fresh-preview.json"
fresh_preview_status="$(curl -sS -o "$fresh_preview_body" -w '%{http_code}' -X POST "$base_url/api/classification/preview" \
  -H 'content-type: application/json' \
  -H "Authorization: Bearer $token_before" \
  --data '{"sourceId":"lifecycle-preview","content":"Private lifecycle sentinel must not be echoed.","sourceType":"note"}')"
test "$fresh_preview_status" = "200"
grep -q '"sourceId":"lifecycle-preview"' "$fresh_preview_body"
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

sensitive_reference_id="lifecycle-sensitive-reference"
docker compose \
  --project-name "$LUTHN_PROJECT_NAME" \
  --env-file "$LUTHN_CONFIG_DIR/luthn.env" \
  -f "$LUTHN_DATA_DIR/compose.yaml" \
  exec -T postgres psql -v ON_ERROR_STOP=1 -U luthn -d luthn >/dev/null <<'SQL'
BEGIN;
INSERT INTO source_events
  ("Id", "SourceSystem", "SourceType", "ReceivedAt", "ContentDigest", "ContainsSensitiveMaterial", "OwnerUserId")
VALUES
  ('lifecycle-sensitive-source', 'lifecycle', 'note', now(), 'sha256:lifecycle-fixture', true, 'local-owner');
INSERT INTO collection_provenance
  ("Id", "ContractVersion", "SourceEventId", "AuthenticatedActor", "ActorTrust", "ClaimsTrust", "AuthenticatedUserId", "ReceivedAt")
VALUES
  ('provenance-lifecycle-sensitive-source', 1, 'lifecycle-sensitive-source', 'lifecycle-test', 'local-runtime', 'no-claims', 'local-owner', now());
INSERT INTO sensitive_record_references
  ("Id", "SourceEventId", "SourceSystem", "SourceType", "ReceivedAt", "ContainsSensitiveMaterial", "ReferenceLabel", "RedactedSummary", "OwnerUserId")
VALUES
  ('lifecycle-sensitive-reference', 'lifecycle-sensitive-source', 'lifecycle', 'note', now(), true, 'sensitive-record:lifecycle-sensitive-source', 'Public-safe lifecycle summary.', 'local-owner');
COMMIT;
SQL
access_request="$(curl -fsS -X POST "$base_url/api/access-requests" \
  -H 'content-type: application/json' \
  -H "Authorization: Bearer $token_before" \
  --data "{\"sensitiveReferenceId\":\"$sensitive_reference_id\",\"reason\":\"Verify the local operator fallback.\",\"sessionId\":\"distribution-operator-fallback\",\"expiresInSeconds\":600}")"
access_request_id="$(printf '%s' "$access_request" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')"
test -n "$access_request_id"
agent_decision_status="$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$base_url/api/access-requests/$access_request_id/approve" \
  -H 'content-type: application/json' \
  -H "Authorization: Bearer $token_before" \
  --data '{"reason":"The agent must not approve its own request."}')"
test "$agent_decision_status" = "403"
operator_decision="$(curl -fsS -X POST "$base_url/api/access-requests/$access_request_id/approve" \
  -H 'content-type: application/json' \
  -H "Authorization: Bearer $operator_token_before" \
  -H 'X-Luthn-Operator: local-console' \
  --data '{"reason":"Approved through the local operator fallback."}')"
grep -q '"status":"Approved"' <<<"$operator_decision"

echo "[2/8] status and MCP"
status_output="$("$cli" status)"
grep -q 'Readiness: ready' <<<"$status_output"
mcp_output="$("$cli" mcp --list-tools)"
grep -q '^get_context_pack$' <<<"$mcp_output"
grep -q '^create_sensitive_access_request$' <<<"$mcp_output"
grep -q '^submit_search_feedback$' <<<"$mcp_output"
mcp_call_output="$(printf '%s\n' '{"jsonrpc":"2.0","id":"access-smoke","method":"tools/call","params":{"name":"create_sensitive_access_request","arguments":{"sensitiveReferenceId":"missing-smoke-reference","reason":"Verify authenticated MCP request scope.","sessionId":"distribution-smoke","expiresInSeconds":60}}}' | "$cli" mcp)"
grep -q '"id":"access-smoke"' <<<"$mcp_call_output"
! grep -q '403' <<<"$mcp_call_output"

echo "[3/8] adapter write"
printf '%s\n' '{"sessionId":"lifecycle","turnId":"before-update","sourceAgent":"codex","summary":"Lifecycle sentinel remains after update.","coreTags":["lifecycle"],"idempotencyKey":"lifecycle-before-update"}' \
  | "$cli" adapter
context_output="$(curl -fsS -X POST "$base_url/api/agent/context-packs" \
  -H 'content-type: application/json' \
  -H "Authorization: Bearer $token_before" \
  --data '{"query":"Lifecycle sentinel","coreTags":["lifecycle"],"maxItems":20}')"
grep -q 'Lifecycle sentinel' <<<"$context_output"

echo "[4/8] update"
operator_digest_before="$(awk -F= '$1 == "Luthn__Auth__Tokens__1__Sha256Digest" { print $2 }' "$LUTHN_CONFIG_DIR/luthn.env")"
cp "$LUTHN_CONFIG_DIR/luthn.env" "$test_root/valid-config-before-update-tests"
grep -v '^Luthn__Auth__Tokens__1__Sha256Digest=' "$LUTHN_CONFIG_DIR/luthn.env" \
  >"$LUTHN_CONFIG_DIR/luthn.env.partial-operator"
mv "$LUTHN_CONFIG_DIR/luthn.env.partial-operator" "$LUTHN_CONFIG_DIR/luthn.env"
cp "$LUTHN_CONFIG_DIR/luthn.env" "$test_root/partial-operator-config-before"
operator_token_hash_before="$(shasum -a 256 "$LUTHN_OPERATOR_TOKEN_FILE" | awk '{print $1}')"
runtime_hash_before="$(shasum -a 256 "$LUTHN_CLI_PATH" "$LUTHN_DATA_DIR/compose.yaml")"
real_docker="$(command -v docker)"
mkdir -p "$test_root/failing-digest-bin"
cat >"$test_root/failing-digest-bin/docker" <<'EOF'
#!/usr/bin/env bash
if [[ " $* " == *" token-digest --stdin "* ]]; then
  exit 41
fi
exec "${LUTHN_REAL_DOCKER:?}" "$@"
EOF
chmod 0755 "$test_root/failing-digest-bin/docker"
if PATH="$test_root/failing-digest-bin:$PATH" LUTHN_REAL_DOCKER="$real_docker" \
  "$cli" update "$image" >/dev/null 2>&1; then
  echo "update unexpectedly succeeded after operator digest generation failed" >&2
  exit 1
fi
cmp "$test_root/partial-operator-config-before" "$LUTHN_CONFIG_DIR/luthn.env"
test "$operator_token_hash_before" = "$(shasum -a 256 "$LUTHN_OPERATOR_TOKEN_FILE" | awk '{print $1}')"
test "$runtime_hash_before" = "$(shasum -a 256 "$LUTHN_CLI_PATH" "$LUTHN_DATA_DIR/compose.yaml")"
cp "$test_root/valid-config-before-update-tests" "$LUTHN_CONFIG_DIR/luthn.env"
awk '
  $0 == "Luthn__Memory__AutomaticTurnRetentionDays=30" {
    print "Luthn__Memory__AutomaticTurnRetentionDays=45"
    next
  }
  $0 == "Luthn__Memory__AutomaticTurnCleanupEnabled=true" {
    print "Luthn__Memory__AutomaticTurnCleanupEnabled=false"
    next
  }
  $0 == "Luthn__Memory__AutomaticTurnCleanupIntervalMinutes=60" {
    print "Luthn__Memory__AutomaticTurnCleanupIntervalMinutes=120"
    next
  }
  $0 == "Luthn__Memory__AutomaticTurnCleanupBatchSize=100" {
    print "Luthn__Memory__AutomaticTurnCleanupBatchSize=25"
    next
  }
  { print }
' "$LUTHN_CONFIG_DIR/luthn.env" >"$LUTHN_CONFIG_DIR/luthn.env.retention-override"
mv "$LUTHN_CONFIG_DIR/luthn.env.retention-override" "$LUTHN_CONFIG_DIR/luthn.env"

grep -v -e '^Luthn__Auth__Tokens__0__Scopes__7=access.request$' \
  -e '^Luthn__Auth__Tokens__1__' "$LUTHN_CONFIG_DIR/luthn.env" \
  >"$LUTHN_CONFIG_DIR/luthn.env.collision"
cat >>"$LUTHN_CONFIG_DIR/luthn.env.collision" <<EOF
Luthn__Auth__Tokens__1__Name=existing-integration
Luthn__Auth__Tokens__1__Sha256Digest=sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc
Luthn__Auth__Tokens__1__Scopes__0=memory.read
Luthn__Auth__Tokens__1__Scopes__1=*
Luthn__Auth__Tokens__1__ExpiresAt=2099-01-01T00:00:00Z
EOF
mv "$LUTHN_CONFIG_DIR/luthn.env.collision" "$LUTHN_CONFIG_DIR/luthn.env"
cp "$LUTHN_CONFIG_DIR/luthn.env" "$test_root/collision-config-before"
if "$cli" update "$image" >/dev/null 2>&1; then
  echo "update unexpectedly succeeded with an occupied operator token slot" >&2
  exit 1
fi
cmp "$test_root/collision-config-before" "$LUTHN_CONFIG_DIR/luthn.env"
test "$operator_token_before" = "$(cat "$LUTHN_OPERATOR_TOKEN_FILE")"

grep -v '^Luthn__Auth__Tokens__1__' "$LUTHN_CONFIG_DIR/luthn.env" \
  >"$LUTHN_CONFIG_DIR/luthn.env.legacy"
cat >>"$LUTHN_CONFIG_DIR/luthn.env.legacy" <<EOF
Luthn__Auth__Tokens__1__Name=local-operator
Luthn__Auth__Tokens__1__Sha256Digest=$operator_digest_before
Luthn__Auth__Tokens__1__Scopes__0=access.decide
Luthn__Auth__Tokens__1__Scopes__1=*
Luthn__Auth__Tokens__1__ExpiresAt=2099-01-01T00:00:00Z
EOF
mv "$LUTHN_CONFIG_DIR/luthn.env.legacy" "$LUTHN_CONFIG_DIR/luthn.env"
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
operator_token_after="$(cat "$LUTHN_OPERATOR_TOKEN_FILE")"
test "$token_before" = "$token_after"
test "$operator_token_before" = "$operator_token_after"
test "$operator_key_manifest_before" = "$(operator_key_manifest)"
backup_path="$(awk -F= '$1 == "BACKUP_PATH" { print $2 }' "$LUTHN_STATE_DIR/install-state.env")"
test -s "$backup_path"
grep -q '^Luthn__Auth__Tokens__0__Scopes__4=classification.preview$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__Scopes__5=agent.connection.read$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__Scopes__6=agent.connection.write$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__Scopes__7=access.request$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__Scopes__8=metrics.write$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__1__Name=local-operator$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__1__IsOperator=true$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__1__Scopes__0=access.decide$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Auth__Tokens__1__Scopes__1=config.write$' "$LUTHN_CONFIG_DIR/luthn.env"
! grep -q '^Luthn__Auth__Tokens__1__ExpiresAt=' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Memory__AutomaticTurnRetentionDays=45$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Memory__AutomaticTurnCleanupEnabled=false$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Memory__AutomaticTurnCleanupIntervalMinutes=120$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Memory__AutomaticTurnCleanupBatchSize=25$' "$LUTHN_CONFIG_DIR/luthn.env"
context_output="$(curl -fsS -X POST "$base_url/api/agent/context-packs" \
  -H 'content-type: application/json' \
  -H "Authorization: Bearer $token_after" \
  --data '{"query":"Lifecycle sentinel","coreTags":["lifecycle"],"maxItems":20}')"
grep -q 'Lifecycle sentinel' <<<"$context_output"
upgraded_access_request="$(curl -fsS -X POST "$base_url/api/access-requests" \
  -H 'content-type: application/json' \
  -H "Authorization: Bearer $token_after" \
  --data "{\"sensitiveReferenceId\":\"$sensitive_reference_id\",\"reason\":\"Verify the upgraded operator credential.\",\"sessionId\":\"distribution-upgraded-operator\",\"expiresInSeconds\":600}")"
upgraded_access_request_id="$(printf '%s' "$upgraded_access_request" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')"
test -n "$upgraded_access_request_id"
operator_request_list="$(curl -fsS "$base_url/api/access-requests" \
  -H "Authorization: Bearer $operator_token_after" \
  -H 'X-Luthn-Operator: local-console')"
grep -q "$upgraded_access_request_id" <<<"$operator_request_list"
upgraded_operator_decision="$(curl -fsS -X POST "$base_url/api/access-requests/$upgraded_access_request_id/approve" \
  -H 'content-type: application/json' \
  -H "Authorization: Bearer $operator_token_after" \
  -H 'X-Luthn-Operator: local-console' \
  --data '{"reason":"Approved after the lifecycle upgrade."}')"
grep -q '"status":"Approved"' <<<"$upgraded_operator_decision"

grep -v -e '^Luthn__Memory__AutomaticTurnRetentionDays=' \
  -e '^Luthn__Memory__AutomaticTurnCleanupEnabled=' \
  -e '^Luthn__Memory__AutomaticTurnCleanupIntervalMinutes=' \
  -e '^Luthn__Memory__AutomaticTurnCleanupBatchSize=' \
  "$LUTHN_CONFIG_DIR/luthn.env" \
  >"$LUTHN_CONFIG_DIR/luthn.env.missing-retention"
mv "$LUTHN_CONFIG_DIR/luthn.env.missing-retention" "$LUTHN_CONFIG_DIR/luthn.env"
"$cli" update "$image"
grep -q '^Luthn__Memory__AutomaticTurnRetentionDays=30$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Memory__AutomaticTurnCleanupEnabled=true$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Memory__AutomaticTurnCleanupIntervalMinutes=60$' "$LUTHN_CONFIG_DIR/luthn.env"
grep -q '^Luthn__Memory__AutomaticTurnCleanupBatchSize=100$' "$LUTHN_CONFIG_DIR/luthn.env"

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
test -f "$LUTHN_OPERATOR_TOKEN_FILE"
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
