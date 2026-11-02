import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("terminfo_triage", ROOT / "scripts/terminfo-triage.py")
triage = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(triage)


class TerminfoTriageTests(unittest.TestCase):
    def setUp(self):
        self.report = json.loads(triage.STEM.with_suffix(".json").read_text(encoding="utf-8"))

    def test_complete_inventory_preserves_all_original_grades_and_reasons(self):
        triage.validate(self.report)
        self.assertEqual(177, len(self.report["observations"]))
        self.assertEqual({"supported": 78, "unsupported": 0, "inconclusive": 177}, self.report["originalOutcomes"])

    def test_note_tampering_duplicate_rows_and_regrading_are_rejected(self):
        changed = copy.deepcopy(self.report)
        changed["observations"][0]["original"]["note"] = "invented runtime evidence"
        with self.assertRaisesRegex(ValueError, "Original observation"):
            triage.validate(changed)
        changed = copy.deepcopy(self.report)
        changed["observations"][0] = changed["observations"][1]
        with self.assertRaisesRegex(ValueError, "duplicate"):
            triage.validate(changed)
        changed = copy.deepcopy(self.report)
        changed["observations"][0]["original"]["outcome"] = "supported"
        with self.assertRaisesRegex(ValueError, "Grade change"):
            triage.validate(changed)

    def test_identity_and_suite_completeness_cannot_be_upgraded_by_derivation(self):
        for field, value in [("identity", "verified"), ("suiteComplete", True)]:
            changed = copy.deepcopy(self.report)
            changed[field] = value
            with self.assertRaisesRegex(ValueError, "Unproven provenance"):
                triage.validate(changed)

    def test_audited_source_manifest_cannot_be_substituted(self):
        changed = copy.deepcopy(self.report)
        first = next(iter(changed["sourceFileSha256"]))
        changed["sourceFileSha256"][first] = "0" * 64
        with self.assertRaisesRegex(ValueError, "Source evidence manifest"):
            triage.validate(changed)

    def test_exact_protocol_variants_and_unsent_clipboard_probes_remain_distinct(self):
        rows = {row["original"]["featureId"]: row for row in self.report["observations"]}
        expected = {"extensions.kitty-graphics.transmit": "Q09", "extensions.osc17-highlight-bg": "Q08",
                    "extensions.iterm2-images": "Q04", "extensions.osc1337-cellsize": "Q07",
                    "modes.xtpushcolors": "Q10", "extensions.osc52-read": "Q11"}
        for feature, work_package in expected.items():
            self.assertEqual(work_package, rows[feature]["action"])
            self.assertEqual("inconclusive", rows[feature]["original"]["outcome"])
        self.assertIn("was not sent", rows["extensions.osc52-read"]["original"]["note"])

    def test_reassigned_work_packages_and_substituted_trace_pins_are_rejected(self):
        changed = copy.deepcopy(self.report)
        changed["observations"][0]["action"] = "Q11"
        with self.assertRaisesRegex(ValueError, "Triage classification"):
            triage.validate(changed)
        changed = copy.deepcopy(self.report)
        changed["observations"][0]["traceSha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "Trace evidence manifest"):
            triage.validate(changed)

    def test_dynamic_color_resets_have_color_state_and_render_validation(self):
        rows = {row["original"]["featureId"]: row for row in self.report["observations"]}
        for feature in ("extensions.osc104-reset-palette", "extensions.osc110-reset-fg", "extensions.osc111-reset-bg",
                        "extensions.osc112-reset-cursor", "extensions.osc117-reset-highlight-bg", "extensions.osc119-reset-highlight-fg"):
            self.assertEqual("Q02", rows[feature]["action"])
            self.assertEqual("inconclusive", rows[feature]["original"]["outcome"])
        colors = self.report["actions"]["Q02"]
        self.assertIn("src/Ntilde.VT/AnsiParser.Colors.cs", colors["sourceFiles"])
        self.assertIn("src/Ntilde.VT/TerminalBuffer.RuntimeColors.cs", colors["sourceFiles"])
        self.assertIn("set known overrides", colors["nextMeasurement"])
        self.assertIn("issue the exact reset", colors["nextMeasurement"])

    def test_markdown_is_regenerated_from_the_validated_inventory(self):
        self.assertEqual(triage.STEM.with_suffix(".md").read_text(encoding="utf-8"), triage.markdown(self.report))

    def test_insert_replace_mode_has_observable_cell_effects_instead_of_screen_save_restore(self):
        row = next(row for row in self.report["observations"] if row["original"]["featureId"] == "modes.insert-replace")
        self.assertEqual("Q01", row["action"])
        self.assertIn("shifted versus overwritten cells", self.report["actions"]["Q01"]["nextMeasurement"])

    def test_substituted_raw_input_is_rejected_before_any_source_command(self):
        with tempfile.TemporaryDirectory() as temporary:
            raw = Path(temporary) / "raw.json"
            raw.write_text('{"observations": []}', encoding="utf-8")
            with patch.object(triage.subprocess, "run", side_effect=AssertionError("Must not run git")):
                with self.assertRaisesRegex(ValueError, "Raw SHA-256"):
                    triage.build(raw)


if __name__ == "__main__":
    unittest.main()
