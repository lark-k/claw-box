"""Upgrade the first Quantum board installation to per-client model tokens."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

BASELINES = {
    "/opt/clawbox-network/clawbox_network/protocol.py": "ad0c08c609f05acfc5416bf1449fa6886fef5be918691749bf88c3c419b0e87b",
    "/opt/clawbox-network/clawbox_network/ble.py": "0c8b5b691833f72d38aa522b8e5bf7503cff68e3657f9b23111fa31eb56d526f",
    "/opt/quantum-harness/model-gateway.py": "172a7e4cd26690c227262a3b6a715c2e4d3e706f78462a87e482d17e20306383",
}


def run(*args):
    subprocess.run(args, check=True, timeout=60, stdout=subprocess.DEVNULL)


def atomic_write(path, content, mode):
    fd, name = tempfile.mkstemp(prefix=".quantum-", dir=path.parent)
    try:
        os.fchmod(fd, mode)
        with os.fdopen(fd, "wb") as output:
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
        os.replace(name, path)
    finally:
        Path(name).unlink(missing_ok=True)


def upgrade(stage):
    if os.geteuid() != 0:
        raise RuntimeError("Root required")
    stage = Path(stage).resolve(strict=True)
    model = Path("/opt/clawbox-network/clawbox_network/model.py")
    config_path = Path("/var/lib/quantum-harness/model-gateway.json")
    if model.exists() or model.is_symlink() or config_path.is_symlink():
        raise RuntimeError("Unexpected existing model module or config")
    for name, digest in BASELINES.items():
        path = Path(name)
        if path.is_symlink() or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise RuntimeError("Installed service differs from reviewed upgrade baseline")
    mapping = {
        Path("/opt/clawbox-network/clawbox_network/protocol.py"): stage / "protocol.py",
        Path("/opt/clawbox-network/clawbox_network/ble.py"): stage / "ble.py",
        Path("/opt/quantum-harness/model-gateway.py"): stage / "gateway.py",
        model: stage / "model.py",
    }
    for source in mapping.values():
        if source.is_symlink() or not source.is_file():
            raise RuntimeError("Missing or unsafe staged file")
    run("/usr/bin/python3", "-B", "-m", "py_compile", *(str(p) for p in mapping.values()))
    backup_root = Path("/var/backups/quantum-harness")
    backup_root.mkdir(mode=0o700, parents=True, exist_ok=True)
    backup = Path(tempfile.mkdtemp(prefix="client-tokens-", dir=backup_root))
    for index, target in enumerate(BASELINES):
        shutil.copy2(target, backup / str(index))
    shutil.copy2(config_path, backup / "config.json")
    try:
        config = json.loads(config_path.read_text(encoding="utf-8"))
        config["clientTokensFile"] = "/var/lib/clawbox-network/model-clients.json"
        for target, source in mapping.items():
            atomic_write(target, source.read_bytes(), 0o600)
        atomic_write(config_path, json.dumps(config).encode(), 0o600)
        run("systemctl", "restart", "clawbox-network.service", "quantum-model-gateway.service")
        run("systemctl", "is-active", "--quiet", "clawbox-network.service")
        run("systemctl", "is-active", "--quiet", "quantum-model-gateway.service")
    except Exception:
        for index, target in enumerate(BASELINES):
            atomic_write(Path(target), (backup / str(index)).read_bytes(), 0o600)
        atomic_write(config_path, (backup / "config.json").read_bytes(), 0o600)
        model.unlink(missing_ok=True)
        subprocess.run(["systemctl", "restart", "clawbox-network.service", "quantum-model-gateway.service"], timeout=60)
        raise
    print("Per-client credential support installed; backup:", backup)


if __name__ == "__main__":
    upgrade(sys.argv[1])
