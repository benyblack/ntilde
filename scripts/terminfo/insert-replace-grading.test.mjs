import assert from 'node:assert/strict';
import { test } from 'node:test';
import { snapshotHash } from './agent-observer.mjs';
import { buildInsertReplaceReport } from './insert-replace-grading.mjs';
import { evidence, runId } from './fixtures/insert-replace-grading-evidence.mjs';
import { syntheticLineEditScreen, rowOf } from './fixtures/line-edit-evidence.mjs';

const screen = (text, col, insert, alternate) => {
    const value = syntheticLineEditScreen([rowOf(text)], [0, col], { insert });
    value.bufferState.autoWrap = false; value.bufferState.alternateScreen = alternate;
    value.horizontalMargins = { leftRightMarginMode: false, leftMargin: 0, rightMargin: value.cols - 1 }; return value;
};
const derive = async ({ frames, run, adapter }) => buildInsertReplaceReport(frames, run, runId, adapter);

test('report rederives pinned paths and refuses a conclusive effect on invalid controls', async () => {
    const report = await derive(evidence());
    assert.equal(report.pinned.observation.outcome, 'supported');
    assert.equal(report.terminalPinned.observation.outcome, 'inconclusive');
    assert.equal(report.effect.outcome, 'inconclusive');
});

test('changed feeds, identity, snapshot bytes and restoration are refused', async () => {
    const a = evidence(); a.run.feeds[1] += 'X'; await assert.rejects(derive(a), /do not match/);
    const b = evidence(); b.adapter.id = 'other'; await assert.rejects(derive(b), /identity/);
    const c = evidence(); c.frames[2].screen.cursorCol = 9; await assert.rejects(derive(c), /snapshot evidence/);
    const d = evidence(); d.frames.at(-1).screen = screen('X', 1, false, false); d.frames.at(-1).sha256 = snapshotHash(d.frames.at(-1).screen);
    await assert.rejects(derive(d), /not restored/);
    const other = evidence(); other.run.pinned.probeId = 'editing.insert-chars'; await assert.rejects(derive(other), /Missing pinned/);
    const terminal = evidence(); terminal.run.terminalPinned.response = '{"row":9,"col":9}'; await assert.rejects(derive(terminal), /terminal result differs/);
    const mode = evidence(); mode.run.original.insertMode = true; await assert.rejects(derive(mode), /not restored/);
});
