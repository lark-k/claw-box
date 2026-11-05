"""First installation of the Quantum gateway and multi-client BLE protocol.

Run as root with a private staging directory containing protocol.py, ble.py,
model.py, gateway.py, gateway.service and model.json. Existing network credentials and SSH keys are
never replaced. A backed-up protocol is restored if service startup fails.
"""
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import tempfile

BASELINE = "758d0dc9dc8ca0abd862fa61fd46c2da9bc74bf34df38984c8ac74057f8e60f6"


def run(*args):
    subprocess.run(args, check=True, timeout=60, stdout=subprocess.DEVNULL)


def install(stage):
    if os.geteuid() != 0:
        raise RuntimeError("Root required")
    stage = Path(stage).resolve(strict=True)
    protocol = Path("/opt/clawbox-network/clawbox_network/protocol.py")
    ble = protocol.with_name("ble.py")
    model = protocol.with_name("model.py")
    service = Path("/etc/systemd/system/quantum-model-gateway.service")
    appdir = Path("/opt/quantum-harness")
    statedir = Path("/var/lib/quantum-harness")
    join_key = Path("/var/lib/clawbox-network/join.key")
    for path in (protocol, ble, model, service, appdir, statedir, join_key):
        if path.is_symlink():
            raise RuntimeError("Installation path is a symlink")
    if any(p.exists() for p in (model, service, appdir, statedir, join_key)):
        raise RuntimeError("Quantum service or join key already exists; use an upgrade procedure")
    if hashlib.sha256(protocol.read_bytes()).hexdigest() != BASELINE:
        raise RuntimeError("Installed network protocol differs from reviewed baseline")
    for name in ("protocol.py", "ble.py", "model.py", "gateway.py", "gateway.service", "model.json"):
        if (stage / name).is_symlink() or not (stage / name).is_file():
            raise RuntimeError("Missing or unsafe staged file")
    run("/usr/bin/python3", "-B", "-m", "py_compile", *(str(stage / name) for name in ("protocol.py", "ble.py", "model.py", "gateway.py")))
    backup_root = Path("/var/backups/quantum-harness")
    backup_root.mkdir(mode=0o700, parents=True, exist_ok=True)
    backup = Path(tempfile.mkdtemp(prefix="network-", dir=backup_root))
    shutil.copy2(protocol, backup / "protocol.py")
    shutil.copy2(ble, backup / "ble.py")
    changed = False
    try:
        appdir.mkdir(mode=0o700)
        statedir.mkdir(mode=0o700)
        shutil.copyfile(stage / "gateway.py", appdir / "model-gateway.py")
        config_fd = os.open(statedir / "model-gateway.json", os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        config = json.loads((stage / "model.json").read_text(encoding="utf-8"))
        config["clientTokensFile"] = "/var/lib/clawbox-network/model-clients.json"
        with os.fdopen(config_fd, "w", encoding="utf-8") as output:
            json.dump(config, output)
        shutil.copyfile(stage / "gateway.service", service)
        key_fd = os.open(join_key, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(key_fd, "w", encoding="ascii") as output:
            output.write(secrets.token_hex(32) + "\n")
        changed = True
        for name, target in (("protocol.py", protocol), ("ble.py", ble), ("model.py", model)):
            temporary = target.with_name(".quantum-" + name)
            if temporary.exists() or temporary.is_symlink():
                raise RuntimeError("Protocol staging path already exists")
            shutil.copy2(stage / name, temporary)
            os.replace(temporary, target)
        run("systemctl", "daemon-reload")
        run("systemctl", "restart", "clawbox-network.service")
        run("systemctl", "is-active", "--quiet", "clawbox-network.service")
        run("systemctl", "enable", "--now", "quantum-model-gateway.service")
        run("systemctl", "is-active", "--quiet", "quantum-model-gateway.service")
    except Exception:
        subprocess.run(["systemctl", "disable", "--now", "quantum-model-gateway.service"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=60)
        if changed:
            shutil.copy2(backup / "protocol.py", protocol)
            shutil.copy2(backup / "ble.py", ble)
            model.unlink(missing_ok=True)
            subprocess.run(["systemctl", "restart", "clawbox-network.service"], timeout=60)
        for path in (service, join_key, statedir / "model-gateway.json", appdir / "model-gateway.py"):
            path.unlink(missing_ok=True)
        for path in (statedir, appdir):
            if path.exists():
                path.rmdir()
        subprocess.run(["systemctl", "daemon-reload"], timeout=60)
        raise
    print("Quantum services installed; network protocol backup:", backup)


if __name__ == "__main__":
    install(sys.argv[1])
