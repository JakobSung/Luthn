import importlib.util
import json
import pathlib
import sys
import unittest
from unittest import mock


SCRIPT_PATH = pathlib.Path(__file__).parents[1] / "release-container.py"
WORKFLOW_PATH = pathlib.Path(__file__).parents[2] / ".github" / "workflows" / "release-container.yml"
SPEC = importlib.util.spec_from_file_location("release_container", SCRIPT_PATH)
release_container = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = release_container
SPEC.loader.exec_module(release_container)


class ReleaseContainerTests(unittest.TestCase):
    def test_strict_semver_derives_minor_tag(self):
        self.assertEqual(
            release_container.validate_version("v1.23.4"),
            ("v1.23.4", "v1.23"),
        )

    def test_rejects_non_strict_versions(self):
        for version in ("1.2.3", "v01.2.3", "v1.2", "v1.2.3-rc.1"):
            with self.subTest(version=version):
                with self.assertRaises(release_container.ReleaseError):
                    release_container.validate_version(version)

    def test_requires_exact_current_remote_main_and_unused_tag(self):
        main_sha = "a" * 40
        with mock.patch.object(
            release_container,
            "read_remote_ref",
            side_effect=[main_sha, ""],
        ):
            request = release_container.prepare_request("v0.1.0", main_sha)
        self.assertEqual(request.source_sha, main_sha)
        self.assertEqual(request.minor_tag, "v0.1")

    def test_rejects_non_main_target(self):
        with mock.patch.object(
            release_container,
            "read_remote_ref",
            return_value="a" * 40,
        ):
            with self.assertRaisesRegex(release_container.ReleaseError, "current remote main"):
                release_container.prepare_request("v0.1.0", "b" * 40)

    def test_rejects_existing_release_tag(self):
        main_sha = "a" * 40
        with mock.patch.object(
            release_container,
            "read_remote_ref",
            side_effect=[main_sha, main_sha],
        ):
            with self.assertRaisesRegex(release_container.ReleaseError, "already exists"):
                release_container.prepare_request("v0.1.0", main_sha)

    def test_dry_run_does_not_dispatch(self):
        request = release_container.ReleaseRequest("v0.1.0", "v0.1", "a" * 40, "JakobSung/Luthn")
        with mock.patch.object(release_container, "prepare_request", return_value=request), mock.patch.object(
            release_container, "dispatch"
        ) as dispatch, mock.patch("builtins.print") as output:
            self.assertEqual(release_container.main(["v0.1.0", "--dry-run"]), 0)
        dispatch.assert_not_called()
        payload = json.loads(output.call_args.args[0])
        self.assertEqual(payload["status"], "validated")

    def test_release_workflow_carries_required_container_contract(self):
        workflow = WORKFLOW_PATH.read_text(encoding="utf-8")
        for required in (
            "type=raw,value=${{ steps.release.outputs.version }}",
            "type=raw,value=${{ steps.release.outputs.minor_tag }}",
            "type=raw,value=stable",
            "type=raw,value=sha-${{ steps.release.outputs.source_sha }}",
            "org.opencontainers.image.version=${{ steps.release.outputs.version }}",
            "org.opencontainers.image.revision=${{ steps.release.outputs.source_sha }}",
            "platforms: linux/amd64,linux/arm64",
            'required_platforms = {"linux/amd64", "linux/arm64"}',
            "Verify anonymous versioned pull",
            "Create immutable Git release tag",
        ):
            with self.subTest(required=required):
                self.assertIn(required, workflow)


if __name__ == "__main__":
    unittest.main()
