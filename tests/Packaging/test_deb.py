"""Exercise the package boundary without Linux or a printer."""
import contextlib
import importlib.util
import io
from pathlib import Path
import tempfile
import tarfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "packaging/deb" / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


builder, verifier = load("build_deb"), load("verify_deb")


def elf(elf_class, machine, hard_float=False):
    header = bytearray(64)
    header[:7] = b"\x7fELF" + bytes([elf_class, 1, 1])
    header[18:20] = machine.to_bytes(2, "little")
    if hard_float:
        header[36:40] = (0x5000400).to_bytes(4, "little")
    return bytes(header)


def publish_fixture(root, header):
    for component in ("Service", "CLI", "UI"):
        directory = root / component
        directory.mkdir(parents=True)
        (directory / f"PrintAgent.{component}").write_bytes(header)
        (directory / "libcoreclr.so").write_bytes(header)
    for library in ("libSkiaSharp.so", "libHarfBuzzSharp.so"):
        (root / "UI" / library).write_bytes(header)
    for component in ("Service", "CLI", "UI"):
        directory = root / component
        (directory / ".nuxprint-publish-manifest").write_text("\n".join(sorted(f.name for f in directory.iterdir())), encoding="utf-8")


def package_parts(package):
    parts = {}
    with package.open("rb") as file:
        if file.read(8) != b"!<arch>\n":
            raise ValueError("Invalid package")
        while header := file.read(60):
            size = int(header[48:58])
            parts[header[:16].decode().strip().rstrip("/")] = file.read(size)
            if size % 2:
                file.read(1)
    return parts


class DebTests(unittest.TestCase):
    def test_identical_runtime_files_share_one_installed_copy(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish_fixture(root / "publish", elf(2, 62))
            builder.build(root / "publish", root / "out", "desktop", "1.9.5")
            package = next((root / "out").glob("*.deb"))
            with tarfile.open(fileobj=io.BytesIO(package_parts(package)["data.tar.gz"])) as data:
                self.assertIn("./usr/share/doc/nuxprint-agent-desktop/README.md", data.getnames())
                first = data.getmember("./opt/nuxprint-agent/service/libcoreclr.so")
                for component in ("cli", "ui"):
                    duplicate = data.getmember(f"./opt/nuxprint-agent/{component}/libcoreclr.so")
                    self.assertTrue(duplicate.islnk())
                    self.assertEqual(first.name, duplicate.linkname)
                    self.assertEqual(data.extractfile(first).read(), data.extractfile(duplicate).read())
                self.assertFalse(any(m.name.endswith(".nuxprint-publish-manifest") for m in data.getmembers()))
                data.extractall(root / "installed", members=[m for m in data.getmembers() if m.name.startswith("./opt/")], filter="data")
            service = root / "installed/opt/nuxprint-agent/service/libcoreclr.so"
            cli = root / "installed/opt/nuxprint-agent/cli/libcoreclr.so"
            self.assertTrue(service.samefile(cli))

    def test_different_runtime_contents_are_not_linked(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish_fixture(root / "publish", elf(2, 62))
            with (root / "publish/CLI/libcoreclr.so").open("ab") as file:
                file.write(b"different-runtime-version")
            builder.build(root / "publish", root / "out", "server", "1.9.5")
            with tarfile.open(fileobj=io.BytesIO(package_parts(next((root / "out").glob("*.deb")))["data.tar.gz"])) as data:
                self.assertTrue(data.getmember("./opt/nuxprint-agent/cli/libcoreclr.so").isfile())

    def test_unexpected_publish_file_is_rejected_before_packaging(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish_fixture(root / "publish", elf(2, 62))
            (root / "publish/Service/forgotten-document.txt").write_text("not a published asset")
            with self.assertRaisesRegex(ValueError, "Unexpected publish files"):
                builder.build(root / "publish", root / "out", "server", "1.9.5")
            self.assertFalse((root / "out").exists())

    def test_manifest_cannot_reference_files_outside_component(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish_fixture(root / "publish", elf(2, 62))
            (root / "publish/Service/.nuxprint-publish-manifest").write_text("../private.txt")
            with self.assertRaisesRegex(ValueError, "Invalid publish path"):
                builder.build(root / "publish", root / "out", "server", "1.9.5")

    def test_manifest_must_be_present_and_complete(self):
        for missing_manifest in (True, False):
            with self.subTest(missing_manifest=missing_manifest), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                publish_fixture(root / "publish", elf(2, 62))
                if missing_manifest:
                    (root / "publish/Service/.nuxprint-publish-manifest").unlink()
                else:
                    (root / "publish/Service/libcoreclr.so").unlink()
                with self.assertRaises(ValueError):
                    builder.build(root / "publish", root / "out", "server", "1.9.5")

    def test_each_architecture_produces_verifiable_desktop_and_server_packages(self):
        for rid, architecture, header in (
            ("linux-x64", "amd64", elf(2, 62)),
            ("linux-arm64", "arm64", elf(2, 183)),
            ("linux-arm", "armhf", elf(1, 40, True)),
        ):
            for kind in ("desktop", "server"):
                with self.subTest(rid=rid, kind=kind), tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    publish_fixture(root / "publish", header)
                    with contextlib.redirect_stdout(io.StringIO()):
                        builder.build(root / "publish", root / "out", kind, "1.9.4", rid)
                        filename_architecture = {"amd64": "x64", "armhf": "arm32"}.get(architecture, architecture)
                        package = root / "out" / f"nuxprint-agent-{kind}_1.9.4-{builder.DEBIAN_REVISION}_{filename_architecture}.deb"
                        verifier.verify(package)
                        parts = package_parts(package)
                        with tarfile.open(fileobj=io.BytesIO(parts["data.tar.gz"])) as data:
                            policy = ET.fromstring(data.extractfile("./usr/share/polkit-1/actions/org.nuxprint.agent.authorize.policy").read())
                            action = policy.find("action")
                            self.assertEqual("no", action.findtext("defaults/allow_any"))
                            self.assertEqual("no", action.findtext("defaults/allow_inactive"))
                            self.assertEqual("auth_admin", action.findtext("defaults/allow_active"))
                            annotations = {a.attrib["key"]: a.text for a in action.findall("annotate")}
                            self.assertEqual("/usr/bin/python3", annotations["org.freedesktop.policykit.exec.path"])
                            self.assertEqual("/usr/lib/nuxprint-agent/authorize_user.py", annotations["org.freedesktop.policykit.exec.argv1"])
                            for helper in ("launch.py", "authorize_user.py"):
                                entry = data.getmember("./usr/lib/nuxprint-agent/" + helper)
                                self.assertEqual(0, entry.uid)
                                self.assertEqual(0, entry.mode & 0o022)
                            if kind == "desktop":
                                self.assertIn(b"ui", data.extractfile("./usr/bin/nuxprint-agent-ui").read())
                                self.assertNotIn(b"--autostart", data.extractfile("./usr/share/applications/nuxprint-agent.desktop").read())
                                self.assertIn(b"--autostart", data.extractfile("./etc/xdg/autostart/nuxprint-agent.desktop").read())
                    self.assertTrue(package.is_file())

    def test_rejects_wrong_architecture_inside_native_library(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish_fixture(root / "publish", elf(2, 183))
            (root / "publish/UI/libSkiaSharp.so").write_bytes(elf(2, 62))
            with self.assertRaisesRegex(ValueError, "libSkiaSharp"):
                builder.build(root / "publish", root / "out", "desktop", "1.9.4", "linux-arm64")
            self.assertFalse((root / "out").exists())

    def test_rejects_arm32_soft_float_runtime(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish_fixture(root / "publish", elf(1, 40, True))
            (root / "publish/Service/libcoreclr.so").write_bytes(elf(1, 40))
            with self.assertRaisesRegex(ValueError, "hard-float"):
                builder.build(root / "publish", root / "out", "server", "1.9.4", "linux-arm")

    def test_rejects_missing_native_ui_dependency(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish_fixture(root / "publish", elf(2, 183))
            (root / "publish/UI/libHarfBuzzSharp.so").unlink()
            with self.assertRaisesRegex(ValueError, "libHarfBuzzSharp"):
                builder.build(root / "publish", root / "out", "desktop", "1.9.4", "linux-arm64")


if __name__ == "__main__":
    unittest.main()
