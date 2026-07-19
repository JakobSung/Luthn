import importlib.util
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "scripts" / "luthn-codex-connector.py"
SPEC = importlib.util.spec_from_file_location("luthn_codex_connector", HELPER)
assert SPEC is not None and SPEC.loader is not None
CONNECTOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CONNECTOR)


class CodexHookConfigurationTests(unittest.TestCase):
    def test_install_is_idempotent_and_preserves_unrelated_hooks(self):
        with tempfile.TemporaryDirectory() as directory:
            hooks_path = Path(directory) / "hooks.json"
            hooks_path.write_text(
                json.dumps(
                    {
                        "description": "keep",
                        "custom": {"enabled": True},
                        "hooks": {
                            "Stop": [
                                {
                                    "matcher": "other.owner",
                                    "hooks": [{"type": "command", "command": "other"}],
                                }
                            ],
                            "SessionStart": [],
                        },
                    }
                ),
                encoding="utf-8",
            )

            CONNECTOR.install_hook(hooks_path, "'/tmp/luthn path' codex-hook")
            legacy = json.loads(hooks_path.read_text(encoding="utf-8"))
            legacy["hooks"]["Stop"][-1]["hooks"][0][
                "statusMessage"
            ] = "Syncing Luthn memory"
            hooks_path.write_text(json.dumps(legacy), encoding="utf-8")
            CONNECTOR.install_hook(hooks_path, "'/tmp/luthn path' codex-hook")
            document = json.loads(hooks_path.read_text(encoding="utf-8"))

            self.assertEqual("keep", document["description"])
            self.assertEqual({"enabled": True}, document["custom"])
            self.assertEqual([], document["hooks"]["SessionStart"])
            groups = document["hooks"]["Stop"]
            self.assertEqual(2, len(groups))
            self.assertEqual("other.owner", groups[0]["matcher"])
            self.assertEqual(CONNECTOR.HOOK_MARKER, groups[1]["matcher"])
            self.assertNotIn("async", groups[1]["hooks"][0])
            self.assertEqual(
                CONNECTOR.HOOK_STATUS_MESSAGE,
                groups[1]["hooks"][0]["statusMessage"],
            )
            self.assertTrue(
                CONNECTOR.hook_is_installed(
                    hooks_path, "'/tmp/luthn path' codex-hook"
                )
            )
            with mock.patch.object(CONNECTOR, "_write_hooks") as write_hooks:
                CONNECTOR.install_hook(hooks_path, "'/tmp/luthn path' codex-hook")
                write_hooks.assert_not_called()

            CONNECTOR.remove_hook(hooks_path)
            document = json.loads(hooks_path.read_text(encoding="utf-8"))
            self.assertEqual([groups[0]], document["hooks"]["Stop"])

    def test_invalid_existing_shape_is_not_rewritten(self):
        with tempfile.TemporaryDirectory() as directory:
            hooks_path = Path(directory) / "hooks.json"
            original = '{"hooks":{"Stop":{}}}\n'
            hooks_path.write_text(original, encoding="utf-8")

            with self.assertRaises(ValueError):
                CONNECTOR.install_hook(hooks_path, "luthn codex-hook")

            self.assertEqual(original, hooks_path.read_text(encoding="utf-8"))

    def test_install_preserves_hooks_symlink(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            target = root / "shared-hooks.json"
            target.write_text('{"hooks":{"Stop":[]}}\n', encoding="utf-8")
            hooks_path = root / "hooks.json"
            hooks_path.symlink_to(target)

            CONNECTOR.install_hook(hooks_path, "luthn codex-hook")

            self.assertTrue(hooks_path.is_symlink())
            self.assertTrue(
                CONNECTOR.hook_is_installed(hooks_path, "luthn codex-hook")
            )

    @unittest.skipUnless(
        os.environ.get("LUTHN_RUN_CODEX_HOOK_SMOKE") == "1",
        "set LUTHN_RUN_CODEX_HOOK_SMOKE=1 to run the authenticated Codex smoke test",
    )
    def test_codex_loads_and_executes_installed_stop_hook(self):
        codex = shutil.which("codex")
        self.assertIsNotNone(codex, "codex CLI is required")

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            marker = root / "stop-hook-ran"
            handler = root / "stop-hook.py"
            handler.write_text(
                "import json\n"
                "from pathlib import Path\n"
                "import sys\n"
                "payload = json.load(sys.stdin)\n"
                f"Path({str(marker)!r}).write_text("
                "payload.get('hook_event_name', ''), encoding='utf-8')\n"
                "print('{}')\n",
                encoding="utf-8",
            )
            CONNECTOR.install_hook(
                root / "hooks.json",
                f"{shlex.quote(sys.executable)} {shlex.quote(str(handler))}",
            )

            original_codex_home = Path(
                os.environ.get("CODEX_HOME", Path.home() / ".codex")
            )
            auth_file = original_codex_home / "auth.json"
            if auth_file.exists():
                (root / "auth.json").symlink_to(auth_file)

            environment = os.environ.copy()
            environment["CODEX_HOME"] = str(root)
            result = subprocess.run(
                [
                    codex,
                    "exec",
                    "--ephemeral",
                    "--ignore-user-config",
                    "--disable",
                    "plugins",
                    "--dangerously-bypass-hook-trust",
                    "--skip-git-repo-check",
                    "-C",
                    str(root),
                    "Reply with OK.",
                ],
                text=True,
                capture_output=True,
                check=False,
                env=environment,
                timeout=120,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("Stop", marker.read_text(encoding="utf-8"))


class CodexInstructionConfigurationTests(unittest.TestCase):
    def test_install_is_idempotent_and_remove_preserves_user_instructions(self):
        with tempfile.TemporaryDirectory() as directory:
            instructions_path = Path(directory) / "AGENTS.md"
            original = "# User instructions\n\nKeep this text.\n"
            instructions_path.write_text(original, encoding="utf-8")

            CONNECTOR.install_auto_recall_instruction(instructions_path)
            with mock.patch.object(
                CONNECTOR, "_write_instructions"
            ) as write_instructions:
                CONNECTOR.install_auto_recall_instruction(instructions_path)
                write_instructions.assert_not_called()
            installed = instructions_path.read_text(encoding="utf-8")

            self.assertEqual(1, installed.count(CONNECTOR.INSTRUCTION_START_MARKER))
            self.assertEqual(1, installed.count(CONNECTOR.INSTRUCTION_END_MARKER))
            self.assertIn("`maxItems`: 3", installed)
            self.assertIn("`maxTokens`: 600", installed)
            self.assertIn("`timeoutMs`: 200", installed)
            self.assertIn("`cacheTtlSeconds`: 600", installed)
            self.assertIn("`failOpen`: true", installed)
            self.assertIn("exactly one commentary line", installed)
            self.assertIn("`Luthn 메모리 N개 참고`", installed)
            self.assertIn("zero actual memory", installed)
            self.assertIn("times out, returns an error, cannot be parsed", installed)
            self.assertIn("uses any fail-open path", installed)
            self.assertIn("when `get_context_pack` was not called", installed)
            self.assertIn("`projectKey`, `taskKey`, and `topicTags`", installed)
            self.assertIn("Never send a raw workspace path", installed)
            self.assertIn("transcript path", installed)
            self.assertIn("at most once per user turn", installed)
            self.assertIn(
                "memory titles, content, IDs, queries, scores, sources", installed
            )
            self.assertIn("normal assistant response or final response", installed)
            self.assertTrue(
                CONNECTOR.auto_recall_instruction_is_installed(instructions_path)
            )

            CONNECTOR.remove_auto_recall_instruction(instructions_path)

            self.assertEqual(original, instructions_path.read_text(encoding="utf-8"))
            self.assertFalse(
                CONNECTOR.auto_recall_instruction_is_installed(instructions_path)
            )

    def test_malformed_markers_fail_without_rewriting_user_instructions(self):
        with tempfile.TemporaryDirectory() as directory:
            instructions_path = Path(directory) / "AGENTS.md"
            original = f"# User instructions\n{CONNECTOR.INSTRUCTION_START_MARKER}\n"
            instructions_path.write_text(original, encoding="utf-8")

            with self.assertRaises(ValueError):
                CONNECTOR.install_auto_recall_instruction(instructions_path)

            self.assertEqual(original, instructions_path.read_text(encoding="utf-8"))

    def test_install_preserves_instruction_symlink(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            target = root / "shared-agents.md"
            target.write_text("# Shared instructions\n", encoding="utf-8")
            instructions_path = root / "AGENTS.md"
            instructions_path.symlink_to(target)

            CONNECTOR.install_auto_recall_instruction(instructions_path)

            self.assertTrue(instructions_path.is_symlink())
            self.assertTrue(
                CONNECTOR.auto_recall_instruction_is_installed(instructions_path)
            )


class TurnCapsuleTests(unittest.TestCase):
    def test_connector_template_exposes_a_bounded_version(self):
        self.assertRegex(CONNECTOR.CONNECTOR_TEMPLATE_VERSION, r"^[1-9][0-9]*$")

    def test_connector_exposes_stable_helper_and_managed_template_digests(self):
        self.assertRegex(CONNECTOR.helper_digest(), r"^[0-9a-f]{64}$")
        self.assertRegex(CONNECTOR.managed_template_digest(), r"^[0-9a-f]{64}$")
        self.assertEqual(
            CONNECTOR.managed_template_digest(), CONNECTOR.managed_template_digest()
        )

    def test_turn_capsule_is_bounded_deterministic_and_excludes_host_paths(self):
        hook_input = {
            "session_id": "session-original",
            "turn_id": "turn-original",
            "cwd": "/Users/example/private-project",
            "transcript_path": "/Users/example/.codex/private.jsonl",
            "hook_event_name": "Stop",
            "last_assistant_message": "x" * 5000,
        }

        first = CONNECTOR.build_turn_capsule(hook_input)
        second = CONNECTOR.build_turn_capsule(hook_input)

        self.assertEqual(first, second)
        assert first is not None
        self.assertEqual(CONNECTOR.MAX_TURN_CAPSULE_CHARS, len(first["summary"]))
        self.assertLessEqual(len(first["sessionId"]), 128)
        self.assertLessEqual(len(first["turnId"]), 128)
        serialized = json.dumps(first)
        self.assertNotIn("/Users/example", serialized)
        self.assertNotIn("transcript", serialized)
        self.assertNotIn("cwd", serialized)

    def test_turn_capsule_fails_closed_for_credential_patterns(self):
        credential_messages = [
            "DATABASE_URL=postgresql://user:password@host/db",
            "Authorization: Basic dXNlcjpwYXNzd29yZA==",
            "JWT eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature123",
            "AWS_SECRET_ACCESS_KEY=abcdefghijklmnopqrstuvwxyz1234567890",
            "AZURE_CLIENT_SECRET=super-secret-value-12345",
            "API_KEY=super-secret-value-12345",
            '{"apiKey": "super-secret-value-12345"}',
            "{'client_secret': 'super-secret-value-12345'}",
            r'payload={\"apiKey\":\"super-secret-value-12345\"}',
            r'payload={\\\"client_secret\\\":\\\"super-secret-value-12345\\\"}',
            "Bearer abcdefghijklmnopqrstuvwxyz.1234567890",
            "sk-abcdefghijklmnopqrstuvwx",
            (
                "-----BEGIN PRIVATE KEY-----\n"
                "private-material\n"
                "-----END PRIVATE KEY-----"
            ),
        ]

        for message in credential_messages:
            with self.subTest(message=message):
                capsule = CONNECTOR.build_turn_capsule(
                    {
                        "session_id": "session-secret",
                        "turn_id": "turn-secret",
                        "hook_event_name": "Stop",
                        "last_assistant_message": message,
                    }
                )

                self.assertIsNone(capsule)

    def test_credential_capsules_are_not_sent_to_turn_summary_intake(self):
        credential_messages = [
            "DATABASE_URL=postgresql://user:password@host/db",
            "Authorization: Basic dXNlcjpwYXNzd29yZA==",
            "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature123",
            "AWS_SECRET_ACCESS_KEY=abcdefghijklmnopqrstuvwxyz1234567890",
            "CLIENT_SECRET=super-secret-value-12345",
            '{"apiKey": "super-secret-value-12345"}',
            "{'client_secret': 'super-secret-value-12345'}",
            r'payload={\"apiKey\":\"super-secret-value-12345\"}',
            r'payload={\\\"client_secret\\\":\\\"super-secret-value-12345\\\"}',
        ]

        with tempfile.TemporaryDirectory() as directory:
            token_file = Path(directory) / "token"
            token_file.write_text("test-token", encoding="utf-8")
            with mock.patch.object(CONNECTOR, "_request_json") as request_json:
                for index, message in enumerate(credential_messages):
                    with self.subTest(message=message):
                        payload = json.dumps(
                            {
                                "session_id": "session-secret",
                                "turn_id": f"turn-secret-{index}",
                                "hook_event_name": "Stop",
                                "last_assistant_message": message,
                            }
                        ).encode("utf-8")
                        with mock.patch.object(
                            CONNECTOR, "_read_bounded_hook_input", return_value=payload
                        ):
                            self.assertEqual(
                                0,
                                CONNECTOR.upload_hook(
                                    "http://127.0.0.1:1", token_file, "1"
                                ),
                            )

                request_json.assert_not_called()

    def test_current_bare_service_token_is_not_sent_to_turn_summary_intake(self):
        token = "0123456789abcdef" * 3
        payload = json.dumps(
            {
                "session_id": "session-secret",
                "turn_id": "turn-secret",
                "hook_event_name": "Stop",
                "last_assistant_message": f"Generated service token: {token}",
            }
        ).encode("utf-8")

        with tempfile.TemporaryDirectory() as directory:
            token_file = Path(directory) / "token"
            token_file.write_text(token, encoding="utf-8")
            with mock.patch.object(
                CONNECTOR, "_read_bounded_hook_input", return_value=payload
            ), mock.patch.object(CONNECTOR, "_request_json") as request_json:
                self.assertEqual(
                    0,
                    CONNECTOR.upload_hook("http://127.0.0.1:1", token_file, "1"),
                )

            request_json.assert_not_called()

    def test_current_bare_operator_token_is_not_sent_to_turn_summary_intake(self):
        operator_token = "fedcba9876543210" * 3
        payload = json.dumps(
            {
                "session_id": "session-operator-secret",
                "turn_id": "turn-operator-secret",
                "hook_event_name": "Stop",
                "last_assistant_message": operator_token,
            }
        ).encode("utf-8")

        with tempfile.TemporaryDirectory() as directory:
            token_file = Path(directory) / "service-token"
            token_file.write_text("test-token", encoding="utf-8")
            operator_token_file = Path(directory) / "operator-token"
            operator_token_file.write_text(operator_token, encoding="utf-8")
            with mock.patch.object(
                CONNECTOR, "_read_bounded_hook_input", return_value=payload
            ), mock.patch.object(CONNECTOR, "_request_json") as request_json:
                self.assertEqual(
                    0,
                    CONNECTOR.upload_hook(
                        "http://127.0.0.1:1",
                        token_file,
                        "1",
                        [operator_token_file],
                    ),
                )

            request_json.assert_not_called()

    def test_truncated_service_token_is_not_sent_to_turn_summary_intake(self):
        token = "0123456789abcdef" * 3
        prefix = "x" * (CONNECTOR.MAX_TURN_CAPSULE_CHARS - len(token) + 1)
        payload = json.dumps(
            {
                "session_id": "session-secret",
                "turn_id": "turn-secret",
                "hook_event_name": "Stop",
                "last_assistant_message": f"{prefix}{token}",
            }
        ).encode("utf-8")

        with tempfile.TemporaryDirectory() as directory:
            token_file = Path(directory) / "token"
            token_file.write_text(token, encoding="utf-8")
            with mock.patch.object(
                CONNECTOR, "_read_bounded_hook_input", return_value=payload
            ), mock.patch.object(CONNECTOR, "_request_json") as request_json:
                self.assertEqual(
                    0,
                    CONNECTOR.upload_hook("http://127.0.0.1:1", token_file, "1"),
                )

            request_json.assert_not_called()

    def test_hook_wrapper_detaches_bounded_payload_and_returns_success(self):
        payload = b'{"hook_event_name":"Stop"}'
        spawned_payloads = []

        def capture_spawn(*args, **kwargs):
            spawned_payloads.append(kwargs["stdin"].read())
            return mock.Mock()

        with mock.patch.object(
            CONNECTOR, "_read_bounded_hook_input", return_value=payload
        ), mock.patch.object(
            CONNECTOR.subprocess, "Popen", side_effect=capture_spawn
        ) as popen:
            result = CONNECTOR.run_hook(
                "http://127.0.0.1:8080",
                Path("/tmp/token"),
                "1",
                [Path("/tmp/operator-token")],
            )

        self.assertEqual(0, result)
        self.assertEqual([payload], spawned_payloads)
        self.assertTrue(popen.call_args.kwargs["start_new_session"])
        self.assertTrue(popen.call_args.kwargs["close_fds"])
        self.assertEqual(CONNECTOR.subprocess.DEVNULL, popen.call_args.kwargs["stdout"])
        self.assertEqual(CONNECTOR.subprocess.DEVNULL, popen.call_args.kwargs["stderr"])
        self.assertIn("/tmp/operator-token", popen.call_args.args[0])

    def test_success_observation_failure_does_not_report_intake_as_failed(self):
        payload = json.dumps(
            {
                "session_id": "session-1",
                "turn_id": "turn-1",
                "hook_event_name": "Stop",
                "last_assistant_message": "Published a safe decision.",
            }
        ).encode("utf-8")
        with tempfile.TemporaryDirectory() as directory:
            token_file = Path(directory) / "token"
            token_file.write_text("test-token", encoding="utf-8")
            with mock.patch.object(
                CONNECTOR, "_read_bounded_hook_input", return_value=payload
            ), mock.patch.object(
                CONNECTOR,
                "_request_json",
                side_effect=[(201, {"ok": True}), OSError("observation unavailable")],
            ) as request_json:
                result = CONNECTOR.upload_hook(
                    "http://127.0.0.1:8080", token_file, "1"
                )

        self.assertEqual(0, result)
        self.assertEqual(2, request_json.call_count)
        observation = request_json.call_args_list[1].args[3]
        self.assertEqual("Succeeded", observation["channels"][0]["activityState"])

    def test_intake_failure_reports_failed_activity_once(self):
        payload = json.dumps(
            {
                "session_id": "session-1",
                "turn_id": "turn-1",
                "hook_event_name": "Stop",
                "last_assistant_message": "Published a safe decision.",
            }
        ).encode("utf-8")
        with tempfile.TemporaryDirectory() as directory:
            token_file = Path(directory) / "token"
            token_file.write_text("test-token", encoding="utf-8")
            with mock.patch.object(
                CONNECTOR, "_read_bounded_hook_input", return_value=payload
            ), mock.patch.object(
                CONNECTOR,
                "_request_json",
                side_effect=[OSError("intake unavailable"), (200, {"ok": True})],
            ) as request_json:
                result = CONNECTOR.upload_hook(
                    "http://127.0.0.1:8080", token_file, "1"
                )

        self.assertEqual(0, result)
        self.assertEqual(2, request_json.call_count)
        observation = request_json.call_args_list[1].args[3]
        self.assertEqual("Failed", observation["channels"][0]["activityState"])

    def test_hook_delivery_uses_stable_idempotency_and_reports_activity(self):
        received = []
        received_condition = threading.Condition()

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                length = int(self.headers.get("content-length", "0"))
                body = json.loads(self.rfile.read(length))
                if self.path.endswith("turn-summaries"):
                    time.sleep(1)
                with received_condition:
                    received.append((self.path, body))
                    received_condition.notify_all()
                response = json.dumps({"ok": True}).encode("utf-8")
                self.send_response(201 if self.path.endswith("turn-summaries") else 200)
                self.send_header("content-type", "application/json")
                self.send_header("content-length", str(len(response)))
                self.end_headers()
                self.wfile.write(response)

            def log_message(self, format, *args):
                return

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory() as directory:
                token_file = Path(directory) / "token"
                token_file.write_text("test-token", encoding="utf-8")
                hook_input = json.dumps(
                    {
                        "session_id": "session-1",
                        "turn_id": "turn-1",
                        "cwd": "/private/path",
                        "transcript_path": "/private/transcript.jsonl",
                        "hook_event_name": "Stop",
                        "last_assistant_message": "Published a safe decision.",
                    }
                )
                command = [
                    "python3",
                    str(HELPER),
                    "hook-run",
                    "--base-url",
                    f"http://127.0.0.1:{server.server_port}",
                    "--token-file",
                    str(token_file),
                    "--connector-version",
                    "1",
                ]

                started_at = time.monotonic()
                first = subprocess.run(
                    command, input=hook_input, text=True, capture_output=True, check=False
                )
                first_elapsed = time.monotonic() - started_at
                second = subprocess.run(
                    command, input=hook_input, text=True, capture_output=True, check=False
                )

                deadline = time.monotonic() + 5
                with received_condition:
                    while len(received) < 4 and time.monotonic() < deadline:
                        received_condition.wait(deadline - time.monotonic())

            self.assertEqual(0, first.returncode)
            self.assertEqual(0, second.returncode)
            self.assertEqual("", first.stdout)
            self.assertLess(first_elapsed, 0.75)
            summaries = [body for path, body in received if path.endswith("turn-summaries")]
            observations = [
                body for path, body in received if path.endswith("/observations")
            ]
            self.assertEqual(2, len(summaries))
            self.assertEqual(
                summaries[0]["idempotencyKey"], summaries[1]["idempotencyKey"]
            )
            self.assertEqual(2, len(observations))
            self.assertEqual("automatic-ingestion", observations[0]["channels"][0]["channel"])
            self.assertEqual("Succeeded", observations[0]["channels"][0]["activityState"])
            self.assertNotIn("cwd", json.dumps(summaries[0]))
            self.assertNotIn("transcript", json.dumps(summaries[0]))
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)


if __name__ == "__main__":
    unittest.main()
