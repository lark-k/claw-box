"""Install checked-in tools into private OpenClaw state; never print credentials."""
import argparse
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import tempfile
import time

PLUGIN_ID = "clawbox-windows"
TOOLS = ["windows_file_read", "windows_file_write", "windows_word_read",
         "windows_word_create", "windows_word_replace", "windows_project_list", "windows_project_check", "windows_project_command",
         "windows_directory_create", "windows_directory_list", "windows_file_info", "windows_file_search",
         "windows_file_copy", "windows_file_move", "windows_file_replace", "windows_word_format"]
FILES = ("package.json", "openclaw.plugin.json", "index.js", "client.js")


def atomic_json(path, value):
    with tempfile.NamedTemporaryFile(dir=path.parent, mode="w", encoding="utf-8", delete=False) as handle:
        os.chmod(handle.name, 0o600)
        json.dump(value, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(handle.name, path)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("staging", type=pathlib.Path)
    parser.add_argument("--port", type=int, default=18791)
    args = parser.parse_args()
    home = pathlib.Path.home() / ".openclaw"
    staging = args.staging.resolve()
    deploy_root = (home / "deployment" / PLUGIN_ID).resolve()
    if not staging.is_relative_to(deploy_root) or staging == deploy_root or not 1024 <= args.port <= 65535:
        raise SystemExit("Invalid plugin staging directory or port.")
    manifest = json.loads((staging / "openclaw.plugin.json").read_text())
    if manifest["id"] != PLUGIN_ID:
        raise SystemExit("Wrong plugin manifest.")
    worker_file = staging / "worker-token"
    token = worker_file.read_text().strip()
    if not re.fullmatch(r"[a-fA-F0-9]{64}", token):
        raise SystemExit("Invalid worker credential.")
    del token
    os.chmod(worker_file, 0o600)
    config_file = home / "openclaw.json"
    config = json.loads(config_file.read_text())
    original = json.loads(config_file.read_text())
    plugins = config.setdefault("plugins", {})
    entries = plugins.setdefault("entries", {})
    old = entries.get(PLUGIN_ID, {})
    if old and not pathlib.Path(old.get("config", {}).get("workerTokenFile", "")).resolve().is_relative_to(deploy_root):
        raise SystemExit("Existing plugin configuration is outside this deployment.")
    tools = config.setdefault("tools", {})
    if "allow" not in tools:
        raise SystemExit("An explicit tools.allow policy is required.")
    # Versioned code avoids partially overwriting a loaded plugin and duplicate auto-discovery IDs.
    paths = plugins.setdefault("load", {}).setdefault("paths", [])
    expected = {name: hashlib.sha256((staging / name).read_bytes().replace(b"\r\n", b"\n")).digest() for name in FILES}
    target = None
    for candidate in paths:
        location = pathlib.Path(candidate)
        if not location.resolve().is_relative_to(deploy_root):
            continue
        try:
            if all(hashlib.sha256((location / name).read_bytes().replace(b"\r\n", b"\n")).digest() == digest
                   for name, digest in expected.items()):
                target = location
                break
        except OSError:
            continue
    reused_code = target is not None
    if not reused_code:
        target = staging / "plugin"
        target.mkdir(mode=0o700)
        for name in FILES:
            shutil.copyfile(staging / name, target / name)
            os.chmod(target / name, 0o600)
    old_worker = pathlib.Path(old.get("config", {}).get("workerTokenFile", str(worker_file)))
    try:
        if (old_worker.resolve().is_relative_to(deploy_root) and not old_worker.is_symlink()
                and old_worker.stat().st_size <= 1024 and (os.name == "nt" or old_worker.stat().st_mode & 0o077 == 0)
                and old_worker.read_text().strip() == worker_file.read_text().strip()):
            worker_file = old_worker
    except OSError:
        pass
    if "allow" not in plugins:
        plugins["allow"] = [name for name, entry in entries.items() if entry.get("enabled", True)]
    if PLUGIN_ID not in plugins["allow"]:
        plugins["allow"].append(PLUGIN_ID)
    old_options = old.get("config", {})
    timeout = old_options.get("timeoutSeconds", 600)
    if not isinstance(timeout, int) or not 10 <= timeout <= 600:
        timeout = 600
    entries[PLUGIN_ID] = {**old, "enabled": True, "config": {
        **old_options, "port": args.port, "workerTokenFile": str(worker_file), "timeoutSeconds": timeout,
    }}
    # Preserve a compatible loaded path and its position; discard obsolete duplicate plugin paths.
    retained = [p for p in paths if not pathlib.Path(p).resolve().is_relative_to(deploy_root) or p == str(target)]
    if str(target) not in retained:
        retained.append(str(target))
    paths[:] = list(dict.fromkeys(retained))
    for name in TOOLS:
        if name not in tools["allow"]:
            tools["allow"].append(name)
    if reused_code and config == original:
        print("CLAWBOX_RESULT=reused")
        return
    backup = home / "deployment" / ("pre-phase7-" + str(time.time_ns()) + ".json")
    atomic_json(backup, original)
    atomic_json(config_file, config)
    try:
        subprocess.run(["openclaw", "config", "validate"], check=True, timeout=120)
    except (subprocess.SubprocessError, OSError):
        atomic_json(config_file, original)
        raise SystemExit("Validation failed. Previous configuration restored.")
    print("CLAWBOX_RESULT=" + ("paired" if reused_code else "installed"))


if __name__ == "__main__":
    main()
