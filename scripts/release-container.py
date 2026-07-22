#!/usr/bin/env python3
"""Validate and dispatch an official Luthn container release."""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from dataclasses import asdict, dataclass


REPOSITORY = "JakobSung/Luthn"
REMOTE_URL = "https://github.com/JakobSung/Luthn.git"
WORKFLOW = "release-container.yml"
VERSION_PATTERN = re.compile(r"^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")


class ReleaseError(RuntimeError):
    """Raised when release preflight rejects the requested release."""


@dataclass(frozen=True)
class ReleaseRequest:
    version: str
    minor_tag: str
    source_sha: str
    repository: str


def run_command(arguments: list[str]) -> str:
    result = subprocess.run(
        arguments,
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip() or "command failed"
        raise ReleaseError(f"{' '.join(arguments[:2])}: {detail}")
    return result.stdout.strip()


def validate_version(version: str) -> tuple[str, str]:
    match = VERSION_PATTERN.fullmatch(version)
    if not match:
        raise ReleaseError("version must use strict vMAJOR.MINOR.PATCH SemVer")
    return version, f"v{match.group(1)}.{match.group(2)}"


def read_remote_ref(ref: str) -> str:
    output = run_command(["git", "ls-remote", REMOTE_URL, ref])
    if not output:
        return ""
    fields = output.splitlines()[0].split()
    return fields[0] if fields else ""


def prepare_request(version: str, source_sha: str | None) -> ReleaseRequest:
    normalized_version, minor_tag = validate_version(version)
    remote_main = read_remote_ref("refs/heads/main")
    if not SHA_PATTERN.fullmatch(remote_main):
        raise ReleaseError("the exact remote main commit could not be resolved")
    selected_sha = source_sha or remote_main
    if not SHA_PATTERN.fullmatch(selected_sha):
        raise ReleaseError("source SHA must contain exactly 40 lowercase hexadecimal characters")
    if selected_sha != remote_main:
        raise ReleaseError("source SHA must equal the current remote main commit")
    if read_remote_ref(f"refs/tags/{normalized_version}"):
        raise ReleaseError(f"release tag already exists: {normalized_version}")
    return ReleaseRequest(normalized_version, minor_tag, selected_sha, REPOSITORY)


def dispatch(request: ReleaseRequest) -> None:
    gh_command = os.environ.get("LUTHN_RELEASE_GH_COMMAND", "gh")
    run_command(
        [
            gh_command,
            "workflow",
            "run",
            WORKFLOW,
            "--repo",
            request.repository,
            "--ref",
            "main",
            "-f",
            f"version={request.version}",
            "-f",
            f"source_sha={request.source_sha}",
        ]
    )


def parse_arguments(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Release one exact remote main commit as versioned Luthn containers."
    )
    parser.add_argument("version", help="strict release version such as v0.1.0")
    parser.add_argument("--source-sha", help="exact 40-character remote main commit")
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="validate and print the request without dispatching GitHub Actions",
    )
    parser.add_argument(
        "--validate-only",
        action="store_true",
        help=argparse.SUPPRESS,
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    arguments = parse_arguments(argv or sys.argv[1:])
    try:
        request = prepare_request(arguments.version, arguments.source_sha)
        if not arguments.dry_run and not arguments.validate_only:
            dispatch(request)
    except ReleaseError as error:
        print(f"release rejected: {error}", file=sys.stderr)
        return 2

    result = asdict(request)
    result["status"] = "validated" if arguments.dry_run or arguments.validate_only else "dispatched"
    print(json.dumps(result, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
