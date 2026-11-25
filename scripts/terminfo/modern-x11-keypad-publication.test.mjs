import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { modernRoot, serializeModern } from './modern-results.mjs';
import { gradeKeypad, id, observeKeyboard, replayKeyboard } from './modern-x11-keypad.mjs';
import { buildKeypadPublication, keypadPaths, publication, readableKeypad, verifyKeypadRun } from './modern-x11-keypad-results.mjs';

test('rederive the owned XTEST keypad result and keep all other frozen Modern rows', () => {
    const report = buildKeypadPublication();
    const previous = JSON.parse(readFileSync(join(modernRoot, report.predecessor.publication, 'report.json')));
    assert.equal(report.rows.length, 69);
    assert.deepEqual(report.rows.map(row => row.id), previous.rows.map(row => row.id));
    assert.deepEqual(report.rows.filter(row => row.id !== id), previous.rows.filter(row => row.id !== id));
    const row = report.rows.find(item => item.id === id);
    const prior = previous.rows.find(item => item.id === id);
    assert.equal(row.requirement, prior.requirement);
    assert.equal(row.stockVerdict, prior.stockVerdict);
    assert.deepEqual([row.observed.controlHex, row.observed.applicationHex, row.observed.numericHex], ['61', '1b4f6b', '2b']);
    assert.deepEqual(report.totals, { pass: 64, fail: 0, inconclusive: 5 });
    const paths = keypadPaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableKeypad(report));
});

function changedPayload(step, hex) {
    const last = step.capture.chunks.at(-1);
    step.capture.chunks = [{ at: last.at, hex: hex + Buffer.from(step.native.fence.tag).toString('hex') }];
}

function retainedRun() {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-keypad-test-'));
    try {
        const directory = join(modernRoot, publication);
        const manifest = JSON.parse(readFileSync(join(directory, 'manifest.json')));
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        return verifyKeypadRun(scratch).run;
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

test('failed plain-a delivery stops keypad dispatch and still attempts DECKPNM', async () => {
    const raw = retainedRun();
    const control = raw.cases[0].control;
    changedPayload(control, '');
    const record = { identity: raw.identity, cases: [], terminal: raw.terminal };
    const writes = [];
    const modes = phase => {
        writes.push(phase);
        return phase === 'incoming' ? raw.incoming : raw.restore.modes;
    };
    const action = (step, request, key) => {
        assert.deepEqual(request, { kind: 'keyboard', chord: 'a' });
        assert.equal(key, id + '/control');
        Object.assign(step, control);
    };
    await observeKeyboard(record, modes, action);
    assert.deepEqual(writes, ['incoming', 'restore']);
    Object.assign(record.restore, { paneId: raw.restore.paneId, marker: raw.restore.marker });
    assert.equal(replayKeyboard(record)[0].conclusion, 'inconclusive');
    assert.equal(replayKeyboard(record)[0].applicationHex, null);
    record.cases[0].enabled = raw.cases[0].enabled;
    assert.throws(() => replayKeyboard(record), /dispatched after failed plain-a/);
});

test('replay rejects stale cross-phase reads, a changed mapping and incomplete restoration', () => {
    const raw = retainedRun();
    const replay = mutate => {
        const copy = structuredClone(raw);
        mutate(copy, copy.cases[0]);
        return replayKeyboard(copy)[0];
    };
    assert.throws(() => replay((_, trial) => { trial.numeric.capture = trial.application.capture; }), /stale/);
    assert.throws(() => replay((_, trial) => {
        trial.numeric.capture.chunks[0].at = trial.application.capture.chunks.at(-1).at;
    }), /stale/);
    assert.throws(() => replay((_, trial) => { trial.numeric.native.mapping.numLockKeysym = 0xffad; }), /KP_Add mapping/);
    assert.throws(() => replay((_, trial) => { trial.application.native.finalKeys.push(86); }), /key restoration/);
    assert.throws(() => replay(copy => { copy.restore.modes.writeHex = copy.incoming.writeHex; }), /mode observations/);
    assert.throws(() => replay(copy => { copy.terminal.restored = []; }), /terminal restoration/);
    assert.equal(replay((_, trial) => changedPayload(trial.application, '2b')).conclusion, 'fail');
    assert.equal(replay((_, trial) => changedPayload(trial.numeric, '1b4f6b')).conclusion, 'inconclusive');
    assert.equal(gradeKeypad('62', '2b', '2b').conclusion, 'inconclusive');
});
