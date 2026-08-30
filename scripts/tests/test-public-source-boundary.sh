#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

python3 - "$repo_root" <<'PY'
import re
import subprocess
import sys
from pathlib import Path

root = Path(sys.argv[1])
excluded = {"scripts/tests/test-public-source-boundary.sh"}
forbidden = {
    "managed product namespace": re.compile(r"Luthn\.Cloud|LuthnCloud|CloudAgent"),
    "managed console origin": re.compile(r"app\.luthn\.com", re.IGNORECASE),
    "managed Hub route": re.compile(r"/api/v[0-9]+/hub-(?:bootstrap|enrollments?|projections?|sessions?|tls-route|remote-mcp)"),
    "managed bootstrap header": re.compile(r"X-Luthn-Bootstrap-Token", re.IGNORECASE),
    "managed contract type": re.compile(r"CloudSyncContract|ConsoleCloud|HubBootstrap|HubEnrollment"),
    "managed platform dependency": re.compile(r"\b(?:Supabase|Paddle|Northflank)\b", re.IGNORECASE),
}

paths = subprocess.check_output(
    ["git", "ls-files", "-co", "--exclude-standard", "-z"],
    cwd=root,
).decode().split("\0")
findings = []
for relative in paths:
    if not relative or relative in excluded:
        continue
    path = root / relative
    try:
        data = path.read_bytes()
    except (OSError, IsADirectoryError):
        continue
    if b"\0" in data:
        continue
    text = data.decode("utf-8", errors="replace")
    for line_number, line in enumerate(text.splitlines(), 1):
        for label, pattern in forbidden.items():
            if pattern.search(line):
                findings.append(f"{relative}:{line_number}: {label}")

if findings:
    print("Private managed-service details are not allowed in the public repository:", file=sys.stderr)
    for finding in findings:
        print(f"  {finding}", file=sys.stderr)
    raise SystemExit(1)

print(f"Public/private boundary passed for {len([path for path in paths if path])} files.")
PY
