#!/usr/bin/env python3
"""Build Debian 2.0 ar/tar packages using only Python's standard library."""
import argparse
import gzip
import hashlib
import io
import os
from pathlib import Path, PurePosixPath
import re
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[2]
EPOCH = int(os.environ.get("SOURCE_DATE_EPOCH", "0"))
TARGETS = {"linux-x64": ("amd64", 2, 62), "linux-arm64": ("arm64", 2, 183), "linux-arm": ("armhf", 1, 40)}
MANIFEST = ".nuxprint-publish-manifest"
DEBIAN_REVISION = "2"


def publish_files(directory):
    manifest = directory / MANIFEST
    if not manifest.is_file() or manifest.is_symlink():
        raise ValueError(f"Missing publish manifest: {manifest}; publish the project again")
    paths = set()
    for line in manifest.read_text(encoding="utf-8-sig").splitlines():
        relative = line.strip().replace("\\", "/")
        if not relative:
            continue
        path = PurePosixPath(relative)
        if path.is_absolute() or any(p in ("", ".", "..") or ":" in p for p in relative.split("/")) or relative == MANIFEST:
            raise ValueError(f"Invalid publish path: {relative}")
        paths.add(relative)
    entries = list(directory.rglob("*"))
    if directory.is_symlink() or any(f.is_symlink() for f in entries):
        raise ValueError(f"Publish directory must not contain links: {directory}")
    actual = {f.relative_to(directory).as_posix() for f in entries if f.is_file() and f != manifest}
    if actual - paths:
        raise ValueError(f"Unexpected publish files: {sorted(actual - paths)}")
    if paths - actual:
        raise ValueError(f"Missing publish files: {sorted(paths - actual)}")
    return [directory / relative for relative in sorted(paths) if PurePosixPath(relative).suffix != ".pdb"]


def check_elf(header, rid, path):
    architecture, elf_class, machine = TARGETS[rid]
    if len(header) < 52 or header[:4] != b"\x7fELF" or header[4] != elf_class or header[5] != 1 or int.from_bytes(header[18:20], "little") != machine:
        raise ValueError(f"Not a {rid} ELF binary: {path}")
    if architecture == "armhf" and not int.from_bytes(header[36:40], "little") & 0x400:
        raise ValueError(f"ARM binary does not use the hard-float ABI: {path}")


def add_bytes(archive, name, data, mode=0o644):
    entry = tarfile.TarInfo("./" + name)
    entry.size = len(data)
    entry.mode, entry.uid, entry.gid, entry.mtime = mode, 0, 0, EPOCH
    entry.uname = entry.gname = "root"
    archive.addfile(entry, io.BytesIO(data))


def archive_file(callback):
    file = tempfile.TemporaryFile()
    with gzip.GzipFile(fileobj=file, mode="wb", mtime=EPOCH, filename="") as compressed:
        with tarfile.open(fileobj=compressed, mode="w|", format=tarfile.GNU_FORMAT) as archive:
            callback(archive)
    file.seek(0)
    return file


def ar_member(output, name, file):
    file.seek(0, 2)
    size = file.tell()
    file.seek(0)
    header = f"{name + '/':<16}{EPOCH:<12}{0:<6}{0:<6}{'100644':<8}{size:<10}`\n".encode("ascii")
    if len(header) != 60:
        raise ValueError("Invalid ar member header")
    output.write(header)
    while data := file.read(1024 * 1024):
        output.write(data)
    if size % 2:
        output.write(b"\n")


def build(publish, output, kind, version, rid="linux-x64"):
    architecture = TARGETS[rid][0]
    name = "nuxprint-agent-" + kind
    other = "nuxprint-agent-" + ("server" if kind == "desktop" else "desktop")
    components = ["Service", "CLI"] + (["UI"] if kind == "desktop" else [])
    files = {component: publish_files(publish / component) for component in components}
    for component in components:
        executable = publish / component / ("PrintAgent." + component)
        check_elf(executable.read_bytes()[:64], rid, executable)
        if not (publish / component / "libcoreclr.so").is_file():
            raise ValueError("Publish must be self-contained")
        if component == "UI":
            for library in ("libSkiaSharp.so", "libHarfBuzzSharp.so"):
                if not (publish / component / library).is_file():
                    raise ValueError(f"Missing UI native library: {library}")
        for binary in files[component]:
            if binary.is_file():
                with binary.open("rb") as file:
                    header = file.read(64)
                if header[:4] == b"\x7fELF":
                    check_elf(header, rid, binary)

    installed_bytes = 0

    def data(archive):
        nonlocal installed_bytes
        stored = {}

        def add_data(name, payload, mode=0o644):
            nonlocal installed_bytes
            add_bytes(archive, name, payload, mode)
            installed_bytes += len(payload)

        for component in components:
            for file in files[component]:
                relative = file.relative_to(publish / component).as_posix()
                payload = file.read_bytes()
                install_path = f"opt/nuxprint-agent/{component.lower()}/{relative}"
                mode = 0o755 if payload[:4] == b"\x7fELF" else 0o644
                key = (relative, mode, hashlib.sha256(payload).digest())
                if key in stored:
                    entry = tarfile.TarInfo("./" + install_path)
                    entry.type, entry.linkname, entry.mode = tarfile.LNKTYPE, "./" + stored[key], mode
                    entry.uid, entry.gid, entry.mtime = 0, 0, EPOCH
                    entry.uname = entry.gname = "root"
                    archive.addfile(entry)
                else:
                    add_data(install_path, payload, mode)
                    stored[key] = install_path
        add_data("lib/systemd/system/nuxprint-agent.service", (ROOT / "packaging/systemd/nuxprint-agent.service").read_bytes())
        add_data("etc/nuxprint-agent/agent.json", (ROOT / "packaging/deb/agent.json").read_bytes())
        add_data(f"usr/share/doc/{name}/README.md", (ROOT / "README.md").read_bytes())
        for helper in ("launch.py", "authorize_user.py"):
            add_data(f"usr/lib/nuxprint-agent/{helper}", (ROOT / "packaging/local-access" / helper).read_bytes())
        add_data("usr/share/polkit-1/actions/org.nuxprint.agent.authorize.policy", (ROOT / "packaging/local-access/org.nuxprint.agent.authorize.policy").read_bytes())
        add_data("usr/bin/nuxprint-agent", b'#!/bin/sh\nexec /usr/bin/python3 /usr/lib/nuxprint-agent/launch.py cli "$@"\n', 0o755)
        if kind == "desktop":
            desktop = (ROOT / "packaging/deb/nuxprint-agent.desktop").read_bytes()
            add_data("usr/share/applications/nuxprint-agent.desktop", desktop)
            autostart = desktop.replace(b"\nExec=/usr/bin/nuxprint-agent-ui\n", b"\nExec=/usr/bin/nuxprint-agent-ui --autostart\n")
            add_data("etc/xdg/autostart/nuxprint-agent.desktop", autostart)
            add_data("usr/share/icons/hicolor/256x256/apps/nuxprint-agent.png", (ROOT / "src/PrintAgent.UI/Assets/icon.png").read_bytes())
            add_data("usr/bin/nuxprint-agent-ui", b'#!/bin/sh\nexec /usr/bin/python3 /usr/lib/nuxprint-agent/launch.py ui "$@"\n', 0o755)

    dependencies = "cups, cups-client, adduser, passwd, login, python3, pkexec | policykit-1, systemd, init-system-helpers, ca-certificates, libc6 (>= 2.31), libgcc-s1, libstdc++6, zlib1g, libicu72 | libicu70 | libicu74 | libicu76, libssl3 | libssl3t64, libgssapi-krb5-2"
    if kind == "desktop":
        dependencies += ", libx11-6, libice6, libsm6, libfontconfig1, libfreetype6, libxrender1, libxext6, libxi6, libxcursor1, libxrandr2, libgl1, xwayland, fonts-dejavu-core, libnotify-bin"
    def metadata(archive):
        installed_size = (installed_bytes + 1023) // 1024
        control = f"Package: {name}\nVersion: {version}-{DEBIAN_REVISION}\nArchitecture: {architecture}\nMaintainer: NuxPrint Agent Project\nSection: utils\nPriority: optional\nInstalled-Size: {installed_size}\nDepends: {dependencies}\nConflicts: {other}\nReplaces: {other}\nProvides: nuxprint-agent\nDescription: NuxPrint Linux cloud print agent ({kind})\n Local CUPS printing through the existing NuxPrint Cloud protocol.\n"
        add_bytes(archive, "control", control.encode())
        add_bytes(archive, "conffiles", b"/etc/nuxprint-agent/agent.json\n")
        for script in ("preinst", "postinst", "prerm", "postrm"):
            add_bytes(archive, script, (ROOT / "packaging/deb" / script).read_bytes(), 0o755)

    output.mkdir(parents=True, exist_ok=True)
    filename_architecture = {"amd64": "x64", "armhf": "arm32"}.get(architecture, architecture)
    target = output / f"{name}_{version}-{DEBIAN_REVISION}_{filename_architecture}.deb"
    with archive_file(data) as data_tar, archive_file(metadata) as control_tar, target.open("wb") as package:
        package.write(b"!<arch>\n")
        ar_member(package, "debian-binary", io.BytesIO(b"2.0\n"))
        ar_member(package, "control.tar.gz", control_tar)
        ar_member(package, "data.tar.gz", data_tar)
    print(f"Created {target} ({target.stat().st_size} bytes)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--rid", choices=TARGETS, default="linux-x64")
    args = parser.parse_args()
    version = re.search(r'AppVersion = "([0-9.]+)"', (ROOT / "src/PrintAgent.Common/Constants.cs").read_text()).group(1)
    for kind in ("desktop", "server"):
        build(args.publish_root, args.output, kind, version, args.rid)
