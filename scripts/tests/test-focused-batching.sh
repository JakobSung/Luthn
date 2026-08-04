#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
runner="$repo_root/scripts/run-focused-tests.sh"

test -x "$runner"

list_output="$(bash "$runner" --list)"
printf '%s\n' "$list_output" | rg -q 'host-safety'
printf '%s\n' "$list_output" | rg -q 'one Host API project invocation'
printf '%s\n' "$list_output" | rg -q 'MemoryEndpointTests'
printf '%s\n' "$list_output" | rg -q 'AgentSafeEndpointTests'
printf '%s\n' "$list_output" | rg -q 'SensitiveMemoryProtectionTests'
printf '%s\n' "$list_output" | rg -q 'RetrievalCandidateSelectorTests'
printf '%s\n' "$list_output" | rg -q 'RetrievalEndpointTests'
printf '%s\n' "$list_output" | rg -q 'OperationalMetricsTests'
printf '%s\n' "$list_output" | rg -q 'OwnershipIsolationTests'
printf '%s\n' "$list_output" | rg -q 'AgentConnectionEndpointTests'
printf '%s\n' "$list_output" | rg -q -- '--no-restore'
printf '%s\n' "$list_output" | rg -q -- "command .*--filter '.*|.*'"
if printf '%s\n' "$list_output" | rg -q -- '--run all'; then
  printf '%s\n' "unsupported all focused batch was advertised" >&2
  exit 1
fi

if bash "$runner" --run unknown >/dev/null 2>&1; then
  printf '%s\n' "unknown focused batch was accepted" >&2
  exit 1
fi

if bash "$runner" --run all >/dev/null 2>&1; then
  printf '%s\n' "unsupported all focused batch was accepted" >&2
  exit 1
fi

printf '%s\n' "focused batching contract passed"
