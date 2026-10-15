"""Explicit pairing recovery; never kill an unknown or still-connected listener."""
import ipaddress
import json
import os
import pathlib
import signal
import socket
import struct
import sys
import time


def endpoint(value):
    address, port = value.split(":")
    return socket.inet_ntoa(struct.pack("<I", int(address, 16))), int(port, 16)


def sockets(proc):
    rows = {}
    for line in (proc / "net/tcp").read_text().splitlines()[1:]:
        fields = line.split()
        rows[fields[9]] = (endpoint(fields[1]), endpoint(fields[2]), fields[3])
    return rows


def owned_inodes(process):
    result = set()
    for fd in (process / "fd").iterdir():
        try:
            target = os.readlink(fd)
            if target.startswith("socket:["):
                result.add(target[8:-1])
        except FileNotFoundError:
            pass
    return result


def candidate(process, rows, client, active, server_port):
    # Require a per-login root sshd, never the listening daemon or another program.
    command = (process / "cmdline").read_bytes().replace(b"\0", b" ").decode(errors="replace")
    if not command.startswith("sshd: root"):
        return None
    own = owned_inodes(process)
    listeners = {inode for inode in own if rows.get(inode) == (("127.0.0.1", 18791), ("0.0.0.0", 0), "0A")}
    peers = [rows[inode] for inode in own if inode in rows and rows[inode][2] in ("01", "08") and rows[inode][0][1] == server_port]
    if len(listeners) != 1 or len(peers) != 1:
        return None
    peer = peers[0][1]
    if peer[0] != client or peer[1] in active:
        return None
    # Include process start time and exact socket identities in the recheck.
    start = (process / "stat").read_text().rsplit(")", 1)[1].split()[19]
    return start, frozenset(own), frozenset(listeners), peer


def recover(snapshot, connection, proc=pathlib.Path("/proc"), terminate=None, now=None):
    now = time.time() if now is None else now
    client, source, _, server_port = connection.split()
    client = str(ipaddress.IPv4Address(client))
    active = snapshot.get("ports", [])
    if (int(server_port) != snapshot.get("sshPort") or not isinstance(active, list) or not all(type(p) is int and 0 < p < 65536 for p in active)
            or int(source) not in active or not 0 <= now - snapshot.get("at", 0) <= 15):
        return "unverified"
    rows = sockets(proc)
    if not any(row[0][1] == 18791 and row[2] == "0A" for row in rows.values()):
        return "free"
    terminate = terminate or (lambda pid: os.kill(pid, signal.SIGTERM))
    released = False
    for process in proc.iterdir():
        if not process.name.isdigit():
            continue
        try:
            proof = candidate(process, rows, client, active, int(server_port))
            if proof is not None and candidate(process, sockets(proc), client, active, int(server_port)) == proof:
                terminate(int(process.name))
                released = True
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            continue
    return "released" if released else "occupied"


if __name__ == "__main__":
    result = recover(json.loads(pathlib.Path(sys.argv[1]).read_text()), os.environ.get("SSH_CONNECTION", ""))
    print("CLAWBOX_RECOVERY=" + result)
