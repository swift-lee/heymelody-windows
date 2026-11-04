"""Portable font and license checks; these do not load a native font engine."""
from contextlib import contextmanager
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

from tools import verify_ui_assets as verification


@contextmanager
def ui_fixture():
    with tempfile.TemporaryDirectory(prefix="ui-assets-test-") as temporary:
        root = Path(temporary)
        app, notices = root / "app", root / "notices"
        app.mkdir()
        notices.mkdir()
        contract = {"schemaVersion": 1, "capability": "fixture-v1", "fontFamily": "MiSans", "fontVersion": "fixture",
                    "fonts": [], "licenses": []}
        for key, base, names in (("fonts", app, ("Regular", "Semibold", "Bold")),
                                 ("licenses", notices, ("MiSans", "Fluent"))):
            for name in names:
                relative = f"fonts/{name}.otf" if key == "fonts" else name + ".txt"
                path = base / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                data = name.encode()
                path.write_bytes(data)
                contract[key].append({"path": relative, "sha256": hashlib.sha256(data).hexdigest()})
        source = root / "UI-Assets-SOURCE.json"
        data = json.dumps(contract).encode()
        source.write_bytes(data)
        (notices / source.name).write_bytes(data)
        with mock.patch.object(verification, "CONTRACT", source):
            yield app, notices, contract


class UiAssetsTests(unittest.TestCase):
    def test_complete_assets_are_accepted(self):
        with ui_fixture() as (app, notices, _):
            self.assertEqual(verification.verify_ui_assets(app, notices)["fonts"], 3)

    def test_historical_package_without_contract_is_accepted(self):
        with tempfile.TemporaryDirectory(prefix="legacy-ui-assets-test-") as temporary:
            root = Path(temporary)
            self.assertIsNone(verification.verify_packaged_ui_assets({}, root, root / "notices"))

    def test_capability_requires_assets_even_if_receipt_is_absent(self):
        with ui_fixture() as (app, notices, contract):
            empty = notices / "absent"
            with self.assertRaisesRegex(ValueError, "Missing or unsafe"):
                verification.verify_packaged_ui_assets({"uiAssets": contract["capability"]}, app, empty)

    def test_unknown_capability_is_rejected(self):
        with ui_fixture() as (app, notices, _):
            with self.assertRaisesRegex(ValueError, "Unsupported packaged"):
                verification.verify_packaged_ui_assets({"uiAssets": "unknown"}, app, notices)

    def test_receipt_without_declaration_still_enables_full_gate(self):
        with ui_fixture() as (app, notices, contract):
            (app / contract["fonts"][0]["path"]).write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "checksum mismatch"):
                verification.verify_packaged_ui_assets({}, app, notices)

    def test_each_font_and_license_is_required(self):
        for kind in ("fonts", "licenses"):
            with ui_fixture() as (_, _, contract):
                count = len(contract[kind])
            for index in range(count):
                with self.subTest(kind=kind, index=index), ui_fixture() as (app, notices, contract):
                    base = app if kind == "fonts" else notices
                    (base / contract[kind][index]["path"]).unlink()
                    with self.assertRaisesRegex(ValueError, "Missing or unsafe"):
                        verification.verify_ui_assets(app, notices)

    def test_modified_font_and_license_are_rejected(self):
        for kind in ("fonts", "licenses"):
            with self.subTest(kind=kind), ui_fixture() as (app, notices, contract):
                base = app if kind == "fonts" else notices
                (base / contract[kind][0]["path"]).write_bytes(b"changed")
                with self.assertRaisesRegex(ValueError, "checksum mismatch"):
                    verification.verify_ui_assets(app, notices)

    def test_modified_source_receipt_is_rejected(self):
        with ui_fixture() as (app, notices, _):
            (notices / verification.CONTRACT.name).write_text("{}")
            with self.assertRaisesRegex(ValueError, "checksum mismatch"):
                verification.verify_ui_assets(app, notices)

    def test_unsafe_asset_paths_are_rejected(self):
        with ui_fixture() as (app, _, _):
            with self.assertRaisesRegex(ValueError, "Missing or unsafe"):
                verification.checked_asset(app, {"path": "../outside.otf", "sha256": "0" * 64})

    def test_repository_font_and_license_sources_match_the_contract(self):
        repo = Path(__file__).resolve().parents[2]
        contract = json.loads(verification.CONTRACT.read_text(encoding="utf-8"))
        self.assertEqual(contract["fontVersion"], "4.009")
        for entry in contract["fonts"]:
            source = entry | {"path": entry["path"].replace("fonts/", "Fonts/", 1)}
            verification.checked_asset(repo / "src/HeyMelody.Desktop", source)
        for entry in contract["licenses"]:
            verification.checked_asset(repo / "packaging/licenses", entry)


if __name__ == "__main__":
    unittest.main()
