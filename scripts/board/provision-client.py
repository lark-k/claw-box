"""Engineering BLE client; passwords are prompted, never accepted on argv."""

import argparse
import asyncio
import getpass
import json
from pathlib import Path
import struct
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "ClawBox.Board.Network"))
from clawbox_network import REQUEST_UUID, RESPONSE_UUID, SERVICE_UUID
from clawbox_network.protocol import Client, MAX_MESSAGE, ProtocolError, canonical
from cryptography.hazmat.primitives import serialization


async def exchange(transport, message):
    raw = canonical(message) + b"\n"
    if len(raw) > MAX_MESSAGE:
        raise ProtocolError("message_too_large")
    # Respect the negotiated MTU, falling back to ATT's minimum 20-byte payload.
    count = min(180, max(20, transport.mtu_size - 3))
    for offset in range(0, len(raw), count):
        await transport.write_gatt_char(REQUEST_UUID, raw[offset:offset + count], response=True)
    result, total = bytearray(), None
    deadline = time.monotonic() + 65
    while time.monotonic() < deadline:
        raw = bytes(await transport.read_gatt_char(RESPONSE_UUID, use_cached=False))
        if len(raw) < 8:
            raise ProtocolError("invalid_response_frame")
        length, offset = struct.unpack(">II", raw[:8])
        if length == 0:
            await asyncio.sleep(0.12)
            continue
        if length > MAX_MESSAGE or offset != len(result) or (total is not None and total != length):
            raise ProtocolError("invalid_response_frame")
        total = length
        result.extend(raw[8:])
        if len(result) == total:
            await asyncio.sleep(0.12)
            return json.loads(result)
        if len(result) > total or len(raw) == 8:
            raise ProtocolError("invalid_response_frame")
    raise ProtocolError("response_timeout")


async def run(args):
    from bleak import BleakClient, BleakScanner
    if args.action == "discover":
        discovered = await BleakScanner.discover(timeout=10, return_adv=True, service_uuids=[SERVICE_UUID])
        devices = [(device, adv) for device, adv in discovered.values()
                   if SERVICE_UUID in [u.lower() for u in adv.service_uuids]]
        return [{"name": adv.local_name, "address": device.address, "rssi": adv.rssi} for device, adv in devices]
    selected = await BleakScanner.find_device_by_address(args.address, timeout=15)
    if selected is None:
        raise ProtocolError("select_discovered_device")
    key = serialization.load_ssh_private_key(Path(args.key).read_bytes(), password=None)
    client = Client(key, args.fingerprint, claim=args.claim)
    request = {"op": args.action}
    if args.action == "configure":
        request.update(ssid=args.ssid, password=getpass.getpass("Wi-Fi password: "), security=args.security,
                       hidden=args.hidden)
    async with BleakClient(selected, timeout=30, winrt={"use_cached_services": False, "address_type": "public"}) as transport:
        authentication = client.accept(await exchange(transport, client.hello))
        result = client.receive(await exchange(transport, authentication))
        if not result.get("ok"):
            return result
        if args.action == "configure" and not args.hidden:
            scanned = client.receive(await exchange(transport, client.request({"op": "scan"})))
            matches = [n for n in scanned.get("result", {}).get("networks", []) if n["ssid"] == args.ssid]
            if len(matches) != 1:
                raise ProtocolError("select_visible_network")
            request["ssid_bytes"] = matches[0]["ssid_bytes"]
        return client.receive(await exchange(transport, client.request(request)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["discover", "status", "scan", "configure"])
    parser.add_argument("--address")
    parser.add_argument("--key", help="Existing dedicated OpenSSH Ed25519 private key")
    parser.add_argument("--fingerprint", help="Previously trusted SSH host fingerprint")
    parser.add_argument("--claim", action="store_true", help="Explicit first-use selection; never replaces a binding")
    parser.add_argument("--ssid")
    parser.add_argument("--security", choices=["wpa-psk", "open"], default="wpa-psk")
    parser.add_argument("--hidden", action="store_true")
    args = parser.parse_args()
    if args.action != "discover" and (not args.key or not args.address or (not args.fingerprint and not args.claim)):
        parser.error("Select a device and supply the owner key plus trusted fingerprint (or explicit --claim).")
    if args.action == "configure" and not args.ssid:
        parser.error("configure requires --ssid")
    try:
        result = asyncio.run(run(args))
        print(json.dumps(result, ensure_ascii=True))
        return 1 if isinstance(result, dict) and result.get("ok") is False else 0
    except ProtocolError as error:
        print(json.dumps({"error": error.code}))
        return 1
    except Exception:
        print(json.dumps({"error": "bluetooth_operation_failed"}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
