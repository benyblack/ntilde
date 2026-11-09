import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { test } from 'node:test';
import { collectCursorQualGrading, cursorQualAppBackend, cursorQualGroupOffsets, cursorQualRepairRecord, cursorQualTermlessContext, requirePristineCursorQualStart, runCursorQualPinned } from './cursor-qual-collector.mjs';
import { makeCursorQualContracts, cursorQualNoncePrefix, cursorQualReset } from './cursor-qual-contracts.mjs';
import { SimTerminal, correctBehavior, cursorRows, cursorCols, encodePng, syntheticCursorEvidence } from './fixtures/cursor-qual-evidence.mjs';
import { syntheticCursorModule } from './fixtures/cursor-qual-module.mjs';

const contracts = makeCursorQualContracts(cursorRows, cursorCols);
const byId = Object.fromEntries(contracts.map(contract => [contract.caseId, contract]));
const group = (caseId, id) => byId[caseId].groups.find(entry => entry.id === id);
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const evidence = syntheticCursorEvidence();
/** The synthetic observed screens and run-log reads of one case, group by group. */
const observed = caseId => {
    const index = contracts.findIndex(contract => contract.caseId === caseId);
    let first = 2;
    for (const contract of contracts.slice(0, index)) first += contract.groups.reduce((sum, entry) => sum + entry.reads.length, 0);
    const count = contracts[index].groups.reduce((sum, entry) => sum + entry.reads.length, 0);
    return { screens: evidence.frames.slice(first, first + count).map(frame => frame.screen), reads: evidence.run.cases[index].reads };
};

test('a pristine private screen is required before any fixture byte is written', () => {
    const sim = new SimTerminal(); sim.feed(cursorQualReset);
    assert.doesNotThrow(() => requirePristineCursorQualStart(sim.screen()));
    for (const edit of [s => s.feed('x'), s => s.feed('\x1b[5;10H'), s => s.feed('\x1b[?25l'), s => s.feed('\x1b[5 q'), s => s.feed('\x1b[5;10r'), s => s.feed('\x1b[?6h'), s => s.feed('\x1b[?1049h')]) {
        const dirty = new SimTerminal(); edit(dirty);
        assert.throws(() => requirePristineCursorQualStart(dirty.screen()), /pristine private main screen/);
    }
});

test('the termless context serves a state only at a feed count where the contract journaled one, and checks the pinned bytes', () => {
    const contract = byId['cursor.hide'], termless = group('cursor.hide', 'termless');
    const { screens } = observed('cursor.hide');
    const context = cursorQualTermlessContext(contract, termless, screens.slice(0, termless.reads.length));
    assert.equal(context.getCursor().visible, true);
    context.feed('\x1b[?25h'); assert.equal(context.getCursor().visible, true);
    context.feed('\x1b[?25l'); assert.equal(context.getCursor().visible, false);
    context.feed('\x1b[?25h'); assert.equal(context.getCursor().visible, true);
    assert.equal(context.complete, true);
    assert.throws(() => context.feed('\x1b[?25l'), /feed 3 differs from the contract/);
    const wrap = cursorQualTermlessContext(byId['cursor.reverse-wrap'], group('cursor.reverse-wrap', 'termless'), observed('cursor.reverse-wrap').screens.slice(0, 5));
    assert.equal(wrap.getMode('autoWrap'), true);
    assert.deepEqual(wrap.getScrollback(), { screenLines: cursorRows, totalLines: cursorRows, viewportY: 0 });
    wrap.feed('\x1b[?7h');
    assert.throws(() => wrap.getCursor(), /read at feed 1 without an observed state/);
    assert.throws(() => wrap.getText(), /must not read text/);
    assert.throws(() => wrap.getMode('altScreen'), /unmodeled mode/);
});

test('an early-returning pinned probe still runs its finally cleanup: only the declared tail may follow', () => {
    const contract = byId['cursor.hide'], termless = group('cursor.hide', 'termless');
    const context = cursorQualTermlessContext(contract, termless, observed('cursor.hide').screens.slice(0, 4));
    context.feed('\x1b[?25h');
    // shown !== true: the probe skips the hide and goes straight to its restore feed.
    context.feed('\x1b[?25h');
    assert.equal(context.complete, true);
    assert.equal(context.fedCount, 3);
    assert.equal(context.getCursor().visible, true, 'the restoration read is served after the tail');
    const refused = cursorQualTermlessContext(contract, termless, observed('cursor.hide').screens.slice(0, 4));
    assert.throws(() => refused.feed('\x1b[?25l'), /feed 0 differs from the contract/, 'a hide is not a legal first feed');
    const cup = group('cursor.cup-scroll-region', 'termless');
    const partial = cursorQualTermlessContext(byId['cursor.cup-scroll-region'], cup, observed('cursor.cup-scroll-region').screens.slice(0, 2));
    partial.feed('\x1b[5;15r');
    assert.equal(partial.complete, false, 'stopping halfway without the cleanup is not a complete replay');
});

test('the app backend serves CPR replies in order, refuses extra queries and checks capture labels against the contract', () => {
    const contract = byId['cursor.cup-scroll-region'], app = group('cursor.cup-scroll-region', 'app');
    const { offset } = cursorQualGroupOffsets(contract)[1];
    const data = observed('cursor.cup-scroll-region');
    const backend = cursorQualAppBackend(contract, app, data.screens.slice(offset, offset + app.reads.length), data.reads.slice(offset, offset + app.reads.length));
    for (const feed of app.pinnedFeeds.slice(0, 3)) backend.write(feed);
    assert.deepEqual(backend.queryCursorPosition(), { row: 1, col: 1 });
    backend.write(app.pinnedFeeds[3]);
    assert.deepEqual(backend.queryCursorPosition(), { row: 2, col: 1 });
    backend.write(app.pinnedFeeds[4]);
    assert.deepEqual(backend.queryCursorPosition(), { row: 11, col: 1 });
    assert.throws(() => backend.queryCursorPosition(), /without a retained report/);
    assert.ok(backend.failure instanceof Error);
    const hide = byId['cursor.hide'], hideApp = group('cursor.hide', 'app'), hideData = observed('cursor.hide');
    const hideOffset = cursorQualGroupOffsets(hide)[1].offset;
    const captures = cursorQualAppBackend(hide, hideApp, hideData.screens.slice(hideOffset, hideOffset + 5), hideData.reads.slice(hideOffset, hideOffset + 5));
    assert.throws(() => captures.capture({ role: 'control', label: 'something else' }), /capture 0 differs from the contract/);
    const digest = hideData.reads[hideOffset].pixelSha256;
    const fresh = cursorQualAppBackend(hide, hideApp, hideData.screens.slice(hideOffset, hideOffset + 5), hideData.reads.slice(hideOffset, hideOffset + 5));
    assert.deepEqual(fresh.capture({ role: 'control', label: 'Shown steady block cursor requested at row 2 column 3' }), { role: 'control', label: 'Shown steady block cursor requested at row 2 column 3', ref: `sha256:${digest}` });
    const bad = structuredClone(hideData.reads.slice(hideOffset, hideOffset + 5)); bad[0].pixelSha256 = 'xyz';
    const unsigned = cursorQualAppBackend(hide, hideApp, hideData.screens.slice(hideOffset, hideOffset + 5), bad);
    assert.throws(() => unsigned.capture({ role: 'control', label: 'Shown steady block cursor requested at row 2 column 3' }), /capture 0 differs/);
});

test('the pinned callbacks run over the observed evidence of every case and the original failures are retained', async () => {
    for (const contract of contracts) {
        const data = observed(contract.caseId);
        const result = await runCursorQualPinned(contract, data.screens, data.reads, syntheticCursorModule);
        assert.equal(result.termless.observation.evidence, 'parser-state');
        assert.equal(result.app.repaired, ['cursor.reverse-wrap', 'cursor.shape'].includes(contract.caseId));
        assert.deepEqual(result.app.geometry, { rows: cursorRows, cols: cursorCols });
        assert.deepEqual(result.app.writes, group(contract.caseId, contract.groups.find(entry => entry.kind.startsWith('pinned-app')).id).pinnedFeeds);
    }
    const wrap = observed('cursor.reverse-wrap');
    const result = await runCursorQualPinned(byId['cursor.reverse-wrap'], wrap.screens, wrap.reads, syntheticCursorModule);
    assert.equal(result.original.threw.name, 'UndeclaredTerminalGeometry');
    assert.deepEqual(result.original.writes, []);
    assert.equal(result.repair.repairedTermNeedsGeometry, true);
    assert.equal(cursorQualRepairRecord(syntheticCursorModule, 'cursor.shape').probeId, 'cursor.shape');
});

test('a pinned callback that sends bytes the contract did not plan, or never finishes its fixture, is refused', async () => {
    const data = observed('cursor.hide'), contract = byId['cursor.hide'];
    const drifting = { ...syntheticCursorModule, async ntildeRunCursorApp(probeId, backend, options) { backend.write('\x1b[?25l'); return syntheticCursorModule.ntildeRunCursorApp(probeId, backend, options); } };
    await assert.rejects(runCursorQualPinned(contract, data.screens, data.reads, drifting), /feed 0 differs from the contract/);
    const idle = { ...syntheticCursorModule, ntildeRunCursorTermless: probeId => ({ probeId, pass: false, observation: { outcome: 'inconclusive', evidence: 'parser-state' } }) };
    await assert.doesNotReject(runCursorQualPinned(contract, data.screens, data.reads, idle), 'a probe that refuses before feeding anything is a complete (empty) replay');
    const failing = { ...syntheticCursorModule, async ntildeRunCursorApp(probeId) { return { probeId, threw: { name: 'Error', message: 'boom' }, writes: [] }; } };
    await assert.rejects(runCursorQualPinned(contract, data.screens, data.reads, failing), /app callback failed: Error: boom/);
});

/** A worker-side harness: a fake fence and owner that drive one simulated terminal exactly as the real parent observer would. */
function harness(behavior = correctBehavior, options = {}) {
    const sim = new SimTerminal(behavior), journal = [];
    const barrier = '\x1b[?62;4;22c';
    const fence = {
        async write(bytes) { sim.feed(bytes); return barrier; },
        async cursorReport() { const [row, col] = sim.report(); return { barrier, reply: { raw: `\x1b[${row};${col}R`, row, col } }; },
    };
    const owner = async (kind, at, extra = {}) => {
        journal.push({ kind, barrier: at, extra });
        if (options.failOn === kind + '#' + journal.filter(entry => entry.kind === kind).length) throw new Error('observer refused');
        if (kind === 'curq-bind') sim.feed(''); // the nonce was written with the bind
        const result = { screen: sim.screen(), paneId: 'pane' };
        if (extra.capture) result.pixel = { sha256: sha(encodePng(sim.render(extra.capture.timeMs))) };
        return result;
    };
    return { sim, fence, owner, journal };
}

test('the worker driver replays every contracted plan through the fence and journals CPR and capture reads', async () => {
    const { sim, fence, owner, journal } = harness();
    sim.feed('\x1b[0 q');
    const config = { runId: 'a'.repeat(32) };
    const run = await collectCursorQualGrading({ config, fence, owner, gradingModule: syntheticCursorModule });
    assert.equal(run.adapterId, syntheticCursorModule.ntildeCursorAdapter.id);
    assert.deepEqual(run.cases.map(entry => entry.caseId), contracts.map(contract => contract.caseId));
    for (const [index, entry] of run.cases.entries()) {
        assert.deepEqual(entry.feeds, contracts[index].plan.filter(step => step.feed).map(step => step.feed));
        assert.equal(entry.reads.length, contracts[index].plan.filter(step => step.read).length);
        assert.deepEqual(entry.reads.map(read => read.kind), contracts[index].plan.filter(step => step.read).map(step => step.read));
    }
    const cprReads = run.cases[0].reads.filter(read => read.kind === 'cpr');
    assert.deepEqual(cprReads.slice(0, 3).map(read => read.cpr.reply.raw), ['\x1b[1;1R', '\x1b[2;1R', '\x1b[11;1R']);
    const captureReads = run.cases[1].reads.filter(read => read.kind === 'capture');
    assert.equal(captureReads.length, 3);
    assert.ok(captureReads.every(read => /^[a-f\d]{64}$/.test(read.pixelSha256) && read.timeMs === 0));
    assert.deepEqual(run.cases[2].reads.filter(read => read.kind === 'capture').map(read => read.timeMs), [0, 0, 530, 0, 0, 0, 0]);
    assert.equal(journal[0].kind, 'curq-start'); assert.equal(journal[1].kind, 'curq-bind'); assert.equal(journal.at(-1).kind, 'curq-finish');
    assert.equal(journal[0].barrier, undefined);
    assert.ok(journal.filter(entry => entry.extra.cpr).every(entry => entry.barrier === entry.extra.cpr.barrier), 'a CPR read is ordered by its own DA barrier');
    assert.equal(run.cases[3].pinned.original.threw.name, 'UndeclaredTerminalGeometry');
    assert.equal(cursorQualNoncePrefix + config.runId, `NTILDE_CQ_${config.runId}`);
});

test('a refusal mid-collection still restores the fixture and reports both failures', async () => {
    const { fence, owner, journal } = harness(correctBehavior, { failOn: 'curq-read#7' });
    await assert.rejects(collectCursorQualGrading({ config: { runId: 'a'.repeat(32) }, fence, owner, gradingModule: syntheticCursorModule }), /observer refused/);
    assert.equal(journal.at(-1).kind, 'curq-finish', 'restoration is always attempted');
    const both = harness(correctBehavior, { failOn: 'curq-read#7' });
    const originalOwner = both.owner;
    both.owner = (kind, ...rest) => (kind === 'curq-finish' ? Promise.reject(new Error('restore refused')) : originalOwner(kind, ...rest));
    await assert.rejects(collectCursorQualGrading({ config: { runId: 'a'.repeat(32) }, fence: both.fence, owner: both.owner, gradingModule: syntheticCursorModule }),
        error => error instanceof AggregateError && /collection and restoration failed/.test(error.message) && error.errors.length === 2);
});

test('collection refuses a dirty start before writing a byte', async () => {
    const { sim, fence, owner, journal } = harness();
    sim.feed('dirty');
    await assert.rejects(collectCursorQualGrading({ config: { runId: 'a'.repeat(32) }, fence, owner, gradingModule: syntheticCursorModule }), /pristine private main screen/);
    assert.deepEqual(journal.map(entry => entry.kind), ['curq-start', 'curq-finish']);
});
