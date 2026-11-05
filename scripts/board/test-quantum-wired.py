"""Opt-in wired pairing smoke with a temporary identity and exact cleanup."""
import argparse
import getpass
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile

import paramiko

root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("ssh_test", root / "scripts/board/test-quantum-ssh-pairing.py")
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("host")
parser.add_argument("fingerprint")
parser.add_argument("--dotnet", required=True)
args = parser.parse_args()
password = getpass.getpass("Board administrator password: ")
admin = paramiko.SSHClient()
admin.set_missing_host_key_policy(helper.Pin(args.fingerprint))
admin.connect(args.host, 22, "root", password=password, look_for_keys=False, allow_agent=False, timeout=15)
private_root = Path(os.environ["LOCALAPPDATA"]) / "QuantumHarnessNext"
private_root.mkdir(parents=True, exist_ok=True)
try:
    with tempfile.TemporaryDirectory(prefix="wired-test-", dir=private_root) as temporary:
        directory = Path(temporary) / "identity"
        environment = {**os.environ, "QUANTUM_WIRED_TEST_ROOT": str(directory), "QUANTUM_WIRED_TEST_HOST": args.host,
                       "QUANTUM_WIRED_TEST_PASSWORD": password}
        try:
            command = [args.dotnet, "test", str(root / "tests/ClawBox.Desktop.Tests/ClawBox.Desktop.Tests.csproj"),
                       "-c", "Release", "--no-restore", "--filter", "FullyQualifiedName~LiveQuantumWiredTests"]
            result = subprocess.run(command, cwd=root, env=environment, capture_output=True, timeout=180)
            if result.returncode:
                details = (result.stdout + result.stderr).decode(errors="replace").replace(password, "[redacted]")
                raise RuntimeError("Wired test failed:\n" + details[-3000:])
            print("PASS: wired pairing, model tunnel, disconnect and reconnect")
        finally:
            public = directory / "tunnel_ed25519.pub"
            if public.exists():
                parts = public.read_text().split()
                if len(parts) >= 2 and parts[0] == "ssh-ed25519":
                    helper.remote(admin, helper.CLEANUP, [" ".join(parts[:2])])
finally:
    admin.close()
