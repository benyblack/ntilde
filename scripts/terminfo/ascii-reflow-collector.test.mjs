import test from 'node:test';
import assert from 'node:assert/strict';
import { collectReflowGrading, reflowRecordingContext } from './ascii-reflow-collector.mjs';
import { asciiReflowAdapterId } from './ascii-reflow-grading-collector.mjs';
import { replayReflowPinned } from './ascii-reflow-grading.mjs';
import { asciiReflowCaseIds, asciiReflowFinalRestore, asciiReflowNoncePrefix, makeReflowCases } from './ascii-reflow-contracts.mjs';
import { initialReflowScreen, reflowWire } from './fixtures/reflow-journal.mjs';

const runId = 'a'.repeat(32), barrier = '\x1b[?1;2c';
const gradingModule = () => ({ ntildeReflowAdapter: { id: asciiReflowAdapterId },
    ntildeRunReflowTermless: (probeId, ctx) => { ctx.capabilities.reflow; return { probeId, ...replayReflowPinned() }; } });

function fixture({ failAt = null, fenceFatalAt = null, screens } = {}) {
    const calls = [], written = [], cleans = [];
    const cases = makeReflowCases(40, 140);
    const stageScreens = screens ?? [cases[0].before, cases[0].after, cases[0].restored, cases[1].before, cases[1].after, cases[1].restored];
    let applied = 0;
    const fence = {
        get failed() { return fenceFatalAt !== null && written.length > fenceFatalAt; },
        write: async bytes => { written.push(bytes); if (fenceFatalAt !== null && written.length > fenceFatalAt) throw new Error('fence died'); return barrier; },
        writeCleanup: async bytes => { cleans.push(bytes); },
    };
    const owner = async (kind, extra1, extra2) => {
        calls.push({ kind, extra1, extra2 });
        if (failAt === kind && (kind !== 'reflow-apply' || applied > 0)) throw new Error(kind + ' failed');
        if (kind === 'reflow-start') return { screen: initialReflowScreen(40, 140) };
        if (kind === 'reflow-read') { applied = applied; return { screen: reflowWire(stageScreens[calls.filter(c => c.kind === 'reflow-read').length - 1]) }; }
        return {};
    };
    const collect = () => collectReflowGrading({ config: { runId }, fence, owner, gradingModule: gradingModule() });
    return { calls, written, cleans, cases, collect };
}

test('the fixed two-experiment plan runs serially and records the run with its pinned replay', async () => {
    const f = fixture();
    const result = await f.collect();
    assert.equal(result.status, 'complete');
    assert.deepEqual(f.calls.map(call => call.kind), ['reflow-start', 'reflow-bind',
        'reflow-read', 'reflow-apply', 'reflow-read', 'reflow-apply', 'reflow-read',
        'reflow-read', 'reflow-apply', 'reflow-read', 'reflow-apply', 'reflow-read', 'reflow-finish']);
    assert.deepEqual(f.calls[3].extra2, { caseId: 'reflow.width-roundtrip', stageId: 'after' });
    assert.deepEqual(f.calls[8].extra2, { caseId: 'reflow.history-roundtrip', stageId: 'after' });
    assert.deepEqual(f.calls[10].extra2, { caseId: 'reflow.history-roundtrip', stageId: 'restored' });
    assert.deepEqual(f.written[0], '\x1b[?1049h\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[H' + asciiReflowNoncePrefix + runId);
    for (const feed of [f.cases[0].setup, f.cases[1].setup, asciiReflowFinalRestore]) assert.ok(f.written.includes(feed), 'Each planned feed is written once through the fence.');
    assert.equal(f.written.filter(bytes => bytes === '').length, 4, 'Each action read fences with an empty ordering write.');
    const { run } = result;
    assert.equal(run.schemaVersion, 1); assert.equal(run.suiteId, 'ntilde-owned-ascii-reflow-v1');
    assert.equal(run.runId, runId); assert.equal(run.adapterId, asciiReflowAdapterId);
    assert.deepEqual(run.probeIds, [...asciiReflowCaseIds]);
    assert.deepEqual(run.feeds, [f.cases[0].setup, f.cases[1].setup, asciiReflowFinalRestore]);
    assert.deepEqual(run.barriers, [barrier, barrier, barrier]);
    assert.deepEqual(run.stages.flatMap(stage => stage.stageId), ['control', 'after', 'restored', 'control', 'after', 'restored']);
    assert.equal(run.reads.length, 6);
    assert.deepEqual(run.pinned, { probeId: 'extensions.reflow', ...replayReflowPinned() });
    // No content is ever rewritten between a capture and its resize: the only feeds are the two seeds and the cleanup.
    assert.equal(run.feeds.length, 3);
});

test('a typed start refusal is returned without any fixture write', async () => {
    const f = fixture(); f.calls.length = 0;
    const failing = fixture();
    const unavailable = await collectReflowGrading({ config: { runId }, fence: { write: async () => { throw new Error('never'); } },
        owner: async kind => { if (kind === 'reflow-start') { const error = new Error('no'); error.code = 'screenDetailsUnavailable'; throw error; } return { screen: initialReflowScreen(40, 140) }; },
        gradingModule: gradingModule() });
    assert.deepEqual(unavailable, { status: 'unavailable', reason: 'screenDetailsUnavailable' });
    const geometry = await collectReflowGrading({ config: { runId }, fence: { write: async () => { throw new Error('never'); } },
        owner: async kind => { if (kind === 'reflow-start') { const error = new Error('no'); error.code = 'asciiReflowGeometryUnavailable'; throw error; } return { screen: initialReflowScreen(12, 140) }; },
        gradingModule: gradingModule() });
    assert.deepEqual(geometry, { status: 'unavailable', reason: 'asciiReflowGeometryUnavailable' });
});

test('an impure private main screen refuses to seed', async () => {
    const dirty = initialReflowScreen(40, 140); dirty.lines[3] = 'X';
    await assert.rejects(collectReflowGrading({ config: { runId }, fence: { write: async () => barrier },
        owner: async () => ({ screen: dirty }), gradingModule: gradingModule() }), /pristine private main screen/);
});

test('a mid-plan failure attempts only the bounded cleanup and preserves the primary error', async () => {
    const f = fixture({ failAt: 'reflow-read' });
    // The first three reads succeed (control reads and their followers); the run aborts when a later read fails.
    let reads = 0;
    const calls = [], written = [];
    const cases = makeReflowCases(40, 140);
    const stageScreens = [cases[0].before, cases[0].after, cases[0].restored, cases[1].before, cases[1].after, cases[1].restored];
    const fence = { get failed() { return false; }, write: async bytes => { written.push(bytes); return barrier; }, writeCleanup: async () => { throw new Error('never'); } };
    let bound = false, applied = 0;
    const owner = async (kind, extra1, extra2) => {
        calls.push(kind);
        if (kind === 'reflow-start') return { screen: initialReflowScreen(40, 140) };
        if (kind === 'reflow-bind') { bound = true; return {}; }
        if (kind === 'reflow-apply') { applied++; return {}; }
        if (kind === 'reflow-abort') return {};
        if (kind === 'reflow-finish') return {};
        reads++;
        if (reads === 4) throw new Error('history control read failed');
        return { screen: reflowWire(stageScreens[reads - 1]) };
    };
    await assert.rejects(collectReflowGrading({ config: { runId }, fence, owner, gradingModule: gradingModule() }),
        error => error.message === 'history control read failed');
    assert.deepEqual(calls.filter(kind => kind === 'reflow-abort'), ['reflow-abort']);
    assert.deepEqual(written.at(-1), asciiReflowFinalRestore);
});

test('a dead fence still writes its bounded cleanup once', async () => {
    const written = [], cleans = [];
    const fence = { get failed() { return true; }, write: async bytes => { written.push(bytes); return barrier; },
        writeCleanup: async bytes => { cleans.push(bytes); } };
    await assert.rejects(collectReflowGrading({ config: { runId }, fence,
        owner: async kind => {
            if (kind === 'reflow-start') return { screen: initialReflowScreen(40, 140) };
            if (kind === 'reflow-bind') throw new Error('bind failed');
            if (kind === 'reflow-abort') return {};
            throw new Error('unexpected ' + kind);
        }, gradingModule: gradingModule() }), /bind failed/);
    assert.deepEqual(cleans, [asciiReflowFinalRestore]);
    assert.ok(written.includes(asciiReflowFinalRestore) === false, 'A dead fence never takes an ordinary write.');
});

test('the pinned replay context records exactly the reflow capability read', async () => {
    const context = reflowRecordingContext();
    void context.capabilities.reflow; void context.capabilities.other;
    assert.deepEqual(context.readCapabilities, ['reflow', 'other']);
});
