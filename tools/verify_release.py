"""Static Avalonia release checks; hardware evidence is explicit and hash-bound."""
import argparse
import base64
import datetime
import hashlib
import json
import pathlib
import re
from pathlib import Path
import struct
import xml.etree.ElementTree as ET

if __package__:
    from .verify_ui_assets import verify_packaged_ui_assets
else:
    from verify_ui_assets import verify_packaged_ui_assets


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify_filament(directory, rid, require_current=False, check_managed=True):
    def runtime_file(relative):
        assert isinstance(relative, str) and relative and "\\" not in relative
        assert not pathlib.PurePosixPath(relative).is_absolute()
        assert not any(part in ("", ".", "..") for part in relative.split("/")), "Unsafe Filament path"
        candidate = directory / relative
        for item in (candidate, *candidate.parents):
            if item == directory.parent:
                break
            assert not item.is_symlink() and not getattr(item.lstat(), "st_file_attributes", 0) & 0x400, "Filament runtime cannot use links"
        return checked_file(directory, relative)

    manifest_path = runtime_file("filament-runtime.json")
    manifest = json.loads(manifest_path.read_text("utf-8-sig"))
    current = manifest["schemaVersion"] == 2 and manifest["abiVersion"] == 2
    legacy = manifest["schemaVersion"] == 1 and manifest["abiVersion"] == 1
    assert (current or legacy) and (not require_current or current), "Unsupported Filament ABI/schema"
    assert manifest["filamentCommit"] == "ea31afeaf66339e7414aa648aa76c7e61adbde28"
    assert manifest["rid"] == rid
    library, probe = "HeyMelody.Filament.Native.dll", "hm_filament_probe.exe"
    if current:
        assert manifest["supportedBackends"] == ["Vulkan", "OpenGL"]
        assert manifest["defaultBackend"] == "Vulkan"
        materials = manifest["materials"]
        assert materials["path"] == "materials/gltfio.uberz"
        assert materials["supportedBackends"] == manifest["supportedBackends"]
        assert re.fullmatch("[a-f0-9]{64}", materials["sha256"])
    else:
        assert manifest["backend"] == "OpenGL"
    files = manifest["files"]
    paths = [entry["path"] for entry in files]
    assert len(paths) == len(set(path.casefold() for path in paths)), "Duplicate Filament runtime files"
    required = [library, probe, materials["path"]] if current else [library]
    assert all(paths.count(path) == 1 for path in required)
    assert any(path.startswith("licenses/") for path in paths)
    if current:
        for prefix in ("third_party/vkmemalloc/", "third_party/smol-v/", "third_party/spirv-headers/", "libs/bluevk/"):
            assert any(path.startswith("licenses/Filament/" + prefix) for path in paths), "Missing Vulkan license"
    for entry in files:
        path = runtime_file(entry["path"])
        assert path.stat().st_size > 0 and re.fullmatch("[a-f0-9]{64}", entry["sha256"])
        assert sha(path) == entry["sha256"], "Filament runtime hash mismatch"
    if current:
        assert any(entry["path"] == materials["path"] and entry["sha256"] == materials["sha256"] for entry in files)
    machines = {"win-x64": 0x8664, "win-arm64": 0xaa64}
    assert rid in machines, "Unsupported Windows Filament runtime"
    for name in ([library, probe] if current else [library]):
        assert PE(runtime_file(name)).machine == machines[rid], "Filament PE architecture does not match its RID"
    if require_current or manifest.get("logCallbackExport") is not None:
        assert manifest.get("logCallbackExport") == "hm_set_log_callback", "Missing Filament diagnostic callback declaration"
        assert "hm_set_log_callback" in PE(runtime_file(library)).exports(), "Missing Filament diagnostic callback export"
    if check_managed:
        checked_file(directory, "HeyMelody.Rendering.Filament.dll")
    assert not (directory / "Ava3D.dll").exists()
    result = dict(schemaVersion=manifest["schemaVersion"], abiVersion=manifest["abiVersion"],
                  filamentCommit=manifest["filamentCommit"], library=library,
                  sha256=sha(directory / library), manifestSha256=sha(manifest_path))
    if current:
        result.update(supportedBackends=manifest["supportedBackends"], defaultBackend=manifest["defaultBackend"],
                      probe=probe, probeSha256=sha(directory / probe), materials=materials)
    else:
        result["backend"] = "OpenGL"
    return result


class PE:
    def __init__(self, path):
        self.data = path.read_bytes()
        if self.data[:2] != b"MZ":
            raise ValueError("Missing DOS header")
        base = struct.unpack_from("<I", self.data, 0x3c)[0]
        if self.data[base:base + 4] != b"PE\0\0":
            raise ValueError("Missing PE header")
        self.machine, count = struct.unpack_from("<HH", self.data, base + 4)
        optional_size = struct.unpack_from("<H", self.data, base + 20)[0]
        optional = base + 24
        magic = struct.unpack_from("<H", self.data, optional)[0]
        directories = optional + {0x10b: 96, 0x20b: 112}[magic]
        self.subsystem = struct.unpack_from("<H", self.data, optional + 68)[0]
        self.managed = struct.unpack_from("<I", self.data, directories + 14 * 8)[0] != 0
        self.resources, _ = struct.unpack_from("<II", self.data, directories + 16)
        self.export_rva, _ = struct.unpack_from("<II", self.data, directories)
        self.sections = []
        for index in range(count):
            entry = optional + optional_size + 40 * index
            virtual_size, rva, raw_size, raw = struct.unpack_from("<IIII", self.data, entry + 8)
            self.sections.append((rva, virtual_size, raw, raw_size))

    def offset(self, rva, size):
        for start, virtual_size, raw, raw_size in self.sections:
            if start <= rva and rva - start + size <= raw_size:
                offset = raw + rva - start
                if offset + size <= len(self.data):
                    return offset
        raise ValueError("PE RVA outside file data")

    def exports(self):
        if not self.export_rva:
            return set()
        directory = self.offset(self.export_rva, 40)
        count = struct.unpack_from("<I", self.data, directory + 24)[0]
        assert count <= 16384, "Invalid export name count"
        names_rva = struct.unpack_from("<I", self.data, directory + 32)[0]
        names = self.offset(names_rva, count * 4)
        result = set()
        for index in range(count):
            name_rva = struct.unpack_from("<I", self.data, names + index * 4)[0]
            start = self.offset(name_rva, 1)
            end = self.data.find(b"\0", start, min(len(self.data), start + 512))
            assert end >= 0, "Invalid PE export name"
            result.add(self.data[start:end].decode("ascii"))
        return result

    def icons(self, kinds=(3, 14)):
        root = self.offset(self.resources, 16)
        found = {}

        def walk(relative, route):
            if len(route) > 3:
                raise ValueError("Unexpected resource depth")
            directory = root + relative
            named, ids = struct.unpack_from("<HH", self.data, directory + 12)
            for index in range(named + ids):
                name, target = struct.unpack_from("<II", self.data, directory + 16 + index * 8)
                if not route and name not in kinds:
                    continue
                if name & 0x80000000:
                    raise ValueError("Unexpected named icon resource")
                branch = (*route, name)
                if target & 0x80000000:
                    walk(target & 0x7fffffff, branch)
                else:
                    rva, size = struct.unpack_from("<II", self.data, root + target)
                    position = self.offset(rva, size)
                    found[branch] = self.data[position:position + size]
        walk(0, ())
        return found


def verify_icon(executable, source):
    ico = source.read_bytes()
    reserved, kind, count = struct.unpack_from("<HHH", ico)
    assert reserved == 0 and kind == 1 and count == 11
    payloads = []
    for index in range(count):
        size, offset = struct.unpack_from("<II", ico, 6 + index * 16 + 8)
        assert offset + size <= len(ico)
        payloads.append(ico[offset:offset + size])
    resources = PE(executable).icons()
    groups = [(route, payload) for route, payload in resources.items() if route[0] == 14]
    assert len(groups) == 1
    route, group = groups[0]
    assert struct.unpack_from("<HHH", group) == (0, 1, count)
    assert len(group) == 6 + count * 14
    used = set()
    for index, expected in enumerate(payloads):
        size, identity = struct.unpack_from("<IH", group, 6 + index * 14 + 8)
        actual = resources[(3, identity, route[2])]
        assert size == len(actual) and actual == expected
        used.add(identity)
    assert len([value for value in resources if value[0] == 3]) == len(used) == count
    return dict(frames=count, rawFramePayloadsMatch=True, sourceSha256=sha(source))


def checked_file(directory, relative):
    path = (directory / relative).resolve()
    assert path.is_relative_to(directory.resolve()) and path.is_file(), str(path)
    return path


COMPONENT_NAMES = {
    "gui": "HeyMelody", "host": "HeyMelody.Host",
    "deviceManager": "HeyMelody.DeviceManager", "fitTest": "HeyMelody.FitTest",
    "hearingSetup": "HeyMelody.HearingSetup",
}
EXPERIMENTAL_COMPILATION = {
    role: "native-aot" if role in ("gui", "host") else "ready-to-run"
    for role in COMPONENT_NAMES
}


def component_compilation(build, mode="ReadyToRun"):
    assert mode in ("ReadyToRun", "AotExperimental"), "Unknown compilation mode"
    expected = EXPERIMENTAL_COMPILATION if mode == "AotExperimental" else {
        role: "ready-to-run" for role in COMPONENT_NAMES
    }
    actual = build.get("componentCompilation", expected if mode == "ReadyToRun" else None)
    assert actual == expected, "Incomplete or invalid component compilation declaration"
    if mode == "AotExperimental":
        assert build["architecture"] == "x64" and build["rid"] == "win-x64", \
            "AotExperimental supports only win-x64"
    return actual


def verify_compilation_layout(folder, compilation):
    checked_file(folder, "coreclr.dll")
    backend = folder / "backend"
    for role, name in COMPONENT_NAMES.items():
        directory = backend if role == "host" else folder
        if compilation[role] == "native-aot":
            for suffix in (".dll", ".runtimeconfig.json", ".deps.json"):
                assert not (directory / (name + suffix)).exists(), \
                    "Unexpected managed AOT entry artifact: " + name + suffix
        else:
            for suffix in (".dll", ".runtimeconfig.json", ".deps.json"):
                checked_file(directory, name + suffix)
    if compilation["host"] == "native-aot":
        for name in ("coreclr.dll", "hostfxr.dll", "hostpolicy.dll"):
            assert not (backend / name).exists(), "AOT backend contains CoreCLR: " + name
    else:
        checked_file(backend, "coreclr.dll")


def pe_file_version(pe):
    versions = [blob for route, blob in pe.icons((16,)).items() if route[0] == 16]
    assert len(versions) == 1, "Missing or ambiguous executable version resource"
    blob = versions[0]
    end = 6
    while end + 2 <= len(blob) and blob[end:end + 2] != b"\0\0":
        end += 2
    assert blob[6:end].decode("utf-16-le") == "VS_VERSION_INFO"
    fixed = (end + 2 + 3) & ~3
    signature, _, most, least = struct.unpack_from("<IIII", blob, fixed)
    assert signature == 0xFEEF04BD, "Invalid executable version resource"
    return ".".join(str(value) for value in (most >> 16, most & 0xffff, least >> 16, least & 0xffff))


def package_license_identity(path):
    metadata = ET.parse(path).getroot().find("{*}metadata")
    assert metadata is not None, "Missing runtime package metadata"
    license_node = metadata.find("{*}license")
    repository = metadata.find("{*}repository")
    assert license_node is not None and repository is not None, "Missing runtime license/repository identity"
    return (license_node.get("type"), license_node.text.strip(), repository.get("url"), repository.get("commit"))


def package_sha512(value):
    assert isinstance(value, str), "Missing NuGet package SHA512"
    # .deps.json prefixes the algorithm; project.assets.json stores bare Base64.
    digest = base64.b64decode(value.removeprefix("sha512-"), validate=True)
    assert len(digest) == 64, "Invalid NuGet package SHA512 length"
    return digest


def verify_experimental_build(root, package, manifest, build, receipt=None):
    compilation = component_compilation(build, manifest.get("compilationMode"))
    folder = package / "x64"
    backend = folder / "backend"
    assert build["selfContained"] is True and build["hardwareExecutionVerified"] is False
    verify_compilation_layout(folder, compilation)
    toolchain = build["toolchain"]
    assert re.fullmatch(r"10\.\d+\.\d+", toolchain["sdkVersion"]), "Unexpected SDK version"
    assert re.fullmatch(r"10\.\d+\.\d+", toolchain["runtimeVersion"]), "Unexpected runtime version"
    assert toolchain["ilCompilerPackages"], "Native AOT compiler identity is missing"
    for compiler in toolchain["ilCompilerPackages"]:
        assert "ilcompiler" in compiler["package"].lower()
        assert compiler["version"] == toolchain["runtimeVersion"], "Compiler/runtime version mismatch"
    assert build["dependencyEvidence"] == "x64/BUILD-DEPENDENCIES.json"
    evidence_path = checked_file(package, build["dependencyEvidence"])
    assert sha(evidence_path) == build["dependencyEvidenceSha256"], "Dependency evidence hash mismatch"
    evidence = json.loads(evidence_path.read_text("utf-8-sig"))
    assert evidence["schemaVersion"] == 1
    assert evidence["sdkVersion"] == toolchain["sdkVersion"] and evidence["runtimeVersion"] == toolchain["runtimeVersion"]
    license_sources = evidence["runtimeLicenseSources"]
    assert set(license_sources) == {"gui", "host"}, "Missing runtime license provenance"
    for role, package_name in (("gui", "Microsoft.NETCore.App.Runtime.win-x64"),
                               ("host", "runtime.win-x64.Microsoft.DotNet.ILCompiler")):
        source = license_sources[role]
        assert source["package"] == package_name and source["version"] == toolchain["runtimeVersion"]
        assert {item["path"] for item in source["files"]} == {"LICENSE.txt", "THIRD-PARTY-NOTICES.txt"}
        directory = backend if role == "host" else folder
        represented_metadata = next(item for item in source["files"] if item["path"] == "THIRD-PARTY-NOTICES.txt")
        assert represented_metadata["sourcePackage"] == package_name and represented_metadata["basis"] == "own-package"
        represented_identity = package_license_identity(checked_file(directory, represented_metadata["sourceMetadata"]))
        for item in source["files"]:
            assert sha(checked_file(directory, item["path"])) == item["sha256"]
            metadata_path = checked_file(directory, item["sourceMetadata"])
            assert sha(metadata_path) == item["sourceMetadataSha256"], "Runtime notice metadata hash mismatch"
            assert item["sourceVersion"] == toolchain["runtimeVersion"]
            expected_source_name = "LICENSE.TXT" if item["path"] == "LICENSE.txt" else "THIRD-PARTY-NOTICES.TXT"
            assert item["sourcePath"] == expected_source_name
            identity = package_license_identity(metadata_path)
            if item["basis"] == "matching-MIT-and-source-revision":
                assert role == "host" and item["path"] == "LICENSE.txt"
                assert item["sourcePackage"] == "Microsoft.NETCore.App.Runtime.win-x64"
                assert identity == represented_identity and identity[:2] == ("expression", "MIT")
                assert identity[2] and identity[3], "Missing shared license source revision"
            else:
                assert item["basis"] == "own-package" and item["sourcePackage"] == package_name
    components = {item["role"]: item for item in evidence["components"]}
    assert len(evidence["components"]) == len(components) == 5 and set(components) == set(COMPONENT_NAMES)
    notices_by_directory = {}
    for directory in (folder, backend):
        notices = json.loads(checked_file(directory, "PACKAGE-NOTICES.json").read_text("utf-8-sig"))
        assert notices["schemaVersion"] == 1 and notices["supplementalNotices"]
        declared = {(item["package"].lower(), item["version"]) for item in notices["packages"]}
        assert {"avalonia", "fluentavaloniaui", "skiasharp", "harfbuzzsharp"}.issubset({name for name, _ in declared})
        assert len(declared) == len(notices["packages"]), "Duplicate package notice"
        for item in notices["packages"]:
            checked_file(directory, item["metadata"])
            for notice in item["files"]:
                assert sha(checked_file(directory, notice["path"])) == notice["sha256"], "Package notice hash mismatch"
        for notice in notices["supplementalNotices"]:
            assert sha(checked_file(directory, notice["path"])) == notice["sha256"], "Supplemental notice hash mismatch"
        for filename in ("LICENSE.txt", "THIRD-PARTY-NOTICES.txt"):
            assert checked_file(directory, filename).stat().st_size > 0
        verify_packaged_ui_assets(manifest, directory, directory / "licenses/upstream")
        notices_by_directory[directory] = declared
    executables = []
    for role, name in COMPONENT_NAMES.items():
        directory = backend if role == "host" else folder
        relative = "x64/" + ("backend/" if role == "host" else "") + name + ".exe"
        assert build[role] == relative and build[role + "Framework"] == "net10.0-windows10.0.19041.0"
        executable = checked_file(package, relative)
        pe = PE(executable)
        assert pe.machine == 0x8664 and pe.subsystem == 2 and not pe.managed, "Wrong executable PE: " + role
        assert pe_file_version(pe) == manifest["version"] + ".0", "Wrong executable file version: " + role
        digest = sha(executable)
        assert digest == build[role + "Sha256"], "Executable hash mismatch: " + role
        component = components[role]
        assert component["compilation"] == compilation[role]
        assert component["executable"] == relative.removeprefix("x64/") and component["executableSha256"] == digest
        assert re.fullmatch(r"[0-9a-f]{64}", component["sourceAssetsSha256"]), "Invalid source assets digest"
        packages = {(item["package"].lower(), item["version"]): item for item in component["packages"]}
        assert len(packages) == len(component["packages"]), "Duplicate dependency package"
        assert {"avalonia", "fluentavaloniaui"}.issubset({key[0] for key in packages})
        assert any(name.startswith("HeyMelody.Desktop/") for name in component["projects"]), "Missing shared desktop build dependency"
        for key in packages:
            if not key[0].startswith("microsoft.netcore.app.runtime."):
                assert key in notices_by_directory[directory], "Missing dependency license: " + "/".join(key)
        if compilation[role] == "ready-to-run":
            config = json.loads(checked_file(directory, name + ".runtimeconfig.json").read_text("utf-8-sig"))["runtimeOptions"]
            assert config["includedFrameworks"] == [{"name": "Microsoft.NETCore.App", "version": toolchain["runtimeVersion"]}]
            dependencies = json.loads(checked_file(directory, name + ".deps.json").read_text("utf-8-sig"))
            assert dependencies["runtimeTarget"]["name"].endswith("/win-x64")
            for library, item in dependencies["libraries"].items():
                if item["type"] == "package":
                    package_name, version = library.split("/", 1)
                    expected = packages[(package_name.lower(), version)]
                    assert package_sha512(item["sha512"]) == package_sha512(expected["sha512"]), \
                        "Runtime/build package identity mismatch: " + library
            for dependency in ("HeyMelody.Desktop", "HeyMelody.Diagnostics"):
                assert any(key.startswith(dependency + "/") for key in dependencies["libraries"])
                assert PE(checked_file(directory, dependency + ".dll")).managed
            assert PE(checked_file(directory, name + ".dll")).managed
        executables.append(dict(role=role, path=relative, sha256=digest, compilation=compilation[role]))
    for directory in (folder, backend):
        for path in directory.glob("*.dll"):
            pe = PE(path)
            if directory == backend:
                assert not pe.managed, "AOT backend contains a managed dependency: " + path.name
            if not pe.managed:
                assert pe.machine == 0x8664, "Wrong native DLL architecture: " + str(path)
        for name in ("libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll"):
            assert PE(checked_file(directory, name)).machine == 0x8664
        for name in ("PresentationCore.dll", "PresentationFramework.dll", "System.Windows.Forms.dll", "wpfgfx_cor3.dll"):
            assert not (directory / name).exists(), "Unexpected WindowsDesktop dependency"
    assert build["hostResourceBundles"] == "gui" and not (backend / "ResourceBundles").exists()
    resources = verify_resource_bundle(folder)
    assert resources["indexSha256"] == build["resourceIndexSha256"]
    assert manifest["modelRenderer"] == "Filament"
    renderer = verify_filament(folder, "win-x64", require_current=True, check_managed=False)
    declared_renderer = build["modelRenderer"]
    for key in ("sha256", "manifestSha256", "abiVersion", "filamentCommit", "library",
                "supportedBackends", "defaultBackend", "probe", "probeSha256", "materials"):
        assert declared_renderer[key] == renderer[key], "Filament release metadata mismatch: " + key
    nested = folder / "runtimes/win-x64/native"
    if (nested / "filament-runtime.json").exists():
        assert verify_filament(nested, "win-x64", require_current=True, check_managed=False)["manifestSha256"] == renderer["manifestSha256"]
    assert not (folder / "Ava3D.dll").exists()
    icon = verify_icon(folder / "HeyMelody.exe", root / "src/HeyMelody.App/Assets/HeyMelody.ico")
    if receipt:
        for role in COMPONENT_NAMES:
            assert receipt[role + "Sha256"] == build[role + "Sha256"], "Execution evidence hash mismatch"
    return dict(architecture="x64", peMachine="0x8664", selfContained=True,
                compilationMode="AotExperimental", componentCompilation=compilation, toolchain=toolchain,
                executables=executables, modelRenderer=renderer, icon=icon,
                guiSha256=build["guiSha256"], hostSha256=build["hostSha256"],
                deviceManagerSha256=build["deviceManagerSha256"], fitTestSha256=build["fitTestSha256"],
                hearingSetupSha256=build["hearingSetupSha256"], resourceBundles=dict(gui=resources, host=dict(sharedWith="gui")),
                packageNoticesVerified=True, readOnlyBluetoothExecutionVerified=False,
                realNativeArmHardwareVerified=False, liveGuiPreviewVerified=False)


def verify_shared_desktop(entrypoints):
    desktop_release = any(
        any(key.startswith("HeyMelody.Desktop/") for key in libraries)
        or (directory / "HeyMelody.Desktop.dll").exists()
        for _, directory, libraries in entrypoints
    )
    if not desktop_release:
        return
    expected = {"HeyMelody", "HeyMelody.DeviceManager", "HeyMelody.Host",
                "HeyMelody.FitTest", "HeyMelody.HearingSetup"}
    assert {name for name, _, _ in entrypoints} == expected, "Incomplete shared desktop entrypoints"
    for name, directory, libraries in entrypoints:
        checked_file(directory, "HeyMelody.Desktop.dll")
        assert any(key.startswith("HeyMelody.Desktop/") for key in libraries), \
            "Missing shared desktop dependency: " + name


def verify_resource_bundle(directory):
    bundle = directory / "ResourceBundles"
    index = checked_file(bundle, "artwork.json")
    rows = json.loads(index.read_text("utf-8"))
    provenance = json.loads(checked_file(bundle, "provenance.json").read_text("utf-8"))
    reference = Path(__file__).resolve().parents[1] / "src/HeyMelody.Resources/Bundles"
    manifests = {"artwork.json", "provenance.json", "supplemental.json"}
    assert {p.name for p in bundle.iterdir() if p.is_file()} == manifests, "Unexpected offline manifest"
    for name in manifests:
        assert sha(checked_file(bundle, name)) == sha(checked_file(reference, name)), "Offline manifest differs from source"
    assert len(provenance) == 1 and provenance[0]["package"] == "com.heytap.headset"
    assert "source" not in provenance[0] and rows, "Invalid HeyMelody provenance"
    reachable = set()
    supplemental = json.loads((bundle / "supplemental.json").read_text("utf-8"))
    assert supplemental and all(row["package"] == "com.heytap.headset" for row in supplemental)
    for row in rows + supplemental:
        assert "source" not in row, "Artwork source selection is no longer supported"
        path = checked_file(bundle, row["path"])
        assert path.stat().st_size == row["length"] and sha(path) == row["sha256"]
        reachable.add(row["path"])
        if row.get("originalPath"):
            assert sha(checked_file(bundle, row["originalPath"])) == row["originalSha256"]
            reachable.add(row["originalPath"])
        if row.get("kind") == "lottie":
            for asset in json.loads(path.read_text("utf-8")).get("assets", []):
                relative = str(asset.get("u", "")) + str(asset.get("p", ""))
                if not relative or relative.startswith("data:"):
                    continue
                assert ":" not in relative and ".." not in Path(relative).parts, "Unsafe animation dependency"
                dependency = checked_file(path.parent, relative)
                name = dependency.relative_to(bundle).as_posix()
                assert sha(dependency) == sha(checked_file(reference, name)), "Animation dependency differs from source"
                reachable.add(name)
    actual = {p.relative_to(bundle).as_posix() for p in (bundle / "data").rglob("*") if p.is_file()}
    assert actual == reachable, "Offline artwork contains missing or unreferenced files"
    checked_file(directory, "HeyMelody.Resources.dll")
    checked_file(directory, "licenses/upstream/Extracted-Earphone-Resources-NOTICE.txt")
    return dict(artworkEntries=len(rows), catalogCount=1, dataFiles=len(reachable), indexSha256=sha(index), allIndexedBytesVerified=True)


def verify(root, package, evidence=None, architectures=("x64", "x86", "arm64")):
    manifest = json.loads((package / "manifest.json").read_text("utf-8-sig"))
    assert manifest["schemaVersion"] == 2 and manifest["version"]
    resource_release = tuple(int(part) for part in manifest["version"].split(".")) >= (0, 8, 0)
    declared = {build["architecture"]: build for build in manifest["builds"]}
    machines = {"x64": 0x8664, "x86": 0x14c, "arm64": 0xaa64}
    requested = set(architectures)
    assert requested and requested.issubset(machines), "Invalid requested architectures"
    expected = {arch: machine for arch, machine in machines.items() if arch in requested}
    assert set(declared) == set(expected)
    receipts = {}
    if evidence is not None:
        assert evidence["version"] == manifest["version"] and evidence["success"]
        assert evidence["schemaVersion"] == 2, "Old release evidence cannot verify this build"
        receipts = {build["architecture"]: build for build in evidence["builds"]}
        assert set(receipts).issubset(expected)
    builds = []
    for arch, machine in expected.items():
        compilation = component_compilation(declared[arch], manifest.get("compilationMode", "ReadyToRun"))
        if manifest.get("compilationMode") == "AotExperimental":
            assert set(expected) == {"x64"}, "AotExperimental supports only one x64 package"
            builds.append(verify_experimental_build(root, package, manifest, declared[arch], receipts.get(arch)))
            continue
        folder = package / arch
        gui, host = folder / "HeyMelody.exe", folder / "backend" / "HeyMelody.Host.exe"
        manager = checked_file(folder, "HeyMelody.DeviceManager.exe")
        gui_assembly = checked_file(folder, "HeyMelody.dll")
        if manifest.get("diagnosticLogging") == "one-host-session-debug-jsonl-v1":
            assert sha(checked_file(folder, "HeyMelody.Diagnostics.dll")) == declared[arch]["guiDiagnosticsSha256"]
            assert sha(checked_file(host.parent, "HeyMelody.Diagnostics.dll")) == declared[arch]["hostDiagnosticsSha256"]
            for entry in ("HeyMelody", "HeyMelody.DeviceManager", "HeyMelody.FitTest", "HeyMelody.HearingSetup", "backend/HeyMelody.Host"):
                entry_deps = json.loads(checked_file(folder, entry + ".deps.json").read_text(encoding="utf-8-sig"))
                assert sum(name.startswith("HeyMelody.Diagnostics/") for name in entry_deps["libraries"]) == 1, entry
        components = [checked_file(folder, name + ".exe") for name in ("HeyMelody.FitTest", "HeyMelody.HearingSetup")] if resource_release else []
        native = [directory / filename for directory in (folder, host.parent)
                  for filename in ("libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll")]
        for path in [gui, manager, host, *components, folder / "coreclr.dll", host.parent / "coreclr.dll", *native]:
            assert PE(path).machine == machine, str(path)
        assert declared[arch]["rid"] == "win-" + arch and declared[arch]["selfContained"] is True
        assert declared[arch]["guiSha256"] == sha(gui) and declared[arch]["hostSha256"] == sha(host)
        assert declared[arch]["deviceManagerSha256"] == sha(manager)
        # Retain historical portable-GUI receipts while matching the current Windows-specific GUI TFM.
        gui_framework = declared[arch]["guiFramework"]
        assert gui_framework in ("net10.0", "net10.0-windows10.0.19041.0")
        assert declared[arch]["hostFramework"] == "net10.0-windows10.0.19041.0"
        assert declared[arch]["deviceManagerFramework"] == gui_framework
        if resource_release:
            for role, executable in zip(("fitTest", "hearingSetup"), components):
                assert declared[arch][role] == arch + "/" + executable.name
                assert declared[arch][role + "Sha256"] == sha(executable)
                assert declared[arch][role + "Framework"] == gui_framework
        gui_dependencies = None
        entrypoints = [("HeyMelody", folder), ("HeyMelody.DeviceManager", folder), ("HeyMelody.Host", host.parent)]
        entrypoints += [(path.stem, folder) for path in components]
        runtime_versions = set()
        desktop_entrypoints = []
        for name, directory in entrypoints:
            runtime = json.loads((directory / (name + ".runtimeconfig.json")).read_text("utf-8-sig"))["runtimeOptions"]
            assert runtime["tfm"] == "net10.0" and runtime.get("includedFrameworks")
            assert {entry["name"] for entry in runtime["includedFrameworks"]} == {"Microsoft.NETCore.App"}
            runtime_versions.update(entry["version"] for entry in runtime["includedFrameworks"])
            dependencies = json.loads((directory / (name + ".deps.json")).read_text("utf-8-sig"))
            assert dependencies["runtimeTarget"]["name"].endswith("/win-" + arch)
            assert (directory / "LICENSE.txt").is_file() and (directory / "THIRD-PARTY-NOTICES.txt").is_file()
            libraries = dependencies["libraries"]
            desktop_entrypoints.append((name, directory, libraries))
            assert any(key.startswith("Avalonia/") for key in libraries)
            assert any(key.startswith("FluentAvaloniaUI/") for key in libraries)
            if name == "HeyMelody":
                gui_dependencies = dependencies
            elif directory == folder:
                for library, item in libraries.items():
                    if item["type"] == "package":
                        assert library in gui_dependencies["libraries"] and item["sha512"] == gui_dependencies["libraries"][library]["sha512"]
        verify_shared_desktop(desktop_entrypoints)
        for directory in {directory for _, directory, _ in desktop_entrypoints}:
            verify_packaged_ui_assets(manifest, directory, directory / "licenses/upstream")
        assert len(runtime_versions) == 1, "Entrypoints do not share the exact runtime"
        renderer_packages = [key.split("/", 1) for key in gui_dependencies["libraries"]
                             if key.split("/", 1)[0].casefold() == "ava3dcontrol"]
        assert len(renderer_packages) <= 1, "Ambiguous Ava3DControl dependency versions"
        renderer = None
        if manifest.get("modelRenderer") == "Filament" or (folder / "HeyMelody.Rendering.Filament.dll").exists():
            assert manifest.get("modelRenderer") == "Filament", "Missing release renderer declaration"
            assert not renderer_packages, "Ava3D dependency remains in Filament release"
            renderer = verify_filament(folder, "win-" + arch,
                                       require_current=manifest.get("diagnosticLogging") == "one-host-session-debug-jsonl-v1")
            declared_renderer = declared[arch]["modelRenderer"]
            assert declared_renderer["success"] and declared_renderer["sha256"] == renderer["sha256"]
            assert declared_renderer["manifestSha256"] == renderer["manifestSha256"]
            for field in ("abiVersion", "filamentCommit", "library"):
                assert declared_renderer[field] == renderer[field], "Release renderer metadata mismatch"
            if renderer["abiVersion"] == 2:
                for field in ("supportedBackends", "defaultBackend", "probe", "probeSha256", "materials"):
                    assert declared_renderer[field] == renderer[field], "Release renderer capability mismatch"
                nested = folder / "runtimes" / ("win-" + arch) / "native"
                if any((nested / name).exists() for name in ("HeyMelody.Filament.Native.dll", "filament-runtime.json", "hm_filament_probe.exe")):
                    nested_renderer = verify_filament(nested, "win-" + arch, require_current=True, check_managed=False)
                    assert nested_renderer["manifestSha256"] == renderer["manifestSha256"], "Nested Filament runtime differs"
        elif renderer_packages:
            renderer_path = checked_file(folder, "Ava3D.dll")
            renderer = dict(package=renderer_packages[0][0], declaredVersion=renderer_packages[0][1],
                            assembly=renderer_path.name, sha256=sha(renderer_path))
        resources = None
        if resource_release:
            assert any(key.startswith("SkiaSharp.Skottie/") for key in gui_dependencies["libraries"])
            resources = dict(gui=verify_resource_bundle(folder))
            # Builds that declare it ship one ResourceBundles beside the GUI and the backend reads that copy; older builds carry two.
            if declared[arch].get("hostResourceBundles") == "gui":
                assert not (host.parent / "ResourceBundles").exists(), "Backend carries a second ResourceBundles"
                checked_file(host.parent, "HeyMelody.Resources.dll")
                checked_file(host.parent, "licenses/upstream/Extracted-Earphone-Resources-NOTICE.txt")
                resources["host"] = dict(sharedWith="gui")
            else:
                resources["host"] = verify_resource_bundle(host.parent)
            assert declared[arch]["resourceIndexSha256"] == resources["gui"]["indexSha256"]
        assert not (folder / "PresentationFramework.dll").exists()
        assert not (folder / "LICENSE-WINDOWSDESKTOP.txt").exists()
        for directory in (folder, host.parent):
            notices = json.loads((directory / "PACKAGE-NOTICES.json").read_text("utf-8-sig"))
            assert notices["schemaVersion"] == 1 and notices["packages"]
            assert {"Avalonia", "FluentAvaloniaUI", "SkiaSharp", "HarfBuzzSharp"}.issubset(
                {entry["package"] for entry in notices["packages"]})
            for entry in notices["packages"]:
                assert entry["version"] and checked_file(directory, entry["metadata"])
                for notice in entry["files"]:
                    assert sha(checked_file(directory, notice["path"])) == notice["sha256"]
            assert notices["supplementalNotices"]
            for notice in notices["supplementalNotices"]:
                assert sha(checked_file(directory, notice["path"])) == notice["sha256"]
        icon = verify_icon(gui, root / "src" / "HeyMelody.App" / "Assets" / "HeyMelody.ico")
        receipt = receipts.get(arch, {})
        if receipt:
            assert receipt["guiSha256"] == sha(gui) and receipt["hostSha256"] == sha(host)
            if resource_release:
                assert receipt["deviceManagerSha256"] == sha(manager)
                for role, executable in zip(("fitTest", "hearingSetup"), components):
                    assert receipt[role + "Sha256"] == sha(executable)
        executed = receipt.get("readOnlyBluetoothExecutionVerified", False)
        if executed:
            diagnostics = receipt["diagnostics"]
            assert diagnostics["productId"] == "06EC10" and diagnostics["applicationGetSubmissions"] > 0
            assert diagnostics["applicationSetSubmissions"] == 0 and diagnostics["applicationOtherSubmissions"] == 0
        builds.append(dict(architecture=arch, peMachine=hex(machine), selfContained=True,
                           guiSha256=sha(gui), guiAssemblySha256=sha(gui_assembly),
                           deviceManagerSha256=sha(manager), hostSha256=sha(host), icon=icon,
                           modelRenderer=renderer,
                           calibrationExecutables=[dict(name=path.name, sha256=sha(path)) for path in components],
                           resourceBundles=resources,
                           nativeRenderingAssets=[dict(name=path.name, sha256=sha(path)) for path in native],
                           packageNoticesVerified=True, readOnlyBluetoothExecutionVerified=executed,
                           liveGuiPreviewVerified=receipt.get("liveGuiPreviewVerified", False),
                           realNativeArmHardwareVerified=arch == "arm64" and receipt.get("nativeHardwareVerified", False)))
    return dict(schemaVersion=2, version=manifest["version"], verifiedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                success=True, builds=builds, supportedModel="capability-gated source catalogs" if resource_release else "OPPO Enco X4 / 06EC10",
                validationScope="static packaging with explicit execution receipts" if evidence else "static packaging only; no hardware execution verified",
                nativeArm64ExecutionVerified=any(build["realNativeArmHardwareVerified"] for build in builds))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--evidence", type=pathlib.Path, help="Optional schema-2 receipt for this version and exact GUI/host hashes")
    parser.add_argument("--architectures", nargs="+", choices=("x64", "x86", "arm64"),
                        default=("x64", "x86", "arm64"),
                        help="Architectures the package must contain; defaults to all three")
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[1]
    evidence = json.loads(args.evidence.read_text("utf-8-sig")) if args.evidence else None
    report = verify(root, args.package.resolve(), evidence, args.architectures)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, ensure_ascii=False)
        stream.write("\n")
    print(json.dumps(report, ensure_ascii=False))
