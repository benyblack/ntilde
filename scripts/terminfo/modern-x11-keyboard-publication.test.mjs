import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { modernRoot, serializeModern } from './modern-results.mjs';
import { gradeKeyboard, id, replayKeyboard } from './modern-x11-keyboard.mjs';
import { buildKeyboardPublication, keyboardPaths, publication, readableKeyboard, verifyKeyboardRun } from './modern-x11-keyboard-results.mjs';

test('replay the archive and preserve every other frozen Modern row', () => {
    const report = buildKeyboardPublication();
    const previous = JSON.parse(readFileSync(join(modernRoot, report.predecessor.publication, 'report.json')));
    assert.equal(report.rows.length, 69);
    assert.deepEqual(report.rows.map(row => row.id), previous.rows.map(row => row.id));
    assert.deepEqual(report.rows.filter(row => row.id !== id), previous.rows.filter(row => row.id !== id));
    const row = report.rows.find(item => item.id === id);
    const prior = previous.rows.find(item => item.id === id);
    assert.equal(row.requirement, prior.requirement);
    assert.equal(row.stockVerdict, prior.stockVerdict);
    assert.deepEqual(report.totals, { pass: 63, fail: 0, inconclusive: 6 });
    const paths = keyboardPaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableKeyboard(report));
});

function changedPayload(step, hex) {
    const last = step.capture.chunks.at(-1);
    step.capture.chunks = [{ at: last.at, hex: hex + Buffer.from(step.native.fence.tag).toString('hex') }];
}

test('keyboard replay rejects stale bytes and incomplete restoration and gates Ctrl+I on a fresh 61', () => {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-keyboard-test-'));
    try {
        const directory = join(modernRoot, publication);
        const manifest = JSON.parse(readFileSync(join(directory, 'manifest.json')));
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const { run } = verifyKeyboardRun(scratch);
        const replay = mutate => {
            const copy = structuredClone(run);
            mutate(copy, copy.cases[0]);
            return replayKeyboard(copy)[0];
        };
        assert.equal(replay((_, trial) => { changedPayload(trial.control, ''); delete trial.target; }).conclusion, 'inconclusive');
        assert.throws(() => replay((_, trial) => changedPayload(trial.control, '62')), /after failed plain-a/);
        assert.throws(() => replay((_, trial) => { trial.target.capture.chunks[0].at = trial.control.arm.armed; }), /stale/);
        assert.equal(replay((_, trial) => changedPayload(trial.target, '09')).conclusion, 'fail');
        assert.equal(replay((_, trial) => changedPayload(trial.target, '')).conclusion, 'inconclusive');
        assert.throws(() => replay(copy => { copy.restore.modes.replyHex = copy.cases[0].enabled.replyHex; }), /mode observations/);
        assert.throws(() => replay(copy => { copy.terminal.restored = []; }), /terminal restoration/);
        assert.throws(() => replay((_, trial) => { trial.target.native.finalKeys.push(37); }), /key restoration/);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
});

test('accept both frozen key reports and refuse to grade an unauthenticated Tab', () => {
    for (const text of ['\x1b[27;5;105~', '\x1b[105;5u']) assert.equal(gradeKeyboard('61', Buffer.from(text).toString('hex')).conclusion, 'pass');
    assert.equal(gradeKeyboard('', '09').conclusion, 'inconclusive');
    assert.equal(gradeKeyboard('61', '0961').conclusion, 'inconclusive');
});
