"""Freeze Rich TUI membership and map existing evidence at the recorded Ntilde revision."""

import argparse
from collections import Counter
import hashlib
import io
import json
from pathlib import Path
import subprocess
import zipfile


REPO = Path(__file__).resolve().parent.parent
QA_PATH = "docs/qa/terminfo"
ROOT = REPO / QA_PATH
PUBLICATION = ROOT / "2026-11-26-rich-tui"
REVISION = "caea956de1f8b391892f61f89ba6fbf6c462cc77"
CATALOGUE = "2026-11-15-modern-results/upstream-snapshot.zip"
SOURCES = {
    "stock": "2026-11-09-stock-baseline.json",
    "inventory": "2026-11-09-qualification-inventory.json",
    "core": "2026-11-11-core-reverse-wide-results/report.json",
    "modern": "2026-11-25-modern-clipboard-paste-results/report.json",
    "commonCore": "2026-11-25-common-core-comparison/report.json",
}
OMITTED = {"input.xtest-key", "input.xtest-click", "input.xtest-wheel"}
KEYBOARD_HISTORY = "historical query and implementation tests"
ADDITIONAL = {
    "extensions.kitty-keyboard.report-alternate": ("2026-11-01-alternate-keys.md", KEYBOARD_HISTORY),
    "extensions.kitty-keyboard.report-all-keys": ("2026-11-01-all-keys.md", KEYBOARD_HISTORY),
    "extensions.kitty-keyboard.report-text": ("2026-11-01-associated-text.md", KEYBOARD_HISTORY),
    "extensions.osc5522-clipboard": ("2026-11-01-clipboard.md", "historical mode recognition and implementation tests"),
    "extensions.osc52-read": ("2026-11-25-modern-osc52-clipboard-results/report.md", "related exact OSC 52 denial observation in the Modern round trip"),
    "input.modify-other-keys-3": ("2026-11-17-modify-other-keys-3-policy.md", "source audit and explicit policy"),
}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main_input(path):
    return subprocess.check_output(["git", "show", f"{REVISION}:{QA_PATH}/{path}"], cwd=REPO)


def read(path):
    return json.loads(path.read_bytes())


def requirements(membership):
    catalogue_zip = main_input(CATALOGUE)
    with zipfile.ZipFile(io.BytesIO(catalogue_zip)) as archive:
        catalogue = json.loads(archive.read("features.json"))
    rich = {case_id: item for case_id, item in catalogue.items() if isinstance(item, dict) and item.get("baseline") == "rich"}
    slugs = {item["slug"]: case_id for case_id, item in rich.items()}
    rows = [{"id": slugs[row["path"].rsplit("/", 1)[-1]], "name": row["name"],
             "page": "https://terminfo.dev" + row["path"]} for row in membership["rows"]]
    ids = [row["id"] for row in rows]
    if len(ids) != 96 or len(set(ids)) != len(ids) or set(rich) - set(ids) != OMITTED:
        raise ValueError("Rich live membership and the catalogue ID crosswalk disagree")
    return {
        "schemaVersion": 1,
        "baseline": "rich",
        "capturedOn": membership["capturedOn"],
        "upstreamPage": membership["source"],
        "membershipSha256": sha((PUBLICATION / "upstream-membership.json").read_bytes()),
        "catalogue": {"path": CATALOGUE, "sha256": sha(catalogue_zip),
                      "omittedXtestDiagnostics": sorted(OMITTED)},
        "rows": rows,
    }


def evidence_map(frozen):
    raw_inputs = {key: main_input(path) for key, path in SOURCES.items()}
    inputs = {key: json.loads(data) for key, data in raw_inputs.items()}
    stock = inputs["stock"]["outcomes"]
    suites = {suite["suite"]: suite["doc"] for suite in inputs["inventory"]["suites"]}
    inventory = {row["id"]: row for row in inputs["inventory"]["benchmark"]}
    core = {row["id"]: row["conclusion"] for row in inputs["core"]["rows"]}
    modern = {row["id"]: row["conclusion"] for row in inputs["modern"]["rows"]}
    common = {row["id"]: row["results"]["ntilde"]["status"] for row in inputs["commonCore"]["rows"]}

    rows = []
    for requirement in frozen["rows"]:
        case_id = requirement["id"]
        owned = [{"publication": suites[claim["suite"]], "status": claim["status"],
                  "pinned": claim["pinned"]["verdict"], "effect": claim["effect"]["verdict"]}
                 for claim in inventory.get(case_id, {}).get("claims", [])]
        additional = ADDITIONAL.get(case_id)
        rows.append({
            "id": case_id, "stock": stock.get(case_id), "owned": owned, "core": core.get(case_id),
            "modern": modern.get(case_id), "commonCore": common.get(case_id),
            "additionalEvidence": dict(zip(("path", "scope"), additional)) if additional else None,
        })
    return {
        "schemaVersion": 1,
        "mainRevision": REVISION,
        "inputs": {key: {"path": path, "sha256": sha(raw_inputs[key])} for key, path in SOURCES.items()},
        "totals": {
            "cases": len(rows),
            "stock": dict(Counter(row["stock"] or "ungraded" for row in rows)),
            "withOwnedInventoryClaims": sum(bool(row["owned"]) for row in rows),
            "withFrozenCoreRows": sum(row["core"] is not None for row in rows),
            "withFrozenModernRows": sum(row["modern"] is not None for row in rows),
            "withCommonCoreRows": sum(row["commonCore"] is not None for row in rows),
        },
        "rows": rows,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    frozen = requirements(read(PUBLICATION / "upstream-membership.json"))
    outputs = {
        "requirements-v1.json": frozen,
        "evidence-map.json": evidence_map(frozen),
    }
    for name, value in outputs.items():
        target = PUBLICATION / name
        text = json.dumps(value, ensure_ascii=False, indent=2) + "\n"
        if args.check:
            if target.read_text(encoding="utf-8") != text:
                parser.error(f"{name} does not rederive")
        else:
            target.write_bytes(text.encode("utf-8"))


if __name__ == "__main__":
    main()
