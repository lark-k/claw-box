import copy
import json
from pathlib import Path
import struct
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src/ClawBox.Board.Network"))
from cryptography.hazmat.primitives.asymmetric import ed25519
from clawbox_network.framing import Frames
from clawbox_network.protocol import (Client, MAX_MESSAGE, Owners, ProtocolError, Service,
                                      canonical, fingerprint, public_text)
from clawbox_network.network import Network, validate_wifi


class MemoryOwners:
    def __init__(self, *keys):
        self.values = {public_text(key.public_key()) for key in keys}
        self.enrolled = bool(keys)

    def keys(self):
        return self.values

    def enrollment_open(self):
        return not self.enrolled

    def claim(self, public):
        if self.enrolled:
            raise ProtocolError("already_bound")
        self.values.add(public)
        self.enrolled = True


class FakeNetwork:
    def __init__(self):
        self.writes = []

    def status(self):
        return {"connected": False}

    def scan(self):
        return {"networks": [{"ssid": "test"}]}

    def configure(self, request):
        validate_wifi(request)
        self.writes.append(request.copy())
        return {"connected": True}

    def connect_saved(self, request):
        self.writes.append(request.copy())
        return {"connected": True}


class ProtocolTests(unittest.TestCase):
    def setUp(self):
        self.host = ed25519.Ed25519PrivateKey.generate()
        self.owner = ed25519.Ed25519PrivateKey.generate()
        self.owners = MemoryOwners(self.owner)
        self.network = FakeNetwork()
        self.now = 1000
        self.server = Service(self.host, self.owners, self.network, lambda: self.now)

    def connect(self, peer="a", key=None, claim=False):
        client = Client(key or self.owner, self.server.identity, claim)
        auth = client.accept(self.server.exchange(peer, client.hello))
        result = client.receive(self.server.exchange(peer, auth))
        self.assertTrue(result["ok"])
        return client

    def test_existing_owner_changes_wifi_without_old_network(self):
        client = self.connect()
        response = client.receive(self.server.exchange("a", client.request(
            {"op": "configure", "ssid": "travel", "password": "temporary-test-password"})))
        self.assertTrue(response["ok"])
        self.assertEqual("travel", self.network.writes[0]["ssid"])

    def test_unknown_owner_and_second_claim_rejected(self):
        stranger = Client(ed25519.Ed25519PrivateKey.generate(), self.server.identity)
        with self.assertRaisesRegex(ProtocolError, "not_owner"):
            self.server.exchange("x", stranger.hello)
        stranger.hello["mode"] = "claim"
        with self.assertRaisesRegex(ProtocolError, "already_bound"):
            self.server.exchange("x", stranger.hello)

    def test_explicit_first_claim_then_reboot_stays_bound(self):
        self.server.owners = MemoryOwners()
        self.connect(claim=True)
        restarted = Service(self.host, self.server.owners, self.network)
        stranger = Client(ed25519.Ed25519PrivateKey.generate(), restarted.identity, claim=True)
        with self.assertRaisesRegex(ProtocolError, "already_bound"):
            restarted.exchange("b", stranger.hello)

    def test_removed_owner_is_revoked_in_live_session(self):
        client = self.connect()
        self.owners.values.clear()
        response = client.receive(self.server.exchange("a", client.request({"op": "scan"})))
        self.assertEqual("not_owner", response["error"])
        self.assertFalse(self.owners.enrollment_open())

    def test_owner_signature_required(self):
        client = Client(self.owner, self.server.identity)
        auth = client.accept(self.server.exchange("a", client.hello))
        from clawbox_network.protocol import seal, encode
        auth = seal(client.outgoing, client.session, "client", 0,
                    {"op": "authenticate", "proof": encode(bytes(64))})
        result = client.receive(self.server.exchange("a", auth))
        self.assertEqual("invalid_signature", result["error"])
        self.assertEqual([], self.network.writes)

    def test_replay_cannot_repeat_write(self):
        client = self.connect()
        request = client.request({"op": "configure", "ssid": "home", "password": "test-password"})
        client.receive(self.server.exchange("a", request))
        with self.assertRaisesRegex(ProtocolError, "invalid_sequence"):
            self.server.exchange("a", request)
        self.assertEqual(1, len(self.network.writes))

    def test_saved_connection_requires_owner_and_replay_cannot_reactivate(self):
        client = self.connect()
        request = client.request({"op": "connect_saved", "ssid": "home", "security": "wpa-psk"})
        self.assertTrue(client.receive(self.server.exchange("a", request))["ok"])
        with self.assertRaisesRegex(ProtocolError, "invalid_sequence"):
            self.server.exchange("a", request)
        self.assertEqual(1, len(self.network.writes))
        client = self.connect()
        self.owners.values.clear()
        response = client.receive(self.server.exchange("a", client.request({"op": "connect_saved", "ssid": "home"})))
        self.assertEqual("not_owner", response["error"])
        self.assertEqual(1, len(self.network.writes))

    def test_ciphertext_tampering_and_cross_peer_rejected(self):
        client = self.connect()
        second = self.connect("b")
        request = client.request({"op": "status"})
        with self.assertRaisesRegex(ProtocolError, "invalid_sequence"):
            self.server.exchange("b", request)
        request["ciphertext"] = request["ciphertext"][:-4] + "AAAA"
        with self.assertRaisesRegex(ProtocolError, "invalid_ciphertext"):
            self.server.exchange("a", request)
        self.assertEqual([], self.network.writes)

    def test_host_substitution_and_transcript_tampering_rejected(self):
        client = Client(self.owner, "SHA256:wrong")
        with self.assertRaisesRegex(ProtocolError, "identity_mismatch"):
            client.accept(self.server.exchange("a", client.hello))
        client = Client(self.owner, self.server.identity)
        response = self.server.exchange("a", client.hello)
        response["transcript"]["nonce"] = "A" * 44
        with self.assertRaisesRegex(ProtocolError, "invalid_handshake"):
            client.accept(response)

    def test_disconnect_and_timeout_forget_sessions(self):
        client = self.connect()
        self.server.disconnect("a")
        with self.assertRaisesRegex(ProtocolError, "session_expired"):
            self.server.exchange("a", client.request({"op": "status"}))
        client = self.connect()
        self.now += 181
        with self.assertRaisesRegex(ProtocolError, "session_expired"):
            self.server.exchange("a", client.request({"op": "status"}))

    def test_no_password_in_wire_or_exception(self):
        client = self.connect()
        password = "special-private-password"
        request = client.request({"op": "configure", "ssid": "home", "password": password})
        self.assertNotIn(password, canonical(request).decode())
        def fail(value):
            raise RuntimeError(password)
        self.network.configure = fail
        reply = self.server.exchange("a", request)
        self.assertNotIn(password, canonical(reply).decode())
        self.assertEqual("operation_failed", client.receive(reply)["error"])

    def test_no_shell_or_file_operation(self):
        client = self.connect()
        reply = client.receive(self.server.exchange("a", client.request({"op": "exec", "command": "anything"})))
        self.assertEqual("unsupported_operation", reply["error"])


class OwnerStoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.store = Owners(root / "ssh/authorized_keys", root / "state/enrolled")
        self.key = public_text(ed25519.Ed25519PrivateKey.generate().public_key())

    def tearDown(self):
        self.temp.cleanup()

    def test_generic_root_key_is_not_provisioning_owner(self):
        self.store.authorized.parent.mkdir()
        self.store.authorized.write_text(self.key + " clawbox-reverse-tunnel\n")
        self.assertEqual(set(), self.store.keys())

    def test_existing_pair_is_imported_without_changing_key(self):
        self.store.authorized.parent.mkdir()
        line = 'no-pty,command="/bin/false" ' + self.key + " clawbox-reverse-tunnel\n"
        self.store.authorized.write_text(line)
        self.store.remember_existing()
        self.assertFalse(self.store.enrollment_open())
        self.assertEqual({self.key}, self.store.keys())
        self.assertEqual(line, self.store.authorized.read_text())
        self.store.authorized.write_text("")
        self.assertFalse(self.store.enrollment_open())

    def test_claim_preserves_other_keys_and_closes_enrollment(self):
        self.store.authorized.parent.mkdir()
        old = self.key + " admin-key\n"
        self.store.authorized.write_text(old)
        # fcntl only exists on Linux; the board also runs this suite with the real file lock.
        fallback = types.SimpleNamespace(flock=lambda *args: None, LOCK_EX=2)
        context = patch.dict(sys.modules, {"fcntl": fallback}) if sys.platform == "win32" else patch.dict(sys.modules, {})
        with context:
            self.store.claim(self.key)
            with self.assertRaisesRegex(ProtocolError, "already_bound"):
                self.store.claim(self.key)
        contents = self.store.authorized.read_text()
        self.assertTrue(contents.startswith(old))
        self.assertIn('command="/bin/false"', contents)
        self.assertIn('permitlisten="127.0.0.1:18791"', contents)
        self.assertFalse(self.store.enrollment_open())


class FramingTests(unittest.TestCase):
    def test_fragments_and_short_reads_at_minimum_mtu(self):
        frames = Frames(clock=lambda: 1000)
        peer = "/org/bluez/hci0/dev_A"
        request = canonical({"op": "hello", "text": "中文"}) + b"\n"
        for byte in request[:-1]:
            self.assertIsNone(frames.write(peer, bytes([byte])))
        self.assertEqual("hello", frames.write(peer, request[-1:])["op"])
        response = {"value": "a" * 1000}
        frames.complete(peer, response)
        result = bytearray()
        while True:
            chunk = frames.read(peer, 23)
            self.assertLess(len(chunk), 22)
            length, offset = struct.unpack(">II", chunk[:8])
            self.assertEqual(len(result), offset)
            result.extend(chunk[8:])
            if len(result) == length:
                break
        self.assertEqual(response, json.loads(result))

    def test_peer_isolation_limits_and_disconnect(self):
        frames = Frames(clock=lambda: 1000)
        a, b = "/org/bluez/hci0/dev_A", "/org/bluez/hci0/dev_B"
        frames.write(a, b'{"op":')
        with self.assertRaises(ProtocolError):
            frames.write(b, b'"hello"}\n')
        self.assertEqual("hello", frames.write(a, b'"hello"}\n')["op"])
        old = frames.peers[a]
        frames.drop(a)
        frames.write(a, b'{')
        frames.complete(a, {"secret": "old-session"}, expected=old)
        self.assertEqual(struct.pack(">II", 0, 0), frames.read(a))
        with self.assertRaisesRegex(ProtocolError, "message_too_large"):
            frames.write(b, b"x" * (MAX_MESSAGE + 1))


class WifiValidationTests(unittest.TestCase):
    def test_valid_unicode_and_psk(self):
        self.assertEqual("测试网络", validate_wifi({"ssid": "测试网络", "password": "test-password"})[0])
        validate_wifi({"ssid": "test", "password": "a" * 64})
        validate_wifi({"ssid": "open", "security": "open"})

    def test_legacy_chinese_ssid_preserves_original_bytes(self):
        import base64
        from clawbox_network.network import display_ssid, ssid_bytes
        raw = "测试网络".encode("gb18030")
        request = {"ssid": "测试网络", "ssid_bytes": base64.b64encode(raw).decode(), "password": "test-password"}
        validate_wifi(request)
        self.assertEqual("测试网络", display_ssid(raw))
        self.assertEqual(raw, ssid_bytes(request))
        request["ssid"] = "another network"
        with self.assertRaisesRegex(ProtocolError, "invalid_ssid"):
            validate_wifi(request)

    def test_invalid_input_cannot_reach_networkmanager(self):
        for request in [{"ssid": "中" * 11, "password": "test-password"},
                        {"ssid": "test", "password": "short"},
                        {"ssid": "test", "password": "line\nbreak"},
                        {"ssid": "test", "security": "enterprise"},
                        {"ssid": "test", "password": "test-password", "hidden": "yes"}]:
            with self.subTest(request=request):
                with self.assertRaises(ProtocolError):
                    validate_wifi(request)


class NetworkTransactionTests(unittest.TestCase):
    def test_closing_private_dbus_connection_cannot_exit_service(self):
        from unittest.mock import MagicMock
        bus = MagicMock()
        dbus = types.SimpleNamespace(SystemBus=MagicMock(return_value=bus), Interface=lambda obj, iface: obj)
        loop = types.SimpleNamespace(NULL_MAIN_LOOP=object())
        with patch.dict(sys.modules, {"dbus": dbus, "dbus.mainloop": loop}):
            Network._connect()
        dbus.SystemBus.assert_called_once_with(private=True, mainloop=loop.NULL_MAIN_LOOP)
        bus.set_exit_on_disconnect.assert_called_once_with(False)

    def simulate(self, failure=None):
        from unittest.mock import MagicMock
        from clawbox_network.network import ROOT
        network = Network()
        manager, settings, connection, old, other = [MagicMock() for _ in range(5)]
        bus = MagicMock()
        values = {ROOT + "/Settings": settings, "/new": connection, "/old": old, "/home": other}
        bus.get_object.side_effect = lambda service, path: values[path]
        dbus = types.SimpleNamespace(Interface=lambda obj, interface: obj,
            Dictionary=lambda value=None, **kw: value or {}, UInt32=int, Int32=int, Int64=int,
            Boolean=bool, ByteArray=bytes, ObjectPath=str, DBusException=RuntimeError)
        network._connect = lambda: (dbus, bus, manager)
        network._device = lambda *args: ("/wifi", {"ActiveConnection": "/previous-active"})
        state = 120 if failure == "connection" else 100
        def properties(dbus, bus, path, interface):
            if path == "/previous-active":
                return {"Connection": "/old"}
            return {"State": state, "ActiveConnection": "/new-active"}
        network._properties = properties
        settings.AddConnection2.return_value = ("/new", {})
        settings.ListConnections.return_value = ["/new", "/old", "/home"]
        request = {"ssid": "travel", "password": "test-password"}
        same_id = Network._settings(dbus, request)["connection"]["id"]
        old.GetSettings.return_value = {"connection": {"id": same_id}}
        other.GetSettings.return_value = {"connection": {"id": "home"}}
        manager.ActivateConnection.return_value = "/new-active"
        network.status = lambda: {"connected": True}
        if failure == "save":
            connection.Save.side_effect = RuntimeError("private-password")
        return network, manager, settings, connection, old, other, bus, request

    def test_success_saves_only_after_activation_and_preserves_other_ssid(self):
        network, manager, settings, new, old, other, bus, request = self.simulate()
        self.assertTrue(network.configure(request)["connected"])
        self.assertEqual(2, settings.AddConnection2.call_args.args[1])
        new.Save.assert_called_once()
        old.Delete.assert_called_once()
        other.Delete.assert_not_called()
        bus.close.assert_called_once()

    def test_failed_password_discards_temporary_profile_and_restores_original(self):
        network, manager, settings, new, old, other, bus, request = self.simulate("connection")
        with self.assertRaisesRegex(ProtocolError, "wifi_connection_failed"):
            network.configure(request)
        new.Save.assert_not_called()
        new.Delete.assert_called_once()
        old.Delete.assert_not_called()
        self.assertEqual("/old", manager.ActivateConnection.call_args.args[0])
        self.assertEqual("/wifi", manager.ActivateConnection.call_args.args[1])
        bus.close.assert_called_once()

    def test_save_failure_restores_original_and_releases_lock(self):
        network, manager, settings, new, old, other, bus, request = self.simulate("save")
        with self.assertRaises(RuntimeError):
            network.configure(request)
        new.Delete.assert_called_once()
        old.Delete.assert_not_called()
        self.assertEqual("/old", manager.ActivateConnection.call_args.args[0])
        self.assertFalse(network.lock.locked())

    def test_saved_connection_uses_existing_profile_without_reading_or_rewriting_secrets(self):
        network, manager, settings, new, old, other, bus, _ = self.simulate()
        network._saved = lambda *args: {(b"home", True): "/home"}
        self.assertTrue(network.connect_saved({"op": "connect_saved", "ssid": "home"})["connected"])
        self.assertEqual("/home", manager.ActivateConnection.call_args.args[0])
        settings.AddConnection2.assert_not_called()
        for connection in (new, old, other):
            connection.Delete.assert_not_called()
            connection.GetSecrets.assert_not_called()
            connection.Save.assert_not_called()
        self.assertFalse(network.lock.locked())

    def test_saved_connection_failure_retains_saved_password_and_restores_previous_network(self):
        network, manager, settings, new, old, other, bus, _ = self.simulate("connection")
        network._saved = lambda *args: {(b"home", True): "/home"}
        with self.assertRaisesRegex(ProtocolError, "wifi_connection_failed"):
            network.connect_saved({"op": "connect_saved", "ssid": "home"})
        self.assertEqual("/old", manager.ActivateConnection.call_args.args[0])
        other.Delete.assert_not_called()
        other.Save.assert_not_called()
        self.assertFalse(network.lock.locked())

    def test_missing_saved_network_or_security_mismatch_never_activates(self):
        network, manager, settings, new, old, other, bus, _ = self.simulate()
        network._saved = lambda *args: {(b"home", True): "/home"}
        for request in ({"op": "connect_saved", "ssid": "missing"},
                        {"op": "connect_saved", "ssid": "home", "security": "open"}):
            with self.assertRaisesRegex(ProtocolError, "saved_network_missing"):
                network.connect_saved(request)
        manager.ActivateConnection.assert_not_called()
        self.assertFalse(network.lock.locked())

    def test_saved_metadata_excludes_temporary_and_unmanaged_profiles(self):
        network, manager, settings, new, old, other, bus, request = self.simulate()
        dbus, _, _ = network._connect()
        managed = Network._settings(dbus, request)
        new.GetSettings.return_value = managed
        old.GetSettings.return_value = managed
        other.GetSettings.return_value = {**managed, "connection": {"type": "802-11-wireless", "id": "external"}}
        network._properties = lambda dbus, bus, path, interface: {"Unsaved": path == "/new"}
        self.assertEqual({(b"travel", True): "/old"}, network._saved(dbus, bus))
        old.GetSecrets.assert_not_called()

    def test_saved_request_rejects_password_or_path_injection_before_dbus(self):
        network = Network()
        network._connect = lambda: self.fail("Invalid request reached D-Bus")
        for field in ("password", "path", "command"):
            with self.assertRaises(ProtocolError):
                network.connect_saved({"op": "connect_saved", "ssid": "home", field: "anything"})


if __name__ == "__main__":
    unittest.main()
