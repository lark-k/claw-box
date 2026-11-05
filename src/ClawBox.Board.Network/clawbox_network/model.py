"""Issue separate model-gateway credentials to authenticated box clients."""
import hashlib
import json
import os
from pathlib import Path
import secrets

from .protocol import ProtocolError, owner_key, public_text


class ModelBootstrap:
    def __init__(self, config=Path("/var/lib/quantum-harness/model-gateway.json"),
                 clients=Path("/var/lib/clawbox-network/model-clients.json")):
        self.config, self.clients = Path(config), Path(clients)

    def setup(self, owner):
        import fcntl
        normalized = public_text(owner_key(owner))
        identity = hashlib.sha256(normalized.encode("ascii")).hexdigest()
        if self.config.is_symlink() or self.clients.is_symlink():
            raise ProtocolError("model_setup_failed")
        try:
            config = json.loads(self.config.read_text(encoding="utf-8"))
            model = config["model"]
            if not isinstance(model, str) or not model or len(model) > 256:
                raise ValueError()
            with (self.clients.parent / "model-clients.lock").open("a") as lock:
                fcntl.flock(lock, fcntl.LOCK_EX)
                clients = json.loads(self.clients.read_text(encoding="utf-8")) if self.clients.exists() else {}
                if not isinstance(clients, dict) or len(clients) > 1000:
                    raise ValueError()
                token = clients.get(identity)
                if not isinstance(token, str) or len(token) < 32:
                    token = secrets.token_urlsafe(32)
                    clients[identity] = token
                    stage = self.clients.with_name(".model-clients." + secrets.token_hex(8))
                    try:
                        fd = os.open(stage, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
                        with os.fdopen(fd, "w", encoding="utf-8") as output:
                            json.dump(clients, output, separators=(",", ":"))
                            output.flush()
                            os.fsync(output.fileno())
                        os.replace(stage, self.clients)
                    finally:
                        stage.unlink(missing_ok=True)
            result = {"token": token, "model": model, "port": 18792}
            context = config.get("contextWindow")
            if context is not None:
                if type(context) is not int or not 1 <= context <= 10485760:
                    raise ValueError()
                result["contextWindow"] = context
            return result
        except (OSError, ValueError, KeyError, TypeError):
            raise ProtocolError("model_setup_failed") from None
