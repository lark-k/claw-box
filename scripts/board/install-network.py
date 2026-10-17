"""Install the fixed board maintenance service from a checked-out source tree."""

import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time


def run(*args):
    subprocess.run(args, check=True, timeout=60)


def install(root):
    if os.geteuid() != 0:
        raise SystemExit("Run as the board administrator.")
    root = Path(root).resolve()
    source = root / "src/ClawBox.Board.Network"
    if not (source / "clawbox_network/ble.py").is_file():
        raise SystemExit("Incomplete source tree.")
    # Fail before touching services when required distro packages are absent.
    import dbus
    import gi
    import cryptography
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
    host = serialization.load_ssh_private_key(Path("/etc/ssh/ssh_host_ed25519_key").read_bytes(), password=None)
    if not isinstance(host, Ed25519PrivateKey):
        raise SystemExit("Missing Ed25519 host identity.")
    target = Path("/opt/clawbox-network")
    stage = Path("/opt/clawbox-network.staging")
    previous = Path("/opt/clawbox-network.previous")
    for path in (target, stage, previous, Path("/root/.ssh"), Path("/etc/avahi/services")):
        if path.is_symlink():
            raise SystemExit("Installation paths must not be symlinks.")
    if stage.exists():
        shutil.rmtree(stage)
    shutil.copytree(source, stage, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    assets = stage / "desktop-assets"
    assets.mkdir()
    for name in ("install-plugin.py", "configure-desktop.py"):
        shutil.copyfile(root / "scripts/board" / name, assets / name)
    for name in ("package.json", "openclaw.plugin.json", "index.js", "client.js"):
        shutil.copyfile(root / "src/ClawBox.OpenClawPlugin" / name, assets / name)
    run("/usr/bin/python3", "-B", "-m", "compileall", "-q", str(stage / "clawbox_network"))
    Path("/root/.ssh").mkdir(mode=0o700, exist_ok=True)
    unit = Path("/etc/systemd/system/clawbox-network.service")
    old_unit = unit.read_bytes() if unit.exists() else None
    avahi = Path("/etc/avahi/services/clawbox.service")
    avahi.parent.mkdir(parents=True, exist_ok=True)
    old_avahi = avahi.read_bytes() if avahi.exists() else None
    identity = host.public_key().public_bytes(serialization.Encoding.OpenSSH, serialization.PublicFormat.OpenSSH)
    device = hashlib.sha256(identity).hexdigest()[:12]
    moved = published = False
    try:
        if previous.exists():
            shutil.rmtree(previous)
        if target.exists():
            run("systemctl", "stop", "clawbox-network.service")
            target.rename(previous)
            moved = True
        stage.rename(target)
        published = True
        unit.write_bytes((source / "clawbox-network.service").read_bytes())
        avahi.write_text('<?xml version="1.0" standalone="no"?>\n<!DOCTYPE service-group SYSTEM "avahi-service.dtd">\n'
                         '<service-group><name>ClawBox-' + device + '</name><service><type>_clawbox._tcp</type>'
                         '<port>22</port><txt-record>version=1</txt-record><txt-record>device=' + device +
                         '</txt-record></service></service-group>\n')
        run("systemctl", "daemon-reload")
        run("systemctl", "enable", "--now", "bluetooth.service", "avahi-daemon.service")
        run("systemctl", "enable", "clawbox-network.service")
        run("systemctl", "restart", "clawbox-network.service")
        time.sleep(3)
        run("systemctl", "is-active", "clawbox-network.service")
    except Exception:
        run("systemctl", "stop", "clawbox-network.service")
        if published and target.exists():
            shutil.rmtree(target)
        if moved and previous.exists():
            previous.rename(target)
        for path, contents in ((unit, old_unit), (avahi, old_avahi)):
            if contents is None:
                path.unlink(missing_ok=True)
            else:
                path.write_bytes(contents)
        run("systemctl", "daemon-reload")
        if old_unit is not None:
            run("systemctl", "start", "clawbox-network.service")
        raise
    print("CLAWBOX_NETWORK=installed")


if __name__ == "__main__":
    install(Path(__file__).resolve().parents[2])
