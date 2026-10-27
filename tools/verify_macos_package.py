#!/usr/bin/env python3
"""Verify a self-contained Apple silicon bundle without accessing Bluetooth or taking screenshots."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import subprocess

COMPONENTS = ("HeyMelody", "HeyMelody.Host", "HeyMelody.DeviceManager", "HeyMelody.FitTest", "HeyMelody.HearingSetup")
MACHO_MAGICS = {b"\xfe\xed\xfa\xce", b"\xce\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xcf\xfa\xed\xfe",
                b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca", b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca"}


def is_macho(path: Path) -> bool:
    with path.open("rb") as stream:
        return stream.read(4) in MACHO_MAGICS


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def checked_file(root: Path, entry: dict) -> Path:
    path = (root / entry["path"]).resolve()
    if root.resolve() not in path.parents or not path.is_file():
        raise ValueError(f"Missing or unsafe manifest entry: {entry['path']}")
    if sha256(path) != entry["sha256"]:
        raise ValueError(f"Checksum mismatch: {entry['path']}")
    return path


def check_native_dependencies(path: Path) -> None:
    # otool -L also reports LC_ID_DYLIB. An upstream library's own install name
    # is its identity, not an absolute dependency that dyld needs to load.
    identity_lines = subprocess.check_output(["otool", "-D", str(path)], text=True).splitlines()[1:]
    identities = {line.strip() for line in identity_lines}
    links = subprocess.check_output(["otool", "-L", str(path)], text=True)
    for line in links.splitlines()[1:]:
        dependency = line.strip().split(" (", 1)[0]
        if dependency in identities:
            continue
        if dependency.startswith("/") and not dependency.startswith(("/System/Library/", "/usr/lib/")):
            raise ValueError(f"Non-system absolute native dependency in {path.name}: {dependency}")


def check_bundle(bundle: Path, version: str | None = None) -> dict:
    bundle = bundle.resolve()
    manifest = json.loads((bundle / "Contents/Resources/manifest.json").read_text(encoding="utf-8"))
    if manifest["schemaVersion"] != 1 or manifest["runtimeIdentifier"] != "osx-arm64":
        raise ValueError("Expected an osx-arm64 package manifest")
    if version is not None and manifest["version"] != version:
        raise ValueError("Package version mismatch")
    version = manifest["version"]
    if manifest["signing"] != "ad-hoc" or manifest["notarized"] is not False:
        raise ValueError("This packaging entry point produces ad-hoc, unnotarized builds")
    for entry in manifest["files"]:
        checked_file(bundle, entry)
    identifiers = set()
    runtime_versions = set()
    for component in COMPONENTS:
        application = bundle if component == "HeyMelody" else bundle / "Contents/Helpers" / (component + ".app")
        with (application / "Contents/Info.plist").open("rb") as stream:
            info = plistlib.load(stream)
        if info["CFBundleExecutable"] != component or info["CFBundleShortVersionString"] != version or info["CFBundleVersion"] != version:
            raise ValueError(f"Wrong executable or version: {component}")
        if info["LSMinimumSystemVersion"] != "15.0" or info["LSUIElement"] != (component != "HeyMelody"):
            raise ValueError(f"Wrong minimum system or agent policy: {component}")
        if info["CFBundleIdentifier"] in identifiers or not info.get("NSBluetoothAlwaysUsageDescription"):
            raise ValueError(f"Duplicate identity or absent Bluetooth purpose: {component}")
        identifiers.add(info["CFBundleIdentifier"])
        binary = application / "Contents/MacOS" / component
        if not binary.is_file() or not os.access(binary, os.X_OK) or not is_macho(binary):
            raise ValueError(f"Missing executable apphost: {component}")
        config = json.loads((binary.parent / (component + ".runtimeconfig.json")).read_text())
        frameworks = config["runtimeOptions"].get("includedFrameworks", [])
        if len(frameworks) != 1 or frameworks[0]["name"] != "Microsoft.NETCore.App" or not frameworks[0]["version"].startswith("10."):
            raise ValueError(f"Expected self-contained .NET 10: {component}")
        runtime_versions.add(frameworks[0]["version"])
        for required in ("libhostfxr.dylib", "libhostpolicy.dylib", "libcoreclr.dylib"):
            if not (binary.parent / required).is_file():
                raise ValueError(f"Missing self-contained runtime file: {component}/{required}")
        if (binary.parent / "HeyMelody.Windows.dll").exists():
            raise ValueError(f"Windows platform implementation leaked into the macOS package: {component}")
        if component == "HeyMelody.Host":
            for required in ("HeyMelody.MacOS.dll", "libHeyMelodyMac.dylib"):
                if not (binary.parent / required).is_file():
                    raise ValueError(f"Missing macOS backend implementation: {required}")
    if len(runtime_versions) != 1:
        raise ValueError("All five entry points must use the same .NET runtime version")
    resources = bundle / "Contents/Resources"
    for relative in ("ResourceBundles/artwork.json", "ResourceBundles/provenance.json", "PACKAGE-NOTICES.json", "SOURCE-NOTES.md"):
        if not (resources / relative).is_file():
            raise ValueError(f"Offline resource or notice missing: {relative}")
    native_root = bundle / "Contents/MacOS"
    native = json.loads((native_root / "filament-runtime.json").read_text(encoding="utf-8-sig"))
    if native["schemaVersion"] != 2 or native["abiVersion"] != 2 or native["rid"] != "osx-arm64" or native["supportedBackends"] != ["Metal"]:
        raise ValueError("Missing complete Filament Metal runtime")
    for entry in native["files"]:
        checked_file(native_root, entry)
    if sha256(native_root / native["materials"]["path"]) != native["materials"]["sha256"]:
        raise ValueError("Filament material archive checksum mismatch")
    if not (native_root / "libHeyMelody.Filament.Native.dylib").is_file() or not (native_root / "hm_filament_probe").is_file():
        raise ValueError("Filament native library or probe is missing")
    if not os.access(native_root / "hm_filament_probe", os.X_OK):
        raise ValueError("Filament native probe is not executable")
    native_count = 0
    for path in bundle.rglob("*"):
        if path.is_symlink():
            raise ValueError(f"Unexpected symlink in self-contained bundle: {path.relative_to(bundle)}")
        if not path.is_file() or not is_macho(path):
            continue
        architectures = subprocess.check_output(["lipo", "-archs", str(path)], text=True).split()
        if architectures != ["arm64"]:
            raise ValueError(f"Expected arm64-only Mach-O: {path.relative_to(bundle)} ({architectures})")
        subprocess.run(["codesign", "--verify", "--strict", str(path)], check=True, capture_output=True)
        check_native_dependencies(path)
        native_count += 1
    subprocess.run(["codesign", "--verify", "--deep", "--strict", str(bundle)], check=True, capture_output=True)
    return {"success": True, "version": version, "runtimeIdentifier": "osx-arm64", "runtimeVersion": next(iter(runtime_versions)),
            "components": len(COMPONENTS), "nativeMachOFiles": native_count, "manifestFiles": len(manifest["files"]),
            "bluetoothHardwareVerified": False, "notarized": False}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("--version")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    report = check_bundle(args.bundle, args.version)
    if args.output:
        args.output.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report))


if __name__ == "__main__":
    main()
