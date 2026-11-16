// A publication-only projection of the retained Core-gaps measurement for the two admitted benchmark ids (text.reverse-index-scroll, reset.method).
//
// The measurement is `ntilde-owned-core-gaps-v1`, collected inside every owned run since the Core requirement run. This projection reads the newest
// merged run (the extended-mouse run) from its retained archive, refuses unless the retained cg-observations.json is byte-for-byte what the live
// grading module rebuilds from the retained journal, and then restates two of its four cases in the report shape the qualification inventory reads.
// It measures nothing and adds no verdict: the pinned callback results and the effect-contract outcomes are copied unchanged.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { buildCoreGapsReport, coreGapsPinned, backendResetBytes } from './core-gaps.mjs';
import { scrollbackView } from './scrollback-contracts.mjs';
import { coreGapsQualCaseIds, coreGapsQualContractsVersion, coreGapsQualEvidence, coreGapsQualGrading } from './core-gaps-contracts.mjs';

export const sourcePublication = '2026-11-15-mouse-extended';
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
const check = (condition, note) => { if (!condition) throw new Error('Core-gaps publication: ' + note); };
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

export const scope = 'Pinned terminfo.dev@4.0.0 probes text.reverse-index-scroll (margins 1;5, "MARKER" at the home cell, ESC M, the marker one row down) and reset.method '
    + '(write "Hello World", call the backend reset(), the cursor home and the text gone). The unchanged pinned callbacks are replayed over the independently journaled '
    + 'screens of their exact writes, beside the separately identified effect contract ntilde-core-gaps-effect-v1: for reverse index, the pinned top-boundary fixture, a separately '
    + 'seeded interior region (rows 2-5, the cursor at its top row) and a control with the cursor below the top margin (ESC M only moves the cursor up; nothing scrolls), with actual scrollback unchanged and the margins restored; for reset, '
    + 'three actual asynchronous backend reset() calls (after Hello World, after a dirty main bank with margins, origin mode, insert mode, no auto-wrap, a hidden cursor and an SGR pen, '
    + 'and from the alternate screen entered over that dirty main bank), each followed by a pristine-state check of cells, cursor, margins, modes, visibility, rendition and actual '
    + 'scrollback. Stored cells, cursor, margins and scrollback only. reset() is the desktop collector\'s own backend API (core-gaps.mjs createCoreBackend), which writes one documented '
    + 'sequence (leave the alternate screen, RIS, then explicit normalization of origin, wrap, insert, cursor visibility, margins, rendition, display and scrollback); the App itself has '
    + 'no reset() endpoint, the stock terminal path still returns insufficient-evidence for reset.method, and RIS (reset.ris) is a separate case. No host presentation, no remote/mux, '
    + 'Windows/Linux or common-harness claim; the stock grades are unchanged by this suite.';

/** The journaled screen read of every role of the retained plan, in plan order (frames 0 and 1 are the start and bind reads). */
function rolesOf(frames, run) {
    const roles = {};
    let index = 2;
    for (const step of run.records) if (step.role) roles[step.role] = frames[index++];
    return roles;
}

const stateOf = view => ({ firstRow: view.lines[0], rows: view.lines.slice(0, 6), cursor: view.cursor, cursorVisible: view.cursorVisible, margins: [view.top, view.bottom],
    alternate: view.alternate, origin: view.origin, insert: view.insert, autoWrap: view.autoWrap, pendingWrap: view.pending, historyLines: view.count,
    storedDefault: view.storedDefault, blankViewport: view.lines.every(line => line === '') });

/** The decisive reads of both cases, restated from the journal exactly as the grading module reads them. */
function observedOf(roles, run) {
    const view = name => scrollbackView(roles[name].screen);
    const pair = (before, after) => ({ before: stateOf(view(before)), after: stateOf(view(after)) });
    return {
        'text.reverse-index-scroll': {
            pinnedTopBoundary: { ...pair('riBefore', 'riAfter'), restoredMargins: stateOf(view('riMarginsRestored')).margins },
            interiorRegion: { ...pair('regionBefore', 'regionAfter'), restoredMargins: stateOf(view('regionRestored')).margins },
            belowTopMarginControl: pair('moveBefore', 'moveAfter'),
        },
        'reset.method': {
            calls: run.calls.map(call => ({ method: call.method, bytesSha256: sha256(call.bytes), bytesMatchContract: call.bytes === backendResetBytes })),
            helloWorld: pair('resetBefore', 'resetAfter'),
            dirtyMainBank: { dirtyHistory: stateOf(view('dirtyHistory')).historyLines, ...pair('dirtyMain', 'cleanMainReset') },
            alternateOverDirtyMain: { dirtyHistory: stateOf(view('dirtyHistoryForAlt')).historyLines, ...pair('dirtyModes', 'cleanReset') },
            penAfterReset: stateOf(view('resetPen')),
        },
    };
}

/**
 * Project the two admitted cases from an extracted owned run. `dir` holds the run's files, `source` is the retained summary of the publication that
 * carries the run (stock counts, measured revisions). Nothing here is trusted from the projection's own bytes: the retained observations must equal
 * the live grading module's rebuild from the retained journal, and every case must carry its two verdicts.
 */
export function coreGapsPublication(dir, source) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const retained = json(dir, 'cg-observations.json'), run = json(dir, 'cg-run.json');
    const frames = readFileSync(join(dir, 'cg-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const binding = receipt.coreGapsEvidence;
    check(binding?.suiteId === retained.suiteId && binding.runId === context.runId && retained.runId === context.runId && run.runId === context.runId, 'run identity mismatch');
    check(binding.sha256 === sha256(readFileSync(join(dir, 'cg-observations.json'))) && binding.runSha256 === sha256(readFileSync(join(dir, 'cg-run.json'))), 'retained bytes differ from the receipt binding');
    check(retained.adapter?.bundleSha256 === sha256(readFileSync(join(dir, 'collector-cg.mjs'))) && binding.adapterBundleSha256 === retained.adapter.bundleSha256, 'adapter bundle differs');
    check(same(buildCoreGapsReport(frames, run, context.runId, retained.adapter), retained), 'the retained observations are not what the retained journal grades to');
    check(source.ownedRunId === context.runId, 'the carrying publication is for another run');
    const roles = rolesOf(frames, run), observed = observedOf(roles, run);
    const cases = coreGapsQualCaseIds.map(caseId => {
        const item = retained.cases.find(entry => entry.caseId === caseId);
        check(item, 'missing case ' + caseId);
        const pinned = item.pinned?.observation?.outcome, effect = item.effectContract?.outcome;
        check(item.pinned.probeId === caseId && ['supported', 'unsupported', 'inconclusive'].includes(pinned) && ['supported', 'unsupported', 'inconclusive'].includes(effect), 'incomplete verdicts for ' + caseId);
        const note = caseId === 'reset.method'
            ? `${item.effectContract.note} The pinned callback ran as a replay over the journaled states of that call; reset() is the collector's backend API, not an App endpoint.`
            : item.effectContract.note;
        return { caseId, pinnedCriterion: coreGapsPinned[caseId], pinned: item.pinned, effectContract: item.effectContract, contractResult: { outcome: effect },
            observed: observed[caseId],
            ...(pinned === effect ? {} : { divergence: { pinnedOutcome: pinned, contractOutcome: effect, kind: 'pinned-differs-from-effect' } }),
            conclusion: { outcome: effect, grading: coreGapsQualGrading, evidence: coreGapsQualEvidence[caseId], note } };
    });
    const report = { schemaVersion: 1, suiteId: retained.suiteId, contractsVersion: coreGapsQualContractsVersion, runId: retained.runId, adapter: retained.adapter, child: retained.child, scope, cases };
    const stock = source.stockCollector;
    const summary = { schemaVersion: 1, measuredAt: source.measuredAt, ownedRunId: context.runId, appRevision: source.appRevision, scriptsRevision: source.scriptsRevision,
        source: { publication: `docs/qa/terminfo/${sourcePublication}/`, summarySha256: source.summarySha256, archiveSha256: source.archiveSha256,
            note: 'The carrying publication retains the owned run, its stock collector grades and its complete receipt; this publication restates two cases of the run\'s Core-gaps suite.' },
        context, receipt,
        stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: stock.probeHash, sourceRevision: stock.sourceRevision,
            note: `Stock terminal-path grades of the same owned run as the carrying ${sourcePublication} publication (its run id is recorded there); both ids stay inconclusive under the unchanged pinned code, so this suite moves no stock grade.` } };
    return { summary, report };
}
