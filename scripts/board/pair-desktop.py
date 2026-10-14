"""Install only a dedicated restricted tunnel key; preserve all other keys."""
import argparse
import os
import pathlib
import re
import subprocess


def pair(public, home=None, settings=None):
    if not re.fullmatch(r"ssh-ed25519 [A-Za-z0-9+/=]+ clawbox-reverse-tunnel", public):
        raise SystemExit("Invalid dedicated public key.")
    settings = settings if settings is not None else subprocess.check_output(
        ["/usr/sbin/sshd", "-T"], text=True, timeout=10).splitlines()
    if "gatewayports no" not in settings or "allowtcpforwarding yes" not in settings:
        raise SystemExit("Unsafe SSH forwarding configuration.")
    root = (home or pathlib.Path.home()) / ".ssh"
    root.mkdir(mode=0o700, exist_ok=True)
    os.chmod(root, 0o700)
    path = root / "authorized_keys"
    line = ('no-agent-forwarding,no-X11-forwarding,no-pty,no-user-rc,command="/bin/false",'
            'permitopen="127.0.0.1:18791",permitlisten="127.0.0.1:18791" ' + public)
    existing = path.read_text() if path.exists() else ""
    matches = [item for item in existing.splitlines() if public.split()[1] in item]
    # A successful older attempt may already have added the gateway permitopen.
    extended = 'permitopen="127.0.0.1:18789",' + line
    if matches and matches != [line] and matches != [extended]:
        raise SystemExit("Existing key has different permissions.")
    if not matches:
        with path.open("a") as handle:
            handle.write(("\n" if existing and not existing.endswith("\n") else "") + line + "\n")
    os.chmod(path, 0o600)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("public_key", type=pathlib.Path)
    args = parser.parse_args()
    pair(args.public_key.read_text().strip())
