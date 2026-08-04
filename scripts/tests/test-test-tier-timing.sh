#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
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
    '| Measurement | Tier | Command | Test files | Cases | Elapsed ms | Environment / availability |' \
    '| --- | --- | --- | ---: | ---: | ---: | --- |'
}

print_failure() {
  measurement="$1"
  tier="$2"
  display_command="$3"
  file_count="$4"
  elapsed_ms="$5"
  exit_code="$6"
  printf '| %s | %s | %s | %s | n/a | %s | failed (exit %s) |\n' \
    "$measurement" "$tier" "$display_command" "$file_count" "$elapsed_ms" "$exit_code"
}

measure_dotnet() {
  measurement="$1"
  tier="$2"
  project_path="$3"
  filter="$4"
  display_command="$5"
  file_root="$6"
  output_path="$temp_root/${measurement}.log"
  file_count="$(count_files "$file_root" '\.(cs|csproj)$')"
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
    print_failure "$measurement" "$tier" "$display_command" "$file_count" "$elapsed_ms" "$exit_code"
    failed=1
    return
  fi
  if ! cases="$(extract_dotnet_cases "$output_path")"; then
    print_failure "$measurement" "$tier" "$display_command" "$file_count" "$elapsed_ms" "summary-unparsed"
    failed=1
    return
  fi
  printf '| %s | %s | %s | %s | %s | %s | %s |\n' \
    "$measurement" "$tier" "$display_command" "$file_count" "$cases" "$elapsed_ms" "$environment"
}

measure_python() {
  measurement="$1"
  tier="$2"
  display_command="$3"
  output_path="$temp_root/${measurement}.log"
  file_count="$(count_files 'scripts/tests' '\.py$')"
  start_ms="$(now_ms)"
  if python3 -m unittest discover -s "$repo_root/scripts/tests" -p 'test_*.py' > "$output_path" 2>&1; then
    exit_code=0
  else
    exit_code=$?
  fi
  end_ms="$(now_ms)"
  elapsed_ms=$((end_ms - start_ms))
  if [ "$exit_code" -ne 0 ]; then
    print_failure "$measurement" "$tier" "$display_command" "$file_count" "$elapsed_ms" "$exit_code"
    failed=1
    return
  fi
  if ! cases="$(extract_python_cases "$output_path")"; then
    print_failure "$measurement" "$tier" "$display_command" "$file_count" "$elapsed_ms" "summary-unparsed"
    failed=1
    return
  fi
  printf '| %s | %s | %s | %s | %s | %s | %s |\n' \
    "$measurement" "$tier" "$display_command" "$file_count" "$cases" "$elapsed_ms" "$environment"
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
