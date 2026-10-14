import importlib.util
import json
import pathlib
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("inspect_network", ROOT / "scripts" / "board" / "inspect-network.py")
network = importlib.util.module_from_spec(spec)
spec.loader.exec_module(network)


class NetworkInspectionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.profile = pathlib.Path(self.temp.name) / "board.nmconnection"
        self.profile.write_text("[connection]\nuuid=board-uuid\nautoconnect=true\n[ipv4]\nmethod=manual\naddress1=192.168.137.2/24,192.168.137.1\n[wifi-security]\npsk=DO-NOT-EXPORT\n")
        self.method = "manual"

    def tearDown(self):
        self.temp.cleanup()

    def run_command(self, arguments):
        if arguments[0] == "ip":
            return json.dumps([{"ifname": "eth0", "addr_info": [{"local": "192.168.137.2", "prefixlen": 24}]}])
        if "GENERAL.CONNECTION" in arguments:
            return "Board connection"
        if "connection.uuid" in arguments:
            return "board-uuid"
        if "ipv4.method,ipv4.addresses,ipv4.gateway" in arguments:
            return self.method + "\n192.168.137.2/24\n192.168.137.1"
        raise AssertionError("Only expected read-only commands may run.")

    def inspect(self, paths=None):
        with patch.object(network, "run", side_effect=self.run_command), patch.object(network.shutil, "which", return_value="/usr/bin/nmcli"), patch.object(network.pathlib.Path, "glob", return_value=paths if paths is not None else [self.profile]):
            return network.inspect("192.168.137.2")

    def test_static_address_is_confirmed_only_with_matching_persistent_profile(self):
        original = self.profile.read_bytes()
        result = self.inspect()
        self.assertEqual("manual", result["method"])
        self.assertTrue(result["persistent"])
        self.assertEqual("192.168.137.2", result["address"])
        self.assertNotIn("DO-NOT-EXPORT", json.dumps(result))
        self.assertEqual(original, self.profile.read_bytes())

    def test_live_manual_address_without_file_is_not_claimed_to_survive_reboot(self):
        self.assertFalse(self.inspect([])["persistent"])

    def test_different_persistent_profile_or_address_is_not_accepted(self):
        self.profile.write_text("[connection]\nuuid=board-uuid\n[ipv4]\nmethod=manual\naddress1=192.168.20.2/24\n")
        self.assertFalse(self.inspect()["persistent"])
        self.profile.write_text("[connection]\nuuid=another-uuid\n[ipv4]\nmethod=manual\naddress1=192.168.137.2/24\n")
        self.assertFalse(self.inspect()["persistent"])

    def test_disabled_autoconnect_is_not_claimed_as_reboot_ready(self):
        self.profile.write_text(self.profile.read_text().replace("autoconnect=true", "autoconnect=false"))
        self.assertFalse(self.inspect()["persistent"])

    def test_dhcp_is_reported_as_auto(self):
        self.method = "auto"
        self.profile.write_text("[connection]\nuuid=board-uuid\n[ipv4]\nmethod=auto\n")
        result = self.inspect()
        self.assertEqual("auto", result["method"])
        self.assertTrue(result["persistent"])

    def test_unknown_backend_or_failed_command_cannot_create_false_positive(self):
        with patch.object(network, "run", side_effect=subprocess.TimeoutExpired("ip", 8)):
            self.assertFalse(network.inspect("192.168.137.2")["persistent"])

    def model_config(self):
        home = pathlib.Path(self.temp.name)
        (home / ".openclaw").mkdir(exist_ok=True)
        (home / ".openclaw" / "openclaw.json").write_text(json.dumps({
            "agents": {"defaults": {"model": {"primary": "remote/qwen"}}},
            "models": {"providers": {"remote": {"baseUrl": "https://model.example/v1", "apiKey": "NEVER-EXPORTED"}}}
        }))
        return home

    def test_model_network_probe_only_connects_to_configured_provider_without_api_key(self):
        home = self.model_config()
        with patch.object(network.socket, "create_connection") as connect:
            self.assertEqual("reachable", network.model_network(home))
            connect.assert_called_once_with(("model.example", 443), timeout=3)

    def test_model_network_failure_is_separate_from_local_board_connectivity(self):
        home = self.model_config()
        with patch.object(network.socket, "create_connection", side_effect=OSError("private failure detail")):
            self.assertEqual("unreachable", network.model_network(home))
        self.assertEqual("unknown", network.model_network(pathlib.Path(self.temp.name) / "absent"))


if __name__ == "__main__":
    unittest.main()
