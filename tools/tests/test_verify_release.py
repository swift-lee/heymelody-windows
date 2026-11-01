"""Portable Windows package dependency contracts; no PE files or SDK required."""
from contextlib import contextmanager
from pathlib import Path
import tempfile
import unittest

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


if __name__ == "__main__":
    unittest.main()
