"""Fixed NetworkManager operations. Passwords travel over D-Bus, never argv."""

import hashlib
import base64
import re
import threading
import time
import uuid

from .protocol import ProtocolError

NM = "org.freedesktop.NetworkManager"
ROOT = "/org/freedesktop/NetworkManager"
PROPS = "org.freedesktop.DBus.Properties"
DEVICE = NM + ".Device"
WIRELESS = DEVICE + ".Wireless"
CONNECTION = NM + ".Settings.Connection"


def display_ssid(raw):
    for encoding in ("utf-8", "gb18030"):
        try:
            return raw.decode(encoding)
        except UnicodeError:
            pass
    return raw.decode("utf-8", "replace")


def ssid_bytes(request):
    try:
        raw = (base64.b64decode(request["ssid_bytes"], validate=True) if "ssid_bytes" in request
               else request["ssid"].encode("utf-8"))
        if not 1 <= len(raw) <= 32:
            raise ValueError()
        if "ssid_bytes" in request and display_ssid(raw) != request["ssid"]:
            raise ValueError()
        return raw
    except (ValueError, TypeError, KeyError, AttributeError, UnicodeError):
        raise ProtocolError("invalid_ssid") from None


def validate_wifi(request):
    ssid, password = request.get("ssid"), request.get("password", "")
    security, hidden = request.get("security", "wpa-psk"), request.get("hidden", False)
    if (not isinstance(ssid, str) or
            any(ord(c) < 32 or ord(c) == 127 for c in ssid)):
        raise ProtocolError("invalid_ssid")
    ssid_bytes(request)
    if security not in ("wpa-psk", "open") or type(hidden) is not bool:
        raise ProtocolError("unsupported_security")
    if not isinstance(password, str) or any(ord(c) < 32 or ord(c) == 127 for c in password):
        raise ProtocolError("invalid_password")
    if security == "open" and password:
        raise ProtocolError("invalid_password")
    if security == "wpa-psk" and not (8 <= len(password.encode("utf-8")) <= 63 or
                                        re.fullmatch(r"[0-9a-fA-F]{64}", password)):
        raise ProtocolError("invalid_password")
    return ssid, password, security, hidden


class Network:
    def __init__(self):
        self.lock = threading.Lock()

    @staticmethod
    def _connect():
        import dbus
        from dbus.mainloop import NULL_MAIN_LOOP
        # This connection belongs exclusively to the worker, never the GATT GLib loop.
        bus = dbus.SystemBus(private=True, mainloop=NULL_MAIN_LOOP)
        bus.set_exit_on_disconnect(False)
        return dbus, bus, dbus.Interface(bus.get_object(NM, ROOT), NM)

    @staticmethod
    def _properties(dbus, bus, path, interface):
        return dbus.Interface(bus.get_object(NM, path), PROPS).GetAll(interface)

    def _device(self, dbus, bus, manager):
        for path in manager.GetDevices():
            values = self._properties(dbus, bus, path, DEVICE)
            if int(values["DeviceType"]) == 2:
                if not values["Managed"]:
                    raise ProtocolError("wifi_unmanaged")
                return path, values
        raise ProtocolError("wifi_driver_missing")

    def status(self):
        dbus, bus, manager = self._connect()
        try:
            path, props = self._device(dbus, bus, manager)
            result = {"interface": str(props["Interface"]), "state": int(props["State"]),
                      "connected": int(props["State"]) == 100, "addresses": []}
            ip4 = props.get("Ip4Config", "/")
            if ip4 != "/":
                data = self._properties(dbus, bus, ip4, NM + ".IP4Config")
                result["addresses"] = [str(a["address"]) for a in data.get("AddressData", [])]
            wireless = self._properties(dbus, bus, path, WIRELESS)
            ap = wireless.get("ActiveAccessPoint", "/")
            if ap != "/":
                values = self._properties(dbus, bus, ap, NM + ".AccessPoint")
                raw = bytes(values["Ssid"])
                result.update(ssid=display_ssid(raw), ssid_bytes=base64.b64encode(raw).decode("ascii"))
            return result
        finally:
            bus.close()

    def scan(self):
        dbus, bus, manager = self._connect()
        try:
            path, _ = self._device(dbus, bus, manager)
            wireless = dbus.Interface(bus.get_object(NM, path), WIRELESS)
            try:
                wireless.RequestScan(dbus.Dictionary(signature="sv"))
                time.sleep(3)
            except dbus.DBusException:
                # NM rate-limits scanning; its existing cache remains usable.
                pass
            saved = self._saved(dbus, bus)
            networks = {}
            for ap in wireless.GetAllAccessPoints():
                values = self._properties(dbus, bus, ap, NM + ".AccessPoint")
                raw = bytes(values["Ssid"])
                ssid = display_ssid(raw)
                if not ssid:
                    continue
                item = {"ssid": ssid, "ssid_bytes": base64.b64encode(raw).decode("ascii"),
                        "strength": int(values["Strength"]),
                        "frequency": int(values["Frequency"]),
                        "security": "secured" if int(values["Flags"]) & 1 else "open",
                        "saved": (raw, bool(int(values["Flags"]) & 1)) in saved}
                if raw not in networks or item["strength"] > networks[raw]["strength"]:
                    networks[raw] = item
            return {"networks": sorted(networks.values(), key=lambda x: -x["strength"])[:24]}
        finally:
            bus.close()

    def _saved(self, dbus, bus):
        settings = dbus.Interface(bus.get_object(NM, ROOT + "/Settings"), NM + ".Settings")
        result = {}
        for path in settings.ListConnections():
            try:
                values = dbus.Interface(bus.get_object(NM, path), CONNECTION).GetSettings()
                raw = bytes(values.get("802-11-wireless", {}).get("ssid", b""))
                connection = values.get("connection", {})
                security = values.get("802-11-wireless-security", {})
                if (not raw or connection.get("type") != "802-11-wireless" or
                        connection.get("id") != "clawbox-wifi-" + hashlib.sha256(raw).hexdigest()[:12] or
                        security.get("key-mgmt", "open") not in ("open", "wpa-psk") or
                        self._properties(dbus, bus, path, CONNECTION).get("Unsaved", True)):
                    continue
                result[(raw, bool(security))] = path
            except dbus.DBusException:
                continue
        return result

    def connect_saved(self, request):
        if (set(request) - {"op", "ssid", "ssid_bytes", "security"} or
                not isinstance(request.get("ssid"), str) or
                any(ord(c) < 32 or ord(c) == 127 for c in request["ssid"])):
            raise ProtocolError("invalid_ssid")
        raw = ssid_bytes(request)
        security = request.get("security", "wpa-psk")
        if security not in ("wpa-psk", "open"):
            raise ProtocolError("unsupported_security")
        if not self.lock.acquire(blocking=False):
            raise ProtocolError("network_busy")
        bus = None
        previous = active = None
        success = False
        try:
            dbus, bus, manager = self._connect()
            path, props = self._device(dbus, bus, manager)
            selected = self._saved(dbus, bus).get((raw, security != "open"))
            if selected is None:
                raise ProtocolError("saved_network_missing")
            old_active = props.get("ActiveConnection", "/")
            if old_active != "/":
                previous = self._properties(dbus, bus, old_active, NM + ".Connection.Active")["Connection"]
                if previous == selected and int(props.get("State", 0)) == 100:
                    success = True
                    return self.status()
            active = manager.ActivateConnection(selected, path, dbus.ObjectPath("/"))
            self._wait(dbus, bus, path, active)
            success = True
            return self.status()
        finally:
            if bus is not None:
                if not success and active is not None:
                    try:
                        manager.DeactivateConnection(active)
                        if previous is not None:
                            manager.ActivateConnection(previous, path, dbus.ObjectPath("/"))
                    except Exception:
                        pass
                bus.close()
            self.lock.release()

    def _wait(self, dbus, bus, path, active):
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            values = self._properties(dbus, bus, path, DEVICE)
            state = int(values["State"])
            if values.get("ActiveConnection") == active and state == 100:
                return
            if state == 120 or (state == 30 and time.monotonic() > deadline - 40):
                raise ProtocolError("wifi_connection_failed")
            time.sleep(0.5)
        raise ProtocolError("wifi_connection_timeout")

    @staticmethod
    def _settings(dbus, request):
        ssid, password, security, hidden = validate_wifi(request)
        raw = ssid_bytes(request)
        settings = {"connection": {"id": "clawbox-wifi-" + hashlib.sha256(raw).hexdigest()[:12],
                                   "uuid": str(uuid.uuid4()), "type": "802-11-wireless",
                                   "autoconnect": dbus.Boolean(True), "autoconnect-priority": dbus.Int32(10)},
                    "802-11-wireless": {"ssid": dbus.ByteArray(raw), "mode": "infrastructure",
                                           "hidden": dbus.Boolean(hidden), "powersave": dbus.UInt32(2)},
                    "ipv4": {"method": "auto", "route-metric": dbus.Int64(600)},
                    "ipv6": {"method": "auto", "route-metric": dbus.Int64(600)}}
        if security == "wpa-psk":
            settings["802-11-wireless-security"] = {"key-mgmt": "wpa-psk", "psk": password,
                                                     "psk-flags": dbus.UInt32(0)}
        return {section: dbus.Dictionary(values, signature="sv") for section, values in settings.items()}

    def configure(self, request):
        validate_wifi(request)
        if not self.lock.acquire(blocking=False):
            raise ProtocolError("network_busy")
        try:
            return self._configure(request)
        finally:
            self.lock.release()

    def _configure(self, request):
        dbus, bus, manager = self._connect()
        created = previous = active = None
        success = False
        try:
            path, props = self._device(dbus, bus, manager)
            old_active = props.get("ActiveConnection", "/")
            if old_active != "/":
                previous = self._properties(dbus, bus, old_active, NM + ".Connection.Active")["Connection"]
            settings = dbus.Interface(bus.get_object(NM, ROOT + "/Settings"), NM + ".Settings")
            # IN_MEMORY=2. A failed password must never replace a known working profile.
            created, _ = settings.AddConnection2(self._settings(dbus, request), dbus.UInt32(2),
                                                 dbus.Dictionary(signature="sv"))
            active = manager.ActivateConnection(created, path, dbus.ObjectPath("/"))
            deadline = time.monotonic() + 45
            while time.monotonic() < deadline:
                values = self._properties(dbus, bus, path, DEVICE)
                state = int(values["State"])
                if values.get("ActiveConnection") == active and state == 100:
                    connection = dbus.Interface(bus.get_object(NM, created), CONNECTION)
                    connection.Save()
                    success = True
                    # Retain other SSIDs, retire only older profiles owned by this service for this SSID.
                    new_id = self._settings(dbus, request)["connection"]["id"]
                    for other in settings.ListConnections():
                        if other == created:
                            continue
                        try:
                            candidate = dbus.Interface(bus.get_object(NM, other), CONNECTION)
                            if candidate.GetSettings()["connection"].get("id") == new_id:
                                candidate.Delete()
                        except dbus.DBusException:
                            pass
                    return self.status()
                if state == 120 or (state == 30 and time.monotonic() > deadline - 40):
                    raise ProtocolError("wifi_connection_failed")
                time.sleep(0.5)
            raise ProtocolError("wifi_connection_timeout")
        finally:
            if not success and created is not None:
                try:
                    if active is not None:
                        manager.DeactivateConnection(active)
                except Exception:
                    pass
                try:
                    dbus.Interface(bus.get_object(NM, created), CONNECTION).Delete()
                except Exception:
                    pass
                if previous is not None:
                    try:
                        manager.ActivateConnection(previous, path, dbus.ObjectPath("/"))
                    except Exception:
                        pass
            bus.close()
