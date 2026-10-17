"""BlueZ external GATT application. All network work runs off the D-Bus loop."""

import hashlib
import json
import os
import signal
import socket
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import dbus
import dbus.service
from dbus.mainloop.glib import DBusGMainLoop
from gi.repository import GLib
from cryptography.hazmat.primitives import serialization

from . import INFO_UUID, REQUEST_UUID, RESPONSE_UUID, SERVICE_UUID
from .framing import Frames
from .network import Network
from .desktop import DesktopBootstrap
from .protocol import Owners, ProtocolError, Service, canonical

PROPS = "org.freedesktop.DBus.Properties"
OBJECTS = "org.freedesktop.DBus.ObjectManager"
GATT_SERVICE = "org.bluez.GattService1"
GATT_CHAR = "org.bluez.GattCharacteristic1"
ROOT = "/com/clawbox/network"


class InvalidRequest(dbus.DBusException):
    _dbus_error_name = "org.bluez.Error.NotPermitted"


class Properties(dbus.service.Object):
    def __init__(self, bus, path):
        super().__init__(bus, path)
        self.path = path

    def get_path(self):
        return dbus.ObjectPath(self.path)

    @dbus.service.method(PROPS, in_signature="s", out_signature="a{sv}")
    def GetAll(self, interface):
        return self.properties().get(interface, {})

    @dbus.service.method(PROPS, in_signature="ss", out_signature="v")
    def Get(self, interface, name):
        return self.GetAll(interface)[name]


class Characteristic(Properties):
    def __init__(self, app, path, uuid, flags):
        super().__init__(app.bus, path)
        self.app, self.uuid, self.flags = app, uuid, flags

    def properties(self):
        return {GATT_CHAR: {"UUID": self.uuid, "Service": dbus.ObjectPath(ROOT + "/service"),
                            "Flags": dbus.Array(self.flags, signature="s")}}

    @dbus.service.method(GATT_CHAR, in_signature="a{sv}", out_signature="ay")
    def ReadValue(self, options):
        if self.uuid == INFO_UUID:
            value = canonical(self.app.protocol.info())
            offset = int(options.get("offset", 0))
            if offset > len(value):
                raise InvalidRequest("invalid_offset")
            return dbus.ByteArray(value[offset:])
        if self.uuid != RESPONSE_UUID or int(options.get("offset", 0)) != 0:
            raise InvalidRequest("invalid_read")
        try:
            return dbus.ByteArray(self.app.frames.read(str(options.get("device", "")), options.get("mtu", 23)))
        except ProtocolError as error:
            raise InvalidRequest(error.code) from None

    @dbus.service.method(GATT_CHAR, in_signature="aya{sv}", out_signature="")
    def WriteValue(self, value, options):
        if self.uuid != REQUEST_UUID or int(options.get("offset", 0)) != 0 or options.get("prepare-authorize", False):
            raise InvalidRequest("invalid_write")
        peer = str(options.get("device", ""))
        try:
            request = self.app.frames.write(peer, bytes(value))
            if request is not None:
                state = self.app.frames.peers[peer]
                future = self.app.worker.submit(self.app.exchange, peer, request)
                future.add_done_callback(lambda task: GLib.idle_add(self.app.complete, peer, state, task))
        except ProtocolError as error:
            if error.code == "message_too_large":
                self.app.protocol.disconnect(peer)
            raise InvalidRequest(error.code) from None


class GattService(Properties):
    def properties(self):
        return {GATT_SERVICE: {"UUID": SERVICE_UUID, "Primary": dbus.Boolean(True)}}


class Advertisement(Properties):
    def __init__(self, bus, name):
        super().__init__(bus, ROOT + "/advertisement")
        self.name = name

    def properties(self):
        return {"org.bluez.LEAdvertisement1": {"Type": "peripheral", "LocalName": self.name,
                                               "Discoverable": dbus.Boolean(True),
                                               "ServiceUUIDs": dbus.Array([SERVICE_UUID], signature="s")}}

    @dbus.service.method("org.bluez.LEAdvertisement1", in_signature="", out_signature="")
    def Release(self):
        pass


class Application(dbus.service.Object):
    def __init__(self, bus, protocol):
        super().__init__(bus, ROOT)
        self.bus, self.protocol, self.frames = bus, protocol, Frames()
        self.worker = ThreadPoolExecutor(max_workers=1, thread_name_prefix="network-maintenance")
        self.service = GattService(bus, ROOT + "/service")
        self.characteristics = [Characteristic(self, ROOT + "/service/" + name, uuid, flags)
                                for name, uuid, flags in [("info", INFO_UUID, ["read"]),
                                                          ("request", REQUEST_UUID, ["write"]),
                                                          ("response", RESPONSE_UUID, ["read"])]]
        self.objects = [self.service] + self.characteristics
        self.advertisement = Advertisement(bus, "ClawBox-" + hashlib.sha256(protocol.public.encode()).hexdigest()[:6])

    @dbus.service.method(OBJECTS, in_signature="", out_signature="a{oa{sa{sv}}}")
    def GetManagedObjects(self):
        return {dbus.ObjectPath(obj.get_path()): obj.properties() for obj in self.objects}

    def exchange(self, peer, value):
        try:
            return self.protocol.exchange(peer, value)
        except ProtocolError as error:
            return {"error": error.code}
        except Exception:
            return {"error": "operation_failed"}

    def complete(self, peer, state, future):
        self.frames.complete(peer, future.result(), expected=state)
        return False

    def changed(self, interface, changed, invalidated, path=None):
        if interface == "org.bluez.Device1" and changed.get("Connected") == dbus.Boolean(False):
            self.frames.drop(str(path))
            # Queue cleanup after any in-flight command. Never replay or cancel a write blindly.
            self.worker.submit(self.protocol.disconnect, str(path))


def main():
    DBusGMainLoop(set_as_default=True)
    owners = Owners()
    owners.remember_existing()
    key = serialization.load_ssh_private_key(Path("/etc/ssh/ssh_host_ed25519_key").read_bytes(), password=None)
    protocol = Service(key, owners, Network(), desktop=DesktopBootstrap())
    bus = dbus.SystemBus()
    manager = dbus.Interface(bus.get_object("org.bluez", "/"), OBJECTS)
    adapters = [path for path, interfaces in manager.GetManagedObjects().items()
                if "org.bluez.GattManager1" in interfaces and "org.bluez.LEAdvertisingManager1" in interfaces]
    if not adapters:
        raise RuntimeError("No BLE peripheral adapter")
    adapter = bus.get_object("org.bluez", sorted(adapters)[0])
    props = dbus.Interface(adapter, PROPS)
    props.Set("org.bluez.Adapter1", "Powered", dbus.Boolean(True))
    # GATT uses application authentication; do not expose unrelated system Bluetooth pairing.
    props.Set("org.bluez.Adapter1", "Pairable", dbus.Boolean(False))
    app = Application(bus, protocol)
    loop = GLib.MainLoop()
    stopping = False
    bus.add_signal_receiver(app.changed, dbus_interface=PROPS, signal_name="PropertiesChanged", path_keyword="path")
    bus.add_signal_receiver(lambda name, old, new: loop.quit() if name == "org.bluez" and old and not new else None,
                            dbus_interface="org.freedesktop.DBus", signal_name="NameOwnerChanged")
    def failed(error):
        print("Bluetooth service registration failed: " + str(error), file=sys.stderr, flush=True)
        loop.quit()
    def registered():
        def advertised():
            address = os.environ.get("NOTIFY_SOCKET")
            if address:
                with socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM) as notify:
                    notify.connect("\0" + address[1:] if address.startswith("@") else address)
                    notify.sendall(b"READY=1")
            print("ClawBox Bluetooth provisioning ready", flush=True)
        dbus.Interface(adapter, "org.bluez.LEAdvertisingManager1").RegisterAdvertisement(
            app.advertisement.get_path(), dbus.Dictionary(signature="sv"),
            reply_handler=advertised, error_handler=failed)
    dbus.Interface(adapter, "org.bluez.GattManager1").RegisterApplication(
        ROOT, dbus.Dictionary(signature="sv"), reply_handler=registered, error_handler=failed)
    def stop():
        nonlocal stopping
        stopping = True
        loop.quit()
        return False
    for signum in (signal.SIGTERM, signal.SIGINT):
        GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signum, stop)
    try:
        loop.run()
    finally:
        app.worker.shutdown(wait=False, cancel_futures=True)
    # Unexpected BlueZ loss and registration failure trigger systemd recovery.
    return 0 if stopping else 1


if __name__ == "__main__":
    sys.exit(main())
