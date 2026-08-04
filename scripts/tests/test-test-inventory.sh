#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
inventory="$repo_root/docs/testing.md"
temp_root="$(mktemp -d "${TMPDIR:-/tmp}/luthn-test-inventory.XXXXXX")"
trap 'rm -rf "$temp_root"' EXIT HUP INT TERM

test -f "$inventory"

actual="$temp_root/actual"
{
  rg --files "$repo_root/tests"
  rg --files "$repo_root/scripts/tests"
} | sed "s#^$repo_root/##" | sort -u > "$actual"

python3 - "$inventory" "$actual" <<'PY'
import re
import sys
from pathlib import Path

inventory_path = Path(sys.argv[1])
actual_path = Path(sys.argv[2])
allowed_tiers = {"fast", "focused", "full", "environmental"}
rows = []
row_pattern = re.compile(r"^\| `((?:tests|scripts/tests)/[^`]+)` \| `([^`]+)` \|")

for line in inventory_path.read_text(encoding="utf-8").splitlines():
    match = row_pattern.match(line)
    if match:
        rows.append((match.group(1), match.group(2)))

actual = {
    line.strip()
    for line in actual_path.read_text(encoding="utf-8").splitlines()
    if line.strip()
}
listed_paths = [path for path, _ in rows]
listed = set(listed_paths)
invalid_tiers = sorted({tier for _, tier in rows} - allowed_tiers)
duplicates = sorted(
    path for path in listed if listed_paths.count(path) > 1
)
missing = sorted(actual - listed)
stale = sorted(listed - actual)

if invalid_tiers or duplicates or missing or stale or len(rows) != len(listed):
    if invalid_tiers:
        print(f"invalid tiers: {', '.join(invalid_tiers)}", file=sys.stderr)
    if duplicates:
        print(f"duplicate paths: {', '.join(duplicates)}", file=sys.stderr)
    if missing:
        print(f"missing paths: {', '.join(missing)}", file=sys.stderr)
    if stale:
        print(f"stale paths: {', '.join(stale)}", file=sys.stderr)
    raise SystemExit(1)

print(f"Test inventory passed: {len(actual)} files mapped across the four tiers.")
PY
