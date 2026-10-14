import importlib.util
import json
import pathlib
import tempfile
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[2]


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / "board" / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


inspect = load("inspect-desktop")
pair = load("pair-desktop")
PUBLIC = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA clawbox-reverse-tunnel"
SETTINGS = ["gatewayports no", "allowtcpforwarding yes"]


class GuidedSetupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.home = pathlib.Path(self.temp.name)
        directory = self.home / ".openclaw"
        directory.mkdir()
        (directory / "env.sh").write_text("# ready")
        self.config = directory / "openclaw.json"
        self.config.write_text(json.dumps({"gateway": {"bind": "loopback", "auth": {"mode": "token", "token": "PRIVATE" * 8}},
                                          "tools": {"allow": []}, "agents": {"defaults": {"model": "provider/model"}}}))

    def tearDown(self):
        self.temp.cleanup()

    def test_read_only_inspection_never_exports_configuration_or_credentials(self):
        original = self.config.read_bytes()
        with patch.object(inspect.shutil, "which", return_value="/bin/openclaw"):
            result = inspect.inspect(self.home, SETTINGS)
        self.assertEqual({"issues": []}, result)
        self.assertNotIn("PRIVATE", json.dumps(result))
        self.assertEqual(original, self.config.read_bytes())
        self.assertFalse((self.home / ".ssh").exists())

    def test_missing_model_and_gateway_are_separate_actionable_codes(self):
        self.config.write_text(json.dumps({"gateway": {}, "tools": {"allow": []}}))
        with patch.object(inspect.shutil, "which", return_value=None):
            result = inspect.inspect(self.home, [])
        self.assertEqual({"openclaw", "gateway", "model", "forwarding"}, set(result["issues"]))

    def test_malformed_configuration_reports_only_code(self):
        self.config.write_text("not json PRIVATE")
        with patch.object(inspect.shutil, "which", return_value="/bin/openclaw"):
            self.assertEqual(["config"], inspect.inspect(self.home, SETTINGS)["issues"])

    def test_pairing_is_restricted_and_preserves_other_keys_on_repeat(self):
        keys = self.home / ".ssh" / "authorized_keys"
        keys.parent.mkdir()
        keys.write_text("another user's existing key\n")
        pair.pair(PUBLIC, self.home, SETTINGS)
        first = keys.read_text()
        self.assertIn('command="/bin/false"', first)
        self.assertIn("no-pty,no-user-rc", first)
        self.assertIn('permitlisten="127.0.0.1:18791"', first)
        pair.pair(PUBLIC, self.home, SETTINGS)
        self.assertEqual(first, keys.read_text())
        self.assertTrue(first.startswith("another user's existing key\n"))
        keys.write_text(first.replace('no-agent-forwarding,', 'permitopen="127.0.0.1:18789",no-agent-forwarding,'))
        extended = keys.read_text()
        pair.pair(PUBLIC, self.home, SETTINGS)
        self.assertEqual(extended, keys.read_text())

    def test_unrestricted_or_conflicting_existing_key_is_never_broadened(self):
        keys = self.home / ".ssh" / "authorized_keys"
        keys.parent.mkdir()
        keys.write_text(PUBLIC + "\n")
        with self.assertRaises(SystemExit):
            pair.pair(PUBLIC, self.home, SETTINGS)
        self.assertEqual(PUBLIC + "\n", keys.read_text())

    def test_unsafe_forwarding_and_invalid_public_key_never_create_keys(self):
        for public, settings in [(PUBLIC, ["gatewayports yes", "allowtcpforwarding yes"]), (PUBLIC + "\nexec anything", SETTINGS)]:
            with self.assertRaises(SystemExit):
                pair.pair(public, self.home, settings)
        self.assertFalse((self.home / ".ssh").exists())


if __name__ == "__main__":
    unittest.main()
