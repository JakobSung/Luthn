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
claude_home="$home_dir/.claude"
claude_mcp_state="$tmp_root/claude-mcp-state"
cli="$bin_dir/luthn"

mkdir -p "$home_dir" "$data_dir" "$config_dir" "$state_dir" "$bin_dir" "$fake_bin" "$claude_home"
cp "$repo_root/scripts/luthn" "$cli"
chmod 0755 "$cli"
touch "$data_dir/compose.yaml"
printf '%s' test-token >"$config_dir/service-token"
chmod 0600 "$config_dir/service-token"

cat >"$config_dir/luthn.env" <<EOF
LUTHN_IMAGE=test/luthn:local
LUTHN_BASE_URL=http://127.0.0.1:1
LUTHN_SERVICE_TOKEN_FILE=$config_dir/service-token
Luthn__Auth__Tokens__0__Scopes__0=agent.connection.read
Luthn__Auth__Tokens__0__Scopes__1=agent.connection.write
Luthn__Auth__Tokens__0__Scopes__2=access.request
Luthn__Auth__Tokens__0__Scopes__3=metrics.write
EOF

cat >"$fake_bin/docker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == "info" || ( "${1:-}" == "compose" && "${2:-}" == "version" ) ]]; then
  exit 0
fi
if [[ " $* " == *" --list-tools "* ]]; then
  printf '%s\n' get_context_pack
fi
EOF
chmod 0755 "$fake_bin/docker"

cat >"$fake_bin/curl" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
chmod 0755 "$fake_bin/curl"

cat >"$fake_bin/claude" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
state="${FAKE_CLAUDE_MCP_STATE:?}"
[[ "${1:-}" == "mcp" ]] || exit 2
case "${2:-}" in
  get)
    [[ "${3:-}" == "luthn" && -f "$state" ]] || exit 1
    printf 'luthn\n  scope: user\n  command: %s\n' "$(cat "$state")"
    ;;
  add)
    [[ "${3:-}" == "--scope" && "${4:-}" == "user" && "${5:-}" == "luthn" && "${6:-}" == "--" ]] || exit 2
    printf '%s %s\n' "${7:?missing MCP command}" "${8:?missing MCP argument}" >"$state"
    ;;
  remove)
    [[ "${3:-}" == "luthn" ]] || exit 2
    rm -f "$state"
    ;;
  *) exit 2 ;;
esac
EOF
chmod 0755 "$fake_bin/claude"

cat >"$claude_home/settings.json" <<'EOF'
{
  "permissions": {"allow": ["Read"]},
  "hooks": {
    "Stop": [{"matcher": "other.owner", "hooks": [{"type": "command", "command": "other"}]}]
  }
}
EOF
printf '%s\n' '# Personal Claude instructions' >"$claude_home/CLAUDE.md"

run_luthn() {
  env \
    HOME="$home_dir" \
    PATH="$fake_bin:$PATH" \
    FAKE_CLAUDE_MCP_STATE="$claude_mcp_state" \
    LUTHN_DATA_DIR="$data_dir" \
    LUTHN_CONFIG_DIR="$config_dir" \
    LUTHN_STATE_DIR="$state_dir" \
    LUTHN_BIN_DIR="$bin_dir" \
    LUTHN_COMPOSE_FILE="$data_dir/compose.yaml" \
    LUTHN_CONFIG_FILE="$config_dir/luthn.env" \
    LUTHN_CLI_PATH="$cli" \
    LUTHN_SERVICE_TOKEN_FILE="$config_dir/service-token" \
    LUTHN_CODEX_CONNECTOR_HELPER="$repo_root/scripts/luthn-codex-connector.py" \
    "$cli" "$@"
}

connect_output="$(run_luthn connect claude)"
grep -q 'Claude Code connector is configured' <<<"$connect_output"
[[ "$(cat "$claude_mcp_state")" == "$cli mcp" ]]
[[ -f "$state_dir/connectors/claude-code.env" ]]
grep -q '^SETUP_STATE=configured$' "$state_dir/connectors/claude-code.env"
! grep -q 'test-token' "$state_dir/connectors/claude-code.env"
python3 - "$claude_home/settings.json" "$config_dir" <<'PY'
import json
import sys

document = json.load(open(sys.argv[1], encoding="utf-8"))
groups = document["hooks"]["Stop"]
assert any(group.get("matcher") == "other.owner" for group in groups), groups
owned = [group for group in groups if group.get("matcher") == "luthn.claude-agent-connector.v1"]
assert len(owned) == 1, groups
handler = owned[0]["hooks"][0]
assert handler["command"] == "python3", handler
assert handler["args"][1:] == [
    "claude-hook-run", "--base-url", "http://127.0.0.1:1", "--token-file",
    f"{sys.argv[2]}/service-token", "--excluded-token-file",
    f"{sys.argv[2]}/operator-token", "--connector-version", "4",
], handler
PY
grep -q '<!-- luthn:auto-recall:start -->' "$claude_home/CLAUDE.md"
grep -q 'Agent memory mutation boundary' "$claude_home/CLAUDE.md"
grep -q 'Never delete, modify, overwrite, approve, or deny Luthn memory' "$claude_home/CLAUDE.md"

status_output="$(run_luthn connection status claude 2>/dev/null)"
grep -q '^Local connector: configured$' <<<"$status_output"
grep -q '^  automatic-ingestion: configured$' <<<"$status_output"
grep -q '^  mcp: configured$' <<<"$status_output"
grep -q '^  lightweight-recall: enabled$' <<<"$status_output"

run_luthn disconnect claude >/dev/null
[[ ! -f "$claude_mcp_state" ]]
[[ ! -f "$state_dir/connectors/claude-code.env" ]]
grep -q 'other.owner' "$claude_home/settings.json"
! grep -q 'luthn.claude-agent-connector.v1' "$claude_home/settings.json"
! grep -q '<!-- luthn:auto-recall:start -->' "$claude_home/CLAUDE.md"

echo "Claude connector lifecycle tests passed."
