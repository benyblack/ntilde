import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';
import { includesTabStopsEvidence, checkTabStopsEvidence } from './core-tab-stops-receipt.mjs';
import { extractOwnedArchive, loadMapping, root } from '../terminfo-core-results.mjs';
import { snapshotHash, OwnedCellObserver } from './agent-observer.mjs';
import { tabStopsIds, tabStopsPlan, tabStopsSteps, tabStopsSuiteId, tabStopsNonce, tabStopsSpecimens, defaultWalk, defaultStopColumns,
    gradeTabStops, buildTabStopsReport, collectTabStops, TabStopsObserver, fullReset } from './core-tab-stops.mjs';

const dir = mkdtempSync(join(tmpdir(), 'nt-ts-'));
extractOwnedArchive(root, loadMapping(), dir);
const baseline = JSON.parse(readFileSync(join(dir, 'cg-snapshots.jsonl'), 'utf8').split('\n')[0]).screen;
rmSync(dir, { recursive: true, force: true });
const cols = baseline.cols;

/** A deliberately separate terminal model: stops are a set that starts as every eighth column and only HTS, TBC 3 and RIS change it. */
function model(feeds) {
    let stops, grid, row, col;
    const power = () => {
        stops = new Set(); for (let c = 8; c < cols; c += 8) stops.add(c);
        grid = Array.from({ length: baseline.rows }, () => Array(cols).fill(' ')); row = 0; col = 0;
    };
    power();
    const snapshots = [];
    for (const feed of feeds) {
        for (let i = 0; i < feed.length; i++) {
            const ch = feed[i];
            if (ch === '\x1b') {
                const next = feed[++i];
                if (next === 'c') power();
                else if (next === 'H') stops.add(col);
                else if (next === '[') {
                    let params = '';
                    while (!/[@-~]/.test(feed[i + 1])) params += feed[++i];
                    const final = feed[++i], numbers = params.replace('?', '').split(';').map(x => (x === '' ? 0 : Number(x)));
                    if (final === 'H') { row = (numbers[0] || 1) - 1; col = (numbers[1] || 1) - 1; }
                    else if (final === 'g' && numbers[0] === 3) stops.clear();
                    else if (final === 'J' && numbers[0] === 2) grid.forEach(line => line.fill(' '));
                }
            } else if (ch === '\t') col = [...stops].sort((a, b) => a - b).find(stop => stop > col) ?? cols - 1;
            else { grid[row][col++] = ch; }
        }
        snapshots.push({ lines: grid.map(line => line.join('').trimEnd()), cursor: [row, col] });
    }
    return snapshots;
}
test('the plan expectations equal an independent byte-level terminal model', () => {
    const steps = tabStopsSteps(cols), observed = model(steps.map(step => step.feed));
    steps.forEach((step, index) => {
        const expected = new Array(baseline.rows).fill(''); step.lines.forEach((line, i) => { expected[i] = line; }); expected[9] = 'GUARD';
        assert.deepEqual(observed[index].lines, expected, step.role);
        assert.deepEqual(observed[index].cursor, step.cursor, step.role);
    });
});
test('the walk proves every default stop and nothing else; a custom or cleared set cannot match it', () => {
    const walk = defaultWalk(cols);
    assert.equal(walk.line.match(/\*/g).length, defaultStopColumns(cols).length);
    assert.ok(walk.line.length <= cols);
    assert.deepEqual(walk.cursor, [0, cols - 1]);
    const custom = model([walk.feed.replace(/^/, '\x1b[3g\x1b[1;5H\x1bH\x1b[H')])[0];
    assert.notEqual(custom.lines[0], walk.line);
    const cleared = model(['\x1b[3g' + walk.feed])[0];
    assert.notEqual(cleared.lines[0], walk.line);
});
test('the exact target bytes are one write each and the default is never installed with HTS', () => {
    const feeds = tabStopsPlan(cols).filter(x => x.feed !== undefined).map(x => x.feed);
    assert.ok(feeds.includes('\t')); assert.ok(feeds.includes('\tX'));
    const ris = feeds.findIndex(feed => feed.startsWith(fullReset)), targets = feeds.lastIndexOf('\t');
    assert.ok(ris >= 0 && targets > ris, 'targets follow the reset');
    // No HTS at column 8 (one-based 9) and no CHT/CBT anywhere; the only HTS columns are the two custom ones, before the reset.
    const hts = feeds.map((feed, index) => [index, [...feed.matchAll(/\x1b\[1;(\d+)H\x1bH/g)].map(match => Number(match[1]))]).filter(([, list]) => list.length);
    assert.deepEqual(hts.map(([, list]) => list), [[5, 21]]);
    assert.ok(hts.every(([index]) => index < ris));
    assert.ok(!feeds.slice(ris).some(feed => /\x1bH|\x1b\[[0-9;]*[gIZ]/.test(feed)));
});

function frame(lines, cursor) {
    const screen = structuredClone(baseline);
    screen.bufferState.alternateScreen = true;
    screen.horizontalMargins = { leftRightMarginMode: false, leftMargin: 0, rightMargin: screen.cols - 1 };
    screen.lines = Array(screen.rows).fill('');
    lines.forEach((line, i) => { screen.lines[i] = line; });
    screen.lines[9] = 'GUARD';
    [screen.cursorRow, screen.cursorCol] = cursor;
    return { screen };
}
const good = () => Object.fromEntries(tabStopsSteps(cols).map(step => [step.role, frame(step.lines, step.cursor)]));
const grade = roles => gradeTabStops(roles, cols);

test('both rows are supported only from the measured default state and are graded separately', () => {
    const { effects, provenance } = grade(good());
    assert.deepEqual(Object.keys(effects), tabStopsIds);
    assert.ok(Object.values(effects).every(e => e.outcome === 'supported'));
    assert.deepEqual(provenance, { strategy: 'documented-reset-restoration', reset: { sequence: 'ESC c (RIS)', bytes: '\x1bc' },
        customStatePrecededReset: true, defaultStopsRestoredAfterReset: true, defaultStopsIntactAfterControls: true, asFoundWalkMatchesDefault: true });
    for (const [role, field, id, other] of [['ht:result', 'cursorCol', 'unicode.tab-stops', 'text.tab'], ['tab:result', 'cursorCol', 'text.tab', 'unicode.tab-stops']]) {
        const roles = good(); roles[role].screen[field] += 1;
        const result = grade(roles).effects;
        assert.equal(result[id].outcome, 'unsupported'); assert.equal(result[other].outcome, 'supported');
    }
});
test('text.tab needs the stored X and blank preceding cells; a cursor-only observation cannot satisfy it', () => {
    const noCell = good(); noCell['tab:result'].screen.lines[0] = '';
    assert.equal(grade(noCell).effects['text.tab'].outcome, 'unsupported');
    const shifted = good(); shifted['tab:result'].screen.lines[0] = ' '.repeat(7) + 'X';
    assert.equal(grade(shifted).effects['text.tab'].outcome, 'unsupported');
    const written = good(); written['tab:result'].screen.lines[0] = '*'.repeat(8) + 'X';
    assert.equal(grade(written).effects['text.tab'].outcome, 'unsupported');
    const wrongCursor = good(); wrongCursor['tab:result'].screen.cursorCol = 8;
    assert.equal(grade(wrongCursor).effects['text.tab'].outcome, 'unsupported');
    const cursorOnlyOk = good(); cursorOnlyOk['ht:result'].screen.lines[0] = 'ignored';
    assert.equal(grade(cursorOnlyOk).effects['unicode.tab-stops'].outcome, 'unsupported');
    assert.equal(grade(cursorOnlyOk).effects['text.tab'].outcome, 'supported');
});
test('missing default-state proof or leftover custom stops is inconclusive, never a verdict', () => {
    for (const [mutate, reason] of [
        [r => { r['taint:ht1'].screen.cursorCol = 8; }, 'default-state-not-established'], // the custom state was never measured
        [r => { r['taint:ht3'].screen.cursorCol = 24; }, 'default-state-not-established'],
        [r => { r['reset:walk'] = frame([defaultWalk(cols).line.replace('*', ' ')], defaultWalk(cols).cursor); }, 'default-state-not-established'], // RIS did not restore
        [r => { r['reset:walk'].screen.cursorCol = 4; }, 'default-state-not-established'],
        [r => { r['final:walk'].screen.lines[0] = r['final:walk'].screen.lines[0].replace('*', ' '); }, 'default-state-not-established'], // controls altered stops
    ]) {
        const roles = good(); mutate(roles);
        const { effects } = grade(roles);
        for (const id of tabStopsIds) assert.deepEqual([effects[id].outcome, effects[id].reason], ['inconclusive', reason], id);
    }
    // An as-found state that is not default is retained but does not decide the conclusion.
    const found = good(); found['found:walk'].screen.cursorCol = 4;
    const result = grade(found);
    assert.equal(result.provenance.asFoundWalkMatchesDefault, false);
    assert.ok(Object.values(result.effects).every(e => e.outcome === 'supported'));
});
test('the false-positive controls guard text.tab only', () => {
    for (const role of ['ctl:x', 'ctl:spaces', 'ctl:tab-preserves', 'ctl:spaces-overwrite']) {
        const roles = good(); roles[role].screen.cursorCol += 1;
        const { effects } = grade(roles);
        assert.deepEqual([effects['text.tab'].outcome, effects['text.tab'].reason], ['inconclusive', 'observation-controls-invalid'], role);
        assert.equal(effects['unicode.tab-stops'].outcome, 'supported');
    }
    const overwritten = good(); overwritten['ctl:tab-preserves'].screen.lines[0] = ' '.repeat(8) + 'XJ';
    assert.equal(grade(overwritten).effects['text.tab'].outcome, 'inconclusive');
});
test('a missing, invalid-setup or altered-mode observation refuses grading', () => {
    for (const mutate of [
        r => { delete r['tab:result']; }, r => { delete r['reset:ris']; }, r => { r['ht:setup'].screen.cursorCol = 1; },
        r => { r['reset:ris'].screen.lines[9] = ''; }, r => { r['tab:setup'].screen.bufferState.originMode = true; },
        r => { r['ctl:x:setup'].screen.cursorRow = 3; },
    ]) { const roles = good(); mutate(roles); assert.throws(() => grade(roles)); }
    // A state the fixture write itself changed is a measured failure of that specimen, not a setup refusal.
    const margins = good(); margins['ht:result'].screen.horizontalMargins.leftMargin = 2;
    assert.equal(grade(margins).effects['unicode.tab-stops'].outcome, 'unsupported');
});

function journal() {
    const id = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111', barrier = '\x1b[?1;2c';
    const blank = structuredClone(baseline); blank.horizontalMargins = { leftRightMarginMode: false, leftMargin: 0, rightMargin: blank.cols - 1 };
    const bind = structuredClone(blank); bind.bufferState.alternateScreen = true; bind.lines[0] = tabStopsNonce + id; bind.cursorCol = bind.lines[0].length;
    const make = (screen, kind, index, cpr) => ({ index, kind, paneId, barrier: index ? barrier : null, screen, sha256: snapshotHash(screen), ...(cpr && { cpr }) });
    const frames = [make(blank, 'ts-start', 0), make(bind, 'ts-bind', 1)], roles = good(), reads = [], plan = tabStopsPlan(cols);
    for (const step of plan.filter(x => x.read)) {
        const screen = roles[step.role].screen, row = screen.cursorRow + 1, col = screen.cursorCol + 1;
        const cpr = { barrier, reply: { raw: `\x1b[${row};${col}R`, row, col } };
        reads.push({ role: step.role, cpr }); frames.push(make(screen, 'ts-read', frames.length, cpr));
    }
    frames.push(make(structuredClone(blank), 'ts-finish', frames.length));
    const feeds = plan.filter(x => x.feed !== undefined).map(x => x.feed);
    return { id, frames, run: { suiteId: tabStopsSuiteId, runId: id, feeds, barriers: feeds.map(() => barrier), reads } };
}
test('journal rederivation retains raw CPR beside physical cursors and rejects altered bytes, stale frames and incomplete restoration', () => {
    const { id, frames, run } = journal(), report = buildTabStopsReport(frames, run, id);
    assert.deepEqual(Object.keys(report.effects), tabStopsIds);
    const ht = report.effects['unicode.tab-stops'], tab = report.effects['text.tab'];
    assert.deepEqual([ht.fixture, ht.observed.after.cursor, ht.observed.after.cpr.raw], ['\t', [0, 8], '\x1b[1;9R']);
    assert.deepEqual([tab.fixture, tab.observed.after.cursor, tab.observed.after.cpr.raw, tab.observed.after.lines[0]], ['\tX', [0, 9], '\x1b[1;10R', '        X']);
    assert.equal(report.provenance.strategy, 'documented-reset-restoration');
    for (const [from, to] of [['\t', '        '], ['\tX', '        X'], ['\tX', '\t X']]) {
        const changed = structuredClone(run), index = changed.feeds.findIndex(feed => feed === from);
        assert.ok(index >= 0); changed.feeds[index] = to;
        assert.throws(() => buildTabStopsReport(frames, changed, id), /fixture bytes/);
    }
    const hts = structuredClone(run), at = hts.feeds.findIndex(feed => feed === '\t');
    hts.feeds[at] = '\x1b[1;9H\x1bH\x1b[1;1H\t';
    assert.throws(() => buildTabStopsReport(frames, hts, id), /fixture bytes/);
    assert.throws(() => buildTabStopsReport(frames, run, 'b'.repeat(32)), /identity/);
    assert.throws(() => buildTabStopsReport(frames.slice(0, -1), run, id), /Incomplete/);
    const stale = structuredClone(frames); stale[3].screen.cursorCol++;
    assert.throws(() => buildTabStopsReport(stale, run, id), /snapshot/);
    const unrestored = structuredClone(frames); unrestored.at(-1).screen.cursorCol++; unrestored.at(-1).sha256 = snapshotHash(unrestored.at(-1).screen);
    assert.throws(() => buildTabStopsReport(unrestored, run, id), /restored/);
    const wrong = structuredClone(run); wrong.reads[0].cpr.reply.raw = '\x1b[2;1R';
    assert.throws(() => buildTabStopsReport(frames, wrong, id), /CPR changed/);
    const reordered = structuredClone(run); [reordered.reads[7], reordered.reads[9]] = [reordered.reads[9], reordered.reads[7]];
    assert.throws(() => buildTabStopsReport(frames, reordered, id), /read order/);
    const noCpr = structuredClone(frames); noCpr[9].cpr = { barrier: noCpr[9].cpr.barrier, reply: null };
    const noCprRun = structuredClone(run); noCprRun.reads[7].cpr = noCpr[9].cpr;
    assert.throws(() => buildTabStopsReport(noCpr, noCprRun, id), /missing ordered CPR|snapshot/);
});

test('receipt rejects stripped artifacts and a coherently rehashed fixture alteration', () => {
    const out = mkdtempSync(join(tmpdir(), 'nt-ts-receipt-'));
    try {
        const { id, frames, run } = journal(), hash = data => createHash('sha256').update(data).digest('hex');
        const runBytes = Buffer.from(JSON.stringify(run)), result = Buffer.from(JSON.stringify(buildTabStopsReport(frames, run, id)));
        writeFileSync(join(out, 'ts-run.json'), runBytes); writeFileSync(join(out, 'ts-observations.json'), result);
        writeFileSync(join(out, 'ts-snapshots.jsonl'), frames.map(f => JSON.stringify(f)).join('\n') + '\n');
        const context = { tabStopsSuiteId }, evidence = { coreEraseEvidence: {}, textCursorEvidence: {}, tabStopsEvidence: { runId: id, suiteId: tabStopsSuiteId,
            file: 'ts-observations.json', runFile: 'ts-run.json', sha256: hash(result), runSha256: hash(runBytes), provisionalUntil: 'cell-receipt.json' } };
        assert.ok(includesTabStopsEvidence(out, evidence, context));
        assert.doesNotThrow(() => checkTabStopsEvidence(out, evidence, id, context));
        const forged = structuredClone(run); forged.feeds[forged.feeds.indexOf('\tX')] = '        X';
        const bytes = Buffer.from(JSON.stringify(forged)); writeFileSync(join(out, 'ts-run.json'), bytes); evidence.tabStopsEvidence.runSha256 = hash(bytes);
        assert.throws(() => checkTabStopsEvidence(out, evidence, id, context), /fixture bytes/);
        writeFileSync(join(out, 'ts-run.json'), runBytes); evidence.tabStopsEvidence.runSha256 = hash(runBytes);
        const mismatched = structuredClone(evidence); mismatched.tabStopsEvidence.runId = 'c'.repeat(32);
        assert.throws(() => checkTabStopsEvidence(out, mismatched, id, context), /binding mismatch/);
        const noPredecessor = structuredClone(evidence); delete noPredecessor.coreEraseEvidence;
        assert.throws(() => checkTabStopsEvidence(out, noPredecessor, id, context), /predecessor/);
        writeFileSync(join(out, 'ts-snapshots.jsonl'), '');
        assert.throws(() => checkTabStopsEvidence(out, evidence, id, context));
        assert.equal(includesTabStopsEvidence(out, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v41' }), true);
    } finally { rmSync(out, { recursive: true, force: true }); }
});

test('observer binds each cursor reply to the completed read and rejects a wrong barrier', async () => {
    const original = OwnedCellObserver.prototype.handle, events = [], observer = Object.create(TabStopsObserver.prototype);
    observer.frames = [];
    OwnedCellObserver.prototype.handle = async function (kind, budget, barrier) { events.push([kind, budget, barrier]); this.frames.push({}); return 'ok'; };
    try {
        const barrier = '\x1b[?1;2c', cpr = { barrier, reply: { raw: '\x1b[1;1R', row: 1, col: 1 } };
        assert.equal(await observer.handleRequest({ kind: 'ts-read', barrier, cpr }, 5), 'ok');
        assert.deepEqual(events, [['ts-read', 5, barrier]]); assert.deepEqual(observer.frames[0].cpr, cpr);
        await assert.rejects(observer.handleRequest({ kind: 'ts-read', barrier: 'wrong', cpr }, 5), /barrier mismatch/);
        await assert.rejects(observer.handleRequest({ kind: 'ts-start', barrier, cpr }, 5), /unexpected CPR/);
    } finally { OwnedCellObserver.prototype.handle = original; }
});

test('collector serializes each write before its CPR and observation, and restores with a full reset after a refusal', async () => {
    const events = [], barrier = '\x1b[?1;2c';
    const fence = { write: async bytes => { events.push(['write', bytes]); return barrier; },
        cursorReport: async () => { events.push(['cpr']); return { barrier, reply: { raw: '\x1b[1;1R', row: 1, col: 1 } }; } };
    let reads = 0;
    const owner = async kind => { events.push([kind]); if (kind === 'ts-read' && ++reads === 3) throw new Error('refused'); return { screen: baseline }; };
    await assert.rejects(collectTabStops({ config: { runId: 'a'.repeat(32) }, fence, owner }), /refused/);
    const taint = events.findIndex(event => event[0] === 'write' && event[1].includes('\x1b[3g'));
    assert.ok(taint > 0); assert.deepEqual(events[taint + 1], ['cpr']); assert.deepEqual(events[taint + 2], ['ts-read']);
    const finish = events.at(-1), last = events.at(-2);
    assert.equal(finish[0], 'ts-finish');
    assert.ok(last[0] === 'write' && last[1].startsWith('\x1bc'), 'restoration begins with RIS so custom stops never outlive the suite');
    assert.equal(events.filter(event => event[0] === 'write' && event[1] === '\tX').length, 0);
});
test('specimen table keeps the exact target bytes and expectations for the two rows', () => {
    assert.deepEqual(Object.keys(tabStopsSpecimens), tabStopsIds);
    assert.equal(tabStopsSpecimens['unicode.tab-stops'].fixture, '\t');
    assert.equal(tabStopsSpecimens['text.tab'].fixture, '\tX');
    assert.deepEqual(tabStopsSpecimens['text.tab'].expected.cursor, [0, 9]);
    assert.deepEqual(tabStopsSpecimens['unicode.tab-stops'].expected.cursor, [0, 8]);
});
