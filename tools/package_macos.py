#!/usr/bin/env python3
"""Assemble and sign the five-process Apple silicon application; create its DMG.

Only generated publish directories are inputs. Original native artifacts and
their build manifests are never modified by signing the packaged copies.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

from verify_macos_package import COMPONENTS, check_bundle, is_macho


def run(*arguments: object) -> None:
    command = [str(a) for a in arguments]
    result = subprocess.run(command, text=True, capture_output=True)
    if result.returncode:
        raise RuntimeError(f"{Path(command[0]).name} failed ({result.returncode}): {(result.stdout + result.stderr)[-3000:]}")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def verify_entry_points(bundle: Path, version: str) -> list[dict]:
    """Exercise only the early read-only check, with no developer SDK on PATH."""
    environment = {key: value for key, value in os.environ.items() if not key.startswith(("DOTNET_", "DYLD_"))}
    environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin"
    environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"
    reports = []
    for component in COMPONENTS:
        app = bundle if component == "HeyMelody" else bundle / "Contents/Helpers" / f"{component}.app"
        executable = app / "Contents/MacOS" / component
        result = subprocess.run([str(executable), "--verify-installation"], cwd=bundle.parent,
                                env=environment, text=True, capture_output=True, timeout=45)
        if result.returncode:
            raise RuntimeError(f"Installed {component} check failed ({result.returncode}): "
                               f"{(result.stdout + result.stderr)[-2000:]}")
        report = json.loads(result.stdout.strip())
        expected = {"success": True, "component": component, "version": version,
                    "runtimeIdentifier": "osx-arm64", "resourceBundles": True,
                    "nativeLibrariesLoaded": True, "nativeBluetoothCalls": 0, "audioPlaybackCalls": 0}
        if any(report.get(key) != value for key, value in expected.items()):
            raise ValueError(f"Unexpected installed {component} check result: {report}")
        reports.append(report)
    return reports


def verify_metal_backend(bundle: Path) -> dict:
    """Create and destroy a real Metal engine from the installed signed payload."""
    native = bundle / "Contents/MacOS"
    environment = {key: value for key, value in os.environ.items() if not key.startswith(("DOTNET_", "DYLD_"))}
    environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin"
    result = subprocess.run([str(native / "hm_filament_probe"), "--backend", "metal"],
                            cwd=native, env=environment, text=True, capture_output=True, timeout=45)
    expected = "Filament ABI 2 backend metal created and destroyed"
    if result.returncode or expected not in result.stdout.splitlines():
        raise RuntimeError(f"Installed Metal probe failed ({result.returncode}): "
                           f"{(result.stdout + result.stderr)[-2000:]}")
    return {"executed": True, "success": True, "abiVersion": 2, "requestedBackend": "Metal", "actualBackend": "Metal",
            "scope": "signed installed payload; native engine creation and destruction; no screenshots or Bluetooth"}


def safe_child(root: Path, relative: str) -> Path:
    child = (root / relative).resolve()
    if root.resolve() not in child.parents:
        raise ValueError(f"Path escapes its input directory: {relative}")
    return child


def make_icon(repo: Path, destination: Path, temporary: Path) -> None:
    iconset = temporary / "HeyMelody.iconset"
    iconset.mkdir()
    source = repo / "src/HeyMelody.App/Assets/HeyMelody.png"
    for logical in (16, 32, 128, 256, 512):
        for scale in (1, 2):
            target = iconset / f"icon_{logical}x{logical}{'@2x' if scale == 2 else ''}.png"
            run("sips", "-z", logical * scale, logical * scale, source, "--out", target)
    run("iconutil", "-c", "icns", iconset, "-o", destination)


def export_notices(repo: Path, resources: Path) -> None:
    """Resolve notices from each actual restore, including project-specific packages."""
    packages: dict[str, dict] = {}
    package_folders: set[Path] = set()
    for component in COMPONENTS:
        project = "HeyMelody.App" if component == "HeyMelody" else component
        assets_path = repo / "src" / project / "obj/project.assets.json"
        assets = json.loads(assets_path.read_text(encoding="utf-8"))
        folders = [Path(folder) for folder in assets["packageFolders"]]
        package_folders.update(folders)
        for name, library in assets["libraries"].items():
            if library["type"] != "package" or name in packages:
                continue
            package = next((safe_child(folder, library["path"]) for folder in folders
                            if safe_child(folder, library["path"]).is_dir()), None)
            if package is None:
                raise FileNotFoundError(f"Resolved package missing: {name}")
            metadata = next(package.glob("*.nuspec"))
            element = ET.parse(metadata).getroot()
            metadata_node = next(node for node in element if node.tag.rsplit("}", 1)[-1] == "metadata")
            fields = {node.tag.rsplit("}", 1)[-1]: node for node in metadata_node}
            relative_base = Path("licenses/nuget") / name
            target = resources / relative_base
            target.mkdir(parents=True, exist_ok=True)
            shutil.copy2(metadata, target / metadata.name)
            selected = {entry for entry in library.get("files", []) if re.search(
                r"(^|/)(license|licence|copying|copyright|notice|third[-_]party[-_]notices)([._-]|$)", entry, re.I)}
            license_node = fields.get("license")
            if license_node is not None and license_node.attrib.get("type") == "file" and license_node.text:
                selected.add(license_node.text)
            copied = []
            for entry in sorted(selected):
                source = safe_child(package, entry)
                if not source.is_file():
                    raise FileNotFoundError(f"Notice missing from package {name}: {entry}")
                output = safe_child(target, entry)
                output.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, output)
                copied.append({"path": output.relative_to(resources).as_posix(), "sha256": sha256(output)})
            packages[name] = {"package": name, "metadata": (relative_base / metadata.name).as_posix(),
                              "license": license_node.text if license_node is not None else None, "files": copied}
    upstream = resources / "licenses/upstream"
    shutil.copytree(repo / "packaging/licenses", upstream)
    runtime_config = json.loads((resources.parent / "MacOS/HeyMelody.runtimeconfig.json").read_text())
    runtime_version = runtime_config["runtimeOptions"]["includedFrameworks"][0]["version"]
    runtime_package = next((folder / "microsoft.netcore.app.runtime.osx-arm64" / runtime_version
                            for folder in package_folders
                            if (folder / "microsoft.netcore.app.runtime.osx-arm64" / runtime_version).is_dir()), None)
    if runtime_package is None:
        raise FileNotFoundError(f"Resolved .NET runtime pack is missing: {runtime_version}")
    runtime_files = {path.name.lower(): path for path in runtime_package.iterdir() if path.is_file()}
    runtime_notices = resources / "licenses/dotnet" / runtime_version
    runtime_notices.mkdir(parents=True)
    for name in ("license.txt", "third-party-notices.txt"):
        source = runtime_files.get(name)
        if source is None:
            raise FileNotFoundError(f".NET runtime notice missing: {name}")
        shutil.copy2(source, runtime_notices / source.name)
    write_json(resources / "PACKAGE-NOTICES.json", {"schemaVersion": 1,
               "source": "resolved NuGet project.assets.json for all five entry points",
               "packages": [packages[key] for key in sorted(packages)],
               "supplementalNotices": [{"path": item.relative_to(resources).as_posix(), "sha256": sha256(item)}
                                       for item in sorted(upstream.rglob("*")) if item.is_file()]})


def bundle_info(component: str, version: str) -> dict:
    suffix = "" if component == "HeyMelody" else "." + component.removeprefix("HeyMelody.").lower()
    return {"CFBundleName": "HeyMelody", "CFBundleDisplayName": "HeyMelody",
            "CFBundleIdentifier": "io.github.cberyellowstone.heymelody" + suffix,
            "CFBundleExecutable": component, "CFBundlePackageType": "APPL",
            "CFBundleShortVersionString": version, "CFBundleVersion": version,
            "CFBundleIconFile": "HeyMelody.icns", "CFBundleDevelopmentRegion": "zh_CN",
            "LSMinimumSystemVersion": "15.0", "LSApplicationCategoryType": "public.app-category.utilities",
            "LSUIElement": component != "HeyMelody", "NSHighResolutionCapable": True,
            "NSPrincipalClass": "NSApplication",
            "NSBluetoothAlwaysUsageDescription": "HeyMelody 需要访问蓝牙以连接和控制您已配对的 OPPO、OnePlus 或 realme 耳机。"}


def copy_validation(bundle: Path, receipts: list[Path]) -> None:
    target = bundle / "Contents/Resources/validation"
    if receipts:
        target.mkdir(parents=True, exist_ok=True)
    with (bundle / "Contents/Info.plist").open("rb") as stream:
        expected_version = plistlib.load(stream)["CFBundleShortVersionString"]
    for source in receipts:
        data = json.loads(source.read_text(encoding="utf-8"))
        if (data.get("success") is not True or data.get("version") != expected_version
                or data.get("runtimeIdentifier") != "osx-arm64"):
            raise ValueError(f"Validation receipt must report success for this version and osx-arm64: {source}")
        shutil.copy2(source, target / f"validation-{sha256(source)}.json")


def sign_inner(repo: Path, bundle: Path) -> None:
    # No --deep signing: every code item is signed explicitly from the inside out.
    for path in sorted(bundle.rglob("*")):
        if not path.is_file() or path.is_symlink() or path.name in COMPONENTS:
            continue
        if is_macho(path):
            run("codesign", "--force", "--sign", "-", "--timestamp=none", path)
    native = bundle / "Contents/MacOS"
    manifest_path = native / "filament-runtime.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    for entry in manifest["files"]:
        entry["sha256"] = sha256(safe_child(native, entry["path"]))
    write_json(manifest_path, manifest)
    # Avalonia's documented flat .NET bundle layout requires signing every
    # file under MacOS, including managed assemblies and configuration data:
    # https://docs.avaloniaui.net/docs/deployment/macos/
    # Generic signatures use extended attributes, preserved by ditto and the
    # DMG. Refresh native hashes above before sealing these non-Mach-O files.
    for application in (bundle, *sorted((bundle / "Contents/Helpers").glob("*.app"))):
        for path in sorted((application / "Contents/MacOS").rglob("*")):
            if path.is_file() and not path.is_symlink() and path.name not in COMPONENTS and not is_macho(path):
                run("codesign", "--force", "--sign", "-", "--timestamp=none", path)
    for helper in sorted((bundle / "Contents/Helpers").glob("*.app")):
        run("codesign", "--force", "--sign", "-", "--timestamp=none", "--entitlements",
            repo / "packaging/macos/entitlements.plist", helper)


def _restore_probe_executable(native_root: Path) -> None:
    # Artifact transports can strip executable bits. Only repair the staged copy.
    probe = native_root / "hm_filament_probe"
    probe.chmod(probe.stat().st_mode | 0o111)


def thin_native_libraries(bundle: Path) -> None:
    # NuGet's macOS graphics packages can contain universal2 binaries. Produce
    # an arm64-only distribution without changing cached NuGet packages.
    for path in bundle.rglob("*"):
        if not path.is_file() or path.is_symlink() or not is_macho(path):
            continue
        architectures = subprocess.check_output(["lipo", "-archs", str(path)], text=True).split()
        if "arm64" not in architectures:
            raise ValueError(f"Native dependency lacks Apple silicon code: {path.name}")
        if len(architectures) > 1:
            temporary = path.with_name(path.name + ".arm64")
            run("lipo", path, "-thin", "arm64", "-output", temporary)
            temporary.chmod(path.stat().st_mode)
            temporary.replace(path)


def write_manifest(bundle: Path, version: str, source_manifest_hash: str) -> None:
    entries = [{"path": path.relative_to(bundle).as_posix(), "sha256": sha256(path)}
               for path in sorted(bundle.rglob("*"))
               if path.is_file() and not path.is_symlink() and "_CodeSignature" not in path.parts
               and path not in (bundle / "Contents/Resources/manifest.json", bundle / "Contents/MacOS/HeyMelody")]
    write_json(bundle / "Contents/Resources/manifest.json", {"schemaVersion": 1, "version": version,
        "runtimeIdentifier": "osx-arm64", "minimumMacOS": "15.0", "signing": "ad-hoc",
        "notarized": False, "bluetoothHardwareVerified": False,
        "filamentSourceManifestSha256": source_manifest_hash, "components": list(COMPONENTS),
        "mainExecutableIntegrity": "outer bundle code signature; hashing it here would be self-referential", "files": entries})


def stage(args: argparse.Namespace) -> None:
    repo, bundle = args.repo.resolve(), args.bundle.resolve()
    if bundle.exists():
        raise FileExistsError(f"Use a fresh staging directory; bundle already exists: {bundle}")
    native_manifest = args.filament_runtime / "filament-runtime.json"
    native = json.loads(native_manifest.read_text(encoding="utf-8-sig"))
    if native["rid"] != "osx-arm64" or native["supportedBackends"] != ["Metal"]:
        raise ValueError("A complete osx-arm64 Metal native runtime is required")
    for entry in native["files"]:
        if sha256(safe_child(args.filament_runtime, entry["path"])) != entry["sha256"]:
            raise ValueError(f"Native source artifact checksum mismatch: {entry['path']}")
    bundle.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="heymelody-icons-", dir=bundle.parent) as temporary:
        icon = Path(temporary) / "HeyMelody.icns"
        make_icon(repo, icon, Path(temporary))
        for component in COMPONENTS:
            project = "HeyMelody.App" if component == "HeyMelody" else component
            destination = bundle if component == "HeyMelody" else bundle / "Contents/Helpers" / (component + ".app")
            contents = destination / "Contents"
            shutil.copytree(args.publish_root / project, contents / "MacOS")
            resources = contents / "Resources"
            resources.mkdir()
            shutil.copy2(icon, resources / "HeyMelody.icns")
            with (contents / "Info.plist").open("wb") as stream:
                plistlib.dump(bundle_info(component, args.version), stream)
    main = bundle / "Contents/MacOS"
    resources = bundle / "Contents/Resources"
    for component in COMPONENTS:
        base = main if component == "HeyMelody" else bundle / "Contents/Helpers" / (component + ".app") / "Contents/MacOS"
        source = base / "ResourceBundles"
        if source.exists():
            if component == "HeyMelody":
                shutil.move(str(source), resources / "ResourceBundles")
            else:
                shutil.rmtree(source)
        nested = base / "runtimes/osx-arm64/native"
        if nested.exists():
            shutil.rmtree(nested)
        for symbols in base.rglob("*.pdb"):
            symbols.unlink()
    shutil.copytree(args.filament_runtime, main, dirs_exist_ok=True)
    _restore_probe_executable(main)
    for name in ("SOURCE-NOTES.md",):
        shutil.copy2(repo / "packaging" / name, resources / name)
    shutil.copy2(repo / "packaging/macos/README.md", resources / "README-使用说明.md")
    export_notices(repo, resources)
    copy_validation(bundle, args.validation)
    thin_native_libraries(bundle)
    sign_inner(repo, bundle)
    write_manifest(bundle, args.version, sha256(native_manifest))
    run("codesign", "--force", "--sign", "-", "--timestamp=none", "--entitlements",
        repo / "packaging/macos/entitlements.plist", bundle)
    report = check_bundle(bundle, args.version)
    entry_reports = ({"executed": False, "reason": "User requested packaging without further runtime tests"}
                     if args.skip_runtime_checks else verify_entry_points(bundle, args.version))
    print(json.dumps({"bundle": str(bundle), **report, "runtimeChecksExecuted": not args.skip_runtime_checks,
                      "entryPointVerification": entry_reports}, ensure_ascii=False))


def dmg(args: argparse.Namespace) -> None:
    bundle, output = args.bundle.resolve(), args.output.resolve()
    check_bundle(bundle, args.version)
    if output.exists():
        raise FileExistsError(f"Refusing to overwrite an existing DMG: {output}")
    if args.validation:
        source_hash = json.loads((bundle / "Contents/Resources/manifest.json").read_text())["filamentSourceManifestSha256"]
        copy_validation(bundle, args.validation)
        write_manifest(bundle, args.version, source_hash)
        run("codesign", "--force", "--sign", "-", "--timestamp=none", "--entitlements",
            args.repo / "packaging/macos/entitlements.plist", bundle)
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="heymelody-dmg-", dir=output.parent) as temporary:
        image_root = Path(temporary) / "image"
        image_path = Path(temporary) / "HeyMelody.dmg"
        image_root.mkdir()
        run("ditto", bundle, image_root / "HeyMelody.app")
        (image_root / "Applications").symlink_to("/Applications", target_is_directory=True)
        shutil.copy2(args.repo / "packaging/macos/README.md", image_root / "使用说明.md")
        run("hdiutil", "create", "-volname", f"HeyMelody {args.version}", "-srcfolder", image_root,
            "-format", "UDZO", "-imagekey", "zlib-level=9", image_path)
        run("codesign", "--force", "--sign", "-", "--timestamp=none", image_path)
        run("codesign", "--verify", "--strict", image_path)
        run("hdiutil", "verify", image_path)
        mounted_report = installed_report = entry_reports = metal_report = {
            "executed": False, "reason": "User requested packaging without further runtime or installation tests"}
        if not args.skip_runtime_checks:
            with tempfile.TemporaryDirectory(prefix="heymelody-volume-", dir=output.parent) as mount:
                run("hdiutil", "attach", "-readonly", "-nobrowse", "-mountpoint", mount, image_path)
                try:
                    mounted_report = check_bundle(Path(mount) / "HeyMelody.app", args.version)
                    if os.readlink(Path(mount) / "Applications") != "/Applications":
                        raise ValueError("The DMG is missing its Applications installation link")
                    with tempfile.TemporaryDirectory(prefix="heymelody-installed-", dir=output.parent) as installed:
                        installed_bundle = Path(installed) / "HeyMelody 安装检查.app"
                        run("ditto", Path(mount) / "HeyMelody.app", installed_bundle)
                        installed_report = check_bundle(installed_bundle, args.version)
                        entry_reports = verify_entry_points(installed_bundle, args.version)
                        metal_report = ({"executed": False, "reason": "Metal probe skipped by explicit build option"}
                                        if args.skip_metal_check else verify_metal_backend(installed_bundle))
                finally:
                    run("hdiutil", "detach", mount)
        image_path.replace(output)
    report = {"schemaVersion": 1, "success": True, "dmg": output.name, "sha256": sha256(output),
              "version": args.version, "runtimeIdentifier": "osx-arm64", "signing": "ad-hoc", "notarized": False,
              "diskImageSignatureVerified": True,
              "runtimeChecksExecuted": not args.skip_runtime_checks,
              "metalProbeExecuted": not args.skip_runtime_checks and not args.skip_metal_check,
              "scope": ("package assembly, signing and integrity only; further tests stopped at user request"
                        if args.skip_runtime_checks else
                        "package, mounted installation and entry-point verification; Metal probe explicitly skipped"
                        if args.skip_metal_check else "package, mounted installation and native runtime verification"),
              "bluetoothHardwareVerified": False, "mainExecutableSha256": sha256(bundle / "Contents/MacOS/HeyMelody"),
              "bundleVerification": check_bundle(bundle, args.version), "mountedImageVerification": mounted_report,
              "installedCopyVerification": installed_report, "installedEntryPointVerification": entry_reports,
              "installedMetalProbeVerification": metal_report}
    write_json(output.with_suffix(".json"), report)
    print(json.dumps({"dmg": str(output), "sha256": report["sha256"], "version": args.version,
                      "signing": "ad-hoc", "notarized": False}, ensure_ascii=False))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    subcommands = parser.add_subparsers(dest="command", required=True)
    for command in ("stage", "dmg"):
        child = subcommands.add_parser(command)
        child.add_argument("--repo", type=Path, required=True)
        child.add_argument("--bundle", type=Path, required=True)
        child.add_argument("--version", required=True)
        child.add_argument("--validation", type=Path, action="append", default=[])
        child.add_argument("--skip-runtime-checks", action="store_true",
                           help="Skip application execution and DMG installation checks; retain integrity and signature checks")
        if command == "stage":
            child.add_argument("--publish-root", type=Path, required=True)
            child.add_argument("--filament-runtime", type=Path, required=True)
        else:
            child.add_argument("--output", type=Path, required=True)
            child.add_argument("--skip-metal-check", action="store_true",
                               help="Skip only the Metal engine probe; retain mounted installation and entry-point checks")
    args = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.version):
        parser.error("version must have three numeric components")
    (stage if args.command == "stage" else dmg)(args)


if __name__ == "__main__":
    main()
