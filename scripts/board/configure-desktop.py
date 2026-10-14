"""Enable private desktop chat ingress and extend only the existing tunnel key."""
import argparse
import json
import os
import pathlib
import re
import subprocess
import tempfile

WINDOWS_TOOLS = ["windows_file_read", "windows_file_write", "windows_word_read",
                 "windows_word_create", "windows_word_replace", "windows_project_list", "windows_project_check", "windows_project_command",
                 "windows_directory_create", "windows_directory_list", "windows_file_info", "windows_file_search",
                 "windows_file_copy", "windows_file_move", "windows_file_replace", "windows_word_format"]
NATIVE_TOOLS = ["read", "write", "edit", "apply_patch", "exec", "process", "browser", "web_fetch", "web_search",
                "sessions_spawn", "sessions_send", "sessions_history", "agents_list", "subagents", "gateway", "nodes", "cron"]


def configure_agent(config, home):
    # The brain remains on this board. Only its effectful tools target Windows.
    agents = config.setdefault("agents", {})
    if "list" in agents:
        raise SystemExit("Legacy agent roster found; migrate with the installed OpenClaw before configuring desktop.")
    entries = agents.setdefault("entries", {})
    if not entries:
        entries["main"] = {}  # Preserve the implicit main agent as the default.
    agents["ownership"] = "explicit"
    defaults = agents.setdefault("defaults", {})
    defaults["timeoutSeconds"] = 0  # APP owns the inactivity deadline; no whole-turn cutoff.
    workspace = str(home / ".openclaw" / "workspace-quantum-harness")
    old = entries.get("quantum-harness", {})
    if old.get("workspace", workspace) != workspace:
        raise SystemExit("Existing Quantum Harness agent has an unmanaged workspace.")
    allow = config.get("tools", {}).get("allow", [])
    if not set(WINDOWS_TOOLS).issubset(allow):
        raise SystemExit("Install all Windows plugin tools before configuring the dedicated desktop agent.")
    entries["quantum-harness"] = {**old, "name": "Quantum Harness", "workspace": workspace,
                                   "tools": {"allow": WINDOWS_TOOLS, "deny": NATIVE_TOOLS}}


def atomic(path, text):
    with tempfile.NamedTemporaryFile(dir=path.parent, mode="w", encoding="utf-8", delete=False) as handle:
        os.chmod(handle.name, 0o600)
        handle.write(text)
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(handle.name, path)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("public_key", type=pathlib.Path)
    args = parser.parse_args()
    public = args.public_key.read_text().strip()
    if not re.fullmatch(r"ssh-ed25519 [A-Za-z0-9+/=]+ clawbox-reverse-tunnel", public):
        raise SystemExit("Wrong dedicated public key.")
    home = pathlib.Path.home()
    keys = home / ".ssh" / "authorized_keys"
    original_keys = keys.read_text()
    lines = original_keys.splitlines()
    matching = [i for i, line in enumerate(lines) if line.endswith(public)]
    if len(matching) != 1:
        raise SystemExit("Expected exactly one paired restricted key.")
    index = matching[0]
    if not all(option in lines[index] for option in ['command="/bin/false"', "no-pty", "no-agent-forwarding", "permitlisten="]):
        raise SystemExit("Refusing to extend an unrestricted key.")
    if 'permitopen="127.0.0.1:18789"' not in lines[index]:
        lines[index] = 'permitopen="127.0.0.1:18789",' + lines[index]
    config_path = home / ".openclaw" / "openclaw.json"
    original = config_path.read_text()
    config = json.loads(original)
    gateway = config["gateway"]
    if gateway.get("bind") != "loopback" or gateway.get("auth", {}).get("mode") != "token":
        raise SystemExit("Require authenticated loopback gateway.")
    token = gateway["auth"].get("token")
    if not isinstance(token, str) or len(token) < 32 or token.startswith(chr(36) + "{"):
        raise SystemExit("Provision a literal gateway token in the existing private configuration first.")
    gateway.setdefault("http", {}).setdefault("endpoints", {}).setdefault("chatCompletions", {})["enabled"] = True
    configure_agent(config, home)
    atomic(config_path, json.dumps(config, ensure_ascii=False, indent=2))
    try:
        subprocess.run(["openclaw", "config", "validate"], check=True, timeout=120)
        atomic(keys, "\n".join(lines) + "\n")
        secret = home / ".openclaw" / "deployment" / "quantum-harness-gateway.secrets.json"
        atomic(secret, json.dumps({"token": token}))
    except (subprocess.SubprocessError, OSError):
        atomic(config_path, original)
        atomic(keys, original_keys)
        raise SystemExit("Desktop configuration failed; original settings restored.")
    print("Authenticated desktop ingress configured. Gateway credential exported privately.")


if __name__ == "__main__":
    main()
