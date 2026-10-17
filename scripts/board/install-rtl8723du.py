"""Pinned RTL8723DU DKMS installation for the verified RK3568 vendor kernel."""

import hashlib
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tarfile

COMMIT = "ae03b0861351f72808405ddda80e8aab105bcfce"
ARCHIVE_SHA256 = "a6d703013a8e1a45123ffc597905d7858ef8ef75397efd494e4746a7d8270002"
KERNEL = "6.1.57-rk35xx-hiasia"


def run(*args):
    subprocess.run(args, check=True, timeout=900)


def install(archive):
    if os.geteuid() != 0 or os.uname().release != KERNEL or os.uname().machine != "aarch64":
        raise SystemExit("This installer is restricted to the verified aarch64 vendor kernel.")
    archive = Path(archive).resolve(strict=True)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != ARCHIVE_SHA256:
        raise SystemExit("Driver source archive checksum mismatch.")
    source = Path("/usr/src/rtl8723du-0.1")
    if source.is_symlink():
        raise SystemExit("Driver source path must not be a symlink.")
    source.mkdir(mode=0o755, exist_ok=True)
    with tarfile.open(archive) as package:
        if package.pax_headers.get("comment") != COMMIT:
            raise SystemExit("Driver source revision mismatch.")
        for member in package:
            parts = Path(member.name).parts[1:]
            if not parts:
                continue
            if member.issym() or member.islnk() or ".." in parts:
                raise SystemExit("Unexpected driver source entry.")
            dest = source.joinpath(*parts)
            if member.isdir():
                dest.mkdir(parents=True, exist_ok=True)
            elif member.isfile():
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes(package.extractfile(member).read())
    # The shipped headers contain x86 host helpers. Rebuild only those helpers, not the kernel.
    headers = Path("/usr/src/linux-headers-" + KERNEL)
    backup = Path("/root/.local/share/clawbox/header-tools-backup")
    backup.mkdir(parents=True, mode=0o700, exist_ok=True)
    for relative, inputs in [("scripts/basic/fixdep", ["scripts/basic/fixdep.c"]),
                             ("scripts/mod/modpost", ["scripts/mod/modpost.c", "scripts/mod/file2alias.c",
                                                      "scripts/mod/sumversion.c"])]:
        target = headers / relative
        if struct.unpack_from("<H", target.read_bytes(), 18)[0] == 183:
            continue
        saved = backup / target.name
        if not saved.exists():
            shutil.copy2(target, saved)
        run("gcc", "-O2", "-I", str(headers / "scripts/mod"), "-o", str(target),
            *(str(headers / name) for name in inputs))
    with (source / "dkms.conf").open("a") as handle:
        handle.write('\nMAKE[0]="make -C /lib/modules/${kernelver}/build '
                     'M=${dkms_tree}/${PACKAGE_NAME}/${PACKAGE_VERSION}/build '
                     'TopDIR=${dkms_tree}/${PACKAGE_NAME}/${PACKAGE_VERSION}/build"\n')
    installed = subprocess.check_output(["dkms", "status", "-m", "rtl8723du", "-v", "0.1"], text=True)
    if not installed.strip():
        run("dkms", "add", "-m", "rtl8723du", "-v", "0.1")
    run("dkms", "build", "-m", "rtl8723du", "-v", "0.1", "-k", KERNEL)
    run("dkms", "install", "-m", "rtl8723du", "-v", "0.1", "-k", KERNEL)
    Path("/etc/modprobe.d/clawbox-wifi.conf").write_text(
        "options 8723du rtw_power_mgnt=0 rtw_ips_mode=0 rtw_enusbss=0\n")
    run("modprobe", "8723du")
    Path("/etc/modules-load.d/clawbox-wifi.conf").write_text("8723du\n")
    print("CLAWBOX_WIFI_DRIVER=installed")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: install-rtl8723du.py <verified source archive>")
    install(sys.argv[1])
