#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
test_id="${LUTHN_TEST_ID:-$$}"
container="luthn-postgres-smoke-$test_id"
database="luthn_test_${test_id//[^a-zA-Z0-9]/_}"

cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
}
trap cleanup EXIT HUP INT TERM

docker run -d --rm \
  --name "$container" \
  -e POSTGRES_USER=luthn \
  -e POSTGRES_DB="$database" \
  -e POSTGRES_HOST_AUTH_METHOD=trust \
  -p 127.0.0.1::5432 \
  postgres:18-alpine >/dev/null

for _ in $(seq 1 60); do
  if docker exec "$container" pg_isready -U luthn -d "$database" >/dev/null 2>&1; then
    break
  fi
  sleep 0.25
done
docker exec "$container" pg_isready -U luthn -d "$database" >/dev/null

port="$(docker port "$container" 5432/tcp | head -n 1 | awk -F: '{print $NF}')"
test -n "$port"

cd "$repo_root"
LUTHN_POSTGRES_TEST_CONNECTION="Host=127.0.0.1;Port=$port;Database=$database;Username=luthn" \
LUTHN_POSTGRES_TEST_ALLOW_RESET=true \
dotnet test tests/Luthn.Host.Api.Tests/Luthn.Host.Api.Tests.csproj \
  --no-build \
  --no-restore \
  --filter PostgresIntegrationSmokeTests

echo "PostgreSQL integration smoke passed."
