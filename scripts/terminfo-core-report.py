"""Derive an offline Core baseline evidence map; never regrade retained observations."""
import argparse
from collections import Counter
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import zipfile

ROOT = Path(__file__).resolve().parents[1]
INPUT = Path("docs/qa/terminfo/core-baseline-inputs.json")
STEM = Path("docs/qa/terminfo/2026-11-04-core-baseline")
CATALOGUE_KEYS = ("id", "name", "slug", "sequence", "requirement", "category")
CATALOGUE_SHA = "07999ed90966013bcdb9c430ea73a1f054dded8108dcac1da7ca9fc687c94bf3"
UPSTREAM_REVISION = "2ee594d336a6ac8030e6bc0d938f22361d5e7447"
CATALOGUE_BLOB = "8a99c9fa03229845502f8099d1c62b11903bc80e"
AUDIT_REVISION = "9648f5171b2d383f4954e2bcf6170c2b92e3f98e"
ARCHIVE_SHA = "d3d85d29f5d90c17ef8dfb62739d94b29ce46018b258534c0c038e9fa3a7992d"
ARCHIVE_PATH = "docs/qa/terminfo/2026-11-04-reset-observations-evidence.zip"
RUN_ID = "65c6c0bd9d1c10793a084e1e79f9f9eb"
STOCK_RUN_ID = "489508bf4da51d4d9f33a0d382982b06"
APP_SOURCE = "dc4e2c297d9bcbf89ab0d3d34b3dfacc2fdec4d6"
SUITE_ID = "16a690f34c0e"
ENVIRONMENT_IDS = {"device.env-term"}
GROUPS = {"sgr": "Styling and colors", "cursor": "Cursor", "erase": "Erasure",
          "modes": "Screen modes", "scrollback": "History and scrolling", "text": "Text and controls",
          "unicode": "Unicode tab stops", "reset": "Reset", "charsets": "Character sets",
          "device": "Environment", "extensions": "Icon name"}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def parse_json(text):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"Duplicate JSON key: {key}")
            result[key] = value
        return result

    def invalid_constant(value):
        raise ValueError(f"Invalid JSON constant: {value}")

    return json.loads(text, object_pairs_hook=unique, parse_constant=invalid_constant)


def repo_path(path):
    if not isinstance(path, str) or "\\" in path:
        raise ValueError("Invalid repository path")
    parsed = PurePosixPath(path)
    if parsed.is_absolute() or ".." in parsed.parts or path != str(parsed):
        raise ValueError("Invalid repository path")
    return path


def validate_inputs(data):
    if (set(data) != {"schemaVersion", "title", "asOf", "sourceAuditRevision", "catalogue", "archive", "rows"}
            or data.get("schemaVersion") != 1 or data.get("sourceAuditRevision") != AUDIT_REVISION
            or data.get("asOf") != "2026-11-04"):
        raise ValueError("Unexpected source audit")
    catalogue = data["catalogue"]
    if (catalogue["revision"] != UPSTREAM_REVISION or catalogue["gitBlobSha"] != CATALOGUE_BLOB
            or catalogue["repository"] != "beorn/terminfo.dev" or catalogue["path"] != "content/features.json"
            or catalogue["manualEnvironmentIds"] != sorted(ENVIRONMENT_IDS)):
        raise ValueError("Unexpected catalogue provenance or manual partition")
    archive = data["archive"]
    expected = {"path": ARCHIVE_PATH, "sha256": ARCHIVE_SHA, "runId": RUN_ID,
                "appBuildSource": APP_SOURCE, "suiteId": SUITE_ID}
    if any(archive.get(key) != value for key, value in expected.items()):
        raise ValueError("Unexpected archive provenance")
    rows = data["rows"]
    if len(rows) != 80 or len({row["id"] for row in rows}) != 80:
        raise ValueError("Core baseline must contain exactly 80 unique rows")
    frozen = [{key: row[key] for key in CATALOGUE_KEYS} for row in rows]
    if digest(canonical(frozen)) != CATALOGUE_SHA:
        raise ValueError("Pinned Core catalogue changed")
    for row in rows:
        validate_row(row)


def validate_row(row):
    if set(row) != set(CATALOGUE_KEYS) | {"owner", "sourceReferences", "ownedReferences", "acceptanceBoundary"}:
        raise ValueError("Unexpected row fields; no unproved grades or claims")
    if not row["owner"] or not row["acceptanceBoundary"] or not row["sourceReferences"]:
        raise ValueError("Missing row scope or source references")
    for path in row["sourceReferences"]:
        repo_path(path)
    references = row["ownedReferences"]
    keys = [(ref["report"], ref["featureId"]) for ref in references]
    if len(set(keys)) != len(keys):
        raise ValueError("Duplicate owned evidence reference")
    for ref in references:
        if not re.fullmatch(r"[a-z]+-observations\.json", ref["report"]):
            raise ValueError("Invalid archived report path")


def indexed(items, key):
    result = {}
    for index, item in enumerate(items):
        value = item[key]
        if value in result:
            raise ValueError(f"Duplicate evidence identity: {value}")
        result[value] = (index, item)
    return result


def auxiliary_report(archive, name):
    encoded = archive.read(name)
    report = parse_json(encoded)
    if report["runId"] != RUN_ID or report["complete"] is not True:
        raise ValueError("Unbound or incomplete auxiliary report")
    cases = indexed(report["observations"], "featureId")
    if any(item["result"] not in ("passed", "failed", "inconclusive") for _, item in cases.values()):
        raise ValueError("Unknown auxiliary result")
    metadata = {"suiteId": report["suiteId"], "sha256": digest(encoded),
                "scope": report["scope"], "caseCounts": dict(sorted(Counter(
                    item["result"] for _, item in cases.values()).items()))}
    return cases, metadata


def read_archive(blob):
    if digest(blob) != ARCHIVE_SHA:
        raise ValueError("Published archive SHA-256 mismatch")
    reports, suites = {}, {}
    with zipfile.ZipFile(io.BytesIO(blob)) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError("Duplicate archive entry")
        raw_bytes = archive.read("raw.json")
        raw = parse_json(raw_bytes)
        context = parse_json(archive.read("context.json"))
        receipt = parse_json(archive.read("cell-receipt.json"))
        if (raw["runId"] != STOCK_RUN_ID or raw["suiteId"] != SUITE_ID or raw["suiteComplete"] is not False
                or raw["identity"] != "unverified" or context["runId"] != RUN_ID):
            raise ValueError("Retained run provenance differs")
        if receipt["runId"] != RUN_ID or receipt["rawSha256"] != digest(raw_bytes):
            raise ValueError("Stock/owned receipt binding differs")
        for name in sorted(names):
            if not re.fullmatch(r"[a-z]+-observations\.json", name):
                continue
            reports[name], suites[name] = auxiliary_report(archive, name)
    return raw, reports, suites


def evidence_row(entry, stock, reports):
    feature = entry["id"]
    original = stock.get(feature)
    owned = []
    for ref in entry["ownedReferences"]:
        try:
            index, observation = reports[ref["report"]][ref["featureId"]]
        except KeyError as error:
            raise ValueError(f"Missing owned evidence: {ref}") from error
        owned.append({**ref, "pointer": f"/observations/{index}", "result": observation["result"]})
    description = {key: value for key, value in entry.items() if key != "ownedReferences"}
    return {**description, "graded": feature not in ENVIRONMENT_IDS,
            "stock": original[1] if original else None, "ownedEvidence": owned}


def build(root=ROOT, inputs=None, archive_bytes=None):
    data = parse_json((root / INPUT).read_text(encoding="utf-8")) if inputs is None else inputs
    validate_inputs(data)
    blob = (root / ARCHIVE_PATH).read_bytes() if archive_bytes is None else archive_bytes
    raw, reports, suites = read_archive(blob)
    stock = indexed(raw["observations"], "featureId")
    stock_counts = Counter({state: 0 for state in ("supported", "unsupported", "inconclusive")})
    stock_counts.update(item["outcome"] for _, item in stock.values())
    if len(stock) != 255 or stock_counts != {"supported": 82, "unsupported": 0, "inconclusive": 173}:
        raise ValueError("Frozen stock outcomes differ")
    rows = [evidence_row(entry, stock, reports) for entry in data["rows"]]
    counts = Counter(row["stock"]["outcome"] if row["stock"] else "not-collected" for row in rows)
    if counts != {"supported": 30, "inconclusive": 49, "not-collected": 1}:
        raise ValueError("Unexpected Core stock partition")
    negative = [{"report": name, "featureId": feature, "pointer": f"/observations/{index}",
                 "result": item["result"]} for name, cases in reports.items()
                for feature, (index, item) in cases.items() if item["result"] == "failed"]
    groups = []
    for category, title in GROUPS.items():
        members = [row for row in rows if row["category"] == category]
        grouped_counts = Counter(row["stock"]["outcome"] if row["stock"] else "not-collected" for row in members)
        groups.append({"category": category, "title": title, "rows": len(members),
                       "stockCounts": dict(sorted(grouped_counts.items()))})
    summary = {"displayedRows": 80, "gradedRows": 79, "manualRows": 1,
               "stockCounts": dict(sorted(counts.items())),
               "rowsWithPositiveOwnedCases": sum(any(ref["result"] == "passed" for ref in row["ownedEvidence"]) for row in rows),
               "rowsWithNegativeOwnedCases": sum(any(ref["result"] == "failed" for ref in row["ownedEvidence"]) for row in rows),
               "stockSupportedOrPositiveOwnedRows": sum((row["stock"] or {}).get("outcome") == "supported"
                   or any(ref["result"] == "passed" for ref in row["ownedEvidence"]) for row in rows),
               "scoringPolicy": "Evidence coverage only; no combined compliance score or regrading."}
    return {"schemaVersion": 1, "asOf": data["asOf"], "sourceAuditRevision": AUDIT_REVISION,
            "catalogue": data["catalogue"], "catalogueSubsetSha256": CATALOGUE_SHA,
            "inputSha256": digest(canonical(data)), "archive": data["archive"],
            "measurement": {"identity": raw["identity"], "suiteComplete": raw["suiteComplete"],
                            "stockRunId": STOCK_RUN_ID, "ownedRunId": RUN_ID,
                            "measuredAt": raw["measuredAt"], "stockAllCounts": dict(sorted(stock_counts.items())),
                            "ungradedDiagnostics": raw["ungradedDiagnostics"]},
            "summary": summary, "groups": groups, "suites": suites,
            "allAuxiliaryNegatives": negative, "rows": rows}


def markdown(report):
    def cell(value):
        return str(value).replace("\\", "\\\\").replace("|", "\\|").replace("`", "\\`").replace("\n", " ").replace("\r", " ")

    summary = report["summary"]
    lines = ["# Ntilde Core TUI baseline evidence map — 2026-11-04", "",
             "An offline reconciliation of the **80 displayed Core rows**, not a new terminal run or published terminfo.dev compliance score.", "",
             f"Pinned [upstream catalogue](https://github.com/beorn/terminfo.dev/blob/{UPSTREAM_REVISION}/content/features.json); Ntilde source/test references are frozen at `{AUDIT_REVISION}`. The website's 80-row list includes TERM, which the [pinned current scorer](https://github.com/beorn/terminfo.dev/blob/{UPSTREAM_REVISION}/packages/probe-defs/src/environment-groups.ts) treats as manual/ungraded: **79 graded capabilities + 1 manual environment row**.", "",
             "## Readout", "",
             "| Measure | Value |", "|---|---:|",
             "| Frozen stock Supported | 30 |", "| Frozen stock Unsupported | 0 |",
             "| Frozen stock Inconclusive | 49 |", "| Not collected: manual TERM | 1 |",
             f"| Rows with an additional passed owned case | {summary['rowsWithPositiveOwnedCases']} |",
             f"| Rows with an additional failed owned case | {summary['rowsWithNegativeOwnedCases']} |",
             f"| Union of stock Supported or a passed owned variant | {summary['stockSupportedOrPositiveOwnedRows']} |", "",
             "The union is a count of rows with some positive evidence, **not a compliance numerator**. Overlaps count once; one row may retain both positive and negative evidence. Variant inputs, cell flags, physical pixels, history and query replies are distinct scopes. No auxiliary case changes a stock grade.", "",
             "30/80 is 37.5% of the displayed list. Excluding the one manual environment row gives 30/79 = 38.0% (38% rounded). These describe the frozen stock supported share only. This older collector suite and the current catalogue do not share a common execution plan, so neither fraction is an official or comparable overall Core score.", "",
             "## Preserved boundaries", "",
             "- **Bold:** the stored SGR 1 flag passes while the measured explicit-RGB glyph comparison fails. The later [bold correction and deterministic tests](bold-glyphs.md) are source evidence, not a replacement owned run. The appearance negative is not automatically a failure of the narrower Core cell-flag criterion.",
             "- **DECSCNM:** private mode 5 is unrecognized in the retained reply; SGR 7 inverse is a different feature. The tracked-mode source has no private-mode-5 implementation. This is a source limitation, not a rewritten stock Unsupported grade.",
             "- **OSC 1:** App/VT has no separate icon-name state/readback. Title changes from OSC 0/2 do not prove it.",
             "- **reset():** this is a Termless backend API requirement. A desktop app cannot qualify it by sending RIS, which has its own row.",
             "- **TERM:** manual environment verification remains uncollected. A source assignment of xterm-256color does not attest the measured child's environment.",
             "- **Known exact-fixture gaps:** owned EL text cases do not observe EL's green erase background; bounded four-line history does not meet the current ten-extra-line count criterion; separate margin-reset and accumulation cases do not prove the joined reset-plus-overflow fixture. Other scoped variants are stated per row.", "",
             "## Stock partition by area", "", "| Area | Rows | Supported | Inconclusive | Not collected |", "|---|---:|---:|---:|---:|"]
    for group in report["groups"]:
        counts = group["stockCounts"]
        lines.append(f"| {group['title']} | {group['rows']} | {counts.get('supported', 0)} | {counts.get('inconclusive', 0)} | {counts.get('not-collected', 0)} |")
    lines.extend(["", "## Retained measurement", "",
                  f"[Published run]({Path(report['archive']['report']).name}) and [complete evidence archive]({Path(ARCHIVE_PATH).name}): `{RUN_ID}`, app/adapter source `{APP_SOURCE}`, macOS arm64, pinned terminfo.dev@4.0.0. The archive SHA-256 is `{ARCHIVE_SHA}`.", "",
                  "The measured identity remains unverified and suiteComplete remains false. The full stock result remains **82 supported / 0 unsupported / 173 inconclusive**. Reverse-wrap's ungraded geometry diagnostic is retained in JSON. No Windows/Linux live-runtime claim or independent binary/source attestation is added.", "",
                  "The generator verifies archive bytes and resolves original case IDs/results; it does not execute archived JavaScript or reconstruct the original desktop. [Whole-archive reproduction](2026-11-04-metadata-audit.md) describes the separate source/journal/PNG verification. Every negative in the archive remains listed in JSON, including the non-Core empty-title observation.", "",
                  "## The 80 rows", "",
                  "Owned references below name the original report and case inside the archive. The JSON supplies its exact /observations/index pointer; the suite inventory supplies report hashes and scope. **Source references are inspected code/tests at the audit revision, not test-run results.** Each row also retains the verbatim pinned upstream requirement in JSON."])
    for group in report["groups"]:
        rows = [row for row in report["rows"] if row["category"] == group["category"]]
        lines.extend(["", f"### {group['title']}", ""])
        sources = sorted({path for row in rows for path in row["sourceReferences"]})
        lines.append("Source references: " + ", ".join(f"[{PurePosixPath(path).name}](https://github.com/benyblack/ntilde/blob/{AUDIT_REVISION}/{path})" for path in sources) + ".")
        lines.extend(["", "| Core item | Stock result | Additional owned cases | Scope / remaining acceptance |", "|---|---|---|---|"])
        for row in rows:
            url = f"https://terminfo.dev/{row['category']}/{row['slug']}"
            stock = row["stock"]["outcome"] if row["stock"] else "not collected (manual)"
            owned = "; ".join(f"[{ref['report'].split('-')[0]}](#suite-{ref['report'].split('-')[0]}) `{cell(ref['featureId'])}`: **{ref['result']}**" for ref in row["ownedEvidence"]) or "None mapped"
            lines.append(f"| [{cell(row['name'])}]({url})<br>`{row['id']}` | {stock} | {owned} | {cell(row['acceptanceBoundary'])} |")
    lines.extend(["", "## Suite inventory", ""])
    for name, suite in report["suites"].items():
        key = name.split("-")[0]
        lines.extend([f"### Suite {key}", "", f"`{name}` — `{suite['suiteId']}`; SHA-256 `{suite['sha256']}`.", "",
                      cell(suite["scope"]), "", "Original case outcomes: " + ", ".join(f"{count} {state}" for state, count in suite["caseCounts"].items()) + ".", ""])
    lines.extend(["## Reproduce", "", "```sh", "python3 scripts/terminfo-core-report.py --check",
                  "python3 scripts/tests/terminfo_core_report_tests.py", "```", "",
                  "Regenerate with --write after an intentional reviewed input-map change. Both JSON and Markdown derive from the pinned catalogue subset, original archive and explicit variant mappings. Checks need no network, Git history, private artifacts, desktop or native process.", "",
                  "Upstream names, sequences and requirement excerpts are by Bjorn Stabell, CC BY 4.0; the source attribution/license and extraction changes are retained in core-baseline-inputs.json. Ntilde annotations are separate additions. No historical report, receipt, matrix status or generated conformance artifact is rewritten."])
    return "\n".join(lines) + "\n"


def encoded_report(report):
    return json.dumps(report, ensure_ascii=False, indent=2) + "\n"


def outputs(report):
    return {STEM.with_suffix(".json"): encoded_report(report), STEM.with_suffix(".md"): markdown(report)}


def check_outputs(root, report):
    for path, expected in outputs(report).items():
        if (root / path).read_text(encoding="utf-8") != expected:
            raise ValueError(f"Stale Core report: {path}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--check", action="store_true")
    mode.add_argument("--write", action="store_true")
    args = parser.parse_args()
    report = build()
    if args.check:
        check_outputs(ROOT, report)
    else:
        for path, text in outputs(report).items():
            (ROOT / path).write_bytes(text.encode("utf-8"))
    print("Core map: 80 rows; stock 30 supported / 49 inconclusive / 1 manual uncollected; no regrading.")


if __name__ == "__main__":
    main()
