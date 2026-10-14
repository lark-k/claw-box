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

    def fresh_stage(self, name, worker="a" * 64):
        previous = self.stage
        self.stage = self.state / "deployment" / installer.PLUGIN_ID / name
        self.stage.mkdir()
        for filename in installer.FILES:
            (self.stage / filename).write_bytes((previous / filename).read_bytes())
        (self.stage / "worker-token").write_text(worker)

    def test_repeat_reuses_code_credentials_and_configuration_without_validation_or_backup(self):
        self.run_installer()
        original = self.config_path.read_bytes()
        backups = list((self.state / "deployment").glob("pre-phase7-*.json"))
        loaded = json.loads(original)["plugins"]["load"]["paths"]
        self.fresh_stage("repeat")
        self.run_installer().assert_not_called()
        self.assertEqual(original, self.config_path.read_bytes())
        self.assertEqual(backups, list((self.state / "deployment").glob("pre-phase7-*.json")))
        self.assertFalse((self.stage / "plugin").exists())
        self.assertEqual(loaded, json.loads(self.config_path.read_text())["plugins"]["load"]["paths"])

    def test_new_computer_updates_only_worker_binding_and_keeps_loaded_plugin_code(self):
        self.run_installer()
        first = json.loads(self.config_path.read_text())
        old_worker = pathlib.Path(first["plugins"]["entries"][installer.PLUGIN_ID]["config"]["workerTokenFile"])
        self.fresh_stage("friend", "b" * 64)
        self.run_installer().assert_called_once()
        second = json.loads(self.config_path.read_text())
        self.assertEqual(first["plugins"]["load"], second["plugins"]["load"])
        self.assertFalse((self.stage / "plugin").exists())
        self.assertEqual("a" * 64, old_worker.read_text())
        self.assertEqual(str(self.stage / "worker-token"), second["plugins"]["entries"][installer.PLUGIN_ID]["config"]["workerTokenFile"])

    def test_changed_plugin_content_is_updated_without_duplicating_load_paths(self):
        self.run_installer()
        self.fresh_stage("update")
        with (self.stage / "client.js").open("a") as file:
            file.write("\n// newer compatible build\n")
        self.run_installer().assert_called_once()
        paths = json.loads(self.config_path.read_text())["plugins"]["load"]["paths"]
        self.assertEqual([str(self.stage / "plugin")], paths)
        self.assertEqual((self.stage / "client.js").read_bytes(), (self.stage / "plugin/client.js").read_bytes())

    def test_failed_new_computer_binding_keeps_previous_worker_and_code(self):
        self.run_installer()
        first = json.loads(self.config_path.read_text())
        old_worker = pathlib.Path(first["plugins"]["entries"][installer.PLUGIN_ID]["config"]["workerTokenFile"])
        self.fresh_stage("friend-fails", "b" * 64)
        with self.assertRaises(SystemExit):
            self.run_installer(subprocess.CalledProcessError(1, "openclaw"))
        self.assertEqual(first, json.loads(self.config_path.read_text()))
        self.assertEqual("a" * 64, old_worker.read_text())
        self.assertFalse((self.stage / "plugin").exists())


if __name__ == "__main__":
    unittest.main()
