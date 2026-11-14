import test from 'node:test';
import assert from 'node:assert/strict';
import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { buildReflowReport, reflowCounts, replayReflowPinned, asciiReflowScope } from './ascii-reflow-grading.mjs';
import { syntheticReflowJournal, reflowWire } from './fixtures/reflow-journal.mjs';

const journal = (runId = 'a'.repeat(32)) => syntheticReflowJournal(runId);
const report = () => { const e = journal(); return buildReflowReport(e.frames, e.actions, e.run, e.runId, e.adapter); };
/** Apply a content mutation to one frame's screen while keeping the journal internally consistent. */
function rehash(e, index, mutate) {
    mutate(e.frames[index].screen, e);
    const screen = screenSnapshot(e.frames[index].screen, false, true, true, true, true);
    const sha = snapshotHash(screen);
    e.frames[index].afterScreen = structuredClone(e.frames[index].screen);
    e.frames[index].sha256 = sha; e.frames[index].afterSha256 = sha;
}

test('the synthetic journal rederives a supported report with the pinned verdict preserved', () => {
    const built = report();
    assert.equal(built.suiteId, 'ntilde-owned-ascii-reflow-v1');
    assert.equal(built.cases.length, 1);
    const item = built.cases[0];
    assert.equal(item.caseId, 'extensions.reflow');
    assert.equal(item.pinned.observation.outcome, 'inconclusive');
    assert.equal(item.pinned.observation.evidence, 'legacy');
    assert.equal(item.pinned.response, '{"declaredReflow":false}');
    assert.equal(item.contractResult.outcome, 'supported');
    assert.equal(item.effectContract.id, 'ntilde-ascii-reflow-effect-v1');
    assert.ok(item.effectContract.attribution.includes('not the pinned probe'));
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.deepEqual(reflowCounts(built), { supported: 1 });
    assert.equal(built.snapshots.length, 9); assert.equal(built.actions.length, 4);
    assert.ok(asciiReflowScope.includes('no Unicode') && asciiReflowScope.includes('not stock') === false);
    assert.match(asciiReflowScope, /no stock grades/);
});

test('the recorded observation brief keeps actual geometries, cursors, history counts and model agreement', () => {
    const { cases: [item] } = report();
    assert.deepEqual(item.observed.width.initial.geometry, { rows: 40, cols: 140 });
    assert.deepEqual(item.observed.width.shrunk.geometry, { rows: 40, cols: 133 });
    assert.deepEqual(item.observed.width.shrunk.cursor, [1, 53]);
    assert.deepEqual(item.observed.history.shrunk.geometry, { rows: 36, cols: 133 });
    assert.equal(item.observed.history.shrunk.history, 44);
    assert.deepEqual(item.observed.history.restored.cursor, [17, 138]);
    assert.equal(item.observed.history.restored.history, 22);
    assert.ok(Object.values(item.observed.width).concat(Object.values(item.observed.history)).every(stage => stage.matchesModel === true));
});

test('journal and action refusals shared with the resize validators keep the ASCII reflow label and request-kind prefix', () => {
    const refusal = /Invalid or incomplete owned ASCII reflow evidence\./;
    const wrongKind = journal(); wrongKind.frames[3].kind = wrongKind.frames[3].kind.replace('reflow-', 'resize-');
    assert.throws(() => buildReflowReport(wrongKind.frames, wrongKind.actions, wrongKind.run, wrongKind.runId, wrongKind.adapter), refusal);
    const failedAction = journal(); failedAction.actions[2].error = 'resize helper failed';
    assert.throws(() => buildReflowReport(failedAction.frames, failedAction.actions, failedAction.run, failedAction.runId, failedAction.adapter), refusal);
    const lateKind = journal(); lateKind.frames[8].kind = 'reflow-read';
    assert.throws(() => buildReflowReport(lateKind.frames, lateKind.actions, lateKind.run, lateKind.runId, lateKind.adapter), refusal);
});

for (const [name, mutate] of [
    ['a changed run id', e => { e.runId = 'b'.repeat(32); }],
    ['a changed suite id', e => { e.run.suiteId = 'ntilde-owned-ascii-reflow-v2'; }],
    ['a changed adapter identity', e => { e.adapter.id = 'ntilde-owned-ascii-reflow-grading-v2'; }],
    ['a wrong predecessor derivation', e => { e.adapter.derivedFromAdapterId = 'ntilde-owned-title-stack-grading-v1'; }],
    ['a missing action', e => { e.actions.pop(); }],
    ['a no-op action whose App grid never changed', e => { e.actions[0].observed.app.cols = 140; }],
    ['an App/kernel geometry disagreement', e => { e.actions[0].observed.terminalOutput.description = '40 rows; 134 columns;\n-opost'; }],
    ['an untranslated-output kernel', e => { e.actions[1].observed.terminalOutput.description += ' opost'; }],
    ['changed fixture bytes', e => { e.run.feeds[0] = e.run.feeds[0] + 'X'; }],
    ['a misaligned ordering barrier', e => { e.run.barriers[0] = '\x1b[?9;9c'; }],
    ['a cross-stage read record', e => { e.run.stages[1] = { caseId: e.run.stages[2].caseId, stageId: e.run.stages[2].stageId }; }],
    ['a stale read cursor', e => { e.run.reads[0].cursor = [0, 0]; }],
    ['a lost frame', e => { e.frames.splice(3, 1); }],
    ['a wrong-width observation', e => { e.frames[3].screen.cols = 132; }],
    ['a nonce rebinding to another run', e => { e.frames[1].screen.lines[0] = 'NTILDE_RF_' + 'c'.repeat(32); }],
    ['an incomplete restoration', e => { e.frames[8].screen.lines[5] = 'LEFTOVER'; }],
]) {
    test(`${name} cannot produce a receipt-ready report`, () => {
        const e = journal();
        mutate(e);
        assert.throws(() => buildReflowReport(e.frames, e.actions, e.run, e.runId, e.adapter));
    });
}

for (const [name, index, mutate] of [
    ['lost content at the smaller width', 3, screen => { screen.lines[0] = screen.lines[0].slice(1); }],
    ['duplicated history rows displacing a distinct row', 6, screen => {
        screen.history.lines[11] = screen.history.lines[10];
    }],
    ['a wrong cursor mapping', 3, (screen, e) => { screen.cursorCol = 1; e.run.reads[1].cursor = [screen.cursorRow, 1]; }],
    ['truncated displaced history', 6, screen => {
        screen.history.lines = screen.history.lines.slice(2);
        screen.bufferState.scrollbackLines = screen.history.lines.length;
        screen.history.totalLines = screen.history.lines.length;
    }],
    ['a changed attribute probe', 3, screen => {
        screen.cellDetails = screen.cellDetails.map(run => run.flags === 777 ? { ...run, flags: 768, underlineStyle: 0 } : run)
            .reduce((runs, run) => {
                const last = runs.at(-1);
                const value = ({ count, ...rest }) => rest;
                if (last && JSON.stringify(value(last)) === JSON.stringify(value(run))) last.count += run.count;
                else runs.push({ ...run });
                return runs;
            }, []);
    }],
]) {
    test(`${name} grades unsupported instead of supported`, () => {
        const e = journal();
        rehash(e, index, mutate);
        const built = buildReflowReport(e.frames, e.actions, e.run, e.runId, e.adapter);
        assert.equal(built.cases[0].contractResult.outcome, 'unsupported');
        assert.ok(built.cases[0].contractResult.note.length > 10);
        assert.deepEqual(reflowCounts(built), { unsupported: 1 });
    });
}

test('the pinned replay reproduces the retained pinned result byte for byte', () => {
    const replayed = replayReflowPinned();
    assert.deepEqual(replayed, { pass: false, response: '{"declaredReflow":false}',
        note: 'No controlled resize and cell-content readback to measure reflow',
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy',
            note: 'No controlled resize and cell-content readback to measure reflow' } });
});

test('the reflow wire projection matches the model exactly', () => {
    const { cases } = journal();
    for (const item of cases) for (const stage of ['before', 'after', 'restored']) {
        const wire = reflowWire(item[stage]);
        assert.equal(wire.lines.length, item[stage].grid.length);
        assert.equal(wire.cursorRow, item[stage].row);
        assert.equal(wire.bufferState.scrollbackLines, item[stage].history.length);
    }
});
