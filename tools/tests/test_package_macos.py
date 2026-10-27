"""Portable packaging checks; no macOS commands, GPU, SDK or devices are used."""
from __future__ import annotations

import argparse
from contextlib import contextmanager, ExitStack, redirect_stdout
import io
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
import package_macos as package
import verify_macos_package as verification

VERSION = "0.9.2"
MACHO = b"\xcf\xfa\xed\xfe"


def write(path: Path, data: bytes = b"fixture") -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def entry_reports() -> list[dict]:
    return [{"success": True, "component": component, "version": VERSION,
             "runtimeIdentifier": "osx-arm64", "resourceBundles": True,
             "nativeLibrariesLoaded": True, "nativeBluetoothCalls": 0,
             "audioPlaybackCalls": 0} for component in package.COMPONENTS]


@contextmanager
def image_fixture(skip_runtime: bool = False, skip_metal: bool = False):
    """Move real temporary files while simulating the external image tools."""
    with tempfile.TemporaryDirectory(prefix="macos-package-test-") as directory:
        root = Path(directory).resolve()
        repo, bundle = root / "repo", root / "HeyMelody.app"
        write(repo / "packaging/macos/README.md")
        write(bundle / "Contents/MacOS/HeyMelody")
        args = argparse.Namespace(repo=repo, bundle=bundle, output=root / "out/HeyMelody.dmg",
                                  version=VERSION, validation=[], skip_runtime_checks=skip_runtime,
                                  skip_metal_check=skip_metal)
        commands = []
        image_root = None
        original_readlink = os.readlink
        simulated_links = set()
        applications_reads = []

        def link_key(path):
            # Compare lexical absolute paths without recursing through Path.resolve().
            return os.path.normcase(os.path.abspath(os.fsdecode(path)))

        def read_link(path, *arguments, **options):
            if not arguments and not options and link_key(path) in simulated_links:
                applications_reads.append(Path(path))
                return "/Applications"
            return original_readlink(path, *arguments, **options)

        def run(*arguments):
            nonlocal image_root
            commands.append(arguments)
            if arguments[0] == "ditto":
                shutil.copytree(arguments[1], arguments[2])
            elif arguments[:2] == ("hdiutil", "create"):
                image_root = Path(arguments[arguments.index("-srcfolder") + 1])
                write(Path(arguments[-1]), b"simulated signed disk image")
            elif arguments[:2] == ("hdiutil", "attach"):
                mount = Path(arguments[arguments.index("-mountpoint") + 1])
                shutil.copytree(image_root / "HeyMelody.app", mount / "HeyMelody.app")
                simulated_links.add(link_key(mount / "Applications"))
            elif arguments[0] != "codesign" and arguments[:2] not in (
                    ("hdiutil", "verify"), ("hdiutil", "detach")):
                raise AssertionError(f"Unexpected external command: {arguments}")

        with ExitStack() as stack:
            stack.enter_context(mock.patch.object(package, "run", side_effect=run))
            stack.enter_context(mock.patch.object(Path, "symlink_to"))
            readlink = stack.enter_context(mock.patch.object(package.os, "readlink", side_effect=read_link))
            check = stack.enter_context(mock.patch.object(package, "check_bundle", return_value={"success": True}))
            entries = stack.enter_context(mock.patch.object(package, "verify_entry_points", return_value=entry_reports()))
            metal = stack.enter_context(mock.patch.object(package, "verify_metal_backend",
                                                        return_value={"executed": True, "success": True}))
            stack.enter_context(redirect_stdout(io.StringIO()))
            yield argparse.Namespace(args=args, commands=commands, check=check, entries=entries,
                                     metal=metal, readlink=readlink, applications_reads=applications_reads)


class DiskImageTests(unittest.TestCase):
    def test_readlink_mock_preserves_non_image_path_resolution(self):
        original_readlink = os.readlink
        with mock.patch.object(os, "readlink", wraps=original_readlink) as delegate:
            with image_fixture() as fixture:
                root = fixture.args.bundle.parent
                missing = root / "missing-unrelated-link"
                delegate.reset_mock()
                with self.assertRaises(OSError):
                    package.os.readlink(missing, dir_fd=None)
                delegate.assert_called_once_with(missing, dir_fd=None)
                if os.name == "posix":
                    target = write(root / "real-target")
                    link = root / "real-unrelated-link"
                    os.symlink(target.name, link)
                    self.assertEqual(link.resolve(), target.resolve())
                self.assertEqual(fixture.applications_reads, [])

    def test_dmg_cli_keeps_installation_checks_when_only_metal_is_skipped(self):
        arguments = ["package_macos.py", "dmg", "--repo", ".", "--bundle", "HeyMelody.app",
                     "--version", VERSION, "--output", "HeyMelody.dmg", "--skip-metal-check"]
        with mock.patch.object(sys, "argv", arguments), mock.patch.object(package, "dmg") as create:
            package.main()
        self.assertTrue(create.call_args.args[0].skip_metal_check)
        self.assertFalse(create.call_args.args[0].skip_runtime_checks)

    def test_four_runtime_policies_preserve_installation_checks(self):
        for skip_runtime in (False, True):
            for skip_metal in (False, True):
                with self.subTest(skip_runtime=skip_runtime, skip_metal=skip_metal):
                    with image_fixture(skip_runtime, skip_metal) as fixture:
                        package.dmg(fixture.args)
                        report = json.loads(fixture.args.output.with_suffix(".json").read_text(encoding="utf-8"))
                        self.assertEqual(report["sha256"], package.sha256(fixture.args.output))
                        self.assertEqual(report["runtimeChecksExecuted"], not skip_runtime)
                        self.assertEqual(report["metalProbeExecuted"], not skip_runtime and not skip_metal)
                        self.assertFalse(report["bluetoothHardwareVerified"])
                        self.assertFalse(report["notarized"])
                        self.assertEqual(fixture.check.call_count, 2 if skip_runtime else 4)
                        self.assertEqual(fixture.entries.call_count, int(not skip_runtime))
                        self.assertEqual(fixture.metal.call_count, int(not skip_runtime and not skip_metal))
                        mounts = [command for command in fixture.commands if command[:2] == ("hdiutil", "attach")]
                        detaches = [command for command in fixture.commands if command[:2] == ("hdiutil", "detach")]
                        self.assertEqual(len(mounts), int(not skip_runtime))
                        self.assertEqual(len(detaches), int(not skip_runtime))
                        if not skip_runtime:
                            mount = Path(mounts[0][mounts[0].index("-mountpoint") + 1])
                            self.assertEqual(fixture.applications_reads, [mount / "Applications"])
                            self.assertEqual(len(report["installedEntryPointVerification"]), 5)
                            self.assertIn("安装检查.app", str(fixture.entries.call_args.args[0]))
                            self.assertNotEqual(fixture.entries.call_args.args[0], fixture.args.bundle)
                        if skip_runtime or skip_metal:
                            probe = report["installedMetalProbeVerification"]
                            self.assertFalse(probe["executed"])
                            self.assertIn("reason", probe)
                            if skip_runtime:
                                self.assertIn("runtime or installation", probe["reason"])
                            else:
                                self.assertIn("Metal probe", probe["reason"])
                                self.assertIn("Metal", report["scope"])

    def test_installed_copy_rejection_detaches_and_does_not_publish(self):
        with image_fixture(skip_metal=True) as fixture:
            def reject_installed(bundle, version):
                if "安装检查.app" in bundle.name:
                    raise ValueError("Installed copy is invalid")
                return {"success": True}
            fixture.check.side_effect = reject_installed
            with self.assertRaisesRegex(ValueError, "Installed copy is invalid"):
                package.dmg(fixture.args)
            self.assertTrue(any(command[:2] == ("hdiutil", "detach") for command in fixture.commands))
            self.assertFalse(fixture.args.output.exists())
            self.assertFalse(fixture.args.output.with_suffix(".json").exists())
            fixture.entries.assert_not_called()
            fixture.metal.assert_not_called()

    def test_default_metal_failure_is_not_downgraded(self):
        with image_fixture() as fixture:
            fixture.metal.side_effect = RuntimeError("Metal unavailable")
            with self.assertRaisesRegex(RuntimeError, "Metal unavailable"):
                package.dmg(fixture.args)
            fixture.entries.assert_called_once()
            self.assertTrue(any(command[:2] == ("hdiutil", "detach") for command in fixture.commands))
            self.assertFalse(fixture.args.output.exists())


class EntryPointTests(unittest.TestCase):
    def test_all_five_read_only_entries_run_without_sdk_environment(self):
        def launch(command, **options):
            self.assertEqual(command[1:], ["--verify-installation"])
            self.assertFalse(any(key.startswith("DYLD_") for key in options["env"]))
            self.assertNotIn("DOTNET_ROOT", options["env"])
            self.assertEqual(options["env"]["PATH"], "/usr/bin:/bin:/usr/sbin:/sbin")
            report = next(item for item in entry_reports() if item["component"] == Path(command[0]).name)
            return subprocess.CompletedProcess(command, 0, json.dumps(report), "")
        with mock.patch.dict(os.environ, {"DOTNET_ROOT": "/developer/sdk", "DYLD_LIBRARY_PATH": "/developer/lib"}), \
                mock.patch.object(package.subprocess, "run", side_effect=launch) as launch_mock:
            reports = package.verify_entry_points(Path("HeyMelody.app"), VERSION)
        self.assertEqual([item["component"] for item in reports], list(package.COMPONENTS))
        self.assertEqual(launch_mock.call_count, 5)

    def test_entry_claiming_bluetooth_calls_is_rejected(self):
        report = entry_reports()[0] | {"nativeBluetoothCalls": 1}
        with mock.patch.object(package.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, json.dumps(report), "")):
            with self.assertRaisesRegex(ValueError, "Unexpected installed"):
                package.verify_entry_points(Path("HeyMelody.app"), VERSION)

    def test_real_metal_success_receipt_is_explicit(self):
        result = subprocess.CompletedProcess([], 0, "Filament ABI 2 backend metal created and destroyed\n", "")
        with mock.patch.object(package.subprocess, "run", return_value=result):
            report = package.verify_metal_backend(Path("HeyMelody.app"))
        self.assertTrue(report["executed"])
        self.assertEqual(report["actualBackend"], "Metal")


class StagingTests(unittest.TestCase):
    def test_corrupt_native_input_is_rejected_before_staging_or_signing(self):
        with tempfile.TemporaryDirectory(prefix="macos-input-test-") as directory:
            root = Path(directory)
            native = root / "native"
            write(native / "hm_filament_probe", b"corrupt artifact")
            package.write_json(native / "filament-runtime.json", {
                "rid": "osx-arm64", "supportedBackends": ["Metal"],
                "files": [{"path": "hm_filament_probe", "sha256": "0" * 64}]})
            args = argparse.Namespace(repo=root / "repo", bundle=root / "Rejected.app",
                                      publish_root=root / "publish", filament_runtime=native,
                                      version=VERSION, validation=[], skip_runtime_checks=False)
            with mock.patch.object(package, "make_icon") as icon, mock.patch.object(package, "run") as command:
                with self.assertRaisesRegex(ValueError, "Native source artifact checksum mismatch"):
                    package.stage(args)
            self.assertFalse(args.bundle.exists())
            icon.assert_not_called()
            command.assert_not_called()

    def test_staged_probe_permissions_and_signed_hashes_preserve_source(self):
        with tempfile.TemporaryDirectory(prefix="macos-stage-test-") as directory:
            root = Path(directory)
            repo, native, publish, bundle = (root / name for name in ("repo", "native", "publish", "HeyMelody.app"))
            write(repo / "packaging/SOURCE-NOTES.md")
            write(repo / "packaging/macos/README.md")
            for component in package.COMPONENTS:
                project = "HeyMelody.App" if component == "HeyMelody" else component
                apphost = write(publish / project / component, MACHO + component.encode())
                apphost.chmod(0o755)
                write(apphost.with_name(component + ".runtimeconfig.json"), json.dumps({"runtimeOptions": {
                    "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.4"}]}}).encode())
                for name in ("libhostfxr.dylib", "libhostpolicy.dylib", "libcoreclr.dylib"):
                    write(apphost.parent / name)
                if component == "HeyMelody.Host":
                    write(apphost.parent / "HeyMelody.MacOS.dll")
                    write(apphost.parent / "libHeyMelodyMac.dylib", MACHO + b"platform")
            source_probe = write(native / "hm_filament_probe", MACHO + b"probe")
            source_probe.chmod(0o644)
            source_library = write(native / "libHeyMelody.Filament.Native.dylib", MACHO + b"filament")
            material = write(native / "materials/gltfio.uberz")
            native_manifest = {"schemaVersion": 2, "abiVersion": 2, "rid": "osx-arm64", "supportedBackends": ["Metal"],
                               "files": [{"path": item.relative_to(native).as_posix(), "sha256": package.sha256(item)}
                                         for item in (source_probe, source_library, material)],
                               "materials": {"path": "materials/gltfio.uberz", "sha256": package.sha256(material)}}
            package.write_json(native / "filament-runtime.json", native_manifest)
            source_bytes = {path.relative_to(native): path.read_bytes() for path in native.rglob("*") if path.is_file()}
            source_mode = source_probe.stat().st_mode
            source_hash = package.sha256(native / "filament-runtime.json")
            chmod_requests, events = [], []
            original_chmod = Path.chmod

            def chmod(path, mode, *args, **kwargs):
                chmod_requests.append((path, mode))
                events.append("chmod")
                return original_chmod(path, mode, *args, **kwargs)

            def thin(staged):
                events.append("thin")
                self.assertEqual(chmod_requests[-1][0], staged / "Contents/MacOS/hm_filament_probe")
                self.assertEqual(chmod_requests[-1][1] & 0o111, 0o111)

            def sign(*command):
                self.assertEqual(command[0], "codesign")
                events.append("sign")
                target = Path(command[-1])
                if target.is_file() and verification.is_macho(target):
                    target.write_bytes(target.read_bytes() + b"-signed")

            def notices(_repo, resources):
                for name in ("PACKAGE-NOTICES.json", "ResourceBundles/artwork.json", "ResourceBundles/provenance.json"):
                    write(resources / name, b"{}")

            args = argparse.Namespace(repo=repo, bundle=bundle, publish_root=publish, filament_runtime=native,
                                      version=VERSION, validation=[], skip_runtime_checks=False)
            with mock.patch.object(package, "make_icon", side_effect=lambda _repo, destination, _temp: write(destination)), \
                    mock.patch.object(package, "export_notices", side_effect=notices), \
                    mock.patch.object(package, "thin_native_libraries", side_effect=thin), \
                    mock.patch.object(package, "run", side_effect=sign), \
                    mock.patch.object(package, "check_bundle", return_value={"success": True}), \
                    mock.patch.object(package, "verify_entry_points", return_value=entry_reports()), \
                    mock.patch.object(Path, "chmod", chmod), redirect_stdout(io.StringIO()):
                package.stage(args)
            self.assertLess(events.index("chmod"), events.index("thin"))
            self.assertLess(events.index("thin"), events.index("sign"))
            self.assertEqual(source_probe.stat().st_mode, source_mode)
            self.assertEqual({path.relative_to(native): path.read_bytes() for path in native.rglob("*") if path.is_file()}, source_bytes)
            manifest = json.loads((bundle / "Contents/Resources/manifest.json").read_text(encoding="utf-8"))
            self.assertEqual(manifest["filamentSourceManifestSha256"], source_hash)
            staged_native = bundle / "Contents/MacOS"
            for entry in json.loads((staged_native / "filament-runtime.json").read_text(encoding="utf-8"))["files"]:
                self.assertEqual(entry["sha256"], package.sha256(staged_native / entry["path"]))
            self.assertNotEqual(package.sha256(staged_native / "hm_filament_probe"), package.sha256(source_probe))
            if os.name == "posix":
                self.assertEqual(stat.S_IMODE((staged_native / "hm_filament_probe").stat().st_mode) & 0o111, 0o111)
            with mock.patch.object(verification.os, "access", side_effect=lambda path, _mode: Path(path).name != "hm_filament_probe"):
                with self.assertRaisesRegex(ValueError, "probe is not executable"):
                    verification.check_bundle(bundle, VERSION)


if __name__ == "__main__":
    unittest.main()
