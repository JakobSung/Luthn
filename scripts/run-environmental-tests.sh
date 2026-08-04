#!/usr/bin/env bash
set -eo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
mode="check"
scope="all"
strict="false"

usage() {
  printf '%s\n' "Usage: scripts/run-environmental-tests.sh [--list|--check] [--run docker|windows|all] [--strict]"
}

is_windows_host() {
  case "$OS" in
    Windows_NT)
      return 0
      ;;
  esac

  case "$(uname -s 2>/dev/null || true)" in
    MINGW*|MSYS*|CYGWIN*)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

docker_result() {
  if ! command -v docker >/dev/null 2>&1; then
    printf '%s\n' "unavailable|docker command not found"
    return 0
  fi

  if docker info >/dev/null 2>&1; then
    printf '%s\n' "available|Docker daemon ready"
  else
    printf '%s\n' "unavailable|Docker daemon not ready"
  fi
}

windows_result() {
  if ! is_windows_host; then
    printf '%s\n' "unavailable|Windows runner required"
    return 0
  fi

  if command -v pwsh >/dev/null 2>&1; then
    printf '%s\n' "available|PowerShell available"
  else
    printf '%s\n' "unavailable|PowerShell command not found"
  fi
}

result_state() {
  printf '%s\n' "$1" | cut -d'|' -f1
}

result_reason() {
  printf '%s\n' "$1" | cut -d'|' -f2-
}

print_docker_rows() {
  result="$1"
  state="$(result_state "$result")"
  reason="$(result_reason "$result")"
  printf '%s\n' "docker-connectors | scripts/tests/test-agent-connector-lifecycle.sh, scripts/tests/test-claude-connector-lifecycle.sh | $state | $reason"
  printf '%s\n' "docker-distribution | scripts/tests/test-distribution-lifecycle.sh | $state | $reason"
  printf '%s\n' "postgres-integration | scripts/tests/test-postgres-integration-smoke.sh | $state | $reason"
}

print_windows_rows() {
  result="$1"
  state="$(result_state "$result")"
  reason="$(result_reason "$result")"
  printf '%s\n' "windows-hook | scripts/tests/test-windows-codex-hook-smoke.ps1 | $state | $reason"
  printf '%s\n' "windows-lifecycle | scripts/tests/test-windows-lifecycle.ps1 | $state | $reason"
}

print_status() {
  docker_state="$1"
  windows_state="$2"
  printf '%s\n' "environmental test environment status"
  printf '%s\n' "suite | source | status | reason"
  if [ "$scope" = "docker" ] || [ "$scope" = "all" ]; then
    print_docker_rows "$docker_state"
  fi
  if [ "$scope" = "windows" ] || [ "$scope" = "all" ]; then
    print_windows_rows "$windows_state"
  fi
}

selected_unavailable() {
  docker_state="$1"
  windows_state="$2"
  case "$scope" in
    docker)
      [ "$(result_state "$docker_state")" != "available" ]
      ;;
    windows)
      [ "$(result_state "$windows_state")" != "available" ]
      ;;
    all)
      [ "$(result_state "$docker_state")" != "available" ] || [ "$(result_state "$windows_state")" != "available" ]
      ;;
  esac
}

run_docker_suites() {
  bash "$repo_root/scripts/tests/test-agent-connector-lifecycle.sh"
  bash "$repo_root/scripts/tests/test-claude-connector-lifecycle.sh"
  bash "$repo_root/scripts/tests/test-distribution-lifecycle.sh"
  bash "$repo_root/scripts/tests/test-postgres-integration-smoke.sh"
}

run_windows_suites() {
  pwsh -File "$repo_root/scripts/tests/test-windows-codex-hook-smoke.ps1"
  pwsh -File "$repo_root/scripts/tests/test-windows-lifecycle.ps1" -RepoRoot "$repo_root"
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --help|-h)
      usage
      exit 0
      ;;
    --list)
      mode="list"
      shift
      ;;
    --check)
      mode="check"
      shift
      ;;
    --strict)
      strict="true"
      shift
      ;;
    --run)
      if [ "$#" -lt 2 ]; then
        usage >&2
        exit 2
      fi
      mode="run"
      scope="$2"
      case "$scope" in
        docker|windows|all)
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

if [ "$mode" = "list" ]; then
  printf '%s\n' "environmental test suites"
  printf '%s\n' "suite | source | status | execution"
  printf '%s\n' "docker-connectors | connector lifecycle scripts | not-sampled | --run docker"
  printf '%s\n' "docker-distribution | distribution lifecycle script | not-sampled | --run docker"
  printf '%s\n' "postgres-integration | PostgreSQL integration smoke script | not-sampled | --run docker"
  printf '%s\n' "windows-hook | Windows Codex hook smoke script | not-sampled | --run windows"
  printf '%s\n' "windows-lifecycle | Windows lifecycle script | not-sampled | --run windows"
  exit 0
fi

docker_state="$(docker_result)"
windows_state="$(windows_result)"
print_status "$docker_state" "$windows_state"

if [ "$mode" = "check" ]; then
  if [ "$strict" = "true" ] && selected_unavailable "$docker_state" "$windows_state"; then
    exit 1
  fi
  exit 0
fi

if [ "$strict" = "true" ] && selected_unavailable "$docker_state" "$windows_state"; then
  printf '%s\n' "strict run rejected: one or more selected environments are unavailable" >&2
  exit 1
fi

if [ "$scope" = "docker" ] || [ "$scope" = "all" ]; then
  if [ "$(result_state "$docker_state")" = "available" ]; then
    run_docker_suites
  else
    printf '%s\n' "skipping Docker suites: $(result_reason "$docker_state")"
  fi
fi

if [ "$scope" = "windows" ] || [ "$scope" = "all" ]; then
  if [ "$(result_state "$windows_state")" = "available" ]; then
    run_windows_suites
  else
    printf '%s\n' "skipping Windows suites: $(result_reason "$windows_state")"
  fi
fi
