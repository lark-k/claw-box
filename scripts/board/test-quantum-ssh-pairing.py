"""Exercise two disposable restricted SSH tunnels without modifying existing pairs.

Run from a developer PC with paramiko and cryptography installed; the board
administrator password is prompted and never stored. The test appends only two
random keys and removes exactly those keys and tokens in a finally block.
"""
import argparse
import base64
import getpass
import hashlib
import http.client
import io
import json
import shlex
import sys

import paramiko
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ed25519

REGISTER = r'''
import json, sys
sys.path.insert(0, "/opt/clawbox-network")
from clawbox_network.protocol import Owners
from clawbox_network.model import ModelBootstrap
keys = json.load(sys.stdin)
for key in keys:
    Owners().join(key)
print(json.dumps([ModelBootstrap().setup(key)["token"] for key in keys]))
'''
CLEANUP = r'''
import fcntl, hashlib, json, os, secrets, sys
from pathlib import Path
keys=json.load(sys.stdin)
auth=Path("/root/.ssh/authorized_keys")
state=Path("/var/lib/clawbox-network")
store=state/"model-clients.json"
with (state/"enrollment.lock").open("a") as lock:
    fcntl.flock(lock,fcntl.LOCK_EX)
    lines=auth.read_text().splitlines(keepends=True)
    lines=[line for line in lines if not any(line.rstrip("\r\n").endswith(" "+key+" clawbox-reverse-tunnel") for key in keys)]
    temp=auth.with_name(".test-authorized-"+secrets.token_hex(8))
    try:
        fd=os.open(temp,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
        with os.fdopen(fd,"w") as out:
            out.writelines(lines); out.flush(); os.fsync(out.fileno())
        os.replace(temp,auth)
    finally:
        temp.unlink(missing_ok=True)
with (state/"model-clients.lock").open("a") as lock:
    fcntl.flock(lock,fcntl.LOCK_EX)
    if store.exists():
        data=json.loads(store.read_text())
        for key in keys: data.pop(hashlib.sha256(key.encode("ascii")).hexdigest(),None)
        temp=store.with_name(".test-tokens-"+secrets.token_hex(8))
        try:
            fd=os.open(temp,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
            with os.fdopen(fd,"w") as out:
                json.dump(data,out); out.flush(); os.fsync(out.fileno())
            os.replace(temp,store)
        finally:
            temp.unlink(missing_ok=True)
'''


def remote(client, source, keys):
    code = base64.b64encode(source.encode()).decode()
    command = "python3 -c " + shlex.quote("import base64;exec(base64.b64decode('" + code + "'))")
    stdin, stdout, stderr = client.exec_command(command, timeout=30)
    stdin.write(json.dumps(keys))
    stdin.channel.shutdown_write()
    result = stdout.read()
    error = stderr.read()
    if stdout.channel.recv_exit_status() != 0:
        raise RuntimeError("Board test operation failed: " + error.decode(errors="replace")[:300])
    return result


class Pin(paramiko.MissingHostKeyPolicy):
    def __init__(self, expected):
        self.expected = expected

    def missing_host_key(self, _client, _host, key):
        actual = "SHA256:" + base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip("=")
        if actual != self.expected:
            raise RuntimeError("Board host fingerprint does not match")


def request(ssh, token):
    channel = ssh.get_transport().open_channel("direct-tcpip", ("127.0.0.1", 18792), ("127.0.0.1", 0), timeout=15)
    connection = http.client.HTTPConnection("127.0.0.1", 18792, timeout=40)
    connection.sock = channel
    try:
        connection.request("GET", "/v1/models", headers={"Authorization": "Bearer " + token})
        response = connection.getresponse()
        response.read()
        return response.status
    finally:
        connection.close()
        channel.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("host")
    parser.add_argument("fingerprint")
    parser.add_argument("--port", type=int, default=22)
    args = parser.parse_args()
    admin = paramiko.SSHClient()
    admin.set_missing_host_key_policy(Pin(args.fingerprint))
    admin.connect(args.host, args.port, "root", password=getpass.getpass("Board administrator password: "),
                  look_for_keys=False, allow_agent=False, timeout=15)
    private = [ed25519.Ed25519PrivateKey.generate() for _ in range(2)]
    public = [key.public_key().public_bytes(serialization.Encoding.OpenSSH, serialization.PublicFormat.OpenSSH).decode() for key in private]
    clients = []
    try:
        tokens = json.loads(remote(admin, REGISTER, public))
        for key in private:
            encoded = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.OpenSSH, serialization.NoEncryption()).decode()
            client = paramiko.SSHClient()
            client.set_missing_host_key_policy(Pin(args.fingerprint))
            client.connect(args.host, args.port, "root", pkey=paramiko.Ed25519Key.from_private_key(io.StringIO(encoded)),
                           look_for_keys=False, allow_agent=False, timeout=15)
            clients.append(client)
        if request(clients[0], tokens[0]) != 200 or request(clients[1], tokens[1]) != 200:
            raise RuntimeError("Both independent SSH tunnels must reach the model gateway")
        if request(clients[0], "invalid-test-token") != 401:
            raise RuntimeError("Gateway accepted an invalid token")
        print("PASS: two independent restricted SSH tunnels reached the box model gateway")
    finally:
        for client in clients:
            client.close()
        try:
            remote(admin, CLEANUP, public)
        finally:
            admin.close()


if __name__ == "__main__":
    main()
