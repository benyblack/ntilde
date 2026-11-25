import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { modernRoot, serializeModern } from './modern-results.mjs';
import { id, observeFocus, replayFocus } from './modern-x11-focus.mjs';
import { buildFocusPublication, focusPaths, publication, readableFocus, verifyFocusRun } from './modern-x11-focus-results.mjs';

const outcome = run => replayFocus(run)[0].status;

function retainedRun(archive = 'owned-run.zip') {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-focus-test-'));
    try {
        const directory = join(modernRoot, publication);
        const manifest = JSON.parse(readFileSync(join(directory, 'manifest.json')));
        extractOwnedArchive(directory, { archives: { 'owned-run.zip': manifest.archives[archive] } }, scratch, join(directory, archive));
        return verifyFocusRun(scratch).run;
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

function changeBytes(step, raw) {
    const at = step.capture.chunks.at(-1).at;
    step.capture.chunks = [{ at, hex: raw + Buffer.from(step.native.fence.tag).toString('hex') }];
}

function changeCheckpoint(step, raw) {
    step.checkpoint.chunks = [{ at: step.checkpoint.chunks.at(-1).at, hex: raw }];
}

test('rederive the owned focus result while preserving the previous frozen rows', () => {
    const report = buildFocusPublication();
    const previous = JSON.parse(readFileSync(join(modernRoot, report.predecessor.publication, 'report.json')));
    assert.deepEqual(report.rows.map(row => row.id), previous.rows.map(row => row.id));
    assert.deepEqual(report.rows.filter(row => row.id !== id), previous.rows.filter(row => row.id !== id));
    const row = report.rows.find(item => item.id === id);
    const prior = previous.rows.find(item => item.id === id);
    assert.equal(row.requirement, prior.requirement);
    assert.equal(row.stockVerdict, prior.stockVerdict);
    assert.deepEqual([row.observed.modeOnHex, row.observed.modeOffHex], ['1b5b4f1b5b4961', '61']);
    assert.deepEqual(report.totals, { pass: 65, fail: 0, inconclusive: 4 });
    const paths = focusPaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableFocus(report));
});

test('DECRPM is optional and the enable-time report cannot supply the round trip', () => {
    const raw = retainedRun();
    const [trial] = raw.cases;
    const status = '1b5b306e';
    changeCheckpoint(trial.on, '1b5b49' + status);
    changeCheckpoint(trial.off, status);
    assert.equal(outcome(raw), 'supported');
    changeBytes(trial.on, '61');
    const [result] = replayFocus(raw);
    assert.equal(result.enableTimeHex, '1b5b49');
    assert.equal(result.status, 'unsupported');
    changeCheckpoint(trial.on, '1b5b491b5b4f' + status);
    assert.equal(outcome(raw), 'probe-invalid');
});

test('replay the unsupported result from the baseline App', () => {
    const run = retainedRun('attempt-1.zip');
    const [result] = replayFocus(run);
    assert.deepEqual([result.status, result.modeOnHex, result.modeOffHex], ['unsupported', '61', '61']);
});

test('wrong focus read, missing delivery key and mode-off reports invalidate a trial', () => {
    const baseline = retainedRun();
    const changes = [
        trial => { trial.on.focus.out.observed = trial.on.focus.back.observed; },
        trial => changeBytes(trial.on, '1b5b4f1b5b49'),
        trial => changeBytes(trial.off, '1b5b4961'),
        trial => changeCheckpoint(trial.off, '1b5b4f1b5b306e'),
        trial => changeBytes(trial.on, '1b5b491b5b4f61'),
    ];
    for (const change of changes) {
        const run = structuredClone(baseline);
        change(run.cases[0]);
        assert.equal(outcome(run), 'probe-invalid');
    }
});

test('refuse stale input, swapped root or phases, and unverified terminal restoration', () => {
    const run = retainedRun(), [trial] = run.cases;
    assert.throws(() => replayFocus({ ...run, identity: { ...run.identity, root: run.identity.root + 1 } }), /unowned X11 focus/);
    assert.throws(() => replayFocus({ ...run, cases: [{ ...trial, on: trial.off, off: trial.on }] }), /unarmed or stale mode write/);
    const stale = structuredClone(run);
    stale.cases[0].on.capture.chunks[0].at = trial.on.checkpoint.finished;
    assert.throws(() => replayFocus(stale), /stale or malformed raw reply/);
    const terminal = { ...run.terminal, restored: [] };
    assert.throws(() => replayFocus({ ...run, terminal }), /terminal restoration/);
});

test('attempt reset on capture failure and preserve the capture error', async () => {
    const raw = retainedRun();
    const calls = [], record = { cases: [] };
    const failure = new Error('focus capture interrupted');
    await assert.rejects(observeFocus(record, phase => {
        calls.push(phase);
        return phase === 'incoming' ? raw.incoming : raw.restore.modes;
    }, () => { throw failure; }), error => error === failure);
    assert.deepEqual(calls, ['incoming', 'restore']);
    assert.deepEqual(record.restore.modes, raw.restore.modes);
});
