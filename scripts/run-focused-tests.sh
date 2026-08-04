#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
action="list"
group=""
host_project="$repo_root/tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj"
host_filter="FullyQualifiedName~MemoryEndpointTests|FullyQualifiedName~AgentSafeEndpointTests|FullyQualifiedName~SensitiveMemoryProtectionTests|FullyQualifiedName~RetrievalCandidateSelectorTests|FullyQualifiedName~RetrievalEndpointTests|FullyQualifiedName~OperationalMetricsTests|FullyQualifiedName~OwnershipIsolationTests|FullyQualifiedName~AgentConnectionEndpointTests"

usage() {
  printf '%s\n' "Usage: scripts/run-focused-tests.sh [--list|--run host-safety|--run all]"
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
        host-safety|all)
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
  printf '%s\n' "command | dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj --no-restore --filter $host_filter"
  printf '%s\n' "run | bash scripts/run-focused-tests.sh --run host-safety"
  exit 0
fi

dotnet test "$host_project" --no-restore --filter "$host_filter"
