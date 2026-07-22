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
channel=stable
version=""
selector_kind=""
while (( $# > 0 )); do
  case "$1" in
    --connect-codex) connect_codex=true ;;
    --channel)
      [[ -z "$selector_kind" ]] || {
        echo "choose exactly one of --channel or --version" >&2
        exit 2
      }
      [[ "${2:-}" == "stable" || "${2:-}" == "main" ]] || {
        echo "usage: install.sh [--channel stable|main|--version vMAJOR.MINOR.PATCH] [--connect-codex]" >&2
        exit 2
      }
      channel="$2"
      selector_kind=channel
      shift
      ;;
    --version)
      [[ -z "$selector_kind" ]] || {
        echo "choose exactly one of --channel or --version" >&2
        exit 2
      }
      [[ "${2:-}" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || {
        echo "usage: install.sh [--channel stable|main|--version vMAJOR.MINOR.PATCH] [--connect-codex]" >&2
        exit 2
      }
      version="$2"
      selector_kind=version
      shift
      ;;
    *)
      echo "usage: install.sh [--channel stable|main|--version vMAJOR.MINOR.PATCH] [--connect-codex]" >&2
      exit 2
      ;;
  esac
  shift
done

install_ref="${LUTHN_INSTALL_REF:-${version:-main}}"
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

install_args=(install)
if [[ -n "$version" ]]; then
  install_args+=(--version "$version")
elif [[ "$selector_kind" == "channel" || -z "${LUTHN_IMAGE:-}" ]]; then
  install_args+=(--channel "$channel")
fi
[[ "$connect_codex" == "true" ]] && install_args+=(--connect-codex)
LUTHN_DISTRIBUTION_REF="$install_ref" \
LUTHN_SOURCE_BASE_URL="$source_base_url" \
  "$cli_path" "${install_args[@]}"

case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *)
    printf '\nAdd Luthn to your PATH:\n  export PATH="%s:$PATH"\n' "$bin_dir"
    ;;
esac
