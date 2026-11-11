import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdtempSync, rmSync, mkdirSync, cpSync } from 'node:fs';
import { join, posix, win32 } from 'node:path';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';
import { root, publication, loadMapping, extractOwnedArchive, verifyOwnedRun, deriveReport, readable, reportText,
    assertRequirementRows, verifyStockTrace } from '../terminfo-core-results.mjs';
import { buildCoreGapsReport, gradeCoreGapEffects } from './core-gaps.mjs';
import { rederivePrivateAdapter } from './private-clipboard-receipt.mjs';
import { checkCoreGapsEvidence } from './core-gaps-receipt.mjs';

const mapping = loadMapping(), directory = mkdtempSync(join(tmpdir(), 'ntilde-core-test-'));
const parse = name => JSON.parse(readFileSync(join(directory, name)));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
extractOwnedArchive(root, mapping, directory);
test.after(() => rmSync(directory, { recursive: true, force: true }));
const frames = readFileSync(join(directory, 'cg-snapshots.jsonl'), 'utf8').trim().split('\n').map(JSON.parse);
const run = parse('cg-run.json'), core = parse('cg-observations.json'), context = parse('context.json'), receipt = parse('cell-receipt.json');
const rolesOf = value => { let i = 2; return Object.fromEntries(run.records.filter(s => s.role).map(s => [s.role, value[i++]])); };

test('the complete current receipt and all 80 result rows reproduce byte for byte', () => {
    const identity = verifyOwnedRun(directory, mapping), report = deriveReport(directory, mapping, identity);
    assert.deepEqual(report.totals, { pass: 73, fail: 0, inconclusive: 7 });
    assert.equal(report.corePassPercentage, 91.25);
    assert.equal(report.rows.length, 80);
    assert.equal(new Set(report.rows.map(r => r.id)).size, 80);
    assert.equal(reportText(report), readFileSync(join(root, publication, 'report.json'), 'utf8').replaceAll('\r\n', '\n'));
    assert.equal(readable(report, mapping), readFileSync(join(root, publication, 'report.md'), 'utf8').replaceAll('\r\n', '\n'));
    for (const row of report.rows) {
        assert.equal(row.measuredRunId, mapping.measurement.runId);
        assert.ok(row.evidence.length && row.explanation && row.requirement);
    }
    for (const id of ['scrollback.set-region', 'scrollback.alt-screen', 'scrollback.decstbm-reset']) {
        const row = report.rows.find(r => r.id === id);
        assert.equal(row.conclusion, 'pass');
        assert.equal(row.pinnedProbe.qualified[0].verdict, 'inconclusive');
        assert.equal(row.effectVerdicts[0].verdict, 'supported');
    }
});

test('IDs cannot be added, omitted, duplicated or substituted', () => {
    const rows = mapping.rows, expected = rows.map(r => r.id);
    assert.doesNotThrow(() => assertRequirementRows(rows, expected));
    assert.throws(() => assertRequirementRows(rows.slice(1), expected), /80 exact/);
    assert.throws(() => assertRequirementRows([...rows, rows[0]], expected), /80 exact/);
    assert.throws(() => assertRequirementRows([rows[0], ...rows.slice(0, -1)], expected), /80 exact/);
    assert.throws(() => assertRequirementRows([{ ...rows[0], id: 'scrollback.not-core' }, ...rows.slice(1)], expected), /80 exact/);
});

test('all four new effects are decided from their own observations', () => {
    assert.deepEqual(buildCoreGapsReport(frames, run, run.runId, core.adapter), core);
    for (const c of core.cases) assert.equal(c.effectContract.outcome, 'supported');
    assert.equal(core.cases.find(c => c.caseId === 'extensions.osc1-icon').pinned.notTested.reason, 'no-semantic-observable');
    assert.equal(core.child.term, 'xterm-256color');
});

for (const [name, change] of [
    ['ignored RI', roles => { roles.riAfter.screen.lines = [...roles.riBefore.screen.lines]; }],
    ['cursor-only RI', roles => { roles.regionAfter.screen.lines = [...roles.regionBefore.screen.lines]; }],
    ['wrong-direction RI', roles => { const lines = [...roles.regionBefore.screen.lines]; lines.splice(1, 1); lines.splice(4, 0, ''); roles.regionAfter.screen.lines = lines; }],
]) test('the RI effect rejects ' + name, () => {
    const roles = rolesOf(structuredClone(frames)); change(roles);
    assert.equal(gradeCoreGapEffects(roles, run)['text.reverse-index-scroll'].outcome, 'unsupported');
});

test('an invalid dirty-reset control refuses a grade; an incomplete reset fails the effect', () => {
    const roles = rolesOf(structuredClone(frames)); roles.dirtyModes.screen.bufferState.insertMode = false;
    assert.throws(() => gradeCoreGapEffects(roles, run), /non-default reset setup/);
    const mismatch = rolesOf(structuredClone(frames)); mismatch.cleanReset.screen.bufferState.insertMode = true;
    assert.equal(gradeCoreGapEffects(mismatch, run)['reset.method'].outcome, 'unsupported');
});

test('RIS bytes or a pristine preparation cannot replace the measured API calls', () => {
    const changed = structuredClone(run); changed.calls = [];
    assert.throws(() => buildCoreGapsReport(frames, changed, run.runId, core.adapter), /API was not called/);
});

test('missing, unpaired or altered evidence and stale run identities are refused', () => {
    assert.throws(() => buildCoreGapsReport(frames.slice(0, -1), run, run.runId, core.adapter), /Incomplete/);
    assert.throws(() => buildCoreGapsReport(frames, run, '0'.repeat(32), core.adapter), /identity mismatch/);
    const changed = structuredClone(frames); changed[2].facts.after.requestedIconName = 'other';
    assert.throws(() => buildCoreGapsReport(changed, run, run.runId, core.adapter), /unpaired/);
    const forged = structuredClone(run); forged.pinned[0].response = '{}';
    assert.throws(() => buildCoreGapsReport(frames, forged, run.runId, core.adapter), /journal replay/);
});

test('title changes and responsiveness cannot qualify an ignored OSC 1', () => {
    const roles = rolesOf(structuredClone(frames));
    roles.iconDistinct.facts.before.requestedIconName = 'test-icon';
    assert.equal(gradeCoreGapEffects(roles, run)['extensions.osc1-icon'].outcome, 'unsupported');
});

test('parent environment or a supplied label cannot replace emitted child output', () => {
    const changed = structuredClone(run); changed.child.term = 'xterm-kitty';
    assert.throws(() => gradeCoreGapEffects(rolesOf(frames), changed), /child environment output/);
});

test('a coherently rehashed API-call deletion still fails the independent receipt check', () => {
    const path = join(directory, 'cg-run.json'), original = readFileSync(path), changed = structuredClone(run);
    changed.calls = []; writeFileSync(path, JSON.stringify(changed) + '\n');
    const evidence = structuredClone(receipt); evidence.coreGapsEvidence.runSha256 = hash(readFileSync(path));
    try { assert.throws(() => checkCoreGapsEvidence(directory, evidence, run.runId, context, parse('ownership-proof.json')), /API was not called/); }
    finally { writeFileSync(path, original); }
});

test('stock-supported outcomes cannot conceal missing or different query controls', () => {
    const raw = parse('raw.json'), geometry = { rows: 40, cols: 140 };
    assert.equal(verifyStockTrace('cursor.move.absolute', raw, geometry).verdict, 'supported');
    const trace = JSON.parse(raw.rawReplies['cursor.move.absolute']); trace.queries.at(-1).match[0] = '\x1b[1;1R';
    raw.rawReplies['cursor.move.absolute'] = JSON.stringify(trace);
    assert.throws(() => verifyStockTrace('cursor.move.absolute', raw, geometry), /reply|order|CPR/);
});

test('tampered mapping and archive inputs are refused before grades are used', () => {
    const base = mkdtempSync(join(tmpdir(), 'ntilde-core-tamper-'));
    try {
        const target = join(base, publication); mkdirSync(target, { recursive: true });
        cpSync(join(root, publication), target, { recursive: true });
        mkdirSync(join(base, 'docs/qa/terminfo'), { recursive: true });
        cpSync(join(root, 'docs/qa/terminfo/core-baseline-inputs.json'), join(base, 'docs/qa/terminfo/core-baseline-inputs.json'));
        const path = join(target, 'owned-run.zip'); writeFileSync(path, Buffer.concat([readFileSync(path), Buffer.from('tampered')]));
        assert.throws(() => loadMapping(base), /changed archive/);
        const map = join(target, 'requirements-v1.json'); writeFileSync(map, readFileSync(map, 'utf8') + ' ');
        assert.throws(() => loadMapping(base), /mapping bytes changed/);
    } finally { rmSync(base, { recursive: true, force: true }); }
});

test('macOS adapter bytes rederive with POSIX or Windows path joining', () => {
    const source = readFileSync(join(directory, 'collector-original.mjs'), 'utf8');
    const actual = readFileSync(join(directory, 'collector-adapter.mjs'), 'utf8');
    assert.equal(rederivePrivateAdapter(source, context.sourceSnapshotRoot, posix.join), actual);
    assert.equal(rederivePrivateAdapter(source, context.sourceSnapshotRoot, win32.join), actual);
});

test('main-screen DECOM cannot be masked by alternate-screen exit or a clean main bank', () => {
    const roles = rolesOf(structuredClone(frames));
    roles.dirtyMainForAlt.screen.bufferState.originMode = false;
    assert.throws(() => gradeCoreGapEffects(roles, run), /main reset setup/);
    const bad = rolesOf(structuredClone(frames)); bad.cleanMainReset.screen.bufferState.originMode = true;
    assert.equal(gradeCoreGapEffects(bad, run)['reset.method'].outcome, 'unsupported');
});
