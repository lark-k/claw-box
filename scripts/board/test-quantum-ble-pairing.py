"""Test two temporary identities over real BLE; never print device credentials."""
import argparse
import asyncio
import getpass
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile

import paramiko
from bleak import BleakClient, BleakScanner
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ed25519

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / "src/ClawBox.Board.Network"))
from clawbox_network.protocol import Client, public_text


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ble = load("provision_client", root / "scripts/board/provision-client.py")
ssh_helpers = load("ssh_test", root / "scripts/board/test-quantum-ssh-pairing.py")


async def pair(device, fingerprint, key, secret):
    client = Client(key, fingerprint, join_secret=secret)
    async with BleakClient(device, timeout=30, winrt={"use_cached_services": False, "address_type": "public"}) as link:
        auth = client.accept(await ble.exchange(link, client.hello))
        response = client.receive(await ble.exchange(link, auth))
        if not response.get("ok"):
            raise RuntimeError("BLE join was rejected")
        result = client.receive(await ble.exchange(link, client.request({"op": "model_setup"})))
        if not result.get("ok") or not result.get("result", {}).get("token"):
            raise RuntimeError("BLE model authorization was rejected")
        return result["result"]["token"]


async def resume(device, fingerprint, key, expected_token):
    client = Client(key, fingerprint)
    async with BleakClient(device, timeout=30, winrt={"use_cached_services": False, "address_type": "public"}) as link:
        auth = client.accept(await ble.exchange(link, client.hello))
        if not client.receive(await ble.exchange(link, auth)).get("ok"):
            raise RuntimeError("BLE resume was rejected")
        result = client.receive(await ble.exchange(link, client.request({"op": "model_setup"})))
        if not result.get("ok") or result["result"]["token"] != expected_token:
            raise RuntimeError("Reconnecting changed the device credential")


async def run(address, fingerprint, secret, keys):
    device = await BleakScanner.find_device_by_address(address, timeout=30)
    if device is None:
        raise RuntimeError("Box was not discovered over BLE within 30 seconds")
    tokens = [await pair(device, fingerprint, key, secret) for key in keys]
    if tokens[0] == tokens[1]:
        raise RuntimeError("BLE identities share a model credential")
    for key, token in zip(keys, tokens):
        await resume(device, fingerprint, key, token)
    print("PASS: two BLE identities paired and independently reconnected")
    return tokens


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("host")
    parser.add_argument("fingerprint")
    parser.add_argument("bluetooth_address")
    parser.add_argument("--dotnet", help="Also exercise the native .NET model tunnel using this dotnet executable")
    args = parser.parse_args()
    admin = paramiko.SSHClient()
    admin.set_missing_host_key_policy(ssh_helpers.Pin(args.fingerprint))
    admin.connect(args.host, 22, "root", password=getpass.getpass("Board administrator password: "),
                  look_for_keys=False, allow_agent=False, timeout=15)
    keys = [ed25519.Ed25519PrivateKey.generate() for _ in range(2)]
    public = [public_text(key.public_key()) for key in keys]
    try:
        with admin.open_sftp() as sftp:
            with sftp.open("/var/lib/clawbox-network/join.key", "r") as stream:
                secret = bytes.fromhex(stream.read().decode("ascii").strip())
        tokens = asyncio.run(run(args.bluetooth_address, args.fingerprint, secret, keys))
        if args.dotnet:
            private_root = Path(os.environ["LOCALAPPDATA"]) / "QuantumHarnessNext"
            private_root.mkdir(parents=True, exist_ok=True)
            with tempfile.TemporaryDirectory(prefix="tunnel-test-", dir=private_root) as temporary:
                identity = Path(temporary) / "identity"
                identity.write_bytes(keys[0].private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.OpenSSH, serialization.NoEncryption()))
                environment = {**os.environ, "QUANTUM_TEST_IDENTITY": str(identity), "QUANTUM_TEST_HOST": args.host,
                               "QUANTUM_TEST_FINGERPRINT": args.fingerprint, "QUANTUM_TEST_GATEWAY_TOKEN": tokens[0],
                               "QUANTUM_TEST_JOIN_SECRET": secret.hex(), "QUANTUM_TEST_BLE_ADDRESS": args.bluetooth_address}
                command = [args.dotnet, "test", str(root / "tests/ClawBox.Desktop.Tests/ClawBox.Desktop.Tests.csproj"),
                           "-c", "Release", "--no-restore", "--filter", "FullyQualifiedName~QuantumModelTunnelTests"]
                result = subprocess.run(command, cwd=root, env=environment, capture_output=True, timeout=120)
                if result.returncode:
                    raise RuntimeError("Native tunnel test failed; check the .NET test result")
                print("PASS: native Windows model tunnel reached the box using a BLE-paired identity")
    finally:
        try:
            ssh_helpers.remote(admin, ssh_helpers.CLEANUP, public)
        finally:
            admin.close()


if __name__ == "__main__":
    main()
