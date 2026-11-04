"""Hash-bound checks for the shared application fonts and upstream licenses."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

CONTRACT = Path(__file__).resolve().parents[1] / "packaging/licenses/UI-Assets-SOURCE.json"


def digest(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def checked_asset(root: Path, entry: dict) -> Path:
    path = (root / entry["path"]).resolve()
    if root.resolve() not in path.parents or not path.is_file():
        raise ValueError(f"Missing or unsafe UI asset: {entry['path']}")
    if digest(path) != entry["sha256"]:
        raise ValueError(f"UI asset checksum mismatch: {entry['path']}")
    return path


def verify_ui_assets(directory: Path, notices: Path) -> dict:
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    if contract["schemaVersion"] != 1:
        raise ValueError("Unsupported UI asset contract")
    for entry in contract["fonts"]:
        checked_asset(directory, entry)
    for entry in contract["licenses"]:
        checked_asset(notices, entry)
    checked_asset(notices, {"path": CONTRACT.name, "sha256": digest(CONTRACT)})
    return {"fontFamily": contract["fontFamily"], "fontVersion": contract["fontVersion"],
            "fonts": len(contract["fonts"]), "fontAndLicenseBytesVerified": True}


def verify_packaged_ui_assets(manifest: dict, directory: Path, notices: Path) -> dict | None:
    # Historical packages predate this contract. A declaration or copied receipt enables the full gate.
    declared = "uiAssets" in manifest
    if not declared and not (notices / CONTRACT.name).exists():
        return None
    if declared:
        expected = json.loads(CONTRACT.read_text(encoding="utf-8"))["capability"]
        if manifest["uiAssets"] != expected:
            raise ValueError("Unsupported packaged UI asset capability")
    return verify_ui_assets(directory, notices)
