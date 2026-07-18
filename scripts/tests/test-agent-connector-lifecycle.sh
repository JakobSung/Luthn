#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
tmp_root="$(mktemp -d)"
trap 'rm -rf "$tmp_root"' EXIT HUP INT TERM

home_dir="$tmp_root/home"
data_dir="$tmp_root/data"
config_dir="$tmp_root/config"
state_dir="$tmp_root/state"
bin_dir="$tmp_root/bin"
fake_bin="$tmp_root/fake-bin"
codex_home="$tmp_root/codex"
mcp_state="$tmp_root/codex-mcp-state"
report_log="$tmp_root/connector-report.log"
docker_log="$tmp_root/docker.log"
docker_fail_marker="$tmp_root/docker-api-recreate-failed"
docker_stop_count="$tmp_root/docker-stop-count"
no_codex_bin="$tmp_root/no-codex-bin"
mkdir -p "$home_dir" "$data_dir" "$config_dir" "$state_dir" "$bin_dir" "$fake_bin" "$codex_home"

cli="$bin_dir/luthn"
cp "$repo_root/scripts/luthn" "$cli"
chmod 0755 "$cli"
touch "$data_dir/compose.yaml"
printf '%s' 'test-token' >"$config_dir/service-token"
chmod 0600 "$config_dir/service-token"

cat >"$config_dir/luthn.env" <<EOF
LUTHN_IMAGE=test/luthn:local
LUTHN_BASE_URL=http://127.0.0.1:1
LUTHN_SERVICE_TOKEN_FILE=$config_dir/service-token
Luthn__Auth__Tokens__0__Scopes__0=agent.read
Luthn__Auth__Tokens__0__Scopes__1=agent.write.summary
Luthn__Auth__Tokens__0__Scopes__2=memory.write
Luthn__Auth__Tokens__0__Scopes__3=memory.read
Luthn__Auth__Tokens__0__Scopes__4=classification.preview
Luthn__Auth__Tokens__0__Scopes__5=custom.read
Luthn__Auth__Tokens__0__Scopes__6=custom.write
EOF

cat >"$fake_bin/docker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >>"${FAKE_DOCKER_LOG:?}"
if [[ "${1:-}" == "info" ]]; then
  exit 0
fi
if [[ "${1:-}" == "compose" && "${2:-}" == "version" ]]; then
  exit 0
fi
if [[ ( "${1:-}" == "stop" || "${1:-}" == "kill" ) \
  && "${FAKE_DOCKER_DIRECT_STOP_FAIL:-false}" == "true" ]]; then
  exit 1
fi
if [[ " $* " == *" stop api "* ]]; then
  count=0
  [[ -f "${FAKE_DOCKER_STOP_COUNT_FILE:?}" ]] && count="$(cat "$FAKE_DOCKER_STOP_COUNT_FILE")"
  count=$((count + 1))
  printf '%s' "$count" >"$FAKE_DOCKER_STOP_COUNT_FILE"
  if [[ -n "${FAKE_DOCKER_STOP_FAIL_AFTER:-}" \
    && "$count" -gt "$FAKE_DOCKER_STOP_FAIL_AFTER" ]]; then
    exit 1
  fi
fi
if [[ " $* " == *" ps -q api "* \
  && "${FAKE_DOCKER_REPORT_RUNNING_API:-false}" == "true" ]]; then
  printf '%s\n' fake-api-id
  exit 0
fi
if [[ "${FAKE_DOCKER_API_RECREATE_FAIL_ALWAYS:-false}" == "true" \
  && " $* " == *" --force-recreate api "* ]]; then
  exit 1
fi
if [[ "${FAKE_DOCKER_API_RECREATE_FAIL_ONCE:-false}" == "true" \
  && " $* " == *" --force-recreate api "* \
  && ! -f "${FAKE_DOCKER_FAILURE_MARKER:?}" ]]; then
  touch "$FAKE_DOCKER_FAILURE_MARKER"
  exit 1
fi
if [[ " $* " == *" --list-tools "* \
  && "${FAKE_DOCKER_MCP_PROBE_FAIL:-false}" != "true" ]]; then
  printf '%s\n' get_context_pack search_safe_context create_shared_memory
  exit 0
fi
exit 0
EOF
chmod 0755 "$fake_bin/docker"

cat >"$fake_bin/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
output=""
source_url=""
while (( $# > 0 )); do
  case "$1" in
    -o)
      output="${2:?missing curl output path}"
      shift 2
      ;;
    -*) shift ;;
    *) source_url="$1"; shift ;;
  esac
done
if [[ -n "$output" && "$source_url" == file://* ]]; then
  cp "${source_url#file://}" "$output" 2>/dev/null
fi
EOF
chmod 0755 "$fake_bin/curl"

cat >"$fake_bin/codex" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
state="${FAKE_CODEX_MCP_STATE:?}"
if [[ "${1:-}" != "mcp" ]]; then
  exit 2
fi
case "${2:-}" in
  get)
    [[ -f "$state" ]] || exit 1
    command_path="$(cat "$state")"
    cat <<OUT
luthn
  enabled: true
  transport: stdio
  command: $command_path
  args: mcp
OUT
    ;;
  add)
    [[ "${FAKE_CODEX_ADD_FAIL:-false}" != "true" ]] || exit 1
    printf '%s' "${5:?missing MCP command}" >"$state"
    ;;
  remove)
    [[ "${FAKE_CODEX_REMOVE_FAIL:-false}" != "true" ]] || exit 1
    rm -f "$state"
    ;;
  *) exit 2 ;;
esac
EOF
chmod 0755 "$fake_bin/codex"

mkdir -p "$no_codex_bin"
ln -s "$fake_bin/docker" "$no_codex_bin/docker"
ln -s "$fake_bin/curl" "$no_codex_bin/curl"
ln -s "$(command -v python3)" "$no_codex_bin/python3"

cat >"$tmp_root/connector-helper.py" <<'EOF'
#!/usr/bin/env python3
import os
import hashlib
from pathlib import Path
import subprocess
import sys

if len(sys.argv) > 1 and sys.argv[1] == "version":
    print("2")
    raise SystemExit(0)

if len(sys.argv) > 1 and sys.argv[1] == "helper-digest":
    print(hashlib.sha256(Path(__file__).read_bytes()).hexdigest())
    raise SystemExit(0)

if len(sys.argv) > 1 and sys.argv[1] == "template-digest":
    raise SystemExit(
        subprocess.call(
            [sys.executable, os.environ["REAL_CONNECTOR_HELPER"], "template-digest"]
        )
    )

if len(sys.argv) > 1 and sys.argv[1] in {"report", "status"}:
    report_log = os.environ.get("FAKE_REPORT_LOG")
    if sys.argv[1] == "report" and report_log:
        with open(report_log, "a", encoding="utf-8") as stream:
            stream.write(" ".join(sys.argv[1:]) + "\n")
        if os.environ.get("FAKE_REPORT_FAIL") == "true":
            raise SystemExit(1)
    if sys.argv[1] == "status":
        print("Server observation: test")
    raise SystemExit(0)

raise SystemExit(
    subprocess.call([sys.executable, os.environ["REAL_CONNECTOR_HELPER"], *sys.argv[1:]])
)
EOF
chmod 0700 "$tmp_root/connector-helper.py"
cp "$tmp_root/connector-helper.py" "$tmp_root/connector-helper-fixture.py"

cat >"$codex_home/hooks.json" <<'EOF'
{
  "description": "preserve",
  "hooks": {
    "Stop": [
      {
        "matcher": "other.owner",
        "hooks": [{"type": "command", "command": "other"}]
      }
    ]
  }
}
EOF
cat >"$codex_home/AGENTS.md" <<'EOF'
# User instructions

Preserve this text.
EOF

run_luthn() {
  env \
    HOME="$home_dir" \
    CODEX_HOME="$codex_home" \
    PATH="$fake_bin:$PATH" \
    FAKE_CODEX_MCP_STATE="$mcp_state" \
    FAKE_REPORT_LOG="$report_log" \
    FAKE_REPORT_FAIL="${FAKE_REPORT_FAIL:-false}" \
    FAKE_DOCKER_LOG="$docker_log" \
    FAKE_DOCKER_FAILURE_MARKER="$docker_fail_marker" \
    FAKE_DOCKER_STOP_COUNT_FILE="$docker_stop_count" \
    REAL_CONNECTOR_HELPER="$repo_root/scripts/luthn-codex-connector.py" \
    LUTHN_DATA_DIR="$data_dir" \
    LUTHN_CONFIG_DIR="$config_dir" \
    LUTHN_STATE_DIR="$state_dir" \
    LUTHN_BIN_DIR="$bin_dir" \
    LUTHN_COMPOSE_FILE="$data_dir/compose.yaml" \
    LUTHN_CONFIG_FILE="$config_dir/luthn.env" \
    LUTHN_CLI_PATH="$cli" \
    LUTHN_SERVICE_TOKEN_FILE="$config_dir/service-token" \
    LUTHN_SOURCE_BASE_URL="file://$repo_root" \
    LUTHN_CODEX_PENDING_STATE_FILE="${LUTHN_CODEX_PENDING_STATE_FILE:-$state_dir/connectors/codex.pending.env}" \
    LUTHN_CODEX_CONNECTOR_HELPER="$tmp_root/connector-helper.py" \
    "$cli" "$@"
}

run_luthn_without_codex() {
  env \
    HOME="$home_dir" \
    CODEX_HOME="$codex_home" \
    PATH="$no_codex_bin:/usr/bin:/bin" \
    FAKE_CODEX_MCP_STATE="$mcp_state" \
    FAKE_REPORT_LOG="$report_log" \
    FAKE_DOCKER_LOG="$docker_log" \
    FAKE_DOCKER_FAILURE_MARKER="$docker_fail_marker" \
    FAKE_DOCKER_STOP_COUNT_FILE="$docker_stop_count" \
    REAL_CONNECTOR_HELPER="$repo_root/scripts/luthn-codex-connector.py" \
    LUTHN_DATA_DIR="$data_dir" \
    LUTHN_CONFIG_DIR="$config_dir" \
    LUTHN_STATE_DIR="$state_dir" \
    LUTHN_BIN_DIR="$bin_dir" \
    LUTHN_COMPOSE_FILE="$data_dir/compose.yaml" \
    LUTHN_CONFIG_FILE="$config_dir/luthn.env" \
    LUTHN_CLI_PATH="$cli" \
    LUTHN_SERVICE_TOKEN_FILE="$config_dir/service-token" \
    LUTHN_CODEX_CONNECTOR_HELPER="$tmp_root/connector-helper.py" \
    "$cli" "$@"
}

assert_hook_counts() {
  expected_luthn="$1"
  expected_other="$2"
  python3 - "$codex_home/hooks.json" "$expected_luthn" "$expected_other" <<'PY'
import json
import sys

document = json.load(open(sys.argv[1], encoding="utf-8"))
groups = document["hooks"]["Stop"]
luthn = sum(group.get("matcher") == "luthn.agent-connector.v1" for group in groups)
other = sum(group.get("matcher") == "other.owner" for group in groups)
assert luthn == int(sys.argv[2]), (luthn, groups)
assert other == int(sys.argv[3]), (other, groups)
for group in groups:
    if group.get("matcher") == "luthn.agent-connector.v1":
        handler = group["hooks"][0]
        assert "async" not in handler, handler
        assert handler["statusMessage"] == "Luthn 메모리 저장 예약 중…", handler
PY
}

echo "[1/18] connect installs both channels and explains required hook trust"
connect_output="$(run_luthn connect codex)"
grep -q '^Required one-time Codex security steps:$' <<<"$connect_output"
grep -q 'enter /hooks' <<<"$connect_output"
grep -q 'luthn.agent-connector.v1 and choose Trust' <<<"$connect_output"
grep -q 'Setup is complete only when automatic-ingestion reports Active' <<<"$connect_output"
[[ "$(cat "$mcp_state")" == "$cli" ]]
[[ -f "$state_dir/connectors/codex.env" ]]
grep -q '^SETUP_STATE=configured$' "$state_dir/connectors/codex.env"
grep -q '^AUTO_RECALL=true$' "$state_dir/connectors/codex.env"
grep -Eq '^HELPER_DIGEST=[0-9a-f]{64}$' "$state_dir/connectors/codex.env"
grep -Eq '^TEMPLATE_DIGEST=[0-9a-f]{64}$' "$state_dir/connectors/codex.env"
! grep -q 'test-token' "$state_dir/connectors/codex.env"
! grep -q 'test-token' "$codex_home/hooks.json"
grep -q '<!-- luthn:auto-recall:start -->' "$codex_home/AGENTS.md"
grep -q '^Luthn__Auth__Tokens__0__Scopes__5=custom.read$' "$config_dir/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__Scopes__6=custom.write$' "$config_dir/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__Scopes__7=agent.connection.read$' "$config_dir/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__Scopes__8=agent.connection.write$' "$config_dir/luthn.env"
grep -q '^Luthn__Auth__Tokens__0__Scopes__9=access.request$' "$config_dir/luthn.env"
assert_hook_counts 1 1
status_output="$(run_luthn connection status codex)"
grep -q '^Local connector: configured$' <<<"$status_output"
grep -q '^  automatic-ingestion: configured$' <<<"$status_output"
grep -q '^  mcp: configured$' <<<"$status_output"
grep -q '^Server observation: test$' <<<"$status_output"
grep -q '^Automatic memory capture is not active yet.$' <<<"$status_output"
grep -q 'Restart Codex, enter /hooks' <<<"$status_output"

echo "[2/18] reconnect upgrades only legacy Luthn-managed configuration"
python3 - "$codex_home/hooks.json" "$codex_home/AGENTS.md" <<'PY'
import json
from pathlib import Path
import sys

hooks_path = Path(sys.argv[1])
document = json.loads(hooks_path.read_text(encoding="utf-8"))
for group in document["hooks"]["Stop"]:
    if group.get("matcher") == "luthn.agent-connector.v1":
        group["hooks"][0]["statusMessage"] = "Syncing Luthn memory"
hooks_path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")

instructions_path = Path(sys.argv[2])
content = instructions_path.read_text(encoding="utf-8")
start_marker = "<!-- luthn:auto-recall:start -->"
end_marker = "<!-- luthn:auto-recall:end -->"
start = content.index(start_marker)
end = content.index(end_marker, start) + len(end_marker)
legacy = f"{start_marker}\n# Luthn lightweight recall\n\nLegacy managed instructions.\n{end_marker}"
instructions_path.write_text(content[:start] + legacy + content[end:], encoding="utf-8")
PY
run_luthn connect codex >/dev/null
assert_hook_counts 1 1
grep -q '^# User instructions$' "$codex_home/AGENTS.md"
grep -q '^Preserve this text\.$' "$codex_home/AGENTS.md"
! grep -q 'Legacy managed instructions' "$codex_home/AGENTS.md"
grep -q 'exactly one commentary line' "$codex_home/AGENTS.md"
grep -q 'Luthn 메모리 N개 참고' "$codex_home/AGENTS.md"
grep -q 'zero actual memory' "$codex_home/AGENTS.md"
grep -q 'times out, returns an error, cannot be parsed' "$codex_home/AGENTS.md"
grep -q 'uses any fail-open path' "$codex_home/AGENTS.md"
grep -q 'when `get_context_pack` was not called' "$codex_home/AGENTS.md"
grep -q 'at most once per user turn' "$codex_home/AGENTS.md"
grep -q 'memory titles, content, IDs, queries, scores, sources' "$codex_home/AGENTS.md"
grep -q 'normal assistant response or final response' "$codex_home/AGENTS.md"
hook_hash="$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')"
recall_hash="$(shasum -a 256 "$codex_home/AGENTS.md" | awk '{print $1}')"
run_luthn connect codex >/dev/null
assert_hook_counts 1 1
[[ "$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')" == "$hook_hash" ]]
[[ "$(shasum -a 256 "$codex_home/AGENTS.md" | awk '{print $1}')" == "$recall_hash" ]]

echo "[2a/18] explicit recall compatibility and opt-out are supported"
run_luthn connect codex --auto-recall >/dev/null
run_luthn connect codex --auto-recall >/dev/null
[[ "$(grep -c '<!-- luthn:auto-recall:start -->' "$codex_home/AGENTS.md")" -eq 1 ]]
[[ "$(grep -c '<!-- luthn:auto-recall:end -->' "$codex_home/AGENTS.md")" -eq 1 ]]
grep -q '^AUTO_RECALL=true$' "$state_dir/connectors/codex.env"
grep -q '^INSTRUCTIONS_FILE=' "$state_dir/connectors/codex.env"
grep -q '`maxItems`: 3' "$codex_home/AGENTS.md"
grep -q '`maxTokens`: 600' "$codex_home/AGENTS.md"
grep -q '`timeoutMs`: 200' "$codex_home/AGENTS.md"
grep -q '`cacheTtlSeconds`: 600' "$codex_home/AGENTS.md"
status_output="$(run_luthn connection status codex)"
grep -q '^  lightweight-recall: enabled$' <<<"$status_output"
run_luthn connect codex --no-auto-recall >/dev/null
! grep -q '<!-- luthn:auto-recall:start -->' "$codex_home/AGENTS.md"
grep -q '^AUTO_RECALL=false$' "$state_dir/connectors/codex.env"
run_luthn connect codex >/dev/null
grep -q '<!-- luthn:auto-recall:start -->' "$codex_home/AGENTS.md"
grep -q '^AUTO_RECALL=true$' "$state_dir/connectors/codex.env"

cp "$state_dir/connectors/codex.env" \
  "$state_dir/connectors/codex.disconnect.pending.env"
: >"$report_log"
run_luthn connection status codex >/dev/null
[[ ! -f "$state_dir/connectors/codex.disconnect.pending.env" ]]
[[ ! -s "$report_log" ]]

echo "[3/18] missing Codex reports MCP as unconfigured"
: >"$report_log"
run_luthn_without_codex reset --yes >/dev/null
grep -q -- '--channel mcp:false:Unknown:Unknown:' "$report_log"

echo "[4/18] failed disconnect restores Luthn-owned configuration"
if FAKE_CODEX_REMOVE_FAIL=true run_luthn disconnect codex >/dev/null 2>&1; then
  echo "expected disconnect failure" >&2
  exit 1
fi
[[ -f "$mcp_state" ]]
[[ -f "$state_dir/connectors/codex.env" ]]
assert_hook_counts 1 1
grep -q '<!-- luthn:auto-recall:start -->' "$codex_home/AGENTS.md"

echo "[5/18] failed disconnect observation is retried after API recovery"
: >"$report_log"
FAKE_REPORT_FAIL=true run_luthn disconnect codex >/dev/null
[[ ! -f "$mcp_state" ]]
[[ ! -f "$state_dir/connectors/codex.env" ]]
[[ -f "$state_dir/connectors/codex.disconnect.pending.env" ]]
assert_hook_counts 0 1
grep -q '^# User instructions$' "$codex_home/AGENTS.md"
grep -q '^Preserve this text\.$' "$codex_home/AGENTS.md"
! grep -q '<!-- luthn:auto-recall:start -->' "$codex_home/AGENTS.md"
grep -q -- '--channel automatic-ingestion:false:Unknown:Unknown:' "$report_log"
run_luthn connection status codex >/dev/null
[[ ! -f "$state_dir/connectors/codex.disconnect.pending.env" ]]
[[ "$(grep -c -- '--channel automatic-ingestion:false:Unknown:Unknown:' "$report_log")" -eq 2 ]]

echo "[6/18] successful disconnect removes only Luthn-owned configuration"
run_luthn connect codex >/dev/null
run_luthn disconnect codex >/dev/null
[[ ! -f "$mcp_state" ]]
[[ ! -f "$state_dir/connectors/codex.env" ]]
[[ ! -f "$state_dir/connectors/codex.pending.env" ]]
[[ ! -f "$state_dir/connectors/codex.disconnect.pending.env" ]]
assert_hook_counts 0 1

echo "[6a/18] malformed default recall rolls connector setup back"
instructions_before_malformed="$(cat "$codex_home/AGENTS.md")"
printf '%s\n<!-- luthn:auto-recall:start -->\n' \
  "$instructions_before_malformed" >"$codex_home/AGENTS.md"
malformed_instructions="$(cat "$codex_home/AGENTS.md")"
if run_luthn connect codex >/dev/null 2>&1; then
  echo "expected malformed auto-recall instructions to fail setup" >&2
  exit 1
fi
[[ ! -f "$mcp_state" ]]
[[ ! -f "$state_dir/connectors/codex.env" ]]
assert_hook_counts 0 1
[[ "$(cat "$codex_home/AGENTS.md")" == "$malformed_instructions" ]]
printf '%s\n' "$instructions_before_malformed" >"$codex_home/AGENTS.md"

normal_config="$tmp_root/luthn-normal.env"
awk '!/^Luthn__Auth__Tokens__0__Scopes__(7|8|9)=/' "$config_dir/luthn.env" >"$normal_config"
cp "$normal_config" "$config_dir/luthn.env"

echo "[7/18] partial scope failure rolls all connector changes back"
for scope_index in {7..14}; do
  printf 'Luthn__Auth__Tokens__0__Scopes__%s=custom.%s\n' "$scope_index" "$scope_index" >>"$config_dir/luthn.env"
done
before_hash="$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')"
before_config_hash="$(shasum -a 256 "$config_dir/luthn.env" | awk '{print $1}')"
if run_luthn connect codex >/dev/null 2>&1; then
  echo "expected connector scope failure" >&2
  exit 1
fi
after_hash="$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')"
after_config_hash="$(shasum -a 256 "$config_dir/luthn.env" | awk '{print $1}')"
[[ "$before_hash" == "$after_hash" ]]
[[ "$before_config_hash" == "$after_config_hash" ]]
[[ ! -f "$mcp_state" ]]
assert_hook_counts 0 1
cp "$normal_config" "$config_dir/luthn.env"

echo "[8/18] API scope refresh failure restores all connector changes"
: >"$docker_log"
rm -f "$docker_fail_marker"
before_hash="$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')"
before_config_hash="$(shasum -a 256 "$config_dir/luthn.env" | awk '{print $1}')"
if FAKE_DOCKER_API_RECREATE_FAIL_ONCE=true run_luthn connect codex >/dev/null 2>&1; then
  echo "expected API scope refresh failure" >&2
  exit 1
fi
after_hash="$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')"
after_config_hash="$(shasum -a 256 "$config_dir/luthn.env" | awk '{print $1}')"
[[ "$before_hash" == "$after_hash" ]]
[[ "$before_config_hash" == "$after_config_hash" ]]
[[ ! -f "$mcp_state" ]]
assert_hook_counts 0 1
[[ "$(grep -c -- '--force-recreate api' "$docker_log")" -eq 2 ]]
grep -q -- ' stop api$' "$docker_log"

echo "[9/18] MCP registration failure rolls hooks and scopes back"
before_hash="$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')"
before_config_hash="$(shasum -a 256 "$config_dir/luthn.env" | awk '{print $1}')"
if FAKE_CODEX_ADD_FAIL=true run_luthn connect codex >/dev/null 2>&1; then
  echo "expected connect failure" >&2
  exit 1
fi
after_hash="$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')"
after_config_hash="$(shasum -a 256 "$config_dir/luthn.env" | awk '{print $1}')"
[[ "$before_hash" == "$after_hash" ]]
[[ "$before_config_hash" == "$after_config_hash" ]]
[[ ! -f "$mcp_state" ]]

echo "[10/18] unrecoverable API stop is reported without a false safety claim"
: >"$docker_log"
rm -f "$docker_stop_count"
before_hash="$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')"
before_config_hash="$(shasum -a 256 "$config_dir/luthn.env" | awk '{print $1}')"
if failure_output="$(
  FAKE_DOCKER_API_RECREATE_FAIL_ALWAYS=true \
  FAKE_DOCKER_STOP_FAIL_AFTER=1 \
  FAKE_DOCKER_REPORT_RUNNING_API=true \
  FAKE_DOCKER_DIRECT_STOP_FAIL=true \
  run_luthn connect codex 2>&1
)"; then
  echo "expected unrecoverable API restart failure" >&2
  exit 1
fi
after_hash="$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')"
after_config_hash="$(shasum -a 256 "$config_dir/luthn.env" | awk '{print $1}')"
[[ "$before_hash" == "$after_hash" ]]
[[ "$before_config_hash" == "$after_config_hash" ]]
[[ ! -f "$mcp_state" ]]
assert_hook_counts 0 1
grep -q 'could not confirm that the API stopped' <<<"$failure_output"
compgen -G "${config_dir}/luthn.env.codex-connect.*" >/dev/null
rm -f "${config_dir}/luthn.env.codex-connect."*

echo "[11/18] unrelated luthn MCP registration is preserved"
printf '%s' /other/product/mcp >"$mcp_state"
before_hash="$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')"
before_config_hash="$(shasum -a 256 "$config_dir/luthn.env" | awk '{print $1}')"
if run_luthn connect codex >/dev/null 2>&1; then
  echo "expected MCP conflict" >&2
  exit 1
fi
after_hash="$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')"
after_config_hash="$(shasum -a 256 "$config_dir/luthn.env" | awk '{print $1}')"
[[ "$before_hash" == "$after_hash" ]]
[[ "$before_config_hash" == "$after_config_hash" ]]
[[ "$(cat "$mcp_state")" == "/other/product/mcp" ]]
rm -f "$mcp_state"

echo "[12/18] missing helper self-heals from the installed runtime revision"
rm -f "$tmp_root/connector-helper.py"
run_luthn connect codex >/dev/null
[[ -x "$tmp_root/connector-helper.py" ]]
[[ "$(python3 "$tmp_root/connector-helper.py" version)" == "2" ]]
[[ -f "$state_dir/connectors/codex.env" ]]
expected_helper_digest="$(awk -F= '$1 == "HELPER_DIGEST" { print $2 }' "$state_dir/connectors/codex.env")"
printf '\n# same-version stale helper\n' >>"$tmp_root/connector-helper.py"
[[ "$(python3 "$tmp_root/connector-helper.py" version)" == "2" ]]
[[ "$(python3 "$tmp_root/connector-helper.py" helper-digest)" != "$expected_helper_digest" ]]
run_luthn connect codex >/dev/null
[[ "$(python3 "$tmp_root/connector-helper.py" helper-digest)" == "$expected_helper_digest" ]]
cp "$tmp_root/connector-helper-fixture.py" "$tmp_root/connector-helper.py"
run_luthn disconnect codex >/dev/null

cat >"$tmp_root/connector-helper.py" <<'EOF'
#!/usr/bin/env python3
import sys
if len(sys.argv) > 1 and sys.argv[1] == "version":
    print("1")
    raise SystemExit(0)
raise SystemExit(1)
EOF
chmod 0700 "$tmp_root/connector-helper.py"
run_luthn connect codex >/dev/null
[[ "$(python3 "$tmp_root/connector-helper.py" version)" == "2" ]]
cp "$tmp_root/connector-helper-fixture.py" "$tmp_root/connector-helper.py"
run_luthn disconnect codex >/dev/null
run_luthn connection status codex >/dev/null

echo "[13/18] failed partial MCP cleanup preserves ownership and blocks uninstall"
if FAKE_DOCKER_MCP_PROBE_FAIL=true FAKE_CODEX_REMOVE_FAIL=true \
  run_luthn connect codex >/dev/null 2>&1; then
  echo "expected connector probe and cleanup failure" >&2
  exit 1
fi
[[ -f "$mcp_state" ]]
[[ -f "$state_dir/connectors/codex.pending.env" ]]
cp "$state_dir/connectors/codex.pending.env" \
  "$state_dir/connectors/codex.disconnect.pending.env"
assert_hook_counts 0 1
if FAKE_CODEX_REMOVE_FAIL=true run_luthn uninstall >/dev/null 2>&1; then
  echo "expected uninstall to stop on partial connector cleanup" >&2
  exit 1
fi
[[ -x "$cli" ]]
[[ -f "$state_dir/connectors/codex.pending.env" ]]
run_luthn disconnect codex >/dev/null

echo "[14/18] ownership state preparation fails before host configuration changes"
before_hash="$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')"
before_config_hash="$(shasum -a 256 "$config_dir/luthn.env" | awk '{print $1}')"
if LUTHN_CODEX_PENDING_STATE_FILE="$tmp_root/missing/state/codex.pending.env" \
  run_luthn connect codex >/dev/null 2>&1; then
  echo "expected connector ownership state preparation failure" >&2
  exit 1
fi
after_hash="$(shasum -a 256 "$codex_home/hooks.json" | awk '{print $1}')"
after_config_hash="$(shasum -a 256 "$config_dir/luthn.env" | awk '{print $1}')"
[[ "$before_hash" == "$after_hash" ]]
[[ "$before_config_hash" == "$after_config_hash" ]]
[[ ! -f "$mcp_state" ]]

echo "[15/18] pre-connector runtime is accepted only without connector ownership"
old_runtime="$tmp_root/old-runtime"
mkdir -p "$old_runtime/deploy" "$old_runtime/scripts"
printf 'services: {}\n' >"$old_runtime/deploy/compose.yaml"
printf '#!/usr/bin/env bash\necho old-runtime\n' >"$old_runtime/scripts/luthn"
(
  export HOME="$home_dir"
  export PATH="$fake_bin:$PATH"
  export LUTHN_DATA_DIR="$data_dir"
  export LUTHN_CONFIG_DIR="$config_dir"
  export LUTHN_STATE_DIR="$state_dir"
  export LUTHN_BIN_DIR="$bin_dir"
  export LUTHN_COMPOSE_FILE="$data_dir/compose.yaml"
  export LUTHN_CONFIG_FILE="$config_dir/luthn.env"
  export LUTHN_CLI_PATH="$cli"
  export LUTHN_SOURCE_BASE_URL="file://$repo_root"
  set -- help
  # shellcheck disable=SC1090
  source "$repo_root/scripts/luthn" >/dev/null
  download_runtime "file://$old_runtime"
  [[ ! -e "$runtime_dir/luthn-codex-connector.py" ]]
)
cp "$repo_root/scripts/luthn" "$cli"
chmod 0755 "$cli"
cp "$repo_root/scripts/luthn-codex-connector.py" "$data_dir/runtime/luthn-codex-connector.py"
touch "$state_dir/connectors/codex.env"
before_cli_hash="$(shasum -a 256 "$cli" | awk '{print $1}')"
if (
  export HOME="$home_dir"
  export PATH="$fake_bin:$PATH"
  export LUTHN_DATA_DIR="$data_dir"
  export LUTHN_CONFIG_DIR="$config_dir"
  export LUTHN_STATE_DIR="$state_dir"
  export LUTHN_BIN_DIR="$bin_dir"
  export LUTHN_COMPOSE_FILE="$data_dir/compose.yaml"
  export LUTHN_CONFIG_FILE="$config_dir/luthn.env"
  export LUTHN_CLI_PATH="$cli"
  export LUTHN_SOURCE_BASE_URL="file://$repo_root"
  set -- help
  # shellcheck disable=SC1090
  source "$repo_root/scripts/luthn" >/dev/null
  download_runtime "file://$old_runtime"
); then
  echo "expected old runtime to reject active connector ownership" >&2
  exit 1
fi
after_cli_hash="$(shasum -a 256 "$cli" | awk '{print $1}')"
[[ "$before_cli_hash" == "$after_cli_hash" ]]

echo "[16/18] non-connector update preserves a full custom scope table"
full_scope_config="$tmp_root/full-scope.env"
printf 'LUTHN_IMAGE=test/luthn:old\n' >"$full_scope_config"
for scope_index in {0..15}; do
  printf 'Luthn__Auth__Tokens__0__Scopes__%s=custom.%s\n' \
    "$scope_index" "$scope_index" >>"$full_scope_config"
done
before_scope_hash="$(rg '^Luthn__Auth__Tokens__0__Scopes__' "$full_scope_config" | shasum -a 256 | awk '{print $1}')"
(
  export HOME="$home_dir"
  export PATH="$fake_bin:$PATH"
  export LUTHN_DATA_DIR="$data_dir"
  export LUTHN_CONFIG_DIR="$config_dir"
  export LUTHN_STATE_DIR="$tmp_root/full-scope-state"
  export LUTHN_BIN_DIR="$bin_dir"
  export LUTHN_COMPOSE_FILE="$data_dir/compose.yaml"
  export LUTHN_CONFIG_FILE="$full_scope_config"
  export LUTHN_CLI_PATH="$cli"
  set -- help
  # shellcheck disable=SC1090
  source "$repo_root/scripts/luthn" >/dev/null
  require_installation() { :; }
  require_docker() { :; }
  require_command() { :; }
  pull_image() { :; }
  ensure_directories() { mkdir -p "$state_dir/backups"; }
  image_id_for_container() { :; }
  download_runtime() { :; }
  compose_cmd() { :; }
  wait_for_postgres() { :; }
  stop_write_paths() { :; }
  wait_for_api() { :; }
  record_state() { :; }
  docker() { :; }
  update_luthn test/luthn:new >/dev/null
)
after_scope_hash="$(rg '^Luthn__Auth__Tokens__0__Scopes__' "$full_scope_config" | shasum -a 256 | awk '{print $1}')"
[[ "$before_scope_hash" == "$after_scope_hash" ]]

echo "[17/18] active connector update stops when scope setup fails"
active_scope_config="$tmp_root/full-scope-active.env"
printf 'LUTHN_IMAGE=test/luthn:old\n' >"$active_scope_config"
printf 'Luthn__Auth__Tokens__0__Scopes__0=agent.connection.read\n' >>"$active_scope_config"
printf 'Luthn__Auth__Tokens__0__Scopes__1=agent.connection.write\n' >>"$active_scope_config"
for scope_index in {2..15}; do
  printf 'Luthn__Auth__Tokens__0__Scopes__%s=custom.%s\n' \
    "$scope_index" "$scope_index" >>"$active_scope_config"
done
active_scope_state="$tmp_root/full-scope-active-state"
mkdir -p "$active_scope_state/connectors"
touch "$active_scope_state/connectors/codex.env"
compose_marker="$tmp_root/full-scope-active-compose-called"
active_cli_hash="$(shasum -a 256 "$cli" | awk '{print $1}')"
active_compose_hash="$(shasum -a 256 "$data_dir/compose.yaml" | awk '{print $1}')"
active_helper_hash="$(shasum -a 256 "$data_dir/runtime/luthn-codex-connector.py" | awk '{print $1}')"
if (
  export HOME="$home_dir"
  export PATH="$fake_bin:$PATH"
  export LUTHN_DATA_DIR="$data_dir"
  export LUTHN_CONFIG_DIR="$config_dir"
  export LUTHN_STATE_DIR="$active_scope_state"
  export LUTHN_BIN_DIR="$bin_dir"
  export LUTHN_COMPOSE_FILE="$data_dir/compose.yaml"
  export LUTHN_CONFIG_FILE="$active_scope_config"
  export LUTHN_CLI_PATH="$cli"
  set -- help
  # shellcheck disable=SC1090
  source "$repo_root/scripts/luthn" >/dev/null
  require_installation() { :; }
  require_docker() { :; }
  require_command() { :; }
  pull_image() { :; }
  ensure_directories() { mkdir -p "$state_dir/backups"; }
  image_id_for_container() { :; }
  download_runtime() {
    printf 'replaced\n' >"$cli_path"
    printf 'replaced\n' >"$compose_file"
    printf 'replaced\n' >"$runtime_dir/luthn-codex-connector.py"
  }
  reconcile_codex_managed_configuration() { :; }
  compose_cmd() { touch "$compose_marker"; }
  record_state() { :; }
  docker() { :; }
  update_luthn test/luthn:new >/dev/null
); then
  echo "expected active connector update scope failure" >&2
  exit 1
fi
[[ ! -e "$compose_marker" ]]
[[ "$(shasum -a 256 "$cli" | awk '{print $1}')" == "$active_cli_hash" ]]
[[ "$(shasum -a 256 "$data_dir/compose.yaml" | awk '{print $1}')" == "$active_compose_hash" ]]
[[ "$(shasum -a 256 "$data_dir/runtime/luthn-codex-connector.py" | awk '{print $1}')" == "$active_helper_hash" ]]

echo "[18/18] active connector update reconciles templates and rolls back probe failures"
reconcile_root="$tmp_root/update-reconcile"
reconcile_data="$reconcile_root/data"
reconcile_config="$reconcile_root/config"
reconcile_state="$reconcile_root/state"
reconcile_bin="$reconcile_root/bin"
reconcile_codex="$reconcile_root/codex"
mkdir -p "$reconcile_data/runtime" "$reconcile_config" \
  "$reconcile_state/connectors" "$reconcile_bin" "$reconcile_codex"
cp "$repo_root/scripts/luthn" "$reconcile_bin/luthn"
chmod 0755 "$reconcile_bin/luthn"
cp "$repo_root/scripts/luthn-codex-connector.py" \
  "$reconcile_data/runtime/luthn-codex-connector.py"
chmod 0700 "$reconcile_data/runtime/luthn-codex-connector.py"
sed 's/print("2")/print("3")/' "$tmp_root/connector-helper-fixture.py" \
  >"$reconcile_root/target-helper.py"
chmod 0700 "$reconcile_root/target-helper.py"
printf 'services: {}\n' >"$reconcile_data/compose.yaml"
cp "$config_dir/luthn.env" "$reconcile_config/luthn.env"
cat >"$reconcile_codex/hooks.json" <<'EOF'
{"hooks":{"Stop":[{"matcher":"other.owner","hooks":[{"type":"command","command":"other"}]},{"matcher":"luthn.agent-connector.v1","hooks":[{"type":"command","command":"old","statusMessage":"stale"}]}]}}
EOF
cat >"$reconcile_codex/AGENTS.md" <<'EOF'
# User instructions

Preserve this text.

<!-- luthn:auto-recall:start -->
Legacy managed instructions.
<!-- luthn:auto-recall:end -->
EOF
cat >"$reconcile_state/connectors/codex.env" <<EOF
AGENT_ID=codex
CONNECTOR_VERSION=1
HOOKS_FILE=$reconcile_codex/hooks.json
INSTRUCTIONS_FILE=$reconcile_codex/AGENTS.md
AUTO_RECALL=true
MCP_CHANGED=false
SETUP_STATE=configured
EOF
(
  export HOME="$home_dir"
  export PATH="$fake_bin:$PATH"
  export LUTHN_DATA_DIR="$reconcile_data"
  export LUTHN_CONFIG_DIR="$reconcile_config"
  export LUTHN_STATE_DIR="$reconcile_state"
  export LUTHN_BIN_DIR="$reconcile_bin"
  export LUTHN_COMPOSE_FILE="$reconcile_data/compose.yaml"
  export LUTHN_CONFIG_FILE="$reconcile_config/luthn.env"
  export LUTHN_CLI_PATH="$reconcile_bin/luthn"
  export LUTHN_CODEX_CONNECTOR_HELPER="$reconcile_data/runtime/luthn-codex-connector.py"
  export REAL_CONNECTOR_HELPER="$repo_root/scripts/luthn-codex-connector.py"
  set -- help
  # shellcheck disable=SC1090
  source "$repo_root/scripts/luthn" >/dev/null
  require_installation() { :; }
  require_docker() { :; }
  require_command() { :; }
  pull_image() { :; }
  image_id_for_container() { printf '%s' sha256:previous; }
  download_runtime() {
    cp "$reconcile_root/target-helper.py" "$LUTHN_CODEX_CONNECTOR_HELPER"
    chmod 0700 "$LUTHN_CODEX_CONNECTOR_HELPER"
  }
  ensure_connector_scopes() { :; }
  compose_cmd() {
    if [[ " $* " == *" --list-tools "* ]]; then
      printf '%s\n' get_context_pack
    elif [[ " $* " == *" pg_dump "* ]]; then
      printf '%s\n' backup
    fi
  }
  wait_for_postgres() { :; }
  stop_write_paths() { :; }
  wait_for_api() { :; }
  record_state() { :; }
  docker() {
    if [[ " $* " == *"{{.Id}}"* ]]; then printf '%s\n' sha256:target; fi
  }
  update_luthn test/luthn:new >/dev/null
)
grep -q '^CONNECTOR_VERSION=3$' "$reconcile_state/connectors/codex.env"
python3 - "$reconcile_codex/hooks.json" <<'PY'
import json
import sys
document = json.load(open(sys.argv[1], encoding="utf-8"))
managed = [
    group for group in document["hooks"]["Stop"]
    if group.get("matcher") == "luthn.agent-connector.v1"
]
assert len(managed) == 1
assert managed[0]["hooks"][0]["statusMessage"] == "Luthn 메모리 저장 예약 중…"
PY
grep -q 'Preserve this text.' "$reconcile_codex/AGENTS.md"
grep -q '`maxItems`: 3' "$reconcile_codex/AGENTS.md"

python3 - "$reconcile_state/connectors/codex.env" <<'PY'
import sys
path = sys.argv[1]
content = open(path, encoding="utf-8").read()
with open(path, "w", encoding="utf-8") as stream:
    stream.write(content.replace("CONNECTOR_VERSION=3\n", "CONNECTOR_VERSION=1\n"))
PY
python3 - "$reconcile_codex/hooks.json" <<'PY'
import json
import sys
path = sys.argv[1]
document = json.load(open(path, encoding="utf-8"))
for group in document["hooks"]["Stop"]:
    if group.get("matcher") == "luthn.agent-connector.v1":
        group["hooks"][0]["statusMessage"] = "stale before failed update"
with open(path, "w", encoding="utf-8") as stream:
    json.dump(document, stream, ensure_ascii=False)
    stream.write("\n")
PY
rollback_cli_hash="$(shasum -a 256 "$reconcile_bin/luthn" | awk '{print $1}')"
rollback_compose_hash="$(shasum -a 256 "$reconcile_data/compose.yaml" | awk '{print $1}')"
rollback_hook_hash="$(shasum -a 256 "$reconcile_codex/hooks.json" | awk '{print $1}')"
rollback_instruction_hash="$(shasum -a 256 "$reconcile_codex/AGENTS.md" | awk '{print $1}')"
rollback_state_hash="$(shasum -a 256 "$reconcile_state/connectors/codex.env" | awk '{print $1}')"
if (
  export HOME="$home_dir"
  export PATH="$fake_bin:$PATH"
  export LUTHN_DATA_DIR="$reconcile_data"
  export LUTHN_CONFIG_DIR="$reconcile_config"
  export LUTHN_STATE_DIR="$reconcile_state"
  export LUTHN_BIN_DIR="$reconcile_bin"
  export LUTHN_COMPOSE_FILE="$reconcile_data/compose.yaml"
  export LUTHN_CONFIG_FILE="$reconcile_config/luthn.env"
  export LUTHN_CLI_PATH="$reconcile_bin/luthn"
  export LUTHN_CODEX_CONNECTOR_HELPER="$reconcile_data/runtime/luthn-codex-connector.py"
  export REAL_CONNECTOR_HELPER="$repo_root/scripts/luthn-codex-connector.py"
  set -- help
  # shellcheck disable=SC1090
  source "$repo_root/scripts/luthn" >/dev/null
  require_installation() { :; }
  require_docker() { :; }
  require_command() { :; }
  pull_image() { :; }
  image_id_for_container() { printf '%s' sha256:previous; }
  download_runtime() {
    printf '# changed runtime\n' >"$compose_file"
    printf '# changed cli\n' >"$cli_path"
  }
  ensure_connector_scopes() { :; }
  compose_cmd() {
    if [[ " $* " == *" --list-tools "* ]]; then
      printf '%s\n' search_safe_context
    fi
  }
  record_state() { :; }
  docker() {
    if [[ " $* " == *"{{.Id}}"* ]]; then printf '%s\n' sha256:target; fi
  }
  update_luthn test/luthn:new >/dev/null
); then
  echo "expected missing get_context_pack tool to stop update" >&2
  exit 1
fi
[[ "$(shasum -a 256 "$reconcile_bin/luthn" | awk '{print $1}')" == "$rollback_cli_hash" ]]
[[ "$(shasum -a 256 "$reconcile_data/compose.yaml" | awk '{print $1}')" == "$rollback_compose_hash" ]]
[[ "$(shasum -a 256 "$reconcile_codex/hooks.json" | awk '{print $1}')" == "$rollback_hook_hash" ]]
[[ "$(shasum -a 256 "$reconcile_codex/AGENTS.md" | awk '{print $1}')" == "$rollback_instruction_hash" ]]
[[ "$(shasum -a 256 "$reconcile_state/connectors/codex.env" | awk '{print $1}')" == "$rollback_state_hash" ]]

echo "legacy connector runtime rollback uses version-only reconciliation"
cat >"$reconcile_root/legacy-helper.py" <<'PY'
#!/usr/bin/env python3
import os
import subprocess
import sys

if len(sys.argv) > 1 and sys.argv[1] == "version":
    print("1")
    raise SystemExit(0)
if len(sys.argv) > 1 and sys.argv[1] in {"helper-digest", "template-digest"}:
    raise SystemExit(2)
raise SystemExit(
    subprocess.call([sys.executable, os.environ["REAL_CONNECTOR_HELPER"], *sys.argv[1:]])
)
PY
chmod 0700 "$reconcile_root/legacy-helper.py"
(
  export HOME="$home_dir"
  export PATH="$fake_bin:$PATH"
  export LUTHN_DATA_DIR="$reconcile_data"
  export LUTHN_CONFIG_DIR="$reconcile_config"
  export LUTHN_STATE_DIR="$reconcile_state"
  export LUTHN_BIN_DIR="$reconcile_bin"
  export LUTHN_COMPOSE_FILE="$reconcile_data/compose.yaml"
  export LUTHN_CONFIG_FILE="$reconcile_config/luthn.env"
  export LUTHN_CLI_PATH="$reconcile_bin/luthn"
  export LUTHN_CODEX_CONNECTOR_HELPER="$reconcile_data/runtime/luthn-codex-connector.py"
  export REAL_CONNECTOR_HELPER="$repo_root/scripts/luthn-codex-connector.py"
  set -- help
  # shellcheck disable=SC1090
  source "$repo_root/scripts/luthn" >/dev/null
  require_installation() { :; }
  require_docker() { :; }
  require_command() { :; }
  pull_image() { :; }
  image_id_for_container() { printf '%s' sha256:previous; }
  download_runtime() {
    cp "$reconcile_root/legacy-helper.py" "$LUTHN_CODEX_CONNECTOR_HELPER"
    chmod 0700 "$LUTHN_CODEX_CONNECTOR_HELPER"
  }
  ensure_connector_scopes() { :; }
  compose_cmd() {
    if [[ " $* " == *" --list-tools "* ]]; then
      printf '%s\n' get_context_pack
    elif [[ " $* " == *" pg_dump "* ]]; then
      printf '%s\n' backup
    fi
  }
  wait_for_postgres() { :; }
  stop_write_paths() { :; }
  wait_for_api() { :; }
  record_state() { :; }
  docker() {
    if [[ " $* " == *"{{.Id}}"* ]]; then printf '%s\n' sha256:target; fi
  }
  update_luthn test/luthn:legacy >/dev/null
)
grep -q '^CONNECTOR_VERSION=1$' "$reconcile_state/connectors/codex.env"
! grep -q '^HELPER_DIGEST=' "$reconcile_state/connectors/codex.env"
! grep -q '^TEMPLATE_DIGEST=' "$reconcile_state/connectors/codex.env"

echo "Agent connector lifecycle tests passed."
