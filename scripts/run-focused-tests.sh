#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
temp_root="$(mktemp -d "${TMPDIR:-/tmp}/luthn-focused-tests.XXXXXX")"
trap 'rm -rf "$temp_root"' EXIT HUP INT TERM
action="list"
group=""
host_project="$repo_root/tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj"
host_filter="FullyQualifiedName~MemoryEndpointTests|FullyQualifiedName~AgentSafeEndpointTests|FullyQualifiedName~SensitiveMemoryProtectionTests|FullyQualifiedName~RetrievalCandidateSelectorTests|FullyQualifiedName~RetrievalEndpointTests|FullyQualifiedName~OperationalMetricsTests|FullyQualifiedName~OwnershipIsolationTests|FullyQualifiedName~AgentConnectionEndpointTests"
expected_classes="MemoryEndpointTests
AgentSafeEndpointTests
SensitiveMemoryProtectionTests
RetrievalCandidateSelectorTests
RetrievalEndpointTests
OperationalMetricsTests
OwnershipIsolationTests
AgentConnectionEndpointTests"

usage() {
  printf '%s\n' "Usage: scripts/run-focused-tests.sh [--list|--run host-safety]"
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --help|-h)
      usage
      exit 0
      ;;
    --list)
      action="list"
      shift
      ;;
    --run)
      if [ "$#" -lt 2 ]; then
        usage >&2
        exit 2
      fi
      action="run"
      group="$2"
      case "$group" in
        host-safety)
          ;;
        *)
          usage >&2
          exit 2
          ;;
      esac
      shift 2
      ;;
    *)
      usage >&2
      exit 2
      ;;
  esac
done

if [ "$action" = "list" ]; then
  printf '%s\n' "focused test batches"
  printf '%s\n' "host-safety | one Host API project invocation | MemoryEndpointTests, AgentSafeEndpointTests, SensitiveMemoryProtectionTests, RetrievalCandidateSelectorTests, RetrievalEndpointTests, OperationalMetricsTests, OwnershipIsolationTests, AgentConnectionEndpointTests"
  printf '%s\n' "filter | $host_filter"
  printf '%s\n' "command | dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter '$host_filter'"
  printf '%s\n' "run | bash scripts/run-focused-tests.sh --run host-safety"
  exit 0
fi

run_host_safety() {
  list_output="$temp_root/list-tests.log"
  if ! dotnet test "$host_project" --no-restore --list-tests --filter "$host_filter" > "$list_output" 2>&1; then
    cat "$list_output"
    printf '%s\n' "focused test discovery failed" >&2
    return 1
  fi
  cat "$list_output"

  while IFS= read -r class_name; do
    if [ -n "$class_name" ] && ! rg -q "$class_name\." "$list_output"; then
      printf '%s\n' "focused test class was not discovered: $class_name" >&2
      return 1
    fi
  done <<EOF
$expected_classes
EOF

  test_output="$temp_root/test.log"
  if dotnet test "$host_project" --no-restore --filter "$host_filter" > "$test_output" 2>&1; then
    exit_code=0
  else
    exit_code=$?
  fi
  cat "$test_output"
  if [ "$exit_code" -ne 0 ]; then
    return "$exit_code"
  fi

  executed_cases="$(python3 - "$test_output" <<'PY'
import re
import sys
from pathlib import Path

text = Path(sys.argv[1]).read_text(encoding="utf-8", errors="replace")
matches = re.findall(r"(?:Total|전체)\s*:\s*([0-9][0-9,]*)", text)
if not matches:
    raise SystemExit(1)
print(matches[-1].replace(",", ""))
PY
)" || {
    printf '%s\n' "focused test result did not contain a total case count" >&2
    return 1
  }

  if [ "$executed_cases" -lt 1 ]; then
    printf '%s\n' "focused test filter executed zero tests" >&2
    return 1
  fi
}

run_host_safety
