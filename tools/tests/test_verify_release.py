"""Portable Windows package dependency contracts; no PE files or SDK required."""
from contextlib import contextmanager
from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace
import struct
import base64

from tools import verify_release as verification


ENTRYPOINTS = ("HeyMelody", "HeyMelody.DeviceManager", "HeyMelody.Host",
              "HeyMelody.FitTest", "HeyMelody.HearingSetup")
DESKTOP = "HeyMelody.Desktop/0.9.2"


@contextmanager
def desktop_fixture(names=ENTRYPOINTS, declared=(), dll_directories=()):
    with tempfile.TemporaryDirectory(prefix="windows-desktop-package-test-") as temporary:
        root = Path(temporary)
        backend = root / "backend"
        backend.mkdir()
        directories = {"gui": root, "backend": backend}
        for key in dll_directories:
            (directories[key] / "HeyMelody.Desktop.dll").write_bytes(b"fixture")
        entries = [(name, backend if name == "HeyMelody.Host" else root,
                    {DESKTOP: {"type": "project"}} if name in declared else {})
                   for name in names]
        yield entries


class SharedDesktopTests(unittest.TestCase):
    def test_legacy_three_entrypoints_without_desktop_are_accepted(self):
        with desktop_fixture(names=ENTRYPOINTS[:3]) as entries:
            verification.verify_shared_desktop(entries)

    def test_legacy_five_entrypoints_without_desktop_are_accepted(self):
        with desktop_fixture() as entries:
            verification.verify_shared_desktop(entries)

    def test_complete_shared_desktop_package_is_accepted(self):
        with desktop_fixture(declared=ENTRYPOINTS, dll_directories=("gui", "backend")) as entries:
            verification.verify_shared_desktop(entries)

    def test_any_entrypoint_declaration_enables_contract(self):
        for name in ENTRYPOINTS:
            with self.subTest(name=name), desktop_fixture(declared=(name,)) as entries:
                with self.assertRaises(AssertionError):
                    verification.verify_shared_desktop(entries)

    def test_any_entrypoint_directory_dll_enables_contract(self):
        for directory in ("gui", "backend"):
            with self.subTest(directory=directory), desktop_fixture(dll_directories=(directory,)) as entries:
                with self.assertRaises(AssertionError):
                    verification.verify_shared_desktop(entries)

    def test_each_entrypoint_requires_desktop_dependency(self):
        for missing in ENTRYPOINTS:
            declarations = tuple(name for name in ENTRYPOINTS if name != missing)
            with self.subTest(name=missing), desktop_fixture(
                    declared=declarations, dll_directories=("gui", "backend")) as entries:
                with self.assertRaisesRegex(AssertionError, "Missing shared desktop dependency: " + missing):
                    verification.verify_shared_desktop(entries)

    def test_each_entrypoint_directory_requires_desktop_dll(self):
        for missing in ("gui", "backend"):
            directories = tuple(name for name in ("gui", "backend") if name != missing)
            with self.subTest(directory=missing), desktop_fixture(
                    declared=ENTRYPOINTS, dll_directories=directories) as entries:
                with self.assertRaises(AssertionError):
                    verification.verify_shared_desktop(entries)

    def test_desktop_package_requires_all_five_entrypoints(self):
        for missing in ENTRYPOINTS:
            names = tuple(name for name in ENTRYPOINTS if name != missing)
            with self.subTest(name=missing), desktop_fixture(
                    names=names, declared=names, dll_directories=("gui", "backend")) as entries:
                with self.assertRaisesRegex(AssertionError, "Incomplete shared desktop entrypoints"):
                    verification.verify_shared_desktop(entries)


class CompilationModeTests(unittest.TestCase):
    def test_deps_prefixed_and_assets_bare_sha512_bind_the_same_package(self):
        raw = base64.b64encode(bytes(range(64))).decode("ascii")
        self.assertEqual(verification.package_sha512("sha512-" + raw), verification.package_sha512(raw))
        changed = base64.b64encode(bytes(reversed(range(64)))).decode("ascii")
        self.assertNotEqual(verification.package_sha512(raw), verification.package_sha512("sha512-" + changed))

    def test_package_hash_requires_a_complete_sha512_digest(self):
        short = base64.b64encode(b"short digest").decode("ascii")
        with self.assertRaisesRegex(AssertionError, "SHA512 length"):
            verification.package_sha512("sha512-" + short)

    def test_old_manifest_defaults_to_ready_to_run(self):
        modes = verification.component_compilation({})
        self.assertEqual(set(modes), set(verification.COMPONENT_NAMES))
        self.assertEqual(set(modes.values()), {"ready-to-run"})

    def test_experimental_modes_require_the_exact_five_components(self):
        build = dict(architecture="x64", rid="win-x64",
                     componentCompilation=dict(verification.EXPERIMENTAL_COMPILATION))
        self.assertEqual(verification.component_compilation(build, "AotExperimental"),
                         verification.EXPERIMENTAL_COMPILATION)
        for role in verification.COMPONENT_NAMES:
            with self.subTest(missing=role):
                incomplete = dict(build, componentCompilation={
                    key: value for key, value in build["componentCompilation"].items() if key != role
                })
                with self.assertRaisesRegex(AssertionError, "compilation declaration"):
                    verification.component_compilation(incomplete, "AotExperimental")

    def test_experimental_component_swaps_are_rejected(self):
        for role, original in verification.EXPERIMENTAL_COMPILATION.items():
            modes = dict(verification.EXPERIMENTAL_COMPILATION)
            modes[role] = "ready-to-run" if original == "native-aot" else "native-aot"
            with self.subTest(role=role), self.assertRaises(AssertionError):
                verification.component_compilation(dict(architecture="x64", rid="win-x64",
                                                       componentCompilation=modes), "AotExperimental")

    def test_experimental_mode_rejects_cross_architecture(self):
        for architecture in ("arm64", "x86"):
            with self.subTest(architecture=architecture), self.assertRaisesRegex(AssertionError, "win-x64"):
                verification.component_compilation(dict(architecture=architecture, rid="win-" + architecture,
                                                       componentCompilation=verification.EXPERIMENTAL_COMPILATION),
                                                   "AotExperimental")

    def test_aot_declaration_cannot_silently_enable_default_mode(self):
        with self.assertRaisesRegex(AssertionError, "compilation declaration"):
            verification.component_compilation(dict(componentCompilation=verification.EXPERIMENTAL_COMPILATION))

    def test_unknown_modes_are_rejected(self):
        with self.assertRaisesRegex(AssertionError, "Unknown compilation"):
            verification.component_compilation({}, "Aot")

    def test_mixed_layout_preserves_gui_coreclr_and_rejects_backend_coreclr(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            backend = folder / "backend"
            backend.mkdir()
            (folder / "coreclr.dll").write_bytes(b"runtime")
            for role in ("deviceManager", "fitTest", "hearingSetup"):
                for suffix in (".dll", ".deps.json", ".runtimeconfig.json"):
                    (folder / (verification.COMPONENT_NAMES[role] + suffix)).write_bytes(b"fixture")
            verification.verify_compilation_layout(folder, verification.EXPERIMENTAL_COMPILATION)
            (backend / "coreclr.dll").write_bytes(b"runtime")
            with self.assertRaisesRegex(AssertionError, "backend contains CoreCLR"):
                verification.verify_compilation_layout(folder, verification.EXPERIMENTAL_COMPILATION)
            (backend / "coreclr.dll").unlink()
            (folder / "coreclr.dll").unlink()
            with self.assertRaises(AssertionError):
                verification.verify_compilation_layout(folder, verification.EXPERIMENTAL_COMPILATION)

    def test_mixed_layout_rejects_stale_managed_aot_entrypoints(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            (folder / "coreclr.dll").write_bytes(b"runtime")
            backend = folder / "backend"
            backend.mkdir()
            for role, name in (("gui", "HeyMelody"), ("host", "HeyMelody.Host")):
                for suffix in (".dll", ".deps.json", ".runtimeconfig.json"):
                    path = (backend if role == "host" else folder) / (name + suffix)
                    path.write_bytes(b"stale")
                    with self.subTest(role=role, suffix=suffix), self.assertRaisesRegex(
                            AssertionError, "Unexpected managed AOT entry"):
                        verification.verify_compilation_layout(folder, verification.EXPERIMENTAL_COMPILATION)
                    path.unlink()

    def test_native_executable_version_resource_is_read(self):
        key = "VS_VERSION_INFO\0".encode("utf-16-le")
        prefix = struct.pack("<HHH", 0, 52, 0) + key
        prefix += b"\0" * (-len(prefix) % 4)
        blob = prefix + struct.pack("<IIII", 0xFEEF04BD, 0x10000, 0x00000009, 0x00020000)
        pe = SimpleNamespace(icons=lambda kinds: {(16, 1, 1033): blob} if kinds == (16,) else {})
        self.assertEqual(verification.pe_file_version(pe), "0.9.2.0")


if __name__ == "__main__":
    unittest.main()
