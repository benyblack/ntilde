import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { routedPrefixSource } from './fixtures/private-clipboard-receipt.mjs';
import { includesCoreEraseEvidence, checkCoreEraseEvidence } from './core-erase-receipt.mjs';
import { extractOwnedArchive, loadMapping, root } from '../terminfo-core-results.mjs';
import { snapshotHash, OwnedCellObserver } from './agent-observer.mjs';
import { deriveEraseCoreReport } from '../terminfo-core-erase-results.mjs';
import { loadTextCursorMeasurement, textCursorPublication } from '../terminfo-core-text-cursor-results.mjs';
import { specimens, erasePlan, gradeErase, buildEraseReport, eraseSuiteId, eraseNonce, EraseObserver, collectErase } from './core-erase.mjs';

const dir = mkdtempSync(join(tmpdir(), 'nt-ce-'));
extractOwnedArchive(root, loadMapping(), dir);
const baseline = JSON.parse(readFileSync(join(dir, 'cg-snapshots.jsonl'), 'utf8').split('\n')[0]).screen;
rmSync(dir, { recursive: true, force: true });
function frame(lines, cursor) {
    const screen = structuredClone(baseline);
    screen.bufferState.alternateScreen = true;
    screen.horizontalMargins = { leftRightMarginMode: false, leftMargin: 0, rightMargin: screen.cols - 1 };
    screen.lines = Array(screen.rows).fill('');
    lines.forEach((line, i) => { screen.lines[i] = line; });
    [screen.cursorRow, screen.cursorCol] = cursor;
    return { screen };
}
function good() {
    return Object.fromEntries(specimens.flatMap(s => [[s.role + ':before', frame(s.before, s.cursor)], [s.role + ':after', frame(s.after, s.cursor)]]));
}
function journal() {
    const id = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111', barrier = '\x1b[?1;2c';
    const blank = structuredClone(baseline); blank.horizontalMargins = { leftRightMarginMode: false, leftMargin: 0, rightMargin: blank.cols - 1 };
    const bind = structuredClone(blank); bind.bufferState.alternateScreen = true; bind.lines[0] = eraseNonce + id; bind.cursorCol = bind.lines[0].length;
    const make = (screen, kind, index, cpr) => ({ index, kind, paneId, barrier: index ? barrier : null, screen, sha256: snapshotHash(screen), ...(cpr && { cpr }) });
    const frames = [make(blank, 'ce-start', 0), make(bind, 'ce-bind', 1)], roles = good(), reads = [];
    const plan = erasePlan();
    for (const step of plan.filter(x => x.read)) {
        const screen = roles[step.role].screen, row = screen.cursorRow + 1, col = screen.cursorCol + 1;
        const cpr = { barrier, reply: { raw: `\x1b[${row};${col}R`, row, col } };
        reads.push({ role: step.role, cpr }); frames.push(make(screen, 'ce-read', frames.length, cpr));
    }
    frames.push(make(structuredClone(blank), 'ce-finish', frames.length));
    const feeds = plan.filter(x => x.feed).map(x => x.feed);
    return { id, frames, run: { suiteId: eraseSuiteId, runId: id, feeds, barriers: feeds.map(() => barrier), reads } };
}
test('exact omitted bytes, separate addresses, and interior controls are in the plan', () => {
    const feeds = erasePlan().filter(x => x.feed).map(x => x.feed);
    assert.ok(feeds.some(x => x.endsWith('XXXXX\x1b[1G')));
    assert.equal(feeds.filter(x => x === '\x1b[K').length, 2);
    assert.equal(feeds.filter(x => x === '\x1b[J').length, 2);
    assert.ok(feeds.some(x => x.endsWith('\x1b[3;2H')));
    assert.ok(feeds.some(x => x.endsWith('\x1b[2;3H')));
    assert.equal(feeds.filter(x => x === '\x1b[1J').length, 2);
});
test('independent expected cells and cursor distinguish ignored, overbroad, shifted and moving erases', () => {
    assert.ok(Object.values(gradeErase(good())).every(x => x.outcome === 'supported'));
    for (const s of specimens) {
        for (const mutate of [f => { f.screen.lines[0] = 'wrong'; }, f => { f.screen.cursorCol++; }, f => { f.screen.lines[8] = 'unexpected'; }]) {
            const roles = good(); mutate(roles[s.role + ':after']);
            assert.equal(gradeErase(roles)[s.role].outcome, 'unsupported');
        }
        const invalid = good(); invalid[s.role + ':before'].screen.lines[0] = '';
        assert.throws(() => gradeErase(invalid), /invalid seed/);
    }
    const wholeLine = good(); wholeLine['el-interior:after'].screen.lines[0] = '';
    assert.equal(gradeErase(wholeLine)['el-interior'].outcome, 'unsupported');
    const wholeScreen = good(); wholeScreen['ed0-interior:after'].screen.lines[0] = '';
    assert.equal(gradeErase(wholeScreen)['ed0-interior'].outcome, 'unsupported');
    const exclusive = good(); exclusive['ed1-catalogue:after'].screen.lines[1] = '  BBB';
    assert.equal(gradeErase(exclusive)['ed1-catalogue'].outcome, 'unsupported');
});
test('journal rejects altered bytes, stale identity, missing frames and incomplete restoration', () => {
    const { id, frames, run } = journal();
    assert.equal(Object.keys(buildEraseReport(frames, run, id).effects).length, 6);
    const forged = structuredClone(run); forged.feeds[1] = '\x1b[0K';
    assert.throws(() => buildEraseReport(frames, forged, id), /fixture bytes/);
    for (const [from, to] of [['\x1b[3;2H', '\x1b[2;3H'], ['\x1b[2;3H', '\x1b[3;2H'], ['\x1b[J', '\x1b[0J']]) {
        const changed = structuredClone(run), index = changed.feeds.findIndex(feed => feed.includes(from));
        assert.ok(index >= 0); changed.feeds[index] = changed.feeds[index].replace(from, to);
        assert.throws(() => buildEraseReport(frames, changed, id), /fixture bytes/);
    }
    assert.throws(() => buildEraseReport(frames, run, 'b'.repeat(32)), /identity/);
    assert.throws(() => buildEraseReport(frames.slice(0, -1), run, id), /Incomplete/);
    const stale = structuredClone(frames); stale[3].screen.cursorCol++;
    assert.throws(() => buildEraseReport(stale, run, id), /snapshot/);
    const unrestored = structuredClone(frames); unrestored.at(-1).screen.cursorCol++; unrestored.at(-1).sha256 = snapshotHash(unrestored.at(-1).screen);
    assert.throws(() => buildEraseReport(unrestored, run, id), /restored/);
});

test('receipt rejects stripped or coherently rehashed altered fixture evidence', () => {
    const dir = mkdtempSync(join(tmpdir(), 'nt-ce-receipt-'));
    try {
        const { id, frames, run } = journal(), hash = data => createHash('sha256').update(data).digest('hex');
        const runBytes = Buffer.from(JSON.stringify(run)), result = Buffer.from(JSON.stringify(buildEraseReport(frames, run, id)));
        writeFileSync(join(dir, 'ce-run.json'), runBytes); writeFileSync(join(dir, 'ce-observations.json'), result);
        writeFileSync(join(dir, 'ce-snapshots.jsonl'), frames.map(f => JSON.stringify(f)).join('\n') + '\n');
        const context = { coreEraseSuiteId: eraseSuiteId }, evidence = { textCursorEvidence: {}, osc133Evidence: {}, coreEraseEvidence: {
            runId: id, suiteId: eraseSuiteId, file: 'ce-observations.json', runFile: 'ce-run.json', sha256: hash(result),
            runSha256: hash(runBytes), provisionalUntil: 'cell-receipt.json' } };
        assert.ok(includesCoreEraseEvidence(dir, evidence, context));
        assert.doesNotThrow(() => checkCoreEraseEvidence(dir, evidence, id, context));
        const forged = structuredClone(run); forged.feeds[1] = '\x1b[0K';
        const bytes = Buffer.from(JSON.stringify(forged)); writeFileSync(join(dir, 'ce-run.json'), bytes); evidence.coreEraseEvidence.runSha256 = hash(bytes);
        assert.throws(() => checkCoreEraseEvidence(dir, evidence, id, context), /fixture bytes/);
        writeFileSync(join(dir, 'ce-run.json'), runBytes); evidence.coreEraseEvidence.runSha256 = hash(runBytes);
        writeFileSync(join(dir, 'ce-snapshots.jsonl'), '');
        assert.throws(() => checkCoreEraseEvidence(dir, evidence, id, context));
    } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('80-row derivation requires both exact specimens for each targeted verdict', () => {
    const temp = mkdtempSync(join(tmpdir(), 'nt-ce-rows-'));
    try {
        const m = loadTextCursorMeasurement(), mapping = loadMapping(), archive = join(root, textCursorPublication, 'owned-run.zip');
        extractOwnedArchive(root, { archives: { 'owned-run.zip': m.ownedArchiveSha256 } }, temp, archive);
        const { frames, run, id } = journal(), supplement = buildEraseReport(frames, run, id);
        supplement.runId = m.measurement.runId;
        const path = join(temp, 'ce-observations.json'); writeFileSync(path, JSON.stringify(supplement));
        const identity = { context: { runId: m.measurement.runId }, receipt: { coreEraseEvidence: { suiteId: eraseSuiteId } } };
        let result = deriveEraseCoreReport(temp, mapping, identity, m.measurement).report;
        assert.equal(result.rows.length, 80); assert.equal(new Set(result.rows.map(x => x.id)).size, 80);
        assert.deepEqual(result.totals, { pass: 71, fail: 0, inconclusive: 9 });
        supplement.effects['ed0-interior'].outcome = 'unsupported'; writeFileSync(path, JSON.stringify(supplement));
        result = deriveEraseCoreReport(temp, mapping, identity, m.measurement).report;
        assert.equal(result.rows.find(x => x.id === 'erase.screen.below').conclusion, 'fail');
        delete supplement.effects['ed1-site']; writeFileSync(path, JSON.stringify(supplement));
        assert.throws(() => deriveEraseCoreReport(temp, mapping, identity, m.measurement), /missing, duplicate/);
    } finally { rmSync(temp, { recursive: true, force: true }); }
});

test('owned worker and runner parse with the new suite imports', () => {
    for (const file of ['scripts/terminfo/worker.mjs', 'scripts/terminfo-owned-runner.mjs']) {
        const result = spawnSync(process.execPath, ['--check', join(root, file)], { encoding: 'utf8' });
        assert.equal(result.status, 0, result.stderr);
    }
});

test('historical synthetic source projection excludes newer erase imports on both line endings', () => {
    for (const newline of ['\n', '\r\n']) {
        const text = ["import { collectErase } from './terminfo/core-erase.mjs';", "import { checkCoreEraseEvidence } from './core-erase-receipt.mjs';", "import { x } from './older.mjs';", ''].join(newline);
        assert.equal(routedPrefixSource(Buffer.from(text)).toString(), "import { x } from './older.mjs';\n");
    }
});

test('observer binds each cursor reply to the completed read and rejects a wrong barrier', async () => {
    const original = OwnedCellObserver.prototype.handle, events = [], observer = Object.create(EraseObserver.prototype);
    observer.frames = [];
    OwnedCellObserver.prototype.handle = async function (kind, budget, barrier) { events.push([kind, budget, barrier]); this.frames.push({}); return 'ok'; };
    try {
        const barrier = '\x1b[?1;2c', cpr = { barrier, reply: { raw: '\x1b[1;1R', row: 1, col: 1 } };
        assert.equal(await observer.handleRequest({ kind: 'ce-read', barrier, cpr }, 5), 'ok');
        assert.deepEqual(events, [['ce-read', 5, barrier]]); assert.deepEqual(observer.frames[0].cpr, cpr);
        await assert.rejects(observer.handleRequest({ kind: 'ce-read', barrier: 'wrong', cpr }, 5), /barrier mismatch/);
        await assert.rejects(observer.handleRequest({ kind: 'ce-start', barrier, cpr }, 5), /unexpected CPR/);
    } finally { OwnedCellObserver.prototype.handle = original; }
});

test('collector waits for each write and read and restores after a failed observation', async () => {
    const events = [], barrier = '\x1b[?1;2c';
    const fence = { write: async bytes => { events.push(['write', bytes]); return barrier; },
        cursorReport: async () => { events.push(['cpr']); return { barrier, reply: { raw: '\x1b[1;1R', row: 1, col: 1 } }; } };
    let reads = 0;
    const owner = async kind => { events.push([kind]); if (kind === 'ce-read' && ++reads === 2) throw new Error('refused'); return { screen: baseline }; };
    await assert.rejects(collectErase({ config: { runId: 'a'.repeat(32) }, fence, owner }), /refused/);
    const erase = events.findIndex(e => e[0] === 'write' && e[1] === '\x1b[K');
    assert.ok(erase > 0); assert.deepEqual(events[erase + 1], ['cpr']);
    assert.equal(events.at(-1)[0], 'ce-finish');
    assert.equal(events.filter(e => e[0] === 'write' && e[1] === '\x1b[1J').length, 0);
});
