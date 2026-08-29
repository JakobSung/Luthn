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
registrations="$tmp_root/registrations"
cli="$bin_dir/luthn"
mkdir -p "$home_dir" "$data_dir" "$config_dir" "$state_dir/connectors" "$bin_dir" "$fake_bin" "$registrations"
cp "$repo_root/scripts/luthn" "$cli"
chmod 0755 "$cli"
touch "$data_dir/compose.yaml"
printf '%s' test-token >"$config_dir/service-token"
chmod 0600 "$config_dir/service-token"
printf '%s\n' 'LUTHN_IMAGE=test/luthn:local' >"$config_dir/luthn.env"
touch "$state_dir/connectors/codex.env"

cat >"$fake_bin/docker" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
chmod 0755 "$fake_bin/docker"

cat >"$fake_bin/codex" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
dir="${FAKE_CODEX_REGISTRATIONS:?}"
[[ "${1:-}" == "mcp" ]] || exit 2
case "${2:-}" in
  get)
    name="${3:-}"
    [[ -f "$dir/$name" ]] || exit 1
    if [[ "$name" == "luthn" ]]; then
      cat <<OUT
luthn
  enabled: true
  transport: stdio
  command: ${FAKE_LUTHN_CLI:?}
  args: mcp
OUT
    else
      printf '%s\n' "$name" '  enabled: true' '  transport: streamable-http'
    fi
    ;;
  add)
    name="${3:-}"
    [[ -n "$name" ]] || exit 2
    if [[ "$name" == "luthn" ]]; then
      [[ "${4:-}" == "--" && "${5:-}" == "${FAKE_LUTHN_CLI:?}" && "${6:-}" == "mcp" ]] || exit 2
    else
      [[ "${4:-}" == "--url" && -n "${5:-}" ]] || exit 2
    fi
    : >"$dir/$name"
    ;;
  login)
    [[ "${FAKE_CODEX_LOGIN_FAIL:-false}" != "true" ]] || exit 1
    [[ -f "$dir/${3:-}" ]] || exit 1
    ;;
  remove)
    rm -f "$dir/${3:-}"
    ;;
  *) exit 2 ;;
esac
EOF
chmod 0755 "$fake_bin/codex"

run_luthn() {
  env HOME="$home_dir" PATH="$fake_bin:$PATH" \
    FAKE_CODEX_REGISTRATIONS="$registrations" FAKE_LUTHN_CLI="$cli" \
    LUTHN_DATA_DIR="$data_dir" LUTHN_CONFIG_DIR="$config_dir" \
    LUTHN_STATE_DIR="$state_dir" LUTHN_BIN_DIR="$bin_dir" \
    LUTHN_COMPOSE_FILE="$data_dir/compose.yaml" LUTHN_CONFIG_FILE="$config_dir/luthn.env" \
    LUTHN_CLI_PATH="$cli" LUTHN_SERVICE_TOKEN_FILE="$config_dir/service-token" \
    LUTHN_HOST_HELPER_DISABLE_AUTOSTART=true \
    "$cli" "$@"
}

run_luthn connect codex >/dev/null 2>&1 || true
# The profile command deliberately requires the owned local registration, but no
# provider-specific values. Seed only that generic local MCP registration.
touch "$registrations/luthn"

echo "[1/6] switches an owned local Codex MCP to an authenticated generic remote MCP"
run_luthn profile remote codex --url https://mcp.example.test/mcp --oauth-client-id remote-client >/dev/null
[[ ! -f "$registrations/luthn" ]]
[[ -f "$registrations/luthn-remote" ]]
[[ -f "$state_dir/connectors/codex.remote-profile.env" ]]
grep -q '^REMOTE_URL=https://mcp.example.test/mcp$' "$state_dir/connectors/codex.remote-profile.env"

echo "[2/6] prevents local setup from re-registering the local MCP while the remote profile is active"
if run_luthn connect codex >/dev/null 2>&1; then
  echo "expected local connector setup to reject an active remote profile" >&2
  exit 1
fi
[[ ! -f "$registrations/luthn" ]]
[[ -f "$registrations/luthn-remote" ]]

echo "[3/6] restores the local MCP explicitly without touching local capture ownership"
run_luthn profile local codex >/dev/null
[[ -f "$registrations/luthn" ]]
[[ ! -f "$registrations/luthn-remote" ]]
[[ ! -f "$state_dir/connectors/codex.remote-profile.env" ]]

echo "[4/6] resolves a duplicate owned local and remote profile in favor of local"
run_luthn profile remote codex --url https://mcp.example.test/mcp >/dev/null
touch "$registrations/luthn"
run_luthn profile local codex >/dev/null
[[ -f "$registrations/luthn" ]]
[[ ! -f "$registrations/luthn-remote" ]]
[[ ! -f "$state_dir/connectors/codex.remote-profile.env" ]]

echo "[5/6] preserves the local MCP when remote OAuth fails"
if FAKE_CODEX_LOGIN_FAIL=true run_luthn profile remote codex --url https://mcp.example.test/mcp >/dev/null 2>&1; then
  echo "expected remote OAuth failure" >&2
  exit 1
fi
[[ -f "$registrations/luthn" ]]
[[ ! -f "$registrations/luthn-remote" ]]
[[ ! -f "$state_dir/connectors/codex.remote-profile.env" ]]

echo "[6/6] preserves the local MCP when remote ownership state cannot be written"
unwritable_state="$tmp_root/missing/codex.remote-profile.env"
if LUTHN_REMOTE_CODEX_PROFILE_STATE_FILE="$unwritable_state" run_luthn profile remote codex --url https://mcp.example.test/mcp >/dev/null 2>&1; then
  echo "expected remote profile state write failure" >&2
  exit 1
fi
[[ -f "$registrations/luthn" ]]
[[ ! -f "$registrations/luthn-remote" ]]

echo "Remote MCP profile tests passed."
