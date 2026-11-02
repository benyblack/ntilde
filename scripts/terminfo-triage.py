"""Offline, pinned triage of the 177 inconclusive owned terminfo observations."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
STEM = ROOT / "docs/qa/terminfo/2026-11-02-inconclusive-triage"
RAW_SHA = "2ad91173e34040c19bd4ee4a97833d724d609e3cf7a2bc99a5fa1b9a99edc018"
COLLECTOR_REVISION = "cf7d8759c7e637aa8dba1e429f3dddd1c0124158"
APP_SOURCE_HINT = "5aa7481b2d3187325378168f68c0548111322ac3"
AUDIT_REVISION = "c1befd9e32b5efb843fca5216eaf2c5c73b8ff8b"
SOURCE_MANIFEST_SHA = "e3c7f9525c204f1dea7fd6bf891d79fc212207d64ad32799ec19c142bdde8dfa"
TRACE_MANIFEST_SHA = "d85277eb560f795ec1181ce4b698a52e155a4c96a56fd7b90b3dee318cc3a77f"
PROVENANCE_SHA = "a669a5cf5f0a447a8d549cace813abe1e0244f403c645141dd294a59dd223ce9"
OBSERVATIONS_SHA = "627af96ede7c271c8eee02be66bd87458c06a108a1bf26f2831ecee88ba900cb"


VT_CSI = "src/Ntilde.VT/AnsiParser.Csi.cs"
VT_OSC = "src/Ntilde.VT/AnsiParser.Osc.cs"
VT_IMAGES = "src/Ntilde.VT/AnsiParser.Images.cs"
VT_KITTY_GRAPHICS = "src/Ntilde.VT/AnsiParser.KittyGraphics.cs"
VT_MATRIX = "docs/vt_coverage_matrix.md"

def action(title, owner, next_measurement, files):
    return {"title": title, "owner": owner, "nextMeasurement": next_measurement, "sourceFiles": files}


ACTIONS = {
    "Q01": action("Observe cells, geometry and history", "VT + App QA",
                  "Use a disposable pane with independently confirmed rows/columns; compare cell contents, cursor, margins and scrollback before/after, then retain the observation and cleanup receipt. For insert/replace mode, write controlled text under SM/RM 4 and compare shifted versus overwritten cells after each transition. CPR alone cannot prove content or erasure.",
                  [VT_CSI, "src/Ntilde.VT/TerminalBuffer.cs", "tests/Ntilde.VT.Tests/EraseInDisplayTests.cs", "tests/Ntilde.App.Tests/ScrollAndWrapCorrectnessTests.cs"]),
    "Q02": action("Observe attributes and rendered pixels", "VT + Rendering + App QA",
                  "Assert decoded cell attributes/charset mapping and independently capture controlled rendered pixels. Use a controlled clock for blink; preserve the input, capture hashes and restored fixture state. For OSC 104/110/111/112/117/119, save prior colors and host defaults, set known overrides, issue the exact reset, then compare palette/default/cursor/selection state and pixels against the known defaults. Query where a concrete reply exists; an unset selection default may have no concrete reply. Restore prior state and verify restoration. Cursor advance is insufficient.",
                  ["src/Ntilde.VT/AnsiParser.Sgr.cs", "tests/Ntilde.App.Tests/SgrAttributeTests.cs", "tests/Ntilde.App.Tests/DecSpecialGraphicsTests.cs", "tests/Ntilde.App.Tests/RenderTests/RuntimeColorRenderTests.cs", "src/Ntilde.VT/AnsiParser.Colors.cs", "src/Ntilde.VT/TerminalBuffer.RuntimeColors.cs", "tests/Ntilde.VT.Tests/RuntimeColorTests.cs"]),
    "Q03": action("Observe host metadata and UI effects", "App QA + VT callbacks",
                  "Read the private pane's title, cwd, hyperlink/shell-marker metadata or notification/progress presentation through an independent host observer. Record private setting/focus preconditions and verify restoration. A consumed marker or DA sentinel is insufficient.",
                  [VT_OSC, "tests/Ntilde.App.Tests/OscShellIntegrationTests.cs", "tests/Ntilde.VT.Tests/Osc9NotificationProgressTests.cs", "tests/Ntilde.App.Tests/Controls/PaneParserWiringTests.cs"]),
    "Q04": action("Observe image pixels and playback", "Rendering + App QA",
                  "Capture decoded/rendered pixels and placement in a private fixture; animation requires timed frame evidence. Do not infer pixel output, placeholders or playback from query recognition or cursor movement.",
                  [VT_IMAGES, VT_KITTY_GRAPHICS, "tests/Ntilde.App.Tests/GraphicsTests.cs", "tests/Ntilde.Rendering.Tests/InlineImageEndToEndTests.cs"]),
    "Q05": action("Generate and validate input events", "App input + QA",
                  "Inject a controlled key/keypad/mouse event in an owned pane and compare the actual encoded child-input report, including coordinates and modifiers. A mode response does not prove input encoding.",
                  [VT_CSI, "tests/Ntilde.App.Tests/Input/TerminalInputModeEncoderTests.cs", "tests/Ntilde.App.Tests/MouseReportingTests.cs"]),
    "Q06": action("Observe saved/restored terminal state", "VT + App QA",
                  "Compare complete main/alternate buffer contents, saved cursor and relevant mode/attribute state before entry, while active, and after restoration. A responsive cursor after exit is insufficient.",
                  [VT_CSI, "src/Ntilde.VT/TerminalBuffer.StateTransfer.cs", "tests/Ntilde.App.Tests/AlternateScreenTests.cs", "tests/Ntilde.VT.Tests/SoftResetAndCursorSaveTests.cs"]),
    "Q07": action("Unimplemented query variant", "VT protocol backlog",
                  "The reviewed dispatcher has no reply for this exact query variant. Add a narrowly scoped contract and request/reply regression before implementation, or explicitly document the variant as intentionally unavailable. Rerun the owned query after any change; retain the original inconclusive grade.",
                  [VT_CSI, VT_OSC, VT_IMAGES, VT_MATRIX]),
    "Q08": action("Conditional selection-color query", "VT colors + App theme QA",
                  "OSC 17/19 are implemented, but unset derived selection colors can be null and intentionally produce no concrete reply. In a private fixture, set a known override, query it, restore it and verify restoration; test host-derived defaults separately.",
                  ["src/Ntilde.VT/AnsiParser.Colors.cs", "src/Ntilde.VT/TerminalBuffer.RuntimeColors.cs", "tests/Ntilde.VT.Tests/RuntimeColorTests.cs", VT_MATRIX]),
    "Q09": action("Kitty image-number/acknowledgement gap", "VT graphics + App ownership QA",
                  "The probe sends uppercase I image numbers. The reviewed implementation reads lowercase i and emits replies for a=q, not correlated allocation acknowledgements for these a=t/T probes. Add image-number allocation/ack/cleanup contract tests; existing basic image decoding is separate evidence.",
                  [VT_KITTY_GRAPHICS, "tests/Ntilde.App.Tests/GraphicsTests.cs", "tests/Ntilde.App.Tests/AnsiParserHardeningTests.cs", VT_MATRIX]),
    "Q10": action("Source-reviewed limited or missing variant", "VT/App protocol backlog",
                  "The exact spelling/variant has a source limitation listed with this row. Add a focused state/effect contract and owned observer, implement separately if desired, and rerun; do not equate related supported protocols with this variant.",
                  [VT_CSI, VT_OSC, VT_KITTY_GRAPHICS, VT_MATRIX]),
    "Q11": action("Keep clipboard ownership refusal", "App native QA + collector policy",
                  "These OSC 52 packets were not sent. Keep them refused until the owned collector itself has a disposable clipboard and verified restoration/cleanup. Separate native CI interchange results do not retroactively qualify these observations. OSC 52 reads intentionally return denial.",
                  [VT_OSC, "scripts/terminfo/ownership.mjs", "docs/qa/clipboard-native.md", "docs/qa/terminfo/2026-11-01-clipboard.md"]),
    "Q12": action("Negative query response is not a full capability result", "VT protocol QA",
                  "DECRQSS and XTGETTCAP return well-formed failure responses for the requested setting/name. Preserve that evidence; enumerate and test individual supported/unsupported settings before claiming the whole query family.",
                  [VT_IMAGES, VT_MATRIX]),
    "Q13": action("Collector API/legacy evidence gap", "QA adapter",
                  "This is an adapter/API observation, not proof of missing VT behavior. Add an owned reset/state readback adapter or replace the legacy feature hint with explicit assertions. Do not invoke an unowned app reset or use a legacy hint as support evidence.",
                  ["scripts/terminfo/collector.mjs", "scripts/terminfo/ownership.mjs", "src/Ntilde.VT/AnsiParser.cs"]),
}

LIMITS = {}


def limits(ids, explanation):
    for feature in ids.split():
        LIMITS[feature] = explanation


limits("extensions.kitty-graphics.animation extensions.kitty-graphics.unicode-placeholders",
       "Graphics dispatch handles a=t/T and q; only cursor-anchored placement is implemented, not the tested animation/Unicode-placeholder variants.")
limits("extensions.osc-633-vscode " + " ".join("extensions.osc633-" + x for x in "abcdep"),
       "OSC 633 has no matching dispatch case; OSC 133 shell markers are a different protocol.")
limits("extensions.osc22-pointer extensions.osc777-notify extensions.osc666-termprop extensions.osc3008-context extensions.osc176-app-id extensions.osc555-flash extensions.osc440-audio extensions.osc113-reset-pointer-fg extensions.osc114-reset-pointer-bg extensions.osc710-font-normal extensions.osc720-scroll-up",
       "The exact OSC code has no handler in the reviewed OSC/color dispatchers; consuming the stream or answering a sentinel does not implement its effect.")
limits("extensions.osc1-icon extensions.osc0-icon-title",
       "OSC 0/2 route to window-title changes; there is no separate OSC 1 icon-name handler/state. The combined icon/title variant is partial.")
limits("editing.decfra editing.decera editing.decsera editing.deccra editing.deccara editing.decrara editing.decsace editing.sl editing.sr editing.decic editing.decdc",
       "These rectangle, attribute-extent, horizontal-scroll or column-edit variants are not handled by the reviewed CSI dispatcher; ordinary ICH/DCH/IL/DL/REP are separate paths.")
limits("erase.selective",
       "DECSED/DECSEL alias ordinary erase because DECSCA protected-character state is unimplemented. Protection-sensitive behavior is not supported by that alias.")
limits("modes.reverse-video modes.left-right-margin modes.alt-scroll-1007 modes.utf8-mouse-1005 modes.deccolm modes.decsclm",
       "The corresponding DEC private mode has no tracked/implemented case in the reviewed private-mode dispatcher; DECRPM zero is not evidence of its behavior.")
limits("modes.xtpushsgr modes.xtpopsgr modes.xtsave modes.xtrestore modes.xtpushcolors modes.xtpopcolors device.xtwinops-22 device.xtwinops-23",
       "These xterm save/restore or stack/title variants are not handled. Kitty OSC 30001/30101 color stacks and ordinary cursor save/restore are different spellings/contracts.")
limits("input.modify-other-keys input.modify-other-keys-3 input.pixel-mouse input.urxvt-mouse",
       "Xterm modifyOtherKeys and pixel/urxvt mouse encodings are not implemented; Kitty keyboard and legacy X10/SGR mouse paths are different contracts.")


SPECIAL_ACTIONS = dict.fromkeys(LIMITS, "Q10")
SPECIAL_ACTIONS.update({
    "device.decrqss": "Q12", "device.xtgettcap": "Q12", "device.term-features": "Q13", "reset.method": "Q13",
    "modes.application-keypad": "Q05", "modes.insert-replace": "Q01", "extensions.truecolor": "Q02", "reset.sgr": "Q02", "cursor.hide": "Q02", "cursor.shape": "Q02",
    "extensions.reflow": "Q01", "reset.decaln": "Q01", "extensions.sixel": "Q04", "extensions.iterm2-images": "Q04",
})
SPECIAL_ACTIONS.update(dict.fromkeys((
    "extensions.osc104-reset-palette", "extensions.osc110-reset-fg", "extensions.osc111-reset-bg",
    "extensions.osc112-reset-cursor", "extensions.osc117-reset-highlight-bg", "extensions.osc119-reset-highlight-fg",
), "Q02"))
NO_RESPONSE_ACTIONS = {"extensions.kitty-graphics.transmit": "Q09", "extensions.kitty-graphics.display": "Q09",
                       "extensions.osc17-highlight-bg": "Q08", "extensions.osc19-highlight-fg": "Q08"}
PREFIX_ACTIONS = (("input.", "Q05"), ("modes.", "Q06"), (("sgr.", "charsets."), "Q02"),
                  (("text.", "erase.", "editing.", "scrollback.", "cursor."), "Q01"), ("extensions.", "Q03"))


def classify(observation):
    feature, reason = observation["featureId"], observation["reason"]
    if reason == "policy-refused":
        return "Q11"
    if reason == "no-response":
        return NO_RESPONSE_ACTIONS.get(feature, "Q07")
    if feature in SPECIAL_ACTIONS:
        return SPECIAL_ACTIONS[feature]
    for prefix, work_package in PREFIX_ACTIONS:
        if feature.startswith(prefix):
            return work_package
    raise ValueError("Untriaged feature: " + feature)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def unique_json_members(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON key: " + key)
        result[key] = value
    return result


def parse_report(text):
    return json.loads(text, object_pairs_hook=unique_json_members)


def original_digest(observations):
    original = sorted((o["original"] for o in observations), key=lambda o: o["featureId"])
    return digest(json.dumps(original, sort_keys=True, separators=(",", ":")).encode())


def validate_pins(report):
    if report["rawSha256"] != RAW_SHA or report["collectorSourceRevision"] != COLLECTOR_REVISION or report["appSourceHint"] != APP_SOURCE_HINT:
        raise ValueError("Wrong source pins")
    if report["actions"] != ACTIONS or report["originalOutcomes"] != {"supported": 78, "unsupported": 0, "inconclusive": 177}:
        raise ValueError("Action or outcome summary changed")
    source_digest = digest(json.dumps(report["sourceFileSha256"], sort_keys=True, separators=(",", ":")).encode())
    if source_digest != SOURCE_MANIFEST_SHA or report["appVersionHint"] != "0.12.0+" + APP_SOURCE_HINT:
        raise ValueError("Source evidence manifest changed")
    metadata = {key: value for key, value in report.items() if key not in {"actions", "sourceFileSha256", "observations"}}
    if digest(json.dumps(metadata, sort_keys=True, separators=(",", ":")).encode()) != PROVENANCE_SHA:
        raise ValueError("Unproven provenance/coverage claim")


def validate_rows(rows):
    if len(rows) != 177 or len({r["original"]["featureId"] for r in rows}) != 177:
        raise ValueError("Missing or duplicate observations")
    if any(r["original"]["outcome"] != "inconclusive" or r["action"] not in ACTIONS for r in rows):
        raise ValueError("Grade change or unknown action")
    if original_digest(rows) != OBSERVATIONS_SHA:
        raise ValueError("Original observation data changed")
    expected = {"insufficient-evidence": 152, "no-response": 22, "policy-refused": 3}
    if dict(Counter(r["original"]["reason"] for r in rows)) != expected:
        raise ValueError("Original reason counts changed")


def validate_assignments(rows):
    for row in rows:
        expected = {"original", "action", "traceSha256"}
        if row["original"]["featureId"] in LIMITS:
            expected.add("sourceLimitation")
        if set(row) != expected:
            raise ValueError("Observation row schema changed")
    if any(r["action"] != classify(r["original"]) or r.get("sourceLimitation") != LIMITS.get(r["original"]["featureId"]) for r in rows):
        raise ValueError("Triage classification changed")
    traces = sorted((r["original"]["featureId"], r["traceSha256"]) for r in rows)
    if digest(json.dumps(traces, separators=(",", ":")).encode()) != TRACE_MANIFEST_SHA:
        raise ValueError("Trace evidence manifest changed")


def validate(report):
    validate_pins(report)
    validate_rows(report["observations"])
    validate_assignments(report["observations"])


def build(raw_path):
    raw_bytes = raw_path.read_bytes()
    if digest(raw_bytes) != RAW_SHA:
        raise ValueError("Raw SHA-256 does not match the retained owned collection")
    raw = json.loads(raw_bytes)
    rows = []
    for observation in raw["observations"]:
        if observation["outcome"] != "inconclusive":
            continue
        trace = raw["rawReplies"].get(observation.get("rawReplyRef"))
        row = {"original": observation, "action": classify(observation), "traceSha256": digest(trace.encode()) if trace is not None else None}
        if observation["featureId"] in LIMITS:
            row["sourceLimitation"] = LIMITS[observation["featureId"]]
        rows.append(row)
    files = sorted({path for item in ACTIONS.values() for path in item["sourceFiles"]})
    pins = {}
    for path in files:
        data = subprocess.run(["git", "show", AUDIT_REVISION + ":" + path], cwd=ROOT, capture_output=True, check=True).stdout
        pins[path] = digest(data)
    report = {"schemaVersion": 1, "rawArtifact": "artifacts/terminfo/owned-lZP2Uv/raw.json", "rawSha256": RAW_SHA,
              "collectorSourceRevision": raw["sourceRevision"], "appSourceHint": APP_SOURCE_HINT, "appVersionHint": raw["target"]["version"], "sourceAuditRevision": AUDIT_REVISION,
              "measuredAt": raw["measuredAt"], "identity": raw["identity"], "suiteComplete": raw["suiteComplete"],
              "originalOutcomes": {result: sum(o["outcome"] == result for o in raw["observations"]) for result in ("supported", "unsupported", "inconclusive")},
              "scope": "Offline source/evidence triage; no new runtime or terminal-emulator measurements, no regrading.",
              "actions": ACTIONS, "sourceFileSha256": pins, "observations": rows}
    validate(report)
    return report


def markdown(report):
    counts = Counter(r["action"] for r in report["observations"])
    lines = ["# Inconclusive terminfo observations — 2026-11-02", "",
             "All **177** retained inconclusive observations are triaged below: **152 insufficient-evidence, 22 no-response, 3 policy-refused**. The original result stays **78 supported / 0 unsupported / 177 inconclusive**, with `suiteComplete=false` and identity `unverified`. Source inspection is a separate inference; it is not a new probe result or a comparison with other emulators.", "",
             "Raw SHA-256: `" + RAW_SHA + "`. Collector source: `" + COLLECTOR_REVISION + "`. App source hint: `" + APP_SOURCE_HINT + "` (from informational version, not independent identity attestation). Source audit: `" + AUDIT_REVISION + "`. The [machine-readable inventory](2026-11-02-inconclusive-triage.json) preserves every original observation, each available trace digest, and the audited source-file hashes. The retained local raw artifact is `artifacts/terminfo/owned-lZP2Uv/raw.json`; private settings are neither read nor included.", "",
             "The 22 missing replies split into 18 unimplemented exact query variants, two conditional selection-color queries, and two Kitty image-number/allocation-ack variants. Basic Kitty/iTerm2/Sixel image decoding does not prove all image-protocol variants. The 152 evidence-limited observations also include source limitations; they are not 152 implemented features or 152 proven missing features.", "",
             "## Work packages", ""]
    for key, item in ACTIONS.items():
        lines += ["### " + key + " — " + item["title"], "", "**" + str(counts[key]) + " observations; owner: " + item["owner"] + ".** " + item["nextMeasurement"], "",
                  "Source context: " + ", ".join("[" + Path(path).name + "](https://github.com/benyblack/ntilde/blob/" + AUDIT_REVISION + "/" + path + ")" for path in item["sourceFiles"]) + ".", ""]
    lines += ["## Observation-by-observation inventory", "", "Every row below retains its original inconclusive outcome. Source-limit notes describe the audited implementation, not a replacement grade.", "",
              "| Feature | Original reason | Work package | Source limitation |", "|---|---|---|---|"]
    for row in sorted(report["observations"], key=lambda r: r["original"]["featureId"]):
        original = row["original"]
        note = row.get("sourceLimitation", "See the original note/trace and work package; no support inference.").replace("|", "\\|")
        lines.append("| `" + original["featureId"] + "` | " + original["reason"] + " | " + row["action"] + " | " + note + " |")
    lines += ["", "## Evidence discipline", "",
              "The geometry-dependent pixel probes measured 41×140, not their required tiny fixtures. CPR, a DA sentinel, consumed input, a rejected setting query, and a legacy feature hint each have narrower meaning than observed effects. Never turn those into a positive or negative capability score.", "",
              "The three OSC 52 probes were deliberately not sent. Native clipboard qualification and session-only OSC 5522 grants are separate follow-ups; they do not retroactively change collector ownership or its historical grades. Keep refusal until that collector has verified disposable ownership and cleanup.", "",
              "Before regrading, retain the new source/runtime/collector pins, assertions or pixel hashes, relevant state-restoration evidence and completed ownership receipt. A source test can document a protocol contract; it cannot masquerade as a live desktop observation.", "",
              "Regenerate with `python3 scripts/terminfo-triage.py --raw <retained-owned-raw.json>`. Validate the committed inventory without a private artifact with `python3 scripts/terminfo-triage.py --check`.", ""]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--raw", type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    if args.raw and args.check:
        parser.error("Choose regeneration or check")
    if args.raw:
        report = build(args.raw)
        STEM.with_suffix(".json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        STEM.with_suffix(".md").write_text(markdown(report), encoding="utf-8")
    else:
        report = parse_report(STEM.with_suffix(".json").read_text(encoding="utf-8"))
        validate(report)
        if STEM.with_suffix(".md").read_text(encoding="utf-8") != markdown(report):
            raise ValueError("Markdown inventory is stale")
    print("Verified 177 unchanged inconclusive observations (152/22/3); no runtime measurements performed.")


if __name__ == "__main__":
    main()
