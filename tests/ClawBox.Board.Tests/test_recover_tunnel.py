import importlib.util
import pathlib
import tempfile
import unittest
from unittest.mock import patch

SOURCE = pathlib.Path(__file__).resolve().parents[2] / "scripts/board/recover-tunnel.py"
spec = importlib.util.spec_from_file_location("recover_tunnel", SOURCE)
recovery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recovery)


class TunnelRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tunnel-recovery-")
        self.addCleanup(self.temp.cleanup)
        self.proc = pathlib.Path(self.temp.name)
        self.process = self.proc / "123"
        self.process.mkdir()
        (self.process / "cmdline").write_bytes(b"sshd: root@notty\0")
        (self.process / "stat").write_text("123 (sshd) " + " ".join(["S"] + ["0"] * 18 + ["999"]))
        self.rows = {"listener": (("127.0.0.1", 18791), ("0.0.0.0", 0), "0A"),
                     "peer": (("192.168.137.2", 22), ("192.168.137.1", 50001), "01")}
        self.snapshot = {"at": 100, "sshPort": 22, "ports": [50002]}
        self.killed = []

    def run_recovery(self, rows=None):
        with patch.object(recovery, "sockets", return_value=self.rows if rows is None else rows), patch.object(recovery, "owned_inodes", return_value={"listener", "peer"}):
            return recovery.recover(self.snapshot, "192.168.137.1 50002 192.168.137.2 22", self.proc, self.killed.append, now=101)

    def test_proven_disconnected_same_pc_session_is_released(self):
        self.assertEqual("released", self.run_recovery())
        self.assertEqual([123], self.killed)

    def test_active_same_pc_session_is_preserved(self):
        self.snapshot["ports"].append(50001)
        self.assertEqual("occupied", self.run_recovery())
        self.assertEqual([], self.killed)

    def test_other_computer_is_preserved(self):
        self.rows["peer"] = (("192.168.137.2", 22), ("192.168.137.3", 50001), "01")
        self.assertEqual("occupied", self.run_recovery())
        self.assertEqual([], self.killed)

    def test_unknown_listener_is_preserved(self):
        (self.process / "cmdline").write_bytes(b"python listener.py\0")
        self.assertEqual("occupied", self.run_recovery())
        self.assertEqual([], self.killed)

    def test_stale_or_incomplete_windows_snapshot_cannot_authorize_cleanup(self):
        self.snapshot["at"] = 1
        self.assertEqual("unverified", self.run_recovery())
        self.snapshot["at"] = 100
        self.snapshot["ports"] = []
        self.assertEqual("unverified", self.run_recovery())
        self.assertEqual([], self.killed)

    def test_free_port_does_not_signal_anything(self):
        self.assertEqual("free", self.run_recovery({}))
        self.assertEqual([], self.killed)

    def test_listener_identity_change_is_rechecked(self):
        changed = dict(self.rows)
        changed["peer"] = (("192.168.137.2", 22), ("192.168.137.1", 50002), "01")
        with patch.object(recovery, "sockets", side_effect=[self.rows, changed]), patch.object(recovery, "owned_inodes", return_value={"listener", "peer"}):
            result = recovery.recover(self.snapshot, "192.168.137.1 50002 192.168.137.2 22", self.proc, self.killed.append, now=101)
        self.assertEqual("occupied", result)
        self.assertEqual([], self.killed)

    def test_tcp_endpoint_parser(self):
        self.assertEqual(("127.0.0.1", 18791), recovery.endpoint("0100007F:4967"))
