"""Fixed first-use desktop initialization for an authenticated device owner."""

import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

from .protocol import ProtocolError


class DesktopBootstrap:
    def __init__(self, home=Path("/root"), assets=Path("/opt/clawbox-network/desktop-assets"), run=subprocess.run):
        self.home, self.assets, self.run = Path(home), Path(assets), run

    def _script(self, name, argument):
        # name is a fixed implementation constant; secrets are private files, never shell arguments.
        command = 'source /root/.openclaw/env.sh 2>/dev/null; python3 /opt/clawbox-network/desktop-assets/' + name + ' "$1"'
        result = self.run(["/bin/bash", "-lc", command, "clawbox", str(argument)],
                          check=True, capture_output=True, text=True, timeout=125)
        final = result.stdout.strip().splitlines()[-1] if result.stdout.strip() else ""
        if final not in ("CLAWBOX_RESULT=reused", "CLAWBOX_RESULT=paired", "CLAWBOX_RESULT=installed", "CLAWBOX_RESULT=configured"):
            raise ProtocolError("desktop_setup_failed")
        return final

    def setup(self, owner, request):
        worker = request.get("worker_token")
        if set(request) != {"op", "worker_token"} or not isinstance(worker, str) or not re.fullmatch(r"[A-Fa-f0-9]{64}", worker):
            raise ProtocolError("invalid_worker_token")
        required = ("install-plugin.py", "configure-desktop.py", "package.json", "openclaw.plugin.json", "index.js", "client.js")
        if not all((self.assets / name).is_file() and not (self.assets / name).is_symlink() for name in required):
            raise ProtocolError("desktop_setup_failed")
        root = self.home / ".openclaw/deployment/clawbox-windows"
        if root.is_symlink():
            raise ProtocolError("desktop_setup_failed")
        root.mkdir(mode=0o700, parents=True, exist_ok=True)
        stage = Path(tempfile.mkdtemp(prefix="wireless-", dir=root))
        succeeded = False
        try:
            for name in required[2:]:
                shutil.copyfile(self.assets / name, stage / name)
            for name, text in (("worker-token", worker), ("owner.pub", owner + " clawbox-reverse-tunnel")):
                fd = os.open(stage / name, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
                with os.fdopen(fd, "w") as handle:
                    handle.write(text)
            plugin = self._script("install-plugin.py", stage)
            desktop = self._script("configure-desktop.py", stage / "owner.pub")
            if plugin != "CLAWBOX_RESULT=reused" or desktop == "CLAWBOX_RESULT=configured":
                self.run(["/bin/bash", "-lc", "export XDG_RUNTIME_DIR=/run/user/0 DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/0/bus; systemctl --user restart openclaw-gateway.service"],
                         check=True, capture_output=True, timeout=60)
            secret = self.home / ".openclaw/deployment/quantum-harness-gateway.secrets.json"
            if secret.is_symlink() or secret.stat().st_size > 4096 or secret.stat().st_mode & 0o077:
                raise ProtocolError("desktop_setup_failed")
            token = json.loads(secret.read_text())["token"]
            if not isinstance(token, str) or len(token) < 32 or any(ord(c) < 32 for c in token):
                raise ProtocolError("desktop_setup_failed")
            succeeded = True
            return {"gateway_token": token}
        except (OSError, ValueError, KeyError, subprocess.SubprocessError):
            raise ProtocolError("desktop_setup_failed") from None
        finally:
            # An installed plugin can retain this directory; remove transient public material only.
            (stage / "owner.pub").unlink(missing_ok=True)
            if not succeeded and not (stage / "plugin").exists():
                # Existing installer may reference this stage's worker-token even after a later failure.
                # Keep it private for an explicit repair, never replay on service restart.
                pass
