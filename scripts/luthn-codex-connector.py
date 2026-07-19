#!/usr/bin/env python3
"""Codex host connector helper for Luthn.

This helper owns only Luthn-marked hook configuration and metadata-only
connection observations. It deliberately does not read Codex transcripts.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile
from typing import Any
from urllib import error, request


HOOK_MARKER = "luthn.agent-connector.v1"
HOOK_STATUS_MESSAGE = "Luthn 메모리 저장 예약 중…"
CONNECTOR_TEMPLATE_VERSION = "2"
INSTRUCTION_START_MARKER = "<!-- luthn:auto-recall:start -->"
INSTRUCTION_END_MARKER = "<!-- luthn:auto-recall:end -->"
MAX_HOOK_INPUT_BYTES = 256 * 1024
MAX_INSTRUCTION_BYTES = 1024 * 1024
MAX_TURN_CAPSULE_CHARS = 3900
HTTP_TIMEOUT_SECONDS = 4

AUTO_RECALL_INSTRUCTION = f"""{INSTRUCTION_START_MARKER}
# Luthn lightweight recall

For a new task or a material topic change, call the Luthn MCP
`get_context_pack` tool once before substantial work. Use a short task query
and non-sensitive project/task cache key with these bounds. When known, also
send only normalized non-sensitive `projectKey`, `taskKey`, and `topicTags`.
Never send a raw workspace path, transcript path, transcript content, or other
sensitive data as recall metadata:

- `maxItems`: 3
- `maxTokens`: 600
- `timeoutMs`: 200
- `cacheKey`: a stable non-sensitive project/task key
- `cacheTtlSeconds`: 600
- `failOpen`: true

For continued work on the same task, reuse the context already returned in the
conversation instead of calling the tool again. Refresh only after a material
topic change or cache expiry. If lightweight recall returns no context, times
out, or fails, continue without memory. Use deeper Luthn MCP search tools only
when the bounded context pack is insufficient.

After calling `get_context_pack`, use Codex commentary for recall status only
under these rules:

- If the response succeeds, parses correctly, and contains one or more actual
  memory items, emit exactly one commentary line for the current user turn:
  `Luthn 메모리 N개 참고`. Replace `N` with the number of actual memory items
  returned.
- Do not emit recall commentary when the response contains zero actual memory
  items, times out, returns an error, cannot be parsed, or uses any fail-open path.
- Do not emit recall commentary when `get_context_pack` was not called.
- Emit the recall commentary at most once per user turn, even if recall is
  refreshed or retried.
- Never include memory titles, content, IDs, queries, scores, sources, or any
  sensitive information in the commentary.
- Do not put the recall status in a normal assistant response or final response.
{INSTRUCTION_END_MARKER}"""


def helper_digest() -> str:
    return hashlib.sha256(Path(__file__).read_bytes()).hexdigest()


def managed_template_digest() -> str:
    template = {
        "hookMarker": HOOK_MARKER,
        "hookStatusMessage": HOOK_STATUS_MESSAGE,
        "hookTimeoutSeconds": HTTP_TIMEOUT_SECONDS + 1,
        "autoRecallInstruction": AUTO_RECALL_INSTRUCTION,
    }
    serialized = json.dumps(
        template, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")
    return hashlib.sha256(serialized).hexdigest()

_PRIVATE_KEY_PATTERN = re.compile(
    r"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?"
    r"-----END [A-Z0-9 ]*PRIVATE KEY-----",
    re.DOTALL,
)
_PREFIXED_SECRET_PATTERN = re.compile(
    r"\b(?:sk-[A-Za-z0-9_-]{16,}|ghp_[A-Za-z0-9]{16,}|"
    r"github_pat_[A-Za-z0-9_]{16,}|AKIA[A-Z0-9]{16})\b"
)
_BEARER_PATTERN = re.compile(r"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{16,}")
_BASIC_AUTH_PATTERN = re.compile(
    r"(?i)\b(?:Authorization\s*[:=]\s*)?Basic\s+[A-Za-z0-9+/]{8,}={0,2}"
)
_JWT_PATTERN = re.compile(
    r"\b[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b"
)
_URI_USER_INFO_PATTERN = re.compile(
    r"(?i)\b[A-Za-z][A-Za-z0-9+.-]*://[^\s/@:]+:[^\s/@]+@"
)
_ASSIGNED_SECRET_PATTERN = re.compile(
    r"(?i)(?<![A-Za-z0-9_])(?:\\+[\"']|[\"'])*"
    r"((?:[A-Za-z][A-Za-z0-9]*[_-])*(?:"
    r"api[_-]?key|access[_-]?key(?:[_-]?id)?|secret[_-]?access[_-]?key|"
    r"access[_-]?token|refresh[_-]?token|session[_-]?token|client[_-]?secret|"
    r"token|password|passwd|secret|private[_-]?key|database[_-]?url|"
    r"connection[_-]?string))(?:\\+[\"']|[\"'])*\s*[:=]\s*\S"
)

_CREDENTIAL_PATTERNS = (
    _PRIVATE_KEY_PATTERN,
    _PREFIXED_SECRET_PATTERN,
    _BEARER_PATTERN,
    _BASIC_AUTH_PATTERN,
    _JWT_PATTERN,
    _URI_USER_INFO_PATTERN,
    _ASSIGNED_SECRET_PATTERN,
)


def _load_hooks(path: Path) -> dict[str, Any]:
    if not path.exists():
        return {}

    with path.open("r", encoding="utf-8") as stream:
        document = json.load(stream)
    if not isinstance(document, dict):
        raise ValueError("Codex hooks configuration must be a JSON object")
    if "hooks" in document and not isinstance(document["hooks"], dict):
        raise ValueError("Codex hooks configuration 'hooks' must be an object")
    return document


def _write_hooks(path: Path, document: dict[str, Any]) -> None:
    destination = path.resolve(strict=True) if path.is_symlink() else path
    destination.parent.mkdir(parents=True, exist_ok=True)
    mode = stat.S_IMODE(destination.stat().st_mode) if destination.exists() else 0o600
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{destination.name}.", dir=destination.parent, text=True
    )
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            json.dump(document, stream, indent=2, ensure_ascii=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary_name, mode)
        os.replace(temporary_name, destination)
    except BaseException:
        try:
            os.unlink(temporary_name)
        except FileNotFoundError:
            pass
        raise


def _read_instructions(path: Path) -> str:
    if not path.exists():
        return ""
    if path.stat().st_size > MAX_INSTRUCTION_BYTES:
        raise ValueError("Codex instructions file exceeds the supported size")
    return path.read_text(encoding="utf-8")


def _write_instructions(path: Path, content: str) -> None:
    destination = path.resolve(strict=True) if path.is_symlink() else path
    destination.parent.mkdir(parents=True, exist_ok=True)
    mode = stat.S_IMODE(destination.stat().st_mode) if destination.exists() else 0o600
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{destination.name}.", dir=destination.parent, text=True
    )
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary_name, mode)
        os.replace(temporary_name, destination)
    except BaseException:
        try:
            os.unlink(temporary_name)
        except FileNotFoundError:
            pass
        raise


def _without_auto_recall_instruction(content: str) -> str:
    start_count = content.count(INSTRUCTION_START_MARKER)
    end_count = content.count(INSTRUCTION_END_MARKER)
    if start_count == 0 and end_count == 0:
        return content
    if start_count != 1 or end_count != 1:
        raise ValueError("Codex instructions contain malformed Luthn markers")

    start = content.index(INSTRUCTION_START_MARKER)
    end = content.index(INSTRUCTION_END_MARKER, start)
    before = content[:start].rstrip("\n")
    after = content[end + len(INSTRUCTION_END_MARKER) :].lstrip("\n")
    remaining = "\n\n".join(part for part in (before, after) if part)
    return f"{remaining}\n" if remaining else ""


def install_auto_recall_instruction(path: Path) -> None:
    if auto_recall_instruction_is_installed(path):
        return
    content = _without_auto_recall_instruction(_read_instructions(path))
    prefix = content.rstrip("\n")
    updated = (
        f"{prefix}\n\n{AUTO_RECALL_INSTRUCTION}\n"
        if prefix
        else f"{AUTO_RECALL_INSTRUCTION}\n"
    )
    _write_instructions(path, updated)


def remove_auto_recall_instruction(path: Path) -> None:
    if not path.exists():
        return
    content = _read_instructions(path)
    updated = _without_auto_recall_instruction(content)
    if updated != content:
        _write_instructions(path, updated)


def auto_recall_instruction_is_installed(path: Path) -> bool:
    if not path.exists():
        return False
    content = _read_instructions(path)
    return (
        content.count(INSTRUCTION_START_MARKER) == 1
        and content.count(INSTRUCTION_END_MARKER) == 1
        and AUTO_RECALL_INSTRUCTION in content
    )


def _stop_groups(document: dict[str, Any], create: bool) -> list[Any] | None:
    hooks = document.get("hooks")
    if hooks is None:
        if not create:
            return None
        hooks = {}
        document["hooks"] = hooks
    if not isinstance(hooks, dict):
        raise ValueError("Codex hooks configuration 'hooks' must be an object")

    groups = hooks.get("Stop")
    if groups is None:
        if not create:
            return None
        groups = []
        hooks["Stop"] = groups
    if not isinstance(groups, list):
        raise ValueError("Codex hooks configuration 'hooks.Stop' must be an array")
    return groups


def install_hook(path: Path, command: str) -> None:
    if hook_is_installed(path, command):
        return
    document = _load_hooks(path)
    groups = _stop_groups(document, create=True)
    assert groups is not None
    groups[:] = [
        group
        for group in groups
        if not isinstance(group, dict) or group.get("matcher") != HOOK_MARKER
    ]
    groups.append(
        {
            "matcher": HOOK_MARKER,
            "hooks": [
                {
                    "type": "command",
                    "command": command,
                    "timeout": HTTP_TIMEOUT_SECONDS + 1,
                    "statusMessage": HOOK_STATUS_MESSAGE,
                }
            ],
        }
    )
    _write_hooks(path, document)


def remove_hook(path: Path) -> None:
    if not path.exists():
        return
    document = _load_hooks(path)
    groups = _stop_groups(document, create=False)
    if groups is None:
        return
    before = len(groups)
    groups[:] = [
        group
        for group in groups
        if not isinstance(group, dict) or group.get("matcher") != HOOK_MARKER
    ]
    if len(groups) != before:
        _write_hooks(path, document)


def hook_is_installed(path: Path, command: str | None) -> bool:
    if not path.exists():
        return False
    document = _load_hooks(path)
    groups = _stop_groups(document, create=False) or []
    matches = [
        group
        for group in groups
        if isinstance(group, dict) and group.get("matcher") == HOOK_MARKER
    ]
    if len(matches) != 1:
        return False
    if command is None:
        return True
    handlers = matches[0].get("hooks")
    return (
        isinstance(handlers, list)
        and len(handlers) == 1
        and isinstance(handlers[0], dict)
        and handlers[0].get("command") == command
        and handlers[0].get("statusMessage") == HOOK_STATUS_MESSAGE
        and "async" not in handlers[0]
    )


def _read_token(path: Path) -> str:
    token = path.read_text(encoding="utf-8").strip()
    if not token or len(token) > 4096:
        raise ValueError("Luthn service token is missing or invalid")
    return token


def _request_json(
    method: str,
    url: str,
    token: str,
    payload: dict[str, Any] | None = None,
) -> tuple[int, dict[str, Any] | None]:
    body = None if payload is None else json.dumps(payload).encode("utf-8")
    headers = {"Authorization": f"Bearer {token}"}
    if body is not None:
        headers["Content-Type"] = "application/json"
    outbound = request.Request(url, data=body, headers=headers, method=method)
    with request.urlopen(outbound, timeout=HTTP_TIMEOUT_SECONDS) as response:
        response_body = response.read(MAX_HOOK_INPUT_BYTES)
        parsed = json.loads(response_body) if response_body else None
        return response.status, parsed


def _channel_spec(value: str) -> dict[str, Any]:
    parts = value.split(":", 4)
    if len(parts) != 5:
        raise argparse.ArgumentTypeError(
            "channel must be name:true|false:Unknown|Verified|Failed:"
            "Unknown|Succeeded|Failed:failure-code-or-empty"
        )
    name, configured, verification, activity, failure_code = parts
    if configured not in {"true", "false"}:
        raise argparse.ArgumentTypeError("channel configured value must be true or false")
    if verification not in {"Unknown", "Verified", "Failed"}:
        raise argparse.ArgumentTypeError("invalid channel verification state")
    if activity not in {"Unknown", "Succeeded", "Failed"}:
        raise argparse.ArgumentTypeError("invalid channel activity state")
    return {
        "channel": name,
        "configured": configured == "true",
        "verificationState": verification,
        "activityState": activity,
        "failureCode": failure_code or None,
    }


def report_observation(
    base_url: str,
    token: str,
    agent_id: str,
    agent_name: str,
    integration_kind: str,
    connector_version: str,
    channels: list[dict[str, Any]],
) -> dict[str, Any] | None:
    _, response = _request_json(
        "POST",
        f"{base_url.rstrip('/')}/api/agent-connections/{agent_id}/observations",
        token,
        {
            "agentName": agent_name,
            "integrationKind": integration_kind,
            "connectorVersion": connector_version,
            "channels": channels,
        },
    )
    return response


def _stable_id(prefix: str, value: str) -> str:
    digest = hashlib.sha256(value.encode("utf-8")).hexdigest()[:32]
    return f"{prefix}-{digest}"


def _contains_credentials(value: str) -> bool:
    return any(pattern.search(value) is not None for pattern in _CREDENTIAL_PATTERNS)


def build_turn_capsule(hook_input: dict[str, Any]) -> dict[str, Any] | None:
    if hook_input.get("hook_event_name") != "Stop":
        raise ValueError("expected Codex Stop hook input")
    session_id = hook_input.get("session_id")
    turn_id = hook_input.get("turn_id")
    assistant_message = hook_input.get("last_assistant_message")
    if not isinstance(session_id, str) or not session_id.strip():
        raise ValueError("Codex Stop hook input is missing session_id")
    if not isinstance(turn_id, str) or not turn_id.strip():
        raise ValueError("Codex Stop hook input is missing turn_id")
    if assistant_message is None:
        return None
    if not isinstance(assistant_message, str):
        raise ValueError("Codex Stop hook last_assistant_message must be text")

    if _contains_credentials(assistant_message):
        return None

    summary = assistant_message.strip()
    if not summary:
        return None
    if len(summary) > MAX_TURN_CAPSULE_CHARS:
        summary = summary[:MAX_TURN_CAPSULE_CHARS].rstrip()

    stable_session = _stable_id("codex-session", session_id.strip())
    stable_turn = _stable_id("codex-turn", turn_id.strip())
    content_digest = f"sha256:{hashlib.sha256(summary.encode('utf-8')).hexdigest()}"
    return {
        "sessionId": stable_session,
        "turnId": stable_turn,
        "sourceAgent": "codex",
        "summary": summary,
        "coreTags": ["codex", "conversation"],
        "contentDigest": content_digest,
        "idempotencyKey": _stable_id(
            "codex-capsule", f"{session_id.strip()}:{turn_id.strip()}"
        ),
        "title": "Codex turn capsule",
    }


def _failure_code(exception: BaseException) -> str:
    if isinstance(exception, error.HTTPError):
        if exception.code in {401, 403}:
            return "auth.failed"
        if exception.code == 429:
            return "delivery.rate_limited"
        if exception.code >= 500:
            return "service.unavailable"
        return "payload.rejected"
    if isinstance(exception, (TimeoutError, error.URLError)):
        return "delivery.unavailable"
    if isinstance(exception, (ValueError, json.JSONDecodeError)):
        return "hook.invalid_payload"
    return "delivery.failed"


def _read_bounded_hook_input() -> bytes:
    raw = sys.stdin.buffer.read(MAX_HOOK_INPUT_BYTES + 1)
    if len(raw) > MAX_HOOK_INPUT_BYTES:
        raise ValueError("Codex hook input exceeded the bounded payload limit")
    return raw


def upload_hook(
    base_url: str,
    token_file: Path,
    connector_version: str,
    excluded_token_files: list[Path] | None = None,
) -> int:
    try:
        raw = _read_bounded_hook_input()
        hook_input = json.loads(raw.decode("utf-8"))
        if not isinstance(hook_input, dict):
            raise ValueError("Codex hook input must be a JSON object")
        token = _read_token(token_file)
        assistant_message = hook_input.get("last_assistant_message")
        if isinstance(assistant_message, str):
            excluded_tokens = [token]
            excluded_tokens.extend(
                _read_token(path) for path in (excluded_token_files or [])
            )
            if any(value in assistant_message for value in excluded_tokens):
                return 0
        capsule = build_turn_capsule(hook_input)
        if capsule is None:
            return 0
    except Exception:
        return 0

    try:
        _request_json(
            "POST",
            f"{base_url.rstrip('/')}/api/agent/turn-summaries",
            token,
            capsule,
        )
    except Exception as exception:
        try:
            report_observation(
                base_url,
                token,
                "codex",
                "Codex",
                "host-hook-mcp",
                connector_version,
                [
                    {
                        "channel": "automatic-ingestion",
                        "configured": True,
                        "verificationState": "Verified",
                        "activityState": "Failed",
                        "failureCode": _failure_code(exception),
                    }
                ],
            )
        except Exception:
            pass
        return 0

    try:
        report_observation(
            base_url,
            token,
            "codex",
            "Codex",
            "host-hook-mcp",
            connector_version,
            [
                {
                    "channel": "automatic-ingestion",
                    "configured": True,
                    "verificationState": "Verified",
                    "activityState": "Succeeded",
                    "failureCode": None,
                }
            ],
        )
    except Exception:
        pass
    return 0


def run_hook(
    base_url: str,
    token_file: Path,
    connector_version: str,
    excluded_token_files: list[Path] | None = None,
) -> int:
    try:
        raw = _read_bounded_hook_input()
        with tempfile.TemporaryFile() as payload:
            payload.write(raw)
            payload.flush()
            payload.seek(0)
            command = [
                sys.executable,
                str(Path(__file__).resolve()),
                "hook-upload",
                "--base-url",
                base_url,
                "--token-file",
                str(token_file),
                "--connector-version",
                connector_version,
            ]
            for path in excluded_token_files or []:
                command.extend(["--excluded-token-file", str(path)])
            subprocess.Popen(
                command,
                stdin=payload,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                close_fds=True,
                start_new_session=True,
            )
    except Exception:
        pass
    return 0


def print_status(base_url: str, token: str, agent_id: str) -> int:
    _, response = _request_json(
        "GET", f"{base_url.rstrip('/')}/api/agent-connections", token
    )
    connections = response.get("connections", []) if isinstance(response, dict) else []
    connection = next(
        (
            item
            for item in connections
            if isinstance(item, dict) and item.get("agentId") == agent_id
        ),
        None,
    )
    if connection is None:
        print(f"Server observation: unknown ({agent_id} has not reported yet)")
        return 0

    print(f"Server observation: {connection.get('state', 'Unknown')}")
    print(f"Connector version: {connection.get('connectorVersion', 'unknown')}")
    for channel in connection.get("channels", []):
        if not isinstance(channel, dict):
            continue
        detail = channel.get("state", "Unknown")
        failure = channel.get("failureCode")
        if failure:
            detail = f"{detail} ({failure})"
        print(f"  {channel.get('channel', 'unknown')}: {detail}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="operation", required=True)

    hooks = subparsers.add_parser("hooks")
    hooks.add_argument("action", choices=["install", "remove", "check"])
    hooks.add_argument("--path", type=Path, required=True)
    hooks.add_argument("--command", dest="hook_command")

    instructions = subparsers.add_parser("instructions")
    instructions.add_argument("action", choices=["install", "remove", "check"])
    instructions.add_argument("--path", type=Path, required=True)

    report = subparsers.add_parser("report")
    report.add_argument("--base-url", required=True)
    report.add_argument("--token-file", type=Path, required=True)
    report.add_argument("--agent-id", default="codex")
    report.add_argument("--agent-name", default="Codex")
    report.add_argument("--integration-kind", default="host-hook-mcp")
    report.add_argument("--connector-version", required=True)
    report.add_argument("--channel", action="append", type=_channel_spec, required=True)

    hook_run = subparsers.add_parser("hook-run")
    hook_run.add_argument("--base-url", required=True)
    hook_run.add_argument("--token-file", type=Path, required=True)
    hook_run.add_argument("--excluded-token-file", type=Path, action="append")
    hook_run.add_argument("--connector-version", required=True)

    hook_upload = subparsers.add_parser("hook-upload", help=argparse.SUPPRESS)
    hook_upload.add_argument("--base-url", required=True)
    hook_upload.add_argument("--token-file", type=Path, required=True)
    hook_upload.add_argument("--excluded-token-file", type=Path, action="append")
    hook_upload.add_argument("--connector-version", required=True)

    status = subparsers.add_parser("status")
    status.add_argument("--base-url", required=True)
    status.add_argument("--token-file", type=Path, required=True)
    status.add_argument("--agent-id", default="codex")
    subparsers.add_parser("version")
    subparsers.add_parser("helper-digest")
    subparsers.add_parser("template-digest")
    return parser


def main() -> int:
    arguments = build_parser().parse_args()
    if arguments.operation == "hooks":
        if arguments.action == "install":
            if not arguments.hook_command:
                raise ValueError("--command is required for hook installation")
            install_hook(arguments.path, arguments.hook_command)
            return 0
        if arguments.action == "remove":
            remove_hook(arguments.path)
            return 0
        return 0 if hook_is_installed(arguments.path, arguments.hook_command) else 1

    if arguments.operation == "instructions":
        if arguments.action == "install":
            install_auto_recall_instruction(arguments.path)
            return 0
        if arguments.action == "remove":
            remove_auto_recall_instruction(arguments.path)
            return 0
        return 0 if auto_recall_instruction_is_installed(arguments.path) else 1

    if arguments.operation == "report":
        token = _read_token(arguments.token_file)
        response = report_observation(
            arguments.base_url,
            token,
            arguments.agent_id,
            arguments.agent_name,
            arguments.integration_kind,
            arguments.connector_version,
            arguments.channel,
        )
        if response is not None:
            print(json.dumps(response, separators=(",", ":")))
        return 0

    if arguments.operation == "hook-run":
        return run_hook(
            arguments.base_url,
            arguments.token_file,
            arguments.connector_version,
            arguments.excluded_token_file,
        )

    if arguments.operation == "hook-upload":
        return upload_hook(
            arguments.base_url,
            arguments.token_file,
            arguments.connector_version,
            arguments.excluded_token_file,
        )

    if arguments.operation == "version":
        print(CONNECTOR_TEMPLATE_VERSION)
        return 0

    if arguments.operation == "helper-digest":
        print(helper_digest())
        return 0

    if arguments.operation == "template-digest":
        print(managed_template_digest())
        return 0

    token = _read_token(arguments.token_file)
    return print_status(arguments.base_url, token, arguments.agent_id)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, json.JSONDecodeError, error.URLError) as exception:
        print(f"luthn codex connector: {exception}", file=sys.stderr)
        raise SystemExit(1)
