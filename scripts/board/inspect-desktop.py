"""Read-only setup checks. Return codes and booleans only, never configuration."""
import json
import pathlib
import shutil
import subprocess
import sys


def inspect(home=None, sshd_settings=None):
    home = home or pathlib.Path.home()
    issues = []
    if sys.version_info < (3, 11):
        issues.append("python")
    if not shutil.which("openclaw"):
        issues.append("openclaw")
    if not (home / ".openclaw" / "env.sh").is_file():
        issues.append("environment")
    try:
        config = json.loads((home / ".openclaw" / "openclaw.json").read_text())
        gateway = config.get("gateway", {})
        token = gateway.get("auth", {}).get("token")
        if (gateway.get("bind") != "loopback" or gateway.get("auth", {}).get("mode") != "token"
                or not isinstance(token, str) or len(token) < 32 or token.startswith("${")):
            issues.append("gateway")
        if not isinstance(config.get("tools", {}).get("allow"), list):
            issues.append("policy")
        agents = config.get("agents", {})
        if "list" in agents:
            issues.append("legacy")
        if not agents.get("defaults", {}).get("model"):
            issues.append("model")
    except (OSError, ValueError, TypeError, AttributeError):
        issues.append("config")
    try:
        settings = sshd_settings if sshd_settings is not None else subprocess.check_output(
            ["/usr/sbin/sshd", "-T"], text=True, stderr=subprocess.DEVNULL, timeout=10).splitlines()
        if "gatewayports no" not in settings or "allowtcpforwarding yes" not in settings:
            issues.append("forwarding")
    except (OSError, subprocess.SubprocessError):
        issues.append("forwarding")
    return {"issues": issues}


if __name__ == "__main__":
    print(json.dumps(inspect()))
