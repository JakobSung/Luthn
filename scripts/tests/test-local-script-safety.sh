#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
tmp_root="$(mktemp -d)"
trap 'rm -rf "$tmp_root"' EXIT HUP INT TERM

test_repo="$tmp_root/repo"
fake_bin="$tmp_root/bin"
mkdir -p "$test_repo/scripts" "$fake_bin"
cp "$repo_root/scripts/install-local.sh" "$test_repo/scripts/install-local.sh"
cp "$repo_root/.env.example" "$test_repo/.env.example"
sed -i.bak 's/^  exit 0$/  return 0/' "$test_repo/scripts/install-local.sh"

cat >"$fake_bin/dotnet" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
chmod 0755 "$fake_bin/dotnet"

PATH="$fake_bin:$PATH" bash -c '
  set -euo pipefail
  cd "$1"
  source scripts/install-local.sh testing >/dev/null
  cat >"$env_file" <<EOF
LUTHN_SERVICE_VALUE=test-token
Luthn__Auth__RequireServiceToken=true
Luthn__Auth__Tokens__0__Name=local-agent
Luthn__Auth__Tokens__0__Sha256Digest=sha256:test
EOF
  for index in {0..14}; do
    printf "Luthn__Auth__Tokens__0__Scopes__%s=custom.%s\n" "$index" "$index" >>"$env_file"
  done
  before="$(shasum -a 256 "$env_file" | cut -d " " -f 1)"
  if ensure_local_service_token; then
    echo "expected one-slot connector scope failure" >&2
    exit 1
  fi
  after="$(shasum -a 256 "$env_file" | cut -d " " -f 1)"
  [[ "$before" == "$after" ]]
' _ "$test_repo"

if rg -q '/api/agent-connections/codex/observations|connectorVersion":"local-check' \
  "$repo_root/scripts/check-local.sh"; then
  echo "check-local.sh must not persist a synthetic Codex observation" >&2
  exit 1
fi
rg -q 'curl -fsS "\$base_url/api/agent-connections"' "$repo_root/scripts/check-local.sh"

echo "Local script safety tests passed."
