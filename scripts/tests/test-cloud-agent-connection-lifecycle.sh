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
mcp_dir="$tmp_root/mcp"
cloud_step_file="$tmp_root/cloud-step"
cli="$bin_dir/luthn"

mkdir -p "$home_dir" "$data_dir" "$config_dir" "$state_dir" "$bin_dir" "$fake_bin" "$mcp_dir"
cp "$repo_root/scripts/luthn" "$cli"
chmod 0755 "$cli"
touch "$data_dir/compose.yaml"
printf '%s\n' 'LUTHN_IMAGE=test/luthn:local' >"$config_dir/luthn.env"
touch "$mcp_dir/luthn"

cat >"$fake_bin/docker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == "info" || ( "${1:-}" == "compose" && "${2:-}" == "version" ) ]]; then
  exit 0
fi
if [[ " $* " == *" cloud-agent cloud-agent "* ]]; then
  if [[ "${FAKE_CLOUD_UNAVAILABLE:-false}" == "true" ]]; then
    printf '%s\n' 'Cloud unavailable' >&2
    exit 42
  fi
  step_file="${FAKE_CLOUD_STEP_FILE:?}"
  if [[ ! -f "$step_file" ]]; then
    touch "$step_file"
    printf '%s\n' '{"state":"approval-required","verificationUri":"https://cloud.example/connect/device","userCode":"ABCD-EFGH","retryAfterSeconds":0}'
  else
    agent_kind="codex"
    [[ " $* " == *" --agent claude "* ]] && agent_kind="claude"
    printf '%s\n' "{\"state\":\"connected\",\"agentConnectionId\":\"81000000-0000-0000-0000-000000000001\",\"organizationId\":\"20000000-0000-0000-0000-000000000001\",\"workspaceId\":\"30000000-0000-0000-0000-000000000001\",\"agentKind\":\"$agent_kind\",\"capabilityPreset\":\"reader\",\"remoteMcpUrl\":\"https://cloud.example/mcp\"}"
  fi
  exit 0
fi
if [[ " $* " == *" mcp --list-tools "* ]]; then
  printf '%s\n' 'get_context_pack' 'search_safe_context'
  exit 0
fi
exit 2
EOF
chmod 0755 "$fake_bin/docker"

cat >"$fake_bin/codex" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
state_dir="${FAKE_MCP_DIR:?}"
[[ "${1:-}" == "mcp" ]] || exit 2
case "${2:-}" in
  get)
    [[ -f "$state_dir/${3:?}" ]] || exit 1
    printf '%s\n' "${3}"
    ;;
  add)
    [[ "${3:-}" == "luthn-cloud" && "${4:-}" == "--url" && "${5:-}" == "https://cloud.example/mcp" ]] || exit 2
    touch "$state_dir/luthn-cloud"
    ;;
  login)
    [[ "${3:-}" == "luthn-cloud" && "${4:-}" == "--scopes" && "${5:-}" == "openid,email" ]] || exit 2
    ;;
  remove)
    [[ "${3:-}" == "luthn-cloud" ]] || exit 2
    rm -f "$state_dir/luthn-cloud"
    ;;
  *) exit 2 ;;
esac
EOF
chmod 0755 "$fake_bin/codex"

cat >"$fake_bin/claude" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
state_dir="${FAKE_MCP_DIR:?}"
[[ "${1:-}" == "mcp" ]] || exit 2
case "${2:-}" in
  get)
    [[ -f "$state_dir/${3:?}" ]] || exit 1
    printf '%s\n' "${3}"
    ;;
  add)
    [[ "${3:-}" == "--scope" && "${4:-}" == "user" && "${5:-}" == "--transport" && "${6:-}" == "http" && "${7:-}" == "luthn-cloud" && "${8:-}" == "https://cloud.example/mcp" ]] || exit 2
    touch "$state_dir/luthn-cloud"
    ;;
  remove)
    [[ "${3:-}" == "luthn-cloud" ]] || exit 2
    rm -f "$state_dir/luthn-cloud"
    ;;
  *) exit 2 ;;
esac
EOF
chmod 0755 "$fake_bin/claude"

cat >"$fake_bin/open" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
chmod 0755 "$fake_bin/open"

run_luthn() {
  env \
    HOME="$home_dir" \
    PATH="$fake_bin:$PATH" \
    FAKE_CLOUD_STEP_FILE="$cloud_step_file" \
    FAKE_MCP_DIR="$mcp_dir" \
    LUTHN_DATA_DIR="$data_dir" \
    LUTHN_CONFIG_DIR="$config_dir" \
    LUTHN_STATE_DIR="$state_dir" \
    LUTHN_BIN_DIR="$bin_dir" \
    LUTHN_COMPOSE_FILE="$data_dir/compose.yaml" \
    LUTHN_CONFIG_FILE="$config_dir/luthn.env" \
    LUTHN_CLI_PATH="$cli" \
    "$cli" "$@"
}

connect_output="$(run_luthn cloud connect codex \
  --workspace 30000000-0000-0000-0000-000000000001 \
  --cloud-url https://cloud.example)"
grep -q 'URL:  https://cloud.example/connect/device' <<<"$connect_output"
grep -q 'Code: ABCD-EFGH' <<<"$connect_output"
grep -q "separate 'luthn-cloud' MCP server" <<<"$connect_output"
[[ -f "$mcp_dir/luthn" ]]
[[ -f "$mcp_dir/luthn-cloud" ]]
ownership_file="$state_dir/connectors/cloud-codex.env"
[[ -f "$ownership_file" ]]
grep -q '^REMOTE_MCP_URL=https://cloud.example/mcp$' "$ownership_file"
! grep -Eq 'access_token|refresh_token|PRIVATE KEY|ABCD-EFGH' "$ownership_file"

status_output="$(run_luthn cloud status codex)"
grep -q 'registered for codex' <<<"$status_output"

run_luthn cloud disconnect codex >/dev/null
[[ -f "$mcp_dir/luthn" ]]
[[ ! -f "$mcp_dir/luthn-cloud" ]]
[[ ! -f "$ownership_file" ]]
state_key_file="$config_dir/cloud-agent-state-key"
[[ -f "$state_key_file" ]]
[[ "$(wc -c <"$state_key_file" | tr -d ' ')" -eq 45 ]]

if FAKE_CLOUD_UNAVAILABLE=true run_luthn cloud connect codex \
  --workspace 30000000-0000-0000-0000-000000000001 \
  --cloud-url https://cloud.example >/dev/null 2>&1; then
  echo "expected Cloud connection failure" >&2
  exit 1
fi
[[ -f "$mcp_dir/luthn" ]]
[[ ! -f "$mcp_dir/luthn-cloud" ]]
local_tools="$(run_luthn mcp --list-tools)"
grep -q '^get_context_pack$' <<<"$local_tools"
grep -q '^search_safe_context$' <<<"$local_tools"

claude_output="$(run_luthn cloud connect claude \
  --workspace 30000000-0000-0000-0000-000000000001 \
  --cloud-url https://cloud.example)"
grep -q "separate 'luthn-cloud' MCP server" <<<"$claude_output"
[[ -f "$mcp_dir/luthn" ]]
[[ -f "$mcp_dir/luthn-cloud" ]]
claude_ownership_file="$state_dir/connectors/cloud-claude.env"
[[ -f "$claude_ownership_file" ]]
grep -q '^AGENT_KIND=claude$' "$claude_ownership_file"
run_luthn cloud disconnect claude >/dev/null
[[ -f "$mcp_dir/luthn" ]]
[[ ! -f "$mcp_dir/luthn-cloud" ]]
[[ ! -f "$claude_ownership_file" ]]

touch "$mcp_dir/luthn-cloud"
rm -f "$state_key_file"
if run_luthn cloud connect codex \
  --workspace 30000000-0000-0000-0000-000000000001 \
  --cloud-url https://cloud.example >/dev/null 2>&1; then
  echo "expected an unrelated luthn-cloud registration to be preserved" >&2
  exit 1
fi
[[ -f "$mcp_dir/luthn-cloud" ]]
[[ ! -f "$ownership_file" ]]
[[ ! -f "$state_key_file" ]]

echo "Cloud Agent connection lifecycle tests passed."
