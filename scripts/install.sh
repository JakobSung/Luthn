#!/usr/bin/env bash
set -euo pipefail

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "missing required command: $1" >&2
    exit 1
  fi
}

require_command curl

connect_codex=false
while (( $# > 0 )); do
  case "$1" in
    --connect-codex) connect_codex=true ;;
    *) echo "usage: install.sh [--connect-codex]" >&2; exit 2 ;;
  esac
  shift
done

install_ref="${LUTHN_INSTALL_REF:-main}"
source_base_url="${LUTHN_SOURCE_BASE_URL:-https://raw.githubusercontent.com/JakobSung/Luthn/${install_ref}}"
bin_dir="${LUTHN_BIN_DIR:-${HOME:?HOME is required}/.local/bin}"
cli_path="$bin_dir/luthn"

mkdir -p "$bin_dir"
tmp_file="$(mktemp "${TMPDIR:-/tmp}/luthn.XXXXXX")"
trap 'rm -f "$tmp_file"' EXIT HUP INT TERM

curl -fsSL "$source_base_url/scripts/luthn" -o "$tmp_file"
bash -n "$tmp_file"
chmod 0755 "$tmp_file"
mv "$tmp_file" "$cli_path"
trap - EXIT HUP INT TERM

if [[ "$connect_codex" == "true" ]]; then
  LUTHN_DISTRIBUTION_REF="$install_ref" \
  LUTHN_SOURCE_BASE_URL="$source_base_url" \
    "$cli_path" install --connect-codex
else
  LUTHN_DISTRIBUTION_REF="$install_ref" \
  LUTHN_SOURCE_BASE_URL="$source_base_url" \
    "$cli_path" install
fi

case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *)
    printf '\nAdd Luthn to your PATH:\n  export PATH="%s:$PATH"\n' "$bin_dir"
    ;;
esac
