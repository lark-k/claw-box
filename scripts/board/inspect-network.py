"""Read only the active Ethernet IPv4 configuration. Never export credentials."""
import configparser
import fnmatch
import ipaddress
import json
import os
import pathlib
import shutil
import subprocess
import socket
import urllib.parse


def run(arguments):
    return subprocess.check_output(arguments, text=True, stderr=subprocess.DEVNULL, timeout=8).strip()


def inspect(target=None):
    peer = os.environ.get("SSH_CONNECTION", "").split()
    target = target or (peer[2] if len(peer) == 4 else None)
    result = {"address": target or "", "interface": "", "method": "unknown", "configuredAddresses": [], "persistent": False, "gateway": ""}
    try:
        addresses = json.loads(run(["ip", "-j", "-4", "address", "show"]))
        active = next((item for item in addresses if any(a.get("local") == target for a in item.get("addr_info", []))), None)
        if active is None:
            return result
        name = active["ifname"]
        result["interface"] = name
        if shutil.which("nmcli"):
            connection = run(["nmcli", "--escape", "no", "--get-values", "GENERAL.CONNECTION", "device", "show", name])
            uuid = run(["nmcli", "--escape", "no", "--get-values", "connection.uuid", "connection", "show", connection])
            properties = run(["nmcli", "--escape", "no", "--get-values", "ipv4.method,ipv4.addresses,ipv4.gateway", "connection", "show", connection]).splitlines()
            if properties:
                result["method"] = properties[0]
                result["configuredAddresses"] = [a.strip() for a in properties[1].split(",")] if len(properties) > 1 else []
                result["gateway"] = properties[2] if len(properties) > 2 else ""
            for path in pathlib.Path("/etc/NetworkManager/system-connections").glob("*"):
                if not path.is_file():
                    continue
                parser = configparser.ConfigParser(interpolation=None, strict=False)
                parser.read(path)
                if parser.get("connection", "uuid", fallback="") != uuid or not parser.getboolean("connection", "autoconnect", fallback=True):
                    continue
                stored_method = parser.get("ipv4", "method", fallback="")
                stored_addresses = [value.split(",")[0].strip() for key, value in parser.items("ipv4") if key.startswith("address") and key[7:].isdigit()] if parser.has_section("ipv4") else []
                if stored_method == result["method"] and (stored_method != "manual" or any(ipaddress.ip_interface(a).ip == ipaddress.ip_address(target) for a in stored_addresses)):
                    result["persistent"] = True
            return result
        if shutil.which("networkctl"):
            text = run(["networkctl", "--no-pager", "status", name])
            for line in text.splitlines():
                if "Network File:" not in line:
                    continue
                path = pathlib.Path(line.split("Network File:", 1)[1].strip())
                if not path.is_absolute() or not str(path).startswith("/etc/systemd/network/") or not path.is_file():
                    continue
                parser = configparser.ConfigParser(interpolation=None, strict=False)
                parser.read(path)
                matching = parser.get("Match", "Name", fallback="").split()
                address = parser.get("Network", "Address", fallback=parser.get("Address", "Address", fallback=""))
                dhcp = parser.get("Network", "DHCP", fallback="no").lower()
                if matching and any(fnmatch.fnmatch(name, pattern) for pattern in matching) and address and dhcp in ("no", "false", "0") and ipaddress.ip_interface(address).ip == ipaddress.ip_address(target):
                    result.update(method="manual", configuredAddresses=[address], persistent=True, gateway=parser.get("Network", "Gateway", fallback=""))
        return result
    except (OSError, subprocess.SubprocessError, ValueError, KeyError, TypeError, configparser.Error):
        return result


def model_network(home=None):
    try:
        config = json.loads(((home or pathlib.Path.home()) / ".openclaw" / "openclaw.json").read_text())
        model = config.get("agents", {}).get("defaults", {}).get("model", "")
        primary = model.get("primary", "") if isinstance(model, dict) else model
        provider = primary.split("/", 1)[0] if isinstance(primary, str) else ""
        base = config.get("models", {}).get("providers", {}).get(provider, {}).get("baseUrl", "")
        if not isinstance(base, str) or not base:
            return "unknown"
        url = urllib.parse.urlsplit(os.path.expandvars(base))
        if url.scheme not in ("http", "https") or not url.hostname:
            return "unknown"
    except (OSError, ValueError, TypeError, AttributeError):
        return "unknown"
    try:
        with socket.create_connection((url.hostname, url.port or (443 if url.scheme == "https" else 80)), timeout=3):
            return "reachable"
    except (OSError, ValueError):
        return "unreachable"


if __name__ == "__main__":
    result = inspect()
    result["modelNetwork"] = model_network()
    print(json.dumps(result))
