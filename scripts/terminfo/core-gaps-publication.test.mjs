import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { backendResetBytes } from './core-gaps.mjs';
import { coreGapsPublication, sourcePublication } from './core-gaps-publication.mjs';
import { coreGapsQualCaseIds, coreGapsQualSuiteId, coreGapsQualContractsVersion } from './core-gaps-contracts.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
const dir = join(qa, '2026-11-15-core-gaps/'), carrier = join(qa, sourcePublication + '/');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = (base, name) => readFileSync(join(base, name));
const manifest = JSON.parse(read(carrier, 'manifest.json'));
const sourceOf = () => ({ ...JSON.parse(read(carrier, 'summary.json')), summarySha256: hash(read(carrier, 'summary.json')), archiveSha256: manifest.archives['owned-run.zip'] });

/** The retained carrying run, extracted once into a fresh directory. */
function withRun(body) {
    const root = mkdtempSync(join(tmpdir(), 'nt-cg-publication-'));
    try {
        extractOwnedArchive(carrier, manifest, root, join(carrier, 'owned-run.zip'));
        return body(root);
    } finally { rmSync(root, { recursive: true, force: true }); }
}
const mutateJson = (root, file, change) => { const path = join(root, file), value = JSON.parse(readFileSync(path)); change(value); writeFileSync(path, JSON.stringify(value)); };

test('the carrying publication binds the retained run and this projection cites its exact bytes', () => {
    const summary = JSON.parse(read(dir, 'summary.json'));
    assert.equal(hash(read(carrier, 'owned-run.zip')), manifest.archives['owned-run.zip']);
    assert.equal(summary.source.archiveSha256, manifest.archives['owned-run.zip']);
    assert.equal(summary.source.summarySha256, hash(read(carrier, 'summary.json')));
    assert.equal(summary.source.publication, `docs/qa/terminfo/${sourcePublication}/`);
    assert.equal(summary.ownedRunId, JSON.parse(read(carrier, 'summary.json')).ownedRunId);
    assert.equal(summary.appRevision, 'aaaece9966cd62ae3e0cbf9afe1c87a52fb120db');
    assert.equal(summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v58');
    assert.equal(summary.receipt.schemaVersion, 64);
});

test('the retained run independently rederives the complete receipt, the Core-gaps observations and this projection', () => withRun(root => {
    const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
    const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
    rmSync(join(root, 'cell-receipt.json'));
    // The re-issued receipt re-checks the Core-gaps binding itself: the retained report must equal the live grading module's rebuild of the retained journal.
    assert.deepEqual({ ...completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence), completedAt }, original);
    writeFileSync(join(root, 'cell-receipt.json'), JSON.stringify(original));
    const projected = coreGapsPublication(root, sourceOf());
    // A newly finalized receipt would carry a new timestamp; the projection keeps the original receipt as retained.
    assert.deepEqual(projected.summary, JSON.parse(read(dir, 'summary.json')));
    assert.deepEqual(projected.report, JSON.parse(read(dir, 'report.json')));
    assert.equal(`${JSON.stringify(projected.report, null, 2)}\n`, read(dir, 'report.json').toString('utf8'));
    assert.equal(`${JSON.stringify(projected.summary, null, 2)}\n`, read(dir, 'summary.json').toString('utf8'));
    assert.equal(original.coreGapsEvidence.sha256, hash(read(root, 'cg-observations.json')));
}));

test('the projection admits exactly the two contracted ids with both verdicts, no divergence and a stated reset() boundary', () => {
    const report = JSON.parse(read(dir, 'report.json'));
    assert.equal(report.suiteId, coreGapsQualSuiteId);
    assert.equal(report.contractsVersion, coreGapsQualContractsVersion);
    assert.deepEqual(report.cases.map(item => item.caseId), [...coreGapsQualCaseIds]);
    assert.deepEqual([...coreGapsQualCaseIds], ['text.reverse-index-scroll', 'reset.method']);
    for (const item of report.cases) {
        assert.equal(item.pinned.pass, true);
        assert.equal(item.pinned.observation.outcome, 'supported');
        assert.equal(item.effectContract.id, 'ntilde-core-gaps-effect-v1');
        assert.equal(item.contractResult.outcome, 'supported');
        assert.equal(item.conclusion.outcome, 'supported');
        assert.equal(item.divergence, undefined);
        assert.ok(item.pinnedCriterion.includes(`probe("${item.caseId}"`));
    }
    const reset = report.cases[1];
    assert.match(reset.conclusion.note, /collector's backend API, not an App endpoint/);
    assert.match(report.scope, /the App itself has no reset\(\) endpoint/);
    assert.match(report.scope, /RIS \(reset\.ris\) is a separate case/);
    // The reported child TERM is the actual session child's, not a label.
    assert.equal(report.child.term, 'xterm-256color');
});

test('decisive observations: the marker moved down inside the margins, nothing else scrolled, and every reset() call returned the pristine state', () => {
    const [ri, reset] = JSON.parse(read(dir, 'report.json')).cases.map(item => item.observed);
    const { pinnedTopBoundary: top, interiorRegion: region, belowTopMarginControl: control } = ri;
    // Pinned fixture: margins 1;5, MARKER at the home cell, ESC M from the home cell.
    assert.deepEqual(top.before.rows.slice(0, 2), ['MARKER', '']);
    assert.deepEqual(top.after.rows.slice(0, 3), ['', 'MARKER', '']);
    assert.deepEqual([top.before.margins, top.after.margins], [[0, 4], [0, 4]]);
    assert.deepEqual(top.after.cursor, [0, 0]);
    assert.equal(top.after.historyLines, 0);
    assert.equal(top.restoredMargins[0], 0);
    // Interior region: rows 2-5 scroll down one line, ABOVE and OUTSIDE are untouched, the cursor stays on the region's top row.
    assert.deepEqual(region.before.rows, ['ABOVE', 'SECOND', 'THIRD', 'FOURTH', 'BOTTOM', 'OUTSIDE']);
    assert.deepEqual(region.after.rows, ['ABOVE', '', 'SECOND', 'THIRD', 'FOURTH', 'OUTSIDE']);
    assert.deepEqual(region.after.cursor, [1, 2]);
    // Cursor below the top margin: only the cursor moves up.
    assert.deepEqual(control.after.rows, control.before.rows);
    assert.deepEqual([control.before.cursor, control.after.cursor], [[3, 2], [2, 2]]);
    for (const state of [top.before, top.after, region.before, region.after, control.before, control.after]) assert.equal(state.storedDefault, true);
    // reset(): three real calls, each with the contracted bytes.
    assert.equal(reset.calls.length, 3);
    for (const call of reset.calls) { assert.equal(call.method, 'reset'); assert.equal(call.bytesMatchContract, true); assert.equal(call.bytesSha256, hash(backendResetBytes)); }
    const pristine = state => state.blankViewport && state.cursor[0] === 0 && state.cursor[1] === 0 && state.historyLines === 0 && !state.alternate && !state.origin && !state.insert
        && !state.pendingWrap && state.autoWrap && state.cursorVisible && state.margins[0] === 0 && state.margins[1] === 39 && state.storedDefault;
    assert.equal(reset.helloWorld.before.firstRow, 'Hello World');
    assert.deepEqual(reset.helloWorld.before.cursor, [0, 11]);
    for (const scenario of [reset.helloWorld, reset.dirtyMainBank, reset.alternateOverDirtyMain]) assert.ok(pristine(scenario.after));
    // The dirty controls are real: modes, margins, hidden cursor, history and the SGR pen were non-default before the call.
    for (const scenario of [reset.dirtyMainBank, reset.alternateOverDirtyMain]) {
        assert.ok(scenario.dirtyHistory > 0);
        const before = scenario.before;
        assert.deepEqual([before.origin, before.insert, before.autoWrap, before.cursorVisible, before.storedDefault], [true, true, false, false, false]);
        assert.deepEqual(before.margins, [2, 8]);
    }
    assert.equal(reset.dirtyMainBank.before.alternate, false);
    assert.equal(reset.alternateOverDirtyMain.before.alternate, true);
    // The reset pen: a later character is stored with default attributes at the home cell.
    assert.equal(reset.penAfterReset.firstRow, 'X');
    assert.equal(reset.penAfterReset.storedDefault, true);
});

test('the projection refuses altered, stale, cross-run or incomplete retained evidence', () => withRun(root => {
    const fresh = () => { const copy = mkdtempSync(join(tmpdir(), 'nt-cg-mutated-')); cpSync(root, copy, { recursive: true }); return copy; };
    const refuses = (change, pattern) => {
        const copy = fresh();
        try { change(copy); assert.throws(() => coreGapsPublication(copy, sourceOf()), pattern); } finally { rmSync(copy, { recursive: true, force: true }); }
    };
    // Flipping a retained effect verdict breaks the byte binding to the receipt.
    refuses(copy => mutateJson(copy, 'cg-observations.json', value => { value.cases[0].effectContract.outcome = 'unsupported'; }), /retained bytes differ|not what the retained journal grades to/);
    // A coherently rewritten report and receipt binding still disagrees with the journal the grading module rebuilds from.
    refuses(copy => {
        mutateJson(copy, 'cg-observations.json', value => { value.cases[1].effectContract.outcome = 'unsupported'; });
        mutateJson(copy, 'cell-receipt.json', value => { value.coreGapsEvidence.sha256 = hash(readFileSync(join(copy, 'cg-observations.json'))); });
    }, /not what the retained journal grades to/);
    // A stripped case, a cross-run report and a stale journal.
    refuses(copy => {
        mutateJson(copy, 'cg-observations.json', value => { value.cases = value.cases.filter(item => item.caseId !== 'reset.method'); });
        mutateJson(copy, 'cell-receipt.json', value => { value.coreGapsEvidence.sha256 = hash(readFileSync(join(copy, 'cg-observations.json'))); });
    }, /not what the retained journal grades to/);
    refuses(copy => mutateJson(copy, 'context.json', value => { value.runId = '0'.repeat(32); }), /run identity mismatch/);
    refuses(copy => {
        const path = join(copy, 'cg-snapshots.jsonl'), lines = readFileSync(path, 'utf8').trim().split('\n');
        const frame = JSON.parse(lines[5]); frame.screen.lines[0] = 'TAMPERED'; lines[5] = JSON.stringify(frame);
        writeFileSync(path, lines.join('\n') + '\n');
    }, /./);
    // A different adapter bundle than the one the receipt bound.
    refuses(copy => writeFileSync(join(copy, 'collector-cg.mjs'), readFileSync(join(copy, 'collector-cg.mjs'), 'utf8') + '\n// tampered\n'), /adapter bundle differs/);
    // The carrying publication must be the one for this run.
    const copy = fresh();
    try { assert.throws(() => coreGapsPublication(copy, { ...sourceOf(), ownedRunId: 'f'.repeat(32) }), /another run/); } finally { rmSync(copy, { recursive: true, force: true }); }
}));
