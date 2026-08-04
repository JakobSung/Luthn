#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
run_count=3
while [ "$#" -gt 0 ]; do
  case "$1" in
    --help|-h)
      printf '%s\n' "Usage: scripts/tests/test-test-tier-timing.sh [--runs N]"
      printf '%s\n' "N must be an integer of at least 2; the default is 3."
      exit 0
      ;;
    --runs)
      if [ "$#" -lt 2 ]; then
        printf '%s\n' "--runs requires an integer" >&2
        exit 2
      fi
      run_count="$2"
      shift 2
      ;;
    *)
      printf '%s\n' "unknown argument: $1" >&2
      exit 2
      ;;
  esac
done
case "$run_count" in
  ''|*[!0-9]*)
    printf '%s\n' "--runs must be an integer of at least 2" >&2
    exit 2
    ;;
esac
if [ "$run_count" -lt 2 ]; then
  printf '%s\n' "--runs must be at least 2" >&2
  exit 2
fi
temp_root="$(mktemp -d "${TMPDIR:-/tmp}/luthn-test-tier-timing.XXXXXX")"
trap 'rm -rf "$temp_root"' EXIT HUP INT TERM

failed=0
platform="$(uname -srm | tr ' ' '-')"
dotnet_version="$(dotnet --version 2>/dev/null || printf 'unavailable')"
python_version="$(python3 --version 2>&1 | tr ' ' '-')"
environment="${platform};dotnet-${dotnet_version};${python_version}"

now_ms() {
  python3 -c 'import time; print(int(time.time() * 1000))'
}

count_files() {
  relative_root="$1"
  pattern="$2"
  rg --files "$repo_root/$relative_root" | rg "$pattern" | wc -l | tr -d ' '
}

extract_dotnet_cases() {
  output_path="$1"
  python3 - "$output_path" <<'PY'
import re
import sys
from pathlib import Path

text = Path(sys.argv[1]).read_text(encoding="utf-8", errors="replace")
matches = re.findall(r"(?:Total|전체)\s*:\s*([0-9][0-9,]*)", text)
if not matches:
    raise SystemExit(1)
print(matches[-1].replace(",", ""))
PY
}

extract_python_cases() {
  output_path="$1"
  python3 - "$output_path" <<'PY'
import re
import sys
from pathlib import Path

text = Path(sys.argv[1]).read_text(encoding="utf-8", errors="replace")
match = re.search(r"Ran\s+([0-9][0-9,]*)\s+tests", text)
if not match:
    raise SystemExit(1)
print(match.group(1).replace(",", ""))
PY
}

print_header() {
  printf '%s\n' \
    '| Measurement | Tier | Command | Test files | Cases | Runs | Min ms | Median ms | P95 ms | Max ms | Variance ms2 | Environment / availability |' \
    '| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |'
}

print_failure() {
  measurement="$1"
  tier="$2"
  display_command="$3"
  file_count="$4"
  run_number="$5"
  elapsed_ms="$6"
  exit_code="$7"
  printf '| %s | %s | %s | %s | n/a | %s | n/a | n/a | n/a | n/a | n/a | failed (exit %s; run %s, elapsed %s ms) |\n' \
    "$measurement" "$tier" "$display_command" "$file_count" "$run_count" "$exit_code" "$run_number" "$elapsed_ms"
}

print_measurement() {
  measurement="$1"
  tier="$2"
  display_command="$3"
  file_count="$4"
  cases="$5"
  times_path="$6"
  timing_environment="$7"
  python3 - "$measurement" "$tier" "$display_command" "$file_count" "$cases" "$times_path" "$timing_environment" <<'PY'
import math
import statistics
import sys
from pathlib import Path

measurement, tier, display_command, file_count, cases, times_path, environment = sys.argv[1:]
values = [
    int(line.strip())
    for line in Path(times_path).read_text(encoding="utf-8").splitlines()
    if line.strip()
]
if len(values) < 2:
    raise SystemExit("timing summary requires at least two samples")

values.sort()
rank = max(1, math.ceil(0.95 * len(values)))
p95 = values[rank - 1]
median = statistics.median(values)
variance = statistics.pvariance(values)

def format_value(value):
    if float(value).is_integer():
        return str(int(value))
    return "{:.1f}".format(value)

print(
    "| {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
        measurement,
        tier,
        display_command,
        file_count,
        cases,
        len(values),
        min(values),
        format_value(median),
        p95,
        max(values),
        format_value(variance),
        environment,
    )
)
PY
}

measure_dotnet() {
  measurement="$1"
  tier="$2"
  project_path="$3"
  filter="$4"
  display_command="$5"
  file_root="$6"
  times_path="$temp_root/$measurement.times"
  file_count="$(count_files "$file_root" '\.(cs|csproj)$')"
  : > "$times_path"
  run_number=1
  cases=""
  while [ "$run_number" -le "$run_count" ]; do
    output_path="$temp_root/$measurement.$run_number.log"
    start_ms="$(now_ms)"
    if [ -n "$filter" ]; then
      if dotnet test "$repo_root/$project_path" --no-restore --filter "$filter" > "$output_path" 2>&1; then
        exit_code=0
      else
        exit_code=$?
      fi
    elif dotnet test "$repo_root/$project_path" --no-restore > "$output_path" 2>&1; then
      exit_code=0
    else
      exit_code=$?
    fi
    end_ms="$(now_ms)"
    elapsed_ms=$((end_ms - start_ms))
    if [ "$exit_code" -ne 0 ]; then
      print_failure "$measurement" "$tier" "$display_command" "$file_count" "$run_number" "$elapsed_ms" "$exit_code"
      failed=1
      return
    fi
    if ! current_cases="$(extract_dotnet_cases "$output_path")"; then
      print_failure "$measurement" "$tier" "$display_command" "$file_count" "$run_number" "$elapsed_ms" "summary-unparsed"
      failed=1
      return
    fi
    if [ -z "$cases" ]; then
      cases="$current_cases"
    elif [ "$cases" != "$current_cases" ]; then
      print_failure "$measurement" "$tier" "$display_command" "$file_count" "$run_number" "$elapsed_ms" "case-count-drift"
      failed=1
      return
    fi
    printf '%s\n' "$elapsed_ms" >> "$times_path"
    run_number=$((run_number + 1))
  done
  print_measurement "$measurement" "$tier" "$display_command" "$file_count" "$cases" "$times_path" "$environment"
}

measure_python() {
  measurement="$1"
  tier="$2"
  display_command="$3"
  times_path="$temp_root/$measurement.times"
  file_count="$(count_files 'scripts/tests' '\.py$')"
  : > "$times_path"
  run_number=1
  cases=""
  while [ "$run_number" -le "$run_count" ]; do
    output_path="$temp_root/$measurement.$run_number.log"
    start_ms="$(now_ms)"
    if python3 -m unittest discover -s "$repo_root/scripts/tests" -p 'test_*.py' > "$output_path" 2>&1; then
      exit_code=0
    else
      exit_code=$?
    fi
    end_ms="$(now_ms)"
    elapsed_ms=$((end_ms - start_ms))
    if [ "$exit_code" -ne 0 ]; then
      print_failure "$measurement" "$tier" "$display_command" "$file_count" "$run_number" "$elapsed_ms" "$exit_code"
      failed=1
      return
    fi
    if ! current_cases="$(extract_python_cases "$output_path")"; then
      print_failure "$measurement" "$tier" "$display_command" "$file_count" "$run_number" "$elapsed_ms" "summary-unparsed"
      failed=1
      return
    fi
    if [ -z "$cases" ]; then
      cases="$current_cases"
    elif [ "$cases" != "$current_cases" ]; then
      print_failure "$measurement" "$tier" "$display_command" "$file_count" "$run_number" "$elapsed_ms" "case-count-drift"
      failed=1
      return
    fi
    printf '%s\n' "$elapsed_ms" >> "$times_path"
    run_number=$((run_number + 1))
  done
  print_measurement "$measurement" "$tier" "$display_command" "$file_count" "$cases" "$times_path" "$environment"
}

print_header
measure_dotnet \
  'fast-core' \
  'fast' \
  'tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj' \
  '' \
  'dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore' \
  'tests/Luthn.Core.Tests'
measure_python \
  'fast-python' \
  'fast' \
  "python3 -m unittest discover -s scripts/tests -p 'test_*.py'"
measure_dotnet \
  'focused-classification' \
  'focused' \
  'tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj' \
  'FullyQualifiedName~ClassificationContractTests' \
  'dotnet test tests/Luthn.Core.Tests/Luthn.Core.Tests.csproj --no-restore --filter FullyQualifiedName~ClassificationContractTests' \
  'tests/Luthn.Core.Tests'
measure_dotnet \
  'focused-memory' \
  'focused' \
  'tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj' \
  'FullyQualifiedName~MemoryEndpointTests' \
  'dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~MemoryEndpointTests' \
  'tests/Luthn.Host.Api.Tests'
measure_dotnet \
  'focused-sensitive-memory' \
  'focused' \
  'tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj' \
  'FullyQualifiedName~SensitiveMemoryProtectionTests' \
  'dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~SensitiveMemoryProtectionTests' \
  'tests/Luthn.Host.Api.Tests'
measure_dotnet \
  'focused-retrieval' \
  'focused' \
  'tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj' \
  'FullyQualifiedName~RetrievalEndpointTests' \
  'dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~RetrievalEndpointTests' \
  'tests/Luthn.Host.Api.Tests'
measure_dotnet \
  'focused-ownership' \
  'focused' \
  'tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj' \
  'FullyQualifiedName~OwnershipIsolationTests' \
  'dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter FullyQualifiedName~OwnershipIsolationTests' \
  'tests/Luthn.Host.Api.Tests'
measure_dotnet \
  'focused-mcp' \
  'focused' \
  'tests/Luthn.McpServer.Tests/Luthn.McpServer.Tests.csproj' \
  'FullyQualifiedName~McpToolBoundaryTests' \
  'dotnet test tests/Luthn.McpServer.Tests/Luthn.McpServer.Tests.csproj --no-restore --filter FullyQualifiedName~McpToolBoundaryTests' \
  'tests/Luthn.McpServer.Tests'

docker_status='unavailable: docker command not found'
if command -v docker >/dev/null 2>&1; then
  docker_status='not sampled: docker is available'
fi
printf '| environmental-docker | environmental | lifecycle and PostgreSQL scripts | n/a | n/a | n/a | %s |\n' "$docker_status"
printf '| environmental-windows | environmental | PowerShell Windows lifecycle scripts | n/a | n/a | n/a | unavailable: Windows runner required |\n'

if [ "$failed" -ne 0 ]; then
  exit 1
fi
