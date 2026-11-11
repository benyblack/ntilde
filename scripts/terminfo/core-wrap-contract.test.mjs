import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { checkCoreWrapEvidence, includesCoreWrapEvidence } from './core-wrap-receipt.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { specimen, wrapCases, wrapPlan, validateWrapFeeds, gradeWrap, buildWrapReport, deriveWrapEvidence } from './core-wrap-contract.mjs';

function view(lines, cursor, autoWrap) {
    return { rows: 12, cols: 80, lines: [...lines, ...Array(12 - lines.length).fill('')], cursor, autoWrap,
        alternate: true, origin: false, insert: false, top: 0, bottom: 11, left: 0, right: 79,
        leftRightMarginMode: false, historyCount: 0, history: [], cursorVisible: true, storedDefault: true, pendingWrap: false };
}
function good() {
    return Object.fromEntries(wrapCases.flatMap(c => [
        [c.role + ':before', view(['', '', 'GUARD'], [0, 0], c.enabled)],
        [c.role + ':after', view(c.lines, c.cursor, c.enabled)],
    ]));
}
test('boundary texts are contiguous, exact ASCII and separately prepared', () => {
    assert.equal(specimen.length, 85);
    const plan = wrapPlan();
    assert.deepEqual(plan.filter(x => x.read).map(x => x.read), wrapCases.flatMap(c => [c.role + ':before', c.role + ':after']));
    assert.deepEqual(plan.filter(x => x.feed && !x.feed.startsWith('\x1b')).map(x => x.feed), wrapCases.map(c => c.text));
    for (const c of wrapCases) assert.match(c.text, /^[0-9A-E]+$/);
    assert.deepEqual(wrapCases.map(c => c.text.length), [80, 81, 85, 85]);
    assert.equal(wrapCases[2].lines[0], '01234567890123456789012345678901234567890123456789012345678901234567890123456789');
    assert.equal(wrapCases[2].lines[1], 'ABCDE');
    assert.equal(wrapCases[3].lines[0], '0123456789012345678901234567890123456789012345678901234567890123456789012345678E');
    const feeds = plan.filter(x => x.feed).map(x => x.feed);
    validateWrapFeeds(feeds);
    for (const suffix of ['\\n', '\\r', '\\x1b[2;1H']) {
        const tampered = [...feeds]; tampered[5] += suffix;
        assert.throws(() => validateWrapFeeds(tampered), /bytes changed/);
    }
});
test('observed cells distinguish wrapping from movement, ignored text and bad splits', () => {
    assert.ok(Object.values(gradeWrap(good())).every(x => x.outcome === 'supported'));
    for (const mutate of [
        a => { a.lines[0] = ''; a.lines[1] = ''; },
        a => { a.lines[1] = ''; },
        a => { a.lines[0] = specimen.slice(0, 79); a.lines[1] = specimen.slice(79); },
        a => { a.lines[2] = ''; },
        a => { a.cursor = [0, 79]; },
    ]) {
        const roles = good(); mutate(roles['enabled-85:after']);
        assert.equal(gradeWrap(roles)['enabled-85'].outcome, 'unsupported');
    }
    const off = good(); off['disabled-85:after'].lines[1] = 'ABCDE';
    assert.equal(gradeWrap(off)['disabled-85'].outcome, 'unsupported');
    const modeOnly = good(); modeOnly['enabled-85:after'] = structuredClone(modeOnly['enabled-85:before']);
    assert.equal(gradeWrap(modeOnly)['enabled-85'].outcome, 'unsupported');
});
test('stale rows, missing observations, wrong geometry and wrong setup invalidate the experiment', () => {
    for (const mutate of [
        r => { r['enabled-85:before'].lines[1] = 'ABCDE'; },
        r => { r['enabled-85:before'].autoWrap = false; },
        r => { r['enabled-85:after'].cols = 140; },
        r => { r['enabled-85:after'].origin = true; },
        r => { delete r['enabled-85:after']; },
    ]) { const roles = good(); mutate(roles); assert.throws(() => gradeWrap(roles)); }
});
test('the 80th character does not require immediate movement or a retained pending latch', () => {
    const roles = good(); roles['enabled-80:after'].pendingWrap = true;
    assert.equal(gradeWrap(roles)['enabled-80'].outcome, 'supported');
    roles['enabled-80:after'].pendingWrap = false;
    assert.equal(gradeWrap(roles)['enabled-80'].outcome, 'supported');
});

function journal() {
    const archive = new URL('../../docs/qa/terminfo/2026-11-11-core-erase-results/owned-run.zip', import.meta.url);
    const raw = execFileSync('/usr/bin/unzip', ['-p', archive.pathname, 'ce-snapshots.jsonl'], { maxBuffer: 1024 * 1024 }).toString();
    const base = JSON.parse(raw.split('\n')[0]).screen;
    base.cols = 80; base.horizontalMargins.rightMargin = 79;
    for (const field of ['attributeRuns', 'cellDetails', 'resolvedColors']) base[field][0].count = base.rows * 80;
    const id = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111', barrier = '\x1b[?1;2c';
    const frame = (screen, kind, index, cpr) => ({ index, kind, paneId, barrier: index ? barrier : null,
        screen, sha256: snapshotHash(screen), ...(cpr && { cpr }) });
    const bound = structuredClone(base); bound.bufferState.alternateScreen = true;
    bound.lines[0] = 'NTILDE_CW_' + id; bound.cursorCol = bound.lines[0].length;
    const frames = [frame(structuredClone(base), 'cw-start', 0), frame(bound, 'cw-bind', 1)], reads = [];
    for (const c of wrapCases) for (const part of ['before', 'after']) {
        const screen = structuredClone(base); screen.bufferState.alternateScreen = true; screen.bufferState.autoWrap = c.enabled;
        const lines = part === 'before' ? ['', '', 'GUARD'] : c.lines;
        screen.lines = Array(screen.rows).fill(''); lines.forEach((line, i) => { screen.lines[i] = line; });
        [screen.cursorRow, screen.cursorCol] = part === 'before' ? [0, 0] : c.cursor;
        const row = screen.cursorRow + 1, col = screen.cursorCol + 1;
        const cpr = { barrier, reply: { raw: `\x1b[${row};${col}R`, row, col } };
        reads.push({ role: c.role + ':' + part, cpr }); frames.push(frame(screen, 'cw-read', frames.length, cpr));
    }
    frames.push(frame(structuredClone(base), 'cw-finish', frames.length));
    const feeds = wrapPlan().filter(s => s.feed).map(s => s.feed);
    return { id, frames, run: { suiteId: 'ntilde-core-wrap-v1', runId: id, feeds, barriers: feeds.map(() => barrier), reads } };
}
test('journal re-derivation rejects missing, altered, stale and unrestored evidence', () => {
    const { id, frames, run } = journal();
    assert.ok(Object.values(buildWrapReport(frames, run, id).effects).every(x => x.outcome === 'supported'));
    assert.throws(() => buildWrapReport(frames, run, 'b'.repeat(32)), /identity/);
    assert.throws(() => buildWrapReport(frames.slice(0, -1), run, id), /Incomplete/);
    const altered = structuredClone(run); altered.feeds[5] += '\n';
    assert.throws(() => buildWrapReport(frames, altered, id), /bytes changed/);
    const badReply = structuredClone(run); badReply.reads[0].cpr.reply.col++;
    assert.throws(() => buildWrapReport(frames, badReply, id), /CPR changed/);
    const stale = structuredClone(frames); stale[3].screen.lines[1] = 'fake';
    assert.throws(() => buildWrapReport(stale, run, id), /snapshot/);
    const unrestored = structuredClone(frames); unrestored.at(-1).screen.cursorCol++;
    unrestored.at(-1).sha256 = snapshotHash(unrestored.at(-1).screen);
    assert.throws(() => buildWrapReport(unrestored, run, id), /restored/);
});

test('geometry and mode restoration are required in addition to the cell journal', () => {
    const { id, frames, run } = journal(), paneId = frames[0].paneId;
    const before = structuredClone(frames[0].screen); before.cols = 140; before.horizontalMargins.rightMargin = 139;
    for (const field of ['attributeRuns', 'cellDetails', 'resolvedColors']) before[field][0].count = before.rows * 140;
    const action = (index, cols) => ({ index, target: { rows: before.rows, cols }, observed: { runId: id, paneId,
        observedAt: '2026-11-11T12:00:00.000Z', app: { rows: before.rows, cols, exact: true, layoutSettled: true },
        terminalOutput: { tool: '/bin/stty', outputProcessing: false, description: `${before.rows} rows; ${cols} columns; -opost` } } });
    const geometry = { paneId, original: { rows: before.rows, cols: 140 }, before, after: structuredClone(before),
        actions: [action(0, 80), action(1, 140)] };
    assert.equal(Object.keys(deriveWrapEvidence(frames, run, id, geometry).effects).length, 4);
    const changed = structuredClone(geometry); changed.after.bufferState.autoWrap = false;
    assert.throws(() => deriveWrapEvidence(frames, run, id, changed), /restored/);
    const wrong = structuredClone(geometry); wrong.actions[0].observed.terminalOutput.description = `${before.rows} rows; 140 columns; -opost`;
    assert.throws(() => deriveWrapEvidence(frames, run, id, wrong), /geometry/);
});

test('receipt rejects a missing declaration, altered or coherently rehashed fixture, and missing restoration', () => {
    const { id, frames, run } = journal(), paneId = frames[0].paneId, dir = mkdtempSync(join(tmpdir(), 'nt-wrap-receipt-'));
    const before = structuredClone(frames[0].screen); before.cols = 140; before.horizontalMargins.rightMargin = 139;
    for (const field of ['attributeRuns', 'cellDetails', 'resolvedColors']) before[field][0].count = before.rows * 140;
    const action = (index, cols) => ({ index, target: { rows: before.rows, cols }, observed: { runId: id, paneId,
        observedAt: '2026-11-11T12:00:00.000Z', app: { rows: before.rows, cols, exact: true, layoutSettled: true },
        terminalOutput: { tool: '/bin/stty', outputProcessing: false, description: `${before.rows} rows; ${cols} columns; -opost` } } });
    const geometry = { paneId, original: { rows: before.rows, cols: 140 }, before, after: structuredClone(before), actions: [action(0, 80), action(1, 140)] };
    const hash = b => createHash('sha256').update(b).digest('hex');
    const write = (name, value) => { const b = Buffer.from(JSON.stringify(value)); writeFileSync(join(dir, name), b); return hash(b); };
    try {
        const proof = { proofs: [{ coreWrap: geometry, terminalOutput: { description: `${before.rows} rows; 140 columns; -opost` } }] };
        const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v42', coreWrapSuiteId: run.suiteId };
        write('cell-observations.json', { snapshots: [{ paneId, screen: before }] });
        writeFileSync(join(dir, 'cw-snapshots.jsonl'), frames.map(f => JSON.stringify(f)).join('\n') + '\n');
        const binding = { runId: id, suiteId: run.suiteId, file: 'cw-observations.json', runFile: 'cw-run.json',
            geometryFile: 'cw-geometry.json', provisionalUntil: 'cell-receipt.json' };
        binding.geometrySha256 = write(binding.geometryFile, geometry); binding.runSha256 = write(binding.runFile, run);
        binding.sha256 = write(binding.file, deriveWrapEvidence(frames, run, id, geometry));
        const evidence = { coreEraseEvidence: {}, tabStopsEvidence: {}, coreWrapEvidence: binding };
        assert.ok(includesCoreWrapEvidence(dir, evidence, context, proof));
        assert.doesNotThrow(() => checkCoreWrapEvidence(dir, evidence, id, context, proof));
        const changed = structuredClone(run); changed.feeds[5] += '\n'; binding.runSha256 = write(binding.runFile, changed);
        assert.throws(() => checkCoreWrapEvidence(dir, evidence, id, context, proof), /bytes changed/);
        binding.runSha256 = write(binding.runFile, run);
        delete evidence.tabStopsEvidence; assert.throws(() => checkCoreWrapEvidence(dir, evidence, id, context, proof), /predecessor/);
        evidence.tabStopsEvidence = {}; const incomplete = structuredClone(geometry); incomplete.actions.pop();
        binding.geometrySha256 = write(binding.geometryFile, incomplete); proof.proofs[0].coreWrap = incomplete;
        assert.throws(() => checkCoreWrapEvidence(dir, evidence, id, context, proof), /missing actions/);
    } finally { rmSync(dir, { recursive: true, force: true }); }
});
