"""Portable, offline regressions for immutable Core evidence reconciliation."""
import copy
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("terminfo_core", ROOT / "scripts/terminfo-core-report.py")
core = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(core)


class CoreReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.inputs = core.parse_json((ROOT / core.INPUT).read_text(encoding="utf-8"))
        cls.report = core.build()

    def test_exact_core_membership_and_manual_environment_partition(self):
        rows = self.report["rows"]
        self.assertEqual(80, len({row["id"] for row in rows}))
        self.assertEqual(["device.env-term"], [row["id"] for row in rows if not row["graded"]])
        self.assertEqual(79, sum(row["graded"] for row in rows))
        self.assertEqual({"supported": 30, "inconclusive": 49, "not-collected": 1},
                         self.report["summary"]["stockCounts"])
        self.assertEqual(80, sum(group["rows"] for group in self.report["groups"]))

    def test_stock_rows_are_exact_original_objects_and_owned_pointers_resolve(self):
        with zipfile.ZipFile(ROOT / core.ARCHIVE_PATH) as archive:
            raw = core.parse_json(archive.read("raw.json"))
            stock = {item["featureId"]: item for item in raw["observations"]}
            for row in self.report["rows"]:
                self.assertEqual(stock.get(row["id"]), row["stock"])
                for ref in row["ownedEvidence"]:
                    source = core.parse_json(archive.read(ref["report"]))
                    index = int(ref["pointer"].rsplit("/", 1)[1])
                    case = source["observations"][index]
                    self.assertEqual(ref["featureId"], case["featureId"])
                    self.assertEqual(ref["result"], case["result"])
        self.assertEqual("unverified", self.report["measurement"]["identity"])
        self.assertFalse(self.report["measurement"]["suiteComplete"])
        self.assertEqual(core.STOCK_RUN_ID, raw["runId"])
        self.assertNotEqual(core.RUN_ID, core.STOCK_RUN_ID)

    def test_positive_cell_and_negative_pixel_results_never_become_a_single_grade(self):
        bold = next(row for row in self.report["rows"] if row["id"] == "sgr.bold")
        self.assertEqual("inconclusive", bold["stock"]["outcome"])
        self.assertEqual({"passed", "failed"}, {ref["result"] for ref in bold["ownedEvidence"]})
        self.assertNotIn("score", bold)
        self.assertNotIn("supported", bold)
        negatives = {(ref["report"], ref["featureId"]) for ref in self.report["allAuxiliaryNegatives"]}
        self.assertEqual({("pixel-observations.json", "rendition.bold"),
                          ("metadata-observations.json", "metadata.title.empty")}, negatives)

    def test_unrelated_cases_are_not_borrowed_for_known_acceptance_gaps(self):
        rows = {row["id"]: row for row in self.report["rows"]}
        for feature in ("erase.el-with-attrs", "modes.reverse-video", "reset.method", "extensions.osc1-icon"):
            self.assertEqual([], rows[feature]["ownedEvidence"])
            self.assertEqual("inconclusive", rows[feature]["stock"]["outcome"])
        self.assertIsNone(rows["device.env-term"]["stock"])

    def test_catalogue_changes_omissions_duplicates_and_reordering_are_rejected(self):
        changes = [lambda d: d["rows"].pop(),
                   lambda d: d["rows"].__setitem__(0, d["rows"][1]),
                   lambda d: d["rows"].reverse(),
                   lambda d: d["rows"][0].__setitem__("requirement", "recognition alone is enough")]
        for change in changes:
            data = copy.deepcopy(self.inputs)
            change(data)
            with self.assertRaises(ValueError):
                core.build(inputs=data)

    def test_unproved_provenance_and_extra_grade_fields_are_rejected(self):
        changes = [lambda d: d.__setitem__("sourceAuditRevision", "0" * 40),
                   lambda d: d["catalogue"].__setitem__("revision", "0" * 40),
                   lambda d: d["catalogue"].__setitem__("gitBlobSha", "0" * 40),
                   lambda d: d["catalogue"].__setitem__("manualEnvironmentIds", []),
                   lambda d: d["archive"].__setitem__("sha256", "0" * 64),
                   lambda d: d["rows"][0].__setitem__("runtimeSupported", True)]
        for change in changes:
            data = copy.deepcopy(self.inputs)
            change(data)
            with self.assertRaises(ValueError):
                core.build(inputs=data)

    def test_bad_archive_bytes_are_rejected_before_zip_loading(self):
        with self.assertRaisesRegex(ValueError, "archive SHA-256"):
            core.build(archive_bytes=b"not an archive")

    def test_missing_duplicate_or_traversing_evidence_references_are_rejected(self):
        for mode in ("missing", "duplicate", "traversal"):
            data = copy.deepcopy(self.inputs)
            references = data["rows"][0]["ownedReferences"]
            if mode == "missing":
                references[0]["featureId"] = "not.a.recorded.case"
            elif mode == "duplicate":
                references.append(copy.deepcopy(references[0]))
            else:
                references[0]["report"] = "../raw.json"
            with self.assertRaises(ValueError):
                core.build(inputs=data)
        data = copy.deepcopy(self.inputs)
        data["rows"][0]["sourceReferences"] = ["../other-checkout/CLAUDE.md"]
        with self.assertRaisesRegex(ValueError, "repository path"):
            core.build(inputs=data)

    def test_duplicate_json_keys_and_nonfinite_constants_are_rejected(self):
        for text in ('{"stock":"supported","stock":"inconclusive"}', '{"count":NaN}'):
            with self.assertRaises(ValueError):
                core.parse_json(text)

    def test_markdown_and_json_drift_are_checked_independently(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for path, text in core.outputs(self.report).items():
                (root / path).parent.mkdir(parents=True, exist_ok=True)
                (root / path).write_bytes(text.encode("utf-8"))
            core.check_outputs(root, self.report)
            for suffix in (".json", ".md"):
                path = root / core.STEM.with_suffix(suffix)
                original = path.read_bytes()
                path.write_bytes(original + b"unproved claim\n")
                with self.assertRaisesRegex(ValueError, "Stale Core report"):
                    core.check_outputs(root, self.report)
                path.write_bytes(original)

    def test_cli_reproduces_both_outputs_without_git_history_or_private_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            script = root / "scripts/terminfo-core-report.py"
            script.parent.mkdir()
            script.write_bytes(Path(core.__file__).read_bytes())
            for relative in (core.INPUT, Path(core.ARCHIVE_PATH)):
                (root / relative).parent.mkdir(parents=True, exist_ok=True)
                (root / relative).write_bytes((ROOT / relative).read_bytes())
            for mode in ("--write", "--check"):
                result = subprocess.run([sys.executable, str(script), mode], cwd=root,
                                        capture_output=True, text=True, check=False)
                self.assertEqual(0, result.returncode, result.stderr)
            for path, text in core.outputs(self.report).items():
                self.assertEqual(text.encode("utf-8"), (root / path).read_bytes())


if __name__ == "__main__":
    unittest.main()
