"""Tune the RK3568's existing BlueZ legacy LE advertising interval."""
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

PATH = Path("/etc/bluetooth/main.conf")
VALUES = {"MinAdvertisementInterval": 160, "MaxAdvertisementInterval": 240}


def revised_config(source):
    """Keep other settings and reject conflicting administrator choices."""
    lines = source.splitlines(keepends=True)
    sections = [i for i, line in enumerate(lines) if re.fullmatch(r"\s*\[LE\]\s*", line.strip())]
    if len(sections) != 1:
        raise ValueError("Expected exactly one [LE] section")
    start = sections[0] + 1
    end = next((i for i in range(start, len(lines)) if re.match(r"\s*\[", lines[i])), len(lines))
    active = {}
    for line in lines[start:end]:
        match = re.match(r"\s*(MinAdvertisementInterval|MaxAdvertisementInterval)\s*=\s*(\S+)\s*(?:[#;].*)?$", line)
        if match:
            name, value = match.groups()
            if name in active or int(value, 0) != VALUES[name]:
                raise ValueError("Existing advertising interval requires administrator review")
            active[name] = True
    missing = [name for name in VALUES if name not in active]
    lines[start:start] = [f"{name}=0x{VALUES[name]:04x}\n" for name in missing]
    return "".join(lines)


def run(*args):
    subprocess.run(args, check=True, timeout=60, stdout=subprocess.DEVNULL)


def atomic(path, data, mode):
    fd, name = tempfile.mkstemp(prefix=".quantum-ble-", dir=path.parent)
    try:
        os.fchmod(fd, mode)
        with os.fdopen(fd, "wb") as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        os.replace(name, path)
    finally:
        Path(name).unlink(missing_ok=True)


def applied():
    return [int(Path("/sys/kernel/debug/bluetooth/hci0/adv_" + name + "_interval").read_text())
            for name in ("min", "max")] == [160, 240]


def main():
    if os.geteuid() != 0 or PATH.is_symlink():
        raise RuntimeError("Root and a regular BlueZ configuration are required")
    original = PATH.read_bytes()
    changed = revised_config(original.decode("utf-8")).encode("utf-8")
    if changed == original:
        if not applied():
            run("systemctl", "restart", "bluetooth.service")
            run("systemctl", "restart", "clawbox-network.service")
            if not applied():
                raise RuntimeError("Kernel did not apply the BlueZ advertising intervals")
        print("BlueZ advertising configuration already present")
        return
    backup_root = Path("/var/backups/quantum-harness")
    if backup_root.is_symlink():
        raise RuntimeError("Backup path is a symlink")
    backup_root.mkdir(mode=0o700, parents=True, exist_ok=True)
    backup = Path(tempfile.mkdtemp(prefix="ble-advertising-", dir=backup_root))
    shutil.copy2(PATH, backup / "main.conf")
    mode = PATH.stat().st_mode & 0o777
    try:
        atomic(PATH, changed, mode)
        run("systemctl", "restart", "bluetooth.service")
        run("systemctl", "restart", "clawbox-network.service")
        run("systemctl", "is-active", "--quiet", "clawbox-network.service")
        if not applied():
            raise RuntimeError("Kernel did not apply the BlueZ advertising intervals")
    except Exception:
        atomic(PATH, original, mode)
        subprocess.run(["systemctl", "restart", "bluetooth.service"], timeout=60)
        subprocess.run(["systemctl", "restart", "clawbox-network.service"], timeout=60)
        raise
    print("BlueZ advertising configured; backup:", backup)


if __name__ == "__main__":
    main()
