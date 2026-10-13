import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SOURCE = pathlib.Path(__file__).resolve().parents[2] / "scripts" / "board" / "install-plugin.py"
spec = importlib.util.spec_from_file_location("clawbox_installer", SOURCE)
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="clawbox-installer-")
        self.home = pathlib.Path(self.temp.name)
        self.state = self.home / ".openclaw"
        self.stage = self.state / "deployment" / installer.PLUGIN_ID / "fixture"
        self.stage.mkdir(parents=True)
        source = SOURCE.parents[2] / "src" / "ClawBox.OpenClawPlugin"
        for name in installer.FILES:
            (self.stage / name).write_bytes((source / name).read_bytes())
        (self.stage / "worker-token").write_text("a" * 64)
        self.config = {"tools": {"allow": ["read", "write", "edit", "session_status"]},
                       "models": {"providers": {"fixture": {"baseUrl": "https://example.invalid/v1"}}}}
        self.config_path = self.state / "openclaw.json"
        self.save()

    def tearDown(self):
        self.temp.cleanup()

    def save(self):
        self.config_path.write_text(json.dumps(self.config))

    def run_installer(self, failure=None):
        with patch.object(pathlib.Path, "home", return_value=self.home), \
             patch.object(sys, "argv", ["install-plugin.py", str(self.stage)]), \
             patch.object(installer.subprocess, "run", side_effect=failure) as validate:
            installer.main()
            return validate

    def test_installs_only_expected_tools_and_preserves_model_settings(self):
        validate = self.run_installer()
        config = json.loads(self.config_path.read_text())
        self.assertEqual(config["models"], self.config["models"])
        self.assertEqual(config["tools"]["allow"], self.config["tools"]["allow"] + installer.TOOLS)
        self.assertNotIn("exec", config["tools"]["allow"])
        self.assertEqual(config["plugins"]["allow"], [installer.PLUGIN_ID])
        self.assertTrue((self.stage / "plugin" / "index.js").is_file())
        self.assertEqual(validate.call_args.args[0], ["openclaw", "config", "validate"])

    def test_validation_failure_restores_original_configuration(self):
        with self.assertRaises(SystemExit):
            self.run_installer(subprocess.CalledProcessError(1, "openclaw"))
        self.assertEqual(json.loads(self.config_path.read_text()), self.config)

    def test_existing_unmanaged_configuration_is_not_replaced(self):
        self.config["plugins"] = {"entries": {installer.PLUGIN_ID: {
            "config": {"workerTokenFile": "/unmanaged/private-token"}}}}
        self.save()
        with self.assertRaises(SystemExit):
            self.run_installer()
        self.assertEqual(json.loads(self.config_path.read_text()), self.config)

    def test_no_explicit_tool_policy_does_not_broaden_default_permissions(self):
        self.config.pop("tools")
        self.save()
        with self.assertRaises(SystemExit):
            self.run_installer()
        self.assertEqual(json.loads(self.config_path.read_text()), self.config)

    def test_invalid_worker_credential_never_changes_configuration(self):
        (self.stage / "worker-token").write_text("invalid")
        with self.assertRaises(SystemExit):
            self.run_installer()
        self.assertEqual(json.loads(self.config_path.read_text()), self.config)


if __name__ == "__main__":
    unittest.main()
