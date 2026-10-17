"""Per-peer bounded request fragments and explicit short ATT response chunks."""

import json
import struct
import time

from .protocol import MAX_MESSAGE, MAX_PEERS, ProtocolError, canonical


class Frames:
    def __init__(self, clock=time.monotonic):
        self.peers = {}
        self.clock = clock

    def drop(self, peer):
        self.peers.pop(peer, None)

    def _peer(self, peer):
        if not isinstance(peer, str) or not peer.startswith("/org/bluez/"):
            raise ProtocolError("invalid_peer")
        for expired in [p for p, state in self.peers.items() if self.clock() - state["time"] > 180]:
            self.drop(expired)
        if peer not in self.peers:
            if len(self.peers) >= MAX_PEERS:
                raise ProtocolError("busy")
            self.peers[peer] = {"buffer": bytearray(), "reply": b"", "offset": 0, "busy": False,
                                "time": self.clock(), "last": 0}
        state = self.peers[peer]
        state["time"] = self.clock()
        return state

    def write(self, peer, data):
        state = self._peer(peer)
        if state["busy"]:
            raise ProtocolError("busy")
        state["buffer"].extend(data)
        if len(state["buffer"]) > MAX_MESSAGE:
            self.drop(peer)
            raise ProtocolError("message_too_large")
        if b"\n" not in state["buffer"]:
            return None
        raw = bytes(state["buffer"])
        state["buffer"].clear()
        if not raw.endswith(b"\n") or b"\n" in raw[:-1]:
            raise ProtocolError("invalid_frame")
        if self.clock() - state["last"] < 0.1:
            raise ProtocolError("rate_limited")
        try:
            message = json.loads(raw)
            if not isinstance(message, dict):
                raise ValueError()
        except (ValueError, UnicodeError):
            raise ProtocolError("invalid_message") from None
        state.update(busy=True, reply=b"", offset=0, last=self.clock())
        return message

    def complete(self, peer, response, expected=None):
        state = self.peers.get(peer)
        if state is None or (expected is not None and state is not expected):
            return
        encoded = canonical(response)
        if len(encoded) > MAX_MESSAGE:
            encoded = canonical({"error": "response_too_large"})
        state.update(busy=False, reply=encoded, offset=0)

    def read(self, peer, mtu=23):
        state = self._peer(peer)
        reply, offset = state["reply"], state["offset"]
        # Shorter than MTU-1, so clients don't perform implicit Read Blob operations.
        count = min(max(int(mtu) - 10, 1), 180)
        chunk = reply[offset:offset + count]
        state["offset"] += len(chunk)
        return struct.pack(">II", len(reply), offset) + chunk
