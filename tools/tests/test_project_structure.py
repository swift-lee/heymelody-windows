"""Portable architecture contracts; no SDK, GPU, platform commands or devices."""
from __future__ import annotations

import os
from pathlib import Path
import re
import tempfile
import unittest
import xml.etree.ElementTree as ET


REPO = Path(__file__).resolve().parents[2]
EXECUTABLE_TYPES = {"Exe", "WinExe"}


def projects_under(directory: Path) -> dict[Path, ET.Element]:
    projects = {}
    for current, directories, files in os.walk(directory):
        directories[:] = [name for name in directories if name not in {"bin", "obj", ".git"}]
        for name in files:
            if name.endswith(".csproj"):
                path = (Path(current) / name).resolve()
                projects[path] = ET.parse(path).getroot()
    return projects


def output_type(project: ET.Element) -> str:
    return next((item.text.strip() for item in project.iter("OutputType") if item.text), "Library")


def includes(project: ET.Element, kind: str):
    for item in project.iter(kind):
        for value in item.get("Include", "").split(";"):
            if value.strip():
                yield value.strip().replace("\\", "/")


def executable_compile_links(repo: Path) -> list[str]:
    projects = projects_under(repo / "src")
    executable_directories = {
        path.parent for path, project in projects.items() if output_type(project) in EXECUTABLE_TYPES
    }
    violations = []
    for path, project in projects.items():
        for include in includes(project, "Compile"):
            target = (path.parent / include).resolve()
            for executable in executable_directories:
                if executable != path.parent and target.is_relative_to(executable):
                    violations.append(f"{path.relative_to(repo)} Compile links executable source: {include}")
    return violations


def desktop_dependencies(repo: Path) -> list[str]:
    projects = projects_under(repo / "src")
    desktop = (repo / "src/HeyMelody.Desktop/HeyMelody.Desktop.csproj").resolve()
    if desktop not in projects:
        return ["The shared HeyMelody.Desktop library is missing."]
    violations = []
    if output_type(projects[desktop]) in EXECUTABLE_TYPES:
        violations.append("HeyMelody.Desktop must be a library, not an application entrypoint.")
    pending, visited = [desktop], set()
    while pending:
        path = pending.pop()
        if path in visited:
            continue
        visited.add(path)
        for reference in includes(projects[path], "ProjectReference"):
            target = (path.parent / reference).resolve()
            if target.name == "HeyMelody.Client.csproj" or (
                target in projects and output_type(projects[target]) in EXECUTABLE_TYPES
            ):
                violations.append(f"{path.relative_to(repo)} reverses the desktop boundary: {reference}")
            elif target in projects:
                pending.append(target)
    return violations


def workflow_jobs(text: str) -> dict[str, list[str]]:
    """Read literal job blocks only; shell-script text is not interpreted as YAML."""
    jobs: dict[str, list[str]] = {}
    in_jobs = False
    current = None
    for line in text.splitlines():
        if line == "jobs:":
            in_jobs = True
            continue
        if not in_jobs:
            continue
        if line and not line[0].isspace() and not line.startswith("#"):
            break
        match = re.fullmatch(r"  ([A-Za-z0-9_-]+):\s*(?:#.*)?", line)
        if match:
            current = match.group(1)
            jobs[current] = []
        elif current is not None:
            jobs[current].append(line)
    return jobs


def job_needs(lines: list[str]) -> list[str]:
    for index, line in enumerate(lines):
        match = re.fullmatch(r"    needs:\s*(.*?)(?:\s+#.*)?", line)
        if not match:
            continue
        inline = match.group(1).strip()
        if inline:
            values = inline.removeprefix("[").removesuffix("]").split(",")
        else:
            values = []
            for following in lines[index + 1:]:
                item = re.fullmatch(r"      -\s*([A-Za-z0-9_'\"-]+)\s*(?:#.*)?", following)
                if item:
                    values.append(item.group(1))
                elif following.strip() and not following.lstrip().startswith("#"):
                    break
        result = [value.strip(" '\"") for value in values]
        if any(not re.fullmatch(r"[A-Za-z0-9_-]+", value) for value in result):
            raise ValueError("DMG dependencies must be literal job names.")
        return result
    return []


def job_has_matrix(lines: list[str]) -> bool:
    for index, line in enumerate(lines):
        match = re.match(r"    strategy:\s*(.*)", line)
        if not match:
            continue
        if "matrix:" in match.group(1):
            return True
        for child in lines[index + 1:]:
            if child.strip() and len(child) - len(child.lstrip()) <= 4:
                break
            if re.match(r"\s+matrix:", child):
                return True
    return False


def dmg_dependencies(repo: Path) -> list[str]:
    workflow = repo / ".github/workflows/macos-build.yml"
    if not workflow.is_file():
        return ["The independent macOS application workflow is missing."]
    jobs = workflow_jobs(workflow.read_text(encoding="utf-8-sig"))
    if "package-arm64" not in jobs or "native-osx-arm64" not in jobs:
        return ["The DMG workflow must contain package-arm64 and its native-osx-arm64 prerequisite."]
    violations = []
    try:
        dependencies = job_needs(jobs["package-arm64"])
    except ValueError as error:
        violations.append(str(error))
    else:
        if dependencies != ["native-osx-arm64"]:
            violations.append("DMG packaging must depend only on native-osx-arm64, not a portability matrix.")
    if job_has_matrix(jobs["native-osx-arm64"]):
        violations.append("The DMG native prerequisite must not expand a cross-platform matrix.")
    filament = repo / ".github/workflows/filament-build.yml"
    if filament.is_file() and re.search(
        r"Build-MacOS\.ps1|Invoke-MacOSPackage\.ps1|package_macos\.py",
        filament.read_text(encoding="utf-8-sig"),
    ):
        violations.append("The Filament portability workflow must not own application DMG packaging.")
    return violations


class ProjectStructureTests(unittest.TestCase):
    def test_product_projects_do_not_compile_other_executable_sources(self):
        self.assertEqual(executable_compile_links(REPO), [])

    def test_desktop_has_no_client_or_application_dependency(self):
        self.assertEqual(desktop_dependencies(REPO), [])

    def test_dmg_is_independent_of_cross_platform_native_matrix(self):
        self.assertEqual(dmg_dependencies(REPO), [])


class ContractFixtureTests(unittest.TestCase):
    def test_cross_entry_source_link_is_rejected_but_resources_and_test_sharing_are_allowed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write(root, "src/App/App.csproj", '<PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup>')
            write(root, "src/Helper/Helper.csproj", '<PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>'
                  '<ItemGroup><Compile Include="../App/Typography.cs" />'
                  '<AvaloniaResource Include="../App/Assets/App.ico" /></ItemGroup>')
            write(root, "tests/Fixture/Fixture.csproj", '<ItemGroup>'
                  '<Compile Include="../../src/App/Typography.cs" />'
                  '<Compile Include="../Shared/Harness.cs" /></ItemGroup>')
            self.assertEqual(len(executable_compile_links(root)), 1)
            write(root, "src/Helper/Helper.csproj", '<PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>'
                  '<ItemGroup><Compile Include="Own.cs" />'
                  '<AvaloniaResource Include="../App/Assets/App.ico" /></ItemGroup>')
            self.assertEqual(executable_compile_links(root), [])

    def test_indirect_client_and_executable_dependencies_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write(root, "src/HeyMelody.Desktop/HeyMelody.Desktop.csproj", '<ItemGroup>'
                  '<ProjectReference Include="../Shared/Shared.csproj" /></ItemGroup>')
            write(root, "src/Shared/Shared.csproj", '<ItemGroup>'
                  '<ProjectReference Include="../HeyMelody.Client/HeyMelody.Client.csproj" />'
                  '<ProjectReference Include="../App/App.csproj" /></ItemGroup>')
            write(root, "src/App/App.csproj", '<PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup>')
            self.assertEqual(len(desktop_dependencies(root)), 2)

    def test_literal_needs_scalar_inline_and_block_lists(self):
        for needs in ("    needs: native-osx-arm64", "    needs: [native-osx-arm64]",
                      "    needs:\n      - native-osx-arm64"):
            with self.subTest(needs=needs):
                jobs = workflow_jobs("name: sample\njobs:\n  package-arm64:\n" + needs
                                     + "\n    steps:\n      - run: |\n          needs: fake\n")
                self.assertEqual(job_needs(jobs["package-arm64"]), ["native-osx-arm64"])

    def test_matrix_dependency_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workflow = root / ".github/workflows/macos-build.yml"
            workflow.parent.mkdir(parents=True)
            workflow.write_text("jobs:\n  native-osx-arm64:\n    strategy:\n      matrix:\n"
                                "        rid: [osx-arm64, linux-x64]\n  package-arm64:\n"
                                "    needs: [native-osx-arm64, portability]\n", encoding="utf-8")
            self.assertEqual(len(dmg_dependencies(root)), 2)


def write(root: Path, relative: str, content: str):
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("<Project>" + content + "</Project>", encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
