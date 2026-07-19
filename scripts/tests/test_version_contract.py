import json
import re
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


if __name__ == "__main__":
    unittest.main()
