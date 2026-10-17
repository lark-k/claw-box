"""Versioned, authenticated provisioning protocol shared by service and test client."""

import base64
import hashlib
import json
import os
import secrets
import time
from dataclasses import dataclass
from pathlib import Path

from cryptography.exceptions import InvalidSignature, InvalidTag
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ed25519, x25519
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

PREFIX = b"clawbox-network-v1\0"
MAX_MESSAGE = 8192
SESSION_SECONDS = 180
MAX_PEERS = 8


class ProtocolError(Exception):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode()


def encode(value):
    return base64.b64encode(value).decode("ascii")


def decode(value, length=None):
    try:
        if not isinstance(value, str) or len(value) > MAX_MESSAGE:
            raise ValueError()
        raw = base64.b64decode(value, validate=True)
        if length is not None and len(raw) != length:
            raise ValueError()
        return raw
    except (ValueError, TypeError):
        raise ProtocolError("invalid_message") from None


def public_text(key):
    return key.public_bytes(serialization.Encoding.OpenSSH, serialization.PublicFormat.OpenSSH).decode()


def owner_key(value):
    if not isinstance(value, str) or len(value) > 256 or "\n" in value or "\r" in value:
        raise ProtocolError("invalid_owner_key")
    try:
        key = serialization.load_ssh_public_key(value.encode())
        if not isinstance(key, ed25519.Ed25519PublicKey):
            raise ValueError()
        return key
    except (ValueError, TypeError):
        raise ProtocolError("invalid_owner_key") from None


def fingerprint(public):
    return "SHA256:" + encode(hashlib.sha256(base64.b64decode(public.split()[1])).digest()).rstrip("=")


def derive(secret, transcript):
    material = HKDF(algorithm=hashes.SHA256(), length=64,
                    salt=hashlib.sha256(transcript).digest(), info=PREFIX).derive(secret)
    return material[:32], material[32:]


def seal(key, session, direction, sequence, value):
    aad = canonical({"session": session, "direction": direction, "seq": sequence})
    cipher = AESGCM(key).encrypt(sequence.to_bytes(12, "big"), canonical(value), aad)
    return {"session": session, "seq": sequence, "ciphertext": encode(cipher)}


def unseal(key, session, direction, sequence, value):
    if (not isinstance(value, dict) or value.get("session") != session or
            type(value.get("seq")) is not int or value["seq"] != sequence):
        raise ProtocolError("invalid_sequence")
    aad = canonical({"session": session, "direction": direction, "seq": sequence})
    try:
        plain = AESGCM(key).decrypt(sequence.to_bytes(12, "big"), decode(value.get("ciphertext")), aad)
        result = json.loads(plain)
        if not isinstance(result, dict):
            raise ValueError()
        return result
    except (InvalidTag, ValueError, OverflowError):
        raise ProtocolError("invalid_ciphertext") from None


class Owners:
    """Reuse restricted tunnel keys; never accept generic root administration keys."""

    def __init__(self, authorized=Path("/root/.ssh/authorized_keys"),
                 marker=Path("/var/lib/clawbox-network/enrolled")):
        self.authorized, self.marker = Path(authorized), Path(marker)

    def keys(self):
        import re
        result = set()
        if self.authorized.is_symlink():
            raise ProtocolError("unsafe_owner_store")
        for line in self.authorized.read_text().splitlines() if self.authorized.exists() else []:
            match = re.search(r"(?:^| )ssh-ed25519 ([A-Za-z0-9+/=]+) clawbox-reverse-tunnel$", line)
            if match and 'command="/bin/false"' in line and "no-pty" in line:
                value = "ssh-ed25519 " + match[1]
                owner_key(value)
                result.add(value)
        return result

    def enrollment_open(self):
        return not self.marker.exists() and not self.keys()

    def remember_existing(self):
        if self.keys():
            self._mark()

    def _mark(self):
        self.marker.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        descriptor = os.open(self.marker, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600) if not self.marker.exists() else None
        if descriptor is not None:
            os.close(descriptor)

    def claim(self, public):
        import fcntl
        normalized = public_text(owner_key(public))
        self.marker.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        with (self.marker.parent / "enrollment.lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            if not self.enrollment_open():
                raise ProtocolError("already_bound")
            self.authorized.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            if self.authorized.parent.is_symlink() or self.authorized.is_symlink():
                raise ProtocolError("unsafe_owner_store")
            existing = self.authorized.read_text() if self.authorized.exists() else ""
            line = ('no-agent-forwarding,no-X11-forwarding,no-pty,no-user-rc,command="/bin/false",'
                    'permitopen="127.0.0.1:18789",permitopen="127.0.0.1:18791",'
                    'permitlisten="127.0.0.1:18791" ' + normalized + " clawbox-reverse-tunnel\n")
            # Mark first: an interrupted enrollment stays closed instead of reopening to strangers.
            self._mark()
            stage = self.authorized.with_name(".authorized_keys." + secrets.token_hex(8))
            try:
                fd = os.open(stage, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
                with os.fdopen(fd, "w") as handle:
                    handle.write(existing + ("\n" if existing and not existing.endswith("\n") else "") + line)
                    handle.flush()
                    os.fsync(handle.fileno())
                os.replace(stage, self.authorized)
            finally:
                stage.unlink(missing_ok=True)


@dataclass
class Session:
    identifier: str
    owner: str
    mode: str
    transcript: bytes
    incoming: bytes
    outgoing: bytes
    created: float
    sequence: int = 0
    authenticated: bool = False


class Service:
    def __init__(self, host_key, owners, network, clock=time.monotonic, desktop=None):
        if not isinstance(host_key, ed25519.Ed25519PrivateKey):
            raise ValueError("An Ed25519 SSH host key is required")
        self.host_key, self.owners, self.network, self.clock = host_key, owners, network, clock
        self.public = public_text(host_key.public_key())
        self.identity = fingerprint(self.public)
        self.sessions = {}
        self.desktop = desktop

    def disconnect(self, peer):
        self.sessions.pop(peer, None)

    def info(self):
        return {"version": 1, "identity": self.identity, "enrollment": self.owners.enrollment_open()}

    def exchange(self, peer, value):
        if not isinstance(value, dict) or len(canonical(value)) > MAX_MESSAGE:
            raise ProtocolError("invalid_message")
        if value.get("op") == "hello":
            return self._hello(peer, value)
        session = self.sessions.get(peer)
        if session is None or self.clock() - session.created > SESSION_SECONDS:
            self.disconnect(peer)
            raise ProtocolError("session_expired")
        sequence = session.sequence
        try:
            request = unseal(session.incoming, session.identifier, "client", sequence, value)
        except ProtocolError:
            self.disconnect(peer)
            raise
        # Consume before dispatch, including failures: reconnect never replays a write.
        session.sequence += 1
        try:
            if not session.authenticated:
                if sequence != 0 or request.get("op") != "authenticate":
                    raise ProtocolError("authentication_required")
                try:
                    owner_key(session.owner).verify(decode(request.get("proof"), 64),
                                                    PREFIX + hashlib.sha256(session.transcript).digest())
                except InvalidSignature:
                    raise ProtocolError("invalid_signature") from None
                if session.mode == "claim":
                    self.owners.claim(session.owner)
                elif session.owner not in self.owners.keys():
                    raise ProtocolError("not_owner")
                session.authenticated = True
                result = {"authenticated": True, "identity": self.identity}
            else:
                if session.owner not in self.owners.keys():
                    raise ProtocolError("not_owner")
                operation = request.get("op")
                if operation == "status":
                    result = self.network.status()
                elif operation == "scan":
                    result = self.network.scan()
                elif operation == "configure":
                    result = self.network.configure(request)
                elif operation == "connect_saved":
                    result = self.network.connect_saved(request)
                elif operation == "desktop_setup" and self.desktop is not None:
                    result = self.desktop.setup(session.owner, request)
                else:
                    raise ProtocolError("unsupported_operation")
            return seal(session.outgoing, session.identifier, "server", sequence, {"ok": True, "result": result})
        except ProtocolError as error:
            return seal(session.outgoing, session.identifier, "server", sequence, {"ok": False, "error": error.code})
        except Exception:
            # D-Bus exceptions can contain SSID/PSK: never log or return exception text.
            return seal(session.outgoing, session.identifier, "server", sequence, {"ok": False, "error": "operation_failed"})

    def _hello(self, peer, request):
        for expired in [p for p, s in self.sessions.items() if self.clock() - s.created > SESSION_SECONDS]:
            self.disconnect(expired)
        if peer not in self.sessions and len(self.sessions) >= MAX_PEERS:
            raise ProtocolError("busy")
        if request.get("version") != 1 or request.get("mode") not in ("claim", "resume"):
            raise ProtocolError("invalid_message")
        owner = public_text(owner_key(request.get("owner")))
        if request["mode"] == "claim" and not self.owners.enrollment_open():
            raise ProtocolError("already_bound")
        if request["mode"] == "resume" and owner not in self.owners.keys():
            raise ProtocolError("not_owner")
        decode(request.get("nonce"), 32)
        try:
            ephemeral = x25519.X25519PublicKey.from_public_bytes(decode(request.get("ephemeral"), 32))
            server = x25519.X25519PrivateKey.generate()
            secret = server.exchange(ephemeral)
        except ValueError:
            raise ProtocolError("invalid_message") from None
        transcript = {"hello": request, "session": secrets.token_hex(16), "host_key": self.public,
                      "identity": self.identity, "nonce": encode(secrets.token_bytes(32)),
                      "ephemeral": encode(server.public_key().public_bytes(
                          serialization.Encoding.Raw, serialization.PublicFormat.Raw))}
        encoded = canonical(transcript)
        incoming, outgoing = derive(secret, encoded)
        self.sessions[peer] = Session(transcript["session"], owner, request["mode"], encoded,
                                      incoming, outgoing, self.clock())
        return {"transcript": transcript, "signature": encode(self.host_key.sign(PREFIX + encoded))}


class Client:
    """No transport or UI; callers must explicitly select a device on first use."""

    def __init__(self, key, expected_identity, claim=False):
        self.key, self.expected = key, expected_identity
        self.ephemeral = x25519.X25519PrivateKey.generate()
        self.hello = {"op": "hello", "version": 1, "mode": "claim" if claim else "resume",
                      "owner": public_text(key.public_key()), "nonce": encode(secrets.token_bytes(32)),
                      "ephemeral": encode(self.ephemeral.public_key().public_bytes(
                          serialization.Encoding.Raw, serialization.PublicFormat.Raw))}
        self.sequence = 0

    def accept(self, response):
        try:
            transcript = response["transcript"]
            public = transcript["host_key"]
            if (transcript["hello"] != self.hello or fingerprint(public) != transcript["identity"] or
                    (self.expected is not None and transcript["identity"] != self.expected)):
                raise ProtocolError("identity_mismatch")
            encoded = canonical(transcript)
            owner_key(public).verify(decode(response["signature"], 64), PREFIX + encoded)
            secret = self.ephemeral.exchange(x25519.X25519PublicKey.from_public_bytes(decode(transcript["ephemeral"], 32)))
            self.outgoing, self.incoming = derive(secret, encoded)
            self.session = transcript["session"]
            self.identity = transcript["identity"]
            return self.request({"op": "authenticate", "proof": encode(
                self.key.sign(PREFIX + hashlib.sha256(encoded).digest()))})
        except (KeyError, ValueError, InvalidSignature):
            raise ProtocolError("invalid_handshake") from None

    def request(self, value):
        return seal(self.outgoing, self.session, "client", self.sequence, value)

    def receive(self, value):
        result = unseal(self.incoming, self.session, "server", self.sequence, value)
        self.sequence += 1
        return result
