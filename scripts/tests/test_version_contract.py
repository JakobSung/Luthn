import json
import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


class VersionContractTests(unittest.TestCase):
    def test_release_contract_matches_all_runtime_surfaces(self) -> None:
        contract = json.loads((ROOT / "release/version.json").read_text())
        shell = (ROOT / "scripts/luthn").read_text()
        powershell = (ROOT / "scripts/luthn.ps1").read_text()
        connector = (ROOT / "scripts/luthn-codex-connector.py").read_text()
        mcp = (ROOT / "src/Luthn.McpServer/McpJsonRpcServer.cs").read_text()

        self.assertEqual(1, contract["contractVersion"])
        self.assertRegex(contract["cliTemplateVersion"], r"^[1-9][0-9]*$")
        self.assertRegex(contract["connectorTemplateVersion"], r"^[1-9][0-9]*$")
        self.assertRegex(contract["mcpSchemaVersion"], r"^[1-9][0-9]*$")
        self.assertIn(
            f'cli_template_version="{contract["cliTemplateVersion"]}"', shell
        )
        self.assertIn(
            f'$script:LuthnWindowsCliVersion = "{contract["cliTemplateVersion"]}"',
            powershell,
        )
        self.assertIn(
            f'codex_connector_version="{contract["connectorTemplateVersion"]}"',
            shell,
        )
        self.assertIn(
            f'$script:CodexConnectorTemplateVersion = "{contract["connectorTemplateVersion"]}"',
            powershell,
        )
        self.assertIn(
            f'CONNECTOR_TEMPLATE_VERSION = "{contract["connectorTemplateVersion"]}"',
            connector,
        )
        self.assertIn(
            f'mcp_schema_version="{contract["mcpSchemaVersion"]}"', shell
        )
        self.assertIn(
            f'$script:McpSchemaVersion = "{contract["mcpSchemaVersion"]}"',
            powershell,
        )
        self.assertIn(
            f'public const string SchemaVersion = "{contract["mcpSchemaVersion"]}";',
            mcp,
        )

    def test_contract_contains_no_secret_shaped_fields(self) -> None:
        contract = json.loads((ROOT / "release/version.json").read_text())
        secret_pattern = re.compile(r"token|secret|password|credential", re.IGNORECASE)
        self.assertFalse(any(secret_pattern.search(key) for key in contract))

    def test_installers_share_stable_channel_and_digest_state_contract(self) -> None:
        shell = (ROOT / "scripts/luthn").read_text()
        powershell = (ROOT / "scripts/luthn.ps1").read_text()
        for surface in (shell, powershell):
            self.assertIn("LUTHN_UPDATE_CHANNEL", surface)
            self.assertIn("stable", surface)
            self.assertIn("installedImageReference", surface)
            self.assertIn("updateChannel", surface)

    def test_bash_immutable_reference_selects_the_official_repo_digest(self) -> None:
        command = r'''
set -euo pipefail
set -- help
source scripts/luthn >/dev/null
docker() {
  printf '%s\n' 'example.invalid/luthn@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa,ghcr.io/jakobsung/luthn@sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd'
}
test "$(immutable_image_ref ghcr.io/jakobsung/luthn:stable)" = 'ghcr.io/jakobsung/luthn@sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd'
'''
        subprocess.run(["bash", "-c", command], cwd=ROOT, check=True)

    def test_bash_update_check_keeps_channel_and_digest_state_unchanged(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            data = root / "data"
            config = root / "config"
            state = root / "state"
            binary = root / "bin"
            fake_bin = root / "fake-bin"
            for directory in (data, config, state, binary, fake_bin):
                directory.mkdir()
            (data / "compose.yaml").write_text("", encoding="utf-8")
            config_file = config / "luthn.env"
            config_file.write_text(
                "LUTHN_IMAGE=ghcr.io/jakobsung/luthn@sha256:" + "d" * 64 + "\n"
                "LUTHN_UPDATE_CHANNEL=ghcr.io/jakobsung/luthn:stable\n",
                encoding="utf-8",
            )
            docker = fake_bin / "docker"
            docker.write_text(
                r'''#!/usr/bin/env bash
set -euo pipefail
joined=" $* "
if [[ "${1:-}" == "info" ]]; then exit 0; fi
if [[ "${1:-}" == "compose" && "${2:-}" == "version" ]]; then exit 0; fi
if [[ "$joined" == *" ps -q api "* ]]; then printf '%s\n' container; exit 0; fi
if [[ "${1:-}" == "inspect" ]]; then printf '%s\n' sha256:current; exit 0; fi
if [[ "${1:-}" == "image" && "${2:-}" == "inspect" ]]; then
  if [[ "$joined" == *"RepoDigests"* ]]; then
    printf '%s\n' 'ghcr.io/jakobsung/luthn@sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd'
  elif [[ "$joined" == *"org.opencontainers.image.revision"* ]]; then
    printf '%s\n' aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
  elif [[ "$joined" == *"org.opencontainers.image.version"* ]]; then
    printf '%s\n' v0.1.0
  elif [[ "$joined" == *"io.luthn.mcp-schema.version"* ]]; then
    printf '%s\n' 3
  fi
  exit 0
fi
if [[ "$joined" == *"buildx imagetools inspect"* && "$joined" == *".Manifest"* ]]; then
  printf '%s\n' '{"digest":"sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}'
  exit 0
fi
if [[ "$joined" == *"buildx imagetools inspect"* && "$joined" == *".Image"* ]]; then
  printf '%s\n' '{"linux/amd64":{"config":{"Labels":{"org.opencontainers.image.revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","org.opencontainers.image.version":"v0.1.0","io.luthn.cli-template.version":"3","io.luthn.connector-template.version":"3","io.luthn.mcp-schema.version":"3"}}}}'
  exit 0
fi
exit 1
''',
                encoding="utf-8",
            )
            docker.chmod(0o755)
            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{fake_bin}{os.pathsep}{environment['PATH']}",
                    "LUTHN_DATA_DIR": str(data),
                    "LUTHN_CONFIG_DIR": str(config),
                    "LUTHN_STATE_DIR": str(state),
                    "LUTHN_BIN_DIR": str(binary),
                    "LUTHN_COMPOSE_FILE": str(data / "compose.yaml"),
                    "LUTHN_CONFIG_FILE": str(config_file),
                    "LUTHN_CLI_PATH": str(ROOT / "scripts/luthn"),
                }
            )
            before = config_file.read_bytes()
            version_result = subprocess.run(
                [str(ROOT / "scripts/luthn"), "version", "--json"],
                cwd=ROOT,
                env=environment,
                check=True,
                capture_output=True,
                text=True,
            )
            check_result = subprocess.run(
                [str(ROOT / "scripts/luthn"), "update", "check", "--json"],
                cwd=ROOT,
                env=environment,
                check=True,
                capture_output=True,
                text=True,
            )
            version = json.loads(version_result.stdout)
            update = json.loads(check_result.stdout)
            self.assertEqual("ghcr.io/jakobsung/luthn:stable", version["updateChannel"])
            self.assertTrue(version["installedImageReference"].endswith("d" * 64))
            self.assertEqual("current", update["status"])
            self.assertEqual(before, config_file.read_bytes())


if __name__ == "__main__":
    unittest.main()
