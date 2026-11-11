#!/usr/bin/env python3
"""Read produced DEBs independently; validate metadata, file layout and ELF architecture."""
import hashlib
import io
from pathlib import Path
import sys
import tarfile


def verify(path):
    parts = {}
    with path.open("rb") as file:
        assert file.read(8) == b"!<arch>\n"
        while header := file.read(60):
            assert len(header) == 60 and header[58:] == b"`\n"
            name = header[:16].decode().strip().rstrip("/")
            size = int(header[48:58])
            parts[name] = file.read(size)
            assert len(parts[name]) == size
            if size % 2:
                assert file.read(1) == b"\n"
    assert list(parts) == ["debian-binary", "control.tar.gz", "data.tar.gz"]
    assert parts["debian-binary"] == b"2.0\n"
    with tarfile.open(fileobj=io.BytesIO(parts["control.tar.gz"]), mode="r:gz") as metadata:
        control = metadata.extractfile("./control").read().decode()
        package_name = next(line.partition(": ")[2] for line in control.splitlines() if line.startswith("Package: "))
        architecture = next(line.partition(": ")[2] for line in control.splitlines() if line.startswith("Architecture: "))
        assert architecture in ("amd64", "arm64", "armhf")
        filename_architecture = {"amd64": "x64", "armhf": "arm32"}.get(architecture, architecture)
        assert path.name.endswith(f"_{filename_architecture}.deb")
        elf_class, machine = {"amd64": (2, 62), "arm64": (2, 183), "armhf": (1, 40)}[architecture]
        for name in ("preinst", "postinst", "prerm", "postrm"):
            entry = metadata.getmember("./" + name)
            assert entry.mode == 0o755
            assert b"\r\n" not in metadata.extractfile(entry).read()
    with tarfile.open(fileobj=io.BytesIO(parts["data.tar.gz"]), mode="r:gz") as data:
        members = {m.name.removeprefix("./"): m for m in data.getmembers()}
        assert all(not n.startswith("/") and ".." not in n.split("/") for n in members)
        assert not any("reference/windows" in n or "referance/windows" in n for n in members)
        assert f"usr/share/doc/{package_name}/README.md" in members
        def assert_native(header):
            assert len(header) >= 52 and header[:4] == b"\x7fELF"
            assert header[4] == elf_class and header[5] == 1 and int.from_bytes(header[18:20], "little") == machine
            if architecture == "armhf":
                assert int.from_bytes(header[36:40], "little") & 0x400
        for component in ("service", "cli"):
            file = data.extractfile(members[f"opt/nuxprint-agent/{component}/PrintAgent.{component.title() if component != 'cli' else 'CLI'}"])
            assert_native(file.read(64))
            assert f"opt/nuxprint-agent/{component}/libcoreclr.so" in members
        if "Package: nuxprint-agent-server\n" in control:
            assert not any("/ui/" in n or "Avalonia" in n or "/applications/" in n for n in members)
        else:
            assert_native(data.extractfile(members["opt/nuxprint-agent/ui/PrintAgent.UI"]).read(64))
            for library in ("libcoreclr.so", "libSkiaSharp.so", "libHarfBuzzSharp.so"):
                assert_native(data.extractfile(members[f"opt/nuxprint-agent/ui/{library}"]).read(64))
            assert "usr/share/applications/nuxprint-agent.desktop" in members
        for member in members.values():
            if member.islnk():
                target = member.linkname.removeprefix("./")
                assert target in members and target.startswith("opt/nuxprint-agent/")
                assert members[target].isfile() and member.mode == members[target].mode
                assert member.name.split("/", 4)[-1] == members[target].name.split("/", 4)[-1]
            if member.isfile() or member.islnk():
                header = data.extractfile(member).read(64)
                if header[:4] == b"\x7fELF":
                    assert_native(header)
                    assert member.mode & 0o111
        unit = data.extractfile(members["lib/systemd/system/nuxprint-agent.service"]).read()
        assert b"ExecStart=/opt/nuxprint-agent/service/PrintAgent.Service\n" in unit
        assert members["usr/bin/nuxprint-agent"].isfile() and members["usr/bin/nuxprint-agent"].mode == 0o755
        assert b"launch.py cli" in data.extractfile(members["usr/bin/nuxprint-agent"]).read()
        assert "usr/lib/nuxprint-agent/authorize_user.py" in members
        assert "usr/share/polkit-1/actions/org.nuxprint.agent.authorize.policy" in members
    with path.open("rb") as package:
        digest = hashlib.file_digest(package, "sha256").hexdigest()
    print(f"Verified {path.name}; SHA256 {digest}")


if __name__ == "__main__":
    for argument in sys.argv[1:]:
        verify(Path(argument))
