import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SOURCE = pathlib.Path(__file__).resolve().parents[2] / "scripts/board/configure-desktop.py"
spec = importlib.util.spec_from_file_location("desktop_config", SOURCE)
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class DesktopConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="quantum-config-")
        self.home = pathlib.Path(self.temp.name)
        (self.home / ".ssh").mkdir()
        (self.home / ".openclaw/deployment").mkdir(parents=True)
        self.public = self.home / "public"
        self.public.write_text("ssh-ed25519 AAAATEST clawbox-reverse-tunnel")
        self.keys = self.home / ".ssh/authorized_keys"
        self.original_keys = 'no-pty,no-agent-forwarding,command="/bin/false",permitlisten="127.0.0.1:18791" ' + self.public.read_text() + "\n"
        self.keys.write_text(self.original_keys)
        self.config = self.home / ".openclaw/openclaw.json"
        self.original_config = json.dumps({"gateway": {"bind": "loopback", "auth": {"mode": "token", "token": "a" * 64}}, "models": {"fixture": True}, "tools": {"allow": ["read", "write"] + installer.WINDOWS_TOOLS}})
        self.config.write_text(self.original_config)

    def tearDown(self):
        self.temp.cleanup()

    def run_installer(self, failure=None):
        with patch.object(pathlib.Path, "home", return_value=self.home), patch.object(sys, "argv", [str(SOURCE), str(self.public)]), patch.object(installer.subprocess, "run", side_effect=failure):
            installer.main()

    def test_ingress_is_authenticated_restricted_and_idempotent(self):
        self.run_installer()
        self.run_installer()
        self.assertEqual(self.keys.read_text().count('permitopen="127.0.0.1:18789"'), 1)
        self.assertIn('command="/bin/false"', self.keys.read_text())
        self.assertIn('permitlisten="127.0.0.1:18791"', self.keys.read_text())
        config = json.loads(self.config.read_text())
        self.assertTrue(config["gateway"]["http"]["endpoints"]["chatCompletions"]["enabled"])
        self.assertEqual(config["models"], {"fixture": True})
        self.assertIn("main", config["agents"]["entries"])
        self.assertEqual(config["agents"]["ownership"], "explicit")
        policy = config["agents"]["entries"]["quantum-harness"]["tools"]
        self.assertEqual(policy["allow"], installer.WINDOWS_TOOLS)
        self.assertTrue({"read", "write", "edit", "exec", "sessions_spawn"}.issubset(policy["deny"]))
        self.assertIn("write", config["tools"]["allow"])
        secret = json.loads((self.home / ".openclaw/deployment/quantum-harness-gateway.secrets.json").read_text())
        self.assertEqual(set(secret), {"token"})

    def test_validate_failure_rolls_back_both_files(self):
        with self.assertRaises(SystemExit):
            self.run_installer(subprocess.CalledProcessError(1, "openclaw"))
        self.assertEqual(self.config.read_text(), self.original_config)
        self.assertEqual(self.keys.read_text(), self.original_keys)

    def test_unrestricted_key_is_rejected_without_changes(self):
        self.keys.write_text(self.public.read_text() + "\n")
        with self.assertRaises(SystemExit):
            self.run_installer()
        self.assertEqual(self.config.read_text(), self.original_config)

    def test_public_gateway_is_rejected_without_changes(self):
        self.config.write_text(self.original_config.replace("loopback", "lan"))
        with self.assertRaises(SystemExit):
            self.run_installer()
        self.assertEqual(self.keys.read_text(), self.original_keys)

    def test_missing_plugin_never_falls_back_to_native_filesystem(self):
        config = json.loads(self.original_config)
        config["tools"]["allow"] = ["read", "write"]
        self.config.write_text(json.dumps(config))
        original = self.config.read_text()
        with self.assertRaises(SystemExit):
            self.run_installer()
        self.assertEqual(self.config.read_text(), original)
        self.assertEqual(self.keys.read_text(), self.original_keys)

    def test_main_agent_settings_survive_dedicated_agent_configuration(self):
        config = json.loads(self.original_config)
        config["agents"] = {"entries": {"main": {"workspace": "/custom-main", "model": "fixture/model"}}}
        self.config.write_text(json.dumps(config))
        self.run_installer()
        actual = json.loads(self.config.read_text())
        self.assertEqual(actual["agents"]["entries"]["main"], config["agents"]["entries"]["main"])


if __name__ == "__main__":
    unittest.main()
