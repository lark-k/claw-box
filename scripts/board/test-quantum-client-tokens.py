"""Exercise two disposable client tokens against the installed board gateway."""
import fcntl
import hashlib
import http.client
import json
import os
from pathlib import Path
import secrets
import sys

sys.path.insert(0, "/opt/clawbox-network")
from cryptography.hazmat.primitives.asymmetric import ed25519
from clawbox_network.model import ModelBootstrap
from clawbox_network.protocol import public_text

store = Path("/var/lib/clawbox-network/model-clients.json")
owners = [public_text(ed25519.Ed25519PrivateKey.generate().public_key()) for _ in range(2)]
ids = [hashlib.sha256(owner.encode("ascii")).hexdigest() for owner in owners]


def status(token):
    connection = http.client.HTTPConnection("127.0.0.1", 18792, timeout=30)
    try:
        connection.request("GET", "/v1/models", headers={"Authorization": "Bearer " + token})
        response = connection.getresponse()
        response.read()
        return response.status
    finally:
        connection.close()


def remove(ids_to_remove):
    with (store.parent / "model-clients.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if not store.exists():
            return
        data = json.loads(store.read_text(encoding="utf-8"))
        for item in ids_to_remove:
            data.pop(item, None)
        temporary = store.with_name(".test-clients." + secrets.token_hex(8))
        try:
            fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as output:
                json.dump(data, output)
                output.flush()
                os.fsync(output.fileno())
            os.replace(temporary, store)
        finally:
            temporary.unlink(missing_ok=True)


try:
    a, b = [ModelBootstrap().setup(owner)["token"] for owner in owners]
    if a == b or status(a) != 200 or status(b) != 200:
        raise RuntimeError("Independent clients did not both reach the model")
    remove(ids[:1])
    if status(a) != 401 or status(b) != 200:
        raise RuntimeError("Revoking one client affected another or did not take effect")
    print("PASS: two client credentials, independent revocation, and upstream model access")
finally:
    remove(ids)
