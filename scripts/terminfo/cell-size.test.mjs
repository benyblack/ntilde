import test from 'node:test';
import assert from 'node:assert/strict';
import { buildCellSizeReport } from './cell-size-grading.mjs';
import { parseCellSizeFrame, validateCellSizeExchange } from './cell-size-frame.mjs';
import { cellSizeContracts, cellSizeGeometry, cellSizeIds } from './cell-size-contracts.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticCellSizeEvidence } from './fixtures/cell-size-evidence.mjs';
import { collectCellSize } from './cell-size-collector.mjs';
import { CellSizeObserver } from './cell-size-capture.mjs';

const build = e => buildCellSizeReport(e.frames, e.run, e.runId, e.adapter);
const da = '\x1b[?62;4;22c';

test('literal synthetic replies qualify both exact effects while preserving two frozen inconclusive verdicts', () => {
    const result = build(syntheticCellSizeEvidence());
    assert.equal(result.snapshots.length, 22);
    assert.deepEqual(result.cases.map(item => [item.caseId, item.conclusion.outcome, item.effectContract.outcome]),
        cellSizeIds.map(id => [id, 'inconclusive', 'supported']));
    assert.equal(result.cases[0].pinned.terminal.observation.reason, 'invalid-reply');
    assert.equal(result.cases[1].pinned.terminal.observation.reason, 'no-response');
});

test('logical point dimensions include interface zoom while the scale field remains the display backing scale', () => {
    const fixture = wrong => {
        const e = syntheticCellSizeEvidence((_id, item) => {
            if (item.protocol === 'csi') return ['\x1b[6;54;24t'];
            if (item.protocol === 'iterm') return [wrong ? '\x1b]1337;ReportCellSize=18;8;3\x1b\\' : '\x1b]1337;ReportCellSize=27;12;2\x1b\\'];
            if (item.protocol === 'urxvt') return ['\x1b]776;24;54;36' + item.terminator];
            return [];
        });
        for (const frame of e.frames) Object.assign(frame.geometry, { cellWidthDip: 8, cellHeightDip: 18, fontAscentDip: 12, renderScaling: 3, backingScale: 2 });
        return e;
    };
    assert.ok(build(fixture(false)).cases.every(item => item.effectContract.outcome === 'supported'));
    const incorrect = build(fixture(true));
    assert.equal(incorrect.cases[0].effectContract.outcome, 'unsupported');
    assert.equal(incorrect.cases[1].effectContract.outcome, 'supported');
});

for (const [name, replies] of [
    ['missing', []], ['swapped dimensions', ['\x1b]1337;ReportCellSize=8.5;17.5;2\x1b\\']],
    ['physical pixels passed as logical points', ['\x1b]1337;ReportCellSize=35;17;2\x1b\\']],
    ['wrong scale', ['\x1b]1337;ReportCellSize=17.5;8.5;1\x1b\\']],
    ['duplicate', Array(2).fill('\x1b]1337;ReportCellSize=17.5;8.5;2\x1b\\')],
]) test('valid controls make a ' + name + ' target reply unsupported', () => {
    const e = syntheticCellSizeEvidence((id, item) => id === cellSizeIds[0] && item.id === 'documented-bel' ? replies : null);
    assert.equal(build(e).cases[0].effectContract.outcome, 'unsupported');
});

test('a failed delivery control is inconclusive, and an answer to an unsupported form fails the effect', () => {
    const control = syntheticCellSizeEvidence((_id, item) => item.id === 'positive-before' ? [] : null);
    assert.ok(build(control).cases.every(item => item.effectContract.outcome === 'inconclusive'));
    const guessed = syntheticCellSizeEvidence((_id, item) => item.id === 'unsupported-form-0' ? ['\x1b]776;17;35;25\x07'] : null);
    assert.ok(build(guessed).cases.every(item => item.effectContract.outcome === 'unsupported'));
});

for (const [name, change] of [
    ['truncated journal', e => e.frames.pop()],
    ['different run', e => { e.runId = 'c'.repeat(32); }],
    ['different pane', e => { e.frames[2].paneId = 'another'; }],
    ['stale barrier', e => { e.frames[2].barrier = '\x1b[?1c'; }],
    ['unpaired reply', e => { e.frames[2].report.replies = []; }],
    ['changed request', e => { e.run.cases[0].exchanges[0].request = '\x1b[14t'; }],
    ['reordered cases', e => { e.run.cases.reverse(); }],
    ['geometry drift', e => { e.frames[3].geometry.renderScaling = 1; }],
    ['geometry from another viewport', e => { e.frames[3].geometry.viewCols++; }],
    ['missing geometry', e => { delete e.frames[0].geometry; }],
    ['changed state', e => { e.frames[3].screen.cursorCol++; e.frames[3].sha256 = snapshotHash(e.frames[3].screen); }],
    ['unrestored state', e => { e.frames.at(-1).screen.cursorCol++; e.frames.at(-1).sha256 = snapshotHash(e.frames.at(-1).screen); }],
    ['forged pinned support', e => { e.run.cases[0].pinned.terminal.pass = true; }],
    ['missing read', e => { e.run.cases[0].exchanges.pop(); }],
]) test('refuses ' + name, () => {
    const evidence = syntheticCellSizeEvidence(); change(evidence);
    assert.throws(() => build(evidence));
});

test('frame parsing preserves exact BEL/ST replies and requires a complete ordered barrier at every input split', () => {
    for (const reply of ['', '\x1b]776;17;35;25\x07', '\x1b]1337;ReportCellSize=17.5;8.5;2\x1b\\', '\x1b[6;35;17t']) {
        const bytes = reply + da;
        for (let split = 0; split < bytes.length; split++) assert.equal(parseCellSizeFrame(bytes.slice(0, split)), null, JSON.stringify(bytes.slice(0, split)));
        assert.deepEqual(parseCellSizeFrame(bytes), { replies: reply ? [reply] : [], barrier: da });
    }
    for (const wrong of ['text' + da, da + 'late', '\x1b]776;17;35;25\x07late' + da, 'x'.repeat(513)]) assert.throws(() => parseCellSizeFrame(wrong));
    assert.throws(() => validateCellSizeExchange({ replies: [], barrier: '\x1b[?' }));
    assert.throws(() => cellSizeGeometry({ viewRows: 40, viewCols: 140, cellWidthDip: 8, cellHeightDip: 18, fontAscentDip: 19, renderScaling: 2, backingScale: 2 }, 40, 140));
});

test('the worker driver executes every exact request and restores after both success and a primary failure', async () => {
    const evidence = syntheticCellSizeEvidence(), calls = [], requests = [];
    let caseIndex = 0, exchangeIndex = 0;
    const owner = async kind => { calls.push(kind); return {}; };
    const fence = { write: async () => da, exchange: async request => {
        const contract = evidence.run.cases[caseIndex], exchange = contract.exchanges[exchangeIndex++];
        assert.equal(request, exchange.request); requests.push(request);
        if (exchangeIndex === contract.exchanges.length) { caseIndex++; exchangeIndex = 0; }
        return structuredClone(exchange.report);
    } };
    const module = {
        ntildeRunCellSizeTermless: id => evidence.run.cases.find(item => item.caseId === id).pinned.termless,
        ntildeRunCellSizeTerminal: async id => evidence.run.cases.find(item => item.caseId === id).pinned.terminal,
    };
    const run = await collectCellSize({ config: { runId: evidence.runId }, fence, owner, module });
    assert.deepEqual(run, evidence.run);
    assert.deepEqual(requests, cellSizeContracts.flatMap(contract => contract.exchanges.map(item => item.request)));
    assert.equal(calls.at(-1), 'csz-finish');
    const broken = { ...fence, exchange: async () => { throw new Error('primary'); } };
    await assert.rejects(collectCellSize({ config: { runId: evidence.runId }, fence: broken, owner, module }), /primary/);
    assert.equal(calls.at(-1), 'csz-finish');
    await assert.rejects(collectCellSize({ config: { runId: evidence.runId }, fence: broken,
        owner: async kind => { if (kind === 'csz-finish') throw new Error('cleanup'); }, module }), AggregateError);
});

test('the parent observer journals independent view geometry and refuses stale pane metrics or a mismatched exchange barrier', async () => {
    const e = syntheticCellSizeEvidence();
    const observer = new CellSizeObserver({ appData: '/synthetic', ownerPid: 42, endpoint: () => 'synthetic',
        rpc: async (_endpoint, method, params) => {
            assert.equal(method, 'readPaneMetadata'); assert.equal(params.paneId, e.frames[0].paneId);
            return { paneId: e.frames[0].paneId, cellGeometry: e.frames[0].geometry };
        } });
    let next = 0;
    observer.cells = { restored: false, frames: [], handle: async () => {
        const frame = structuredClone(e.frames[next++]); observer.cells.frames.push(frame);
        return { paneId: frame.paneId, screen: frame.screen };
    } };
    await observer.handle({ kind: 'csz-start' }, () => 1000);
    assert.deepEqual(observer.frames[0].geometry, e.frames[0].geometry);
    await assert.rejects(observer.handle({ kind: 'csz-read', barrier: '\x1b[?1c', report: e.frames[2].report }, () => 1000), /Unbound/);
    observer.options.rpc = async () => ({ paneId: 'stale' });
    await assert.rejects(observer.handle({ kind: 'csz-bind', barrier: da }, () => 1000), /identity/);
});
