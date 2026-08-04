#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
timing="$repo_root/scripts/tests/test-test-tier-timing.sh"

help_output="$(bash "$timing" --help)"
printf '%s\n' "$help_output" | rg -q -- '--runs N'
printf '%s\n' "$help_output" | rg -q 'at least 2'

if bash "$timing" --runs 1 >/dev/null 2>&1; then
  printf '%s\n' "timing harness accepted a single sample" >&2
  exit 1
fi

if bash "$timing" --runs invalid >/dev/null 2>&1; then
  printf '%s\n' "timing harness accepted a nonnumeric run count" >&2
  exit 1
fi

printf '%s\n' "timing harness contract passed"
