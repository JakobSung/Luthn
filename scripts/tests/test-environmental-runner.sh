#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
runner="$repo_root/scripts/run-environmental-tests.sh"

test -x "$runner"

list_output="$(bash "$runner" --list)"
printf '%s\n' "$list_output" | rg -q 'docker-connectors'
printf '%s\n' "$list_output" | rg -q 'docker-distribution'
printf '%s\n' "$list_output" | rg -q 'postgres-integration'
printf '%s\n' "$list_output" | rg -q 'windows-lifecycle'
printf '%s\n' "$list_output" | rg -q 'not-sampled'

check_output="$(bash "$runner" --check)"
printf '%s\n' "$check_output" | rg -q 'environmental test environment status'
printf '%s\n' "$check_output" | rg -q 'available|unavailable'

if bash "$runner" --run unknown >/dev/null 2>&1; then
  printf '%s\n' "unknown environmental scope was accepted" >&2
  exit 1
fi

printf '%s\n' "environmental runner contract passed"
