#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

require_docker_daemon() {
  if docker info >/dev/null 2>&1; then
    return 0
  fi

  cat >&2 <<'ERROR'
Docker is installed, but the Docker daemon is not reachable.

If you use OrbStack, start OrbStack and wait until Docker is running.
If you use Docker Desktop, start Docker Desktop.
If your Docker context points at a stale socket, switch to a working context:
  docker context ls
  docker context use <context-name>
ERROR
  exit 1
}

if [[ "${1:-}" != "--yes" ]]; then
  cat <<'USAGE'
usage: scripts/reset-local.sh --yes

Stops the local Docker Compose stack and removes local Luthn Docker volumes.
This deletes local PostgreSQL and operator-console state created by the
self-host quickstart.
USAGE
  exit 2
fi

require_docker_daemon
docker compose down -v
echo "Local Luthn Docker stack and volumes removed."
