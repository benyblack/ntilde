import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { modernRoot, serializeModern } from './modern-results.mjs';
import { id, observeClipboard, replayClipboard } from './modern-x11-osc52-write.mjs';
import { buildOsc52WritePublication, osc52WritePaths, publication, readableOsc52Write, verifyOsc52WriteRun } from './modern-x11-osc52-write-results.mjs';

function withArchive(check) {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-osc52-test-'));
    try {
        const directory = join(modernRoot, publication);
        const manifest = JSON.parse(readFileSync(join(directory, 'manifest.json')));
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        return check(scratch);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

const retained = () => withArchive(scratch => verifyOsc52WriteRun(scratch).run);

test('rederive the OSC 52 write and preserve the other frozen rows', () => {
    const report = buildOsc52WritePublication();
    const previous = JSON.parse(readFileSync(join(modernRoot, report.predecessor.publication, 'report.json')));
    const index = report.rows.findIndex(row => row.id === id);
    const { observed, requirement, stockVerdict } = report.rows[index];
    assert.deepEqual(report.rows.with(index, previous.rows[index]), previous.rows);
    assert.equal(requirement, previous.rows[index].requirement);
    assert.equal(stockVerdict, previous.rows[index].stockVerdict);
    assert.equal(observed.keyHex, '61');
    assert.equal(observed.reads.at(-1).text, observed.nonce);
    assert.equal(observed.restored, observed.baseline);
    assert.deepEqual(report.totals, { pass: 66, fail: 0, inconclusive: 3 });
    const paths = osc52WritePaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableOsc52Write(report));
});

test('refuse a misattributed peer, missing key bytes, altered OSC bytes or unproved restoration', () => {
    const raw = retained();
    const changes = [
        ['X11 clipboard authority', trial => { trial.baselineRead.before.peerPid = trial.seed.process.pid; }],
        ['CLIPBOARD read', trial => { trial.reads.at(-1).after.ownerWindow++; }],
        ['baseline did not read back', trial => { trial.baselineRead.result.text = trial.nonce; }],
        ['XTEST a', trial => {
            const { capture } = trial.key;
            capture.chunks = [{ at: capture.chunks.at(-1).at, hex: '7a68' }];
        }],
        ['observations', trial => { trial.write.writeHex = '1b5d35323b703b07'; }],
        ['observations', trial => { trial.write.replyHex = ''; }],
        ['restoration', trial => { trial.restoreRead.result.text = trial.nonce; }],
    ];
    for (const [reason, change] of changes) {
        const run = structuredClone(raw);
        change(run.cases[0]);
        assert.throws(() => replayClipboard(run), new RegExp(reason));
    }
});

test('apply the frozen negative rule only to an unchanged owned baseline after the post-CPR deadline', () => {
    const run = retained(), [trial] = run.cases, sample = trial.reads.at(-1);
    sample.result.text = 'an unexpected synthetic value';
    assert.equal(replayClipboard(run)[0].conclusion, 'inconclusive');
    sample.result.text = trial.baseline;
    for (const state of [sample.before, sample.after, trial.restoreSeed.before]) {
        state.ownerPid = trial.seed.process.pid;
        state.ownerWindow = trial.seed.after.ownerWindow;
    }
    assert.throws(() => replayClipboard(run), /incomplete or extra settled CLIPBOARD reads/);
    shiftClipboardEnd(run, [sample, trial.restoreSeed, trial.restoreRead]);
    assert.equal(replayClipboard(run)[0].conclusion, 'fail');
});

function shiftClipboardEnd(run, steps) {
    const later = timestamp => (BigInt(timestamp) + 1_000_000_000n).toString();
    for (const step of steps) {
        step.before.at = later(step.before.at);
        step.dispatched = later(step.dispatched);
        step.result.started = later(step.result.started);
        step.result.finished = later(step.result.finished);
        step.after.at = later(step.after.at);
    }
    run.terminal.at = later(run.terminal.at);
}

test('a seed-to-App transfer during read requires a fresh read from the settled App owner', () => {
    const run = retained(), [trial] = run.cases, sample = trial.reads.at(-1);
    const transition = structuredClone(sample);
    transition.process.pid = 65535;
    transition.process.argv[6] = transition.process.argv[6].replace(/gtk-\d+$/, 'gtk-80');
    transition.before.peerPid = transition.after.peerPid = transition.process.pid;
    transition.before.peerWindow = transition.after.peerWindow = sample.before.peerWindow + 100;
    transition.before.ownerPid = trial.seed.process.pid;
    transition.before.ownerWindow = trial.seed.after.ownerWindow;
    transition.result.text = trial.baseline;
    shiftClipboardEnd(run, [sample, trial.restoreSeed, trial.restoreRead]);
    trial.reads.splice(-1, 0, transition);
    assert.equal(replayClipboard(run)[0].conclusion, 'pass');
    trial.reads.pop();
    assert.throws(() => replayClipboard(run), /incomplete or extra settled CLIPBOARD reads/);
});

test('protect a clipboard read containing Markdown delimiters in the readable result', () => {
    const [{ result }] = retained().cases[0].reads;
    const report = JSON.parse(readFileSync(osc52WritePaths(modernRoot).json));
    const row = report.rows.find(item => item.id === id);
    row.observed.reads[0].text = result.text + '|`';
    assert.ok(readableOsc52Write(report).includes(result.text + '\\u007c\\u0060'));
});

test('refuse a success archive with policy drift or missing GTK process cleanup', () => {
    withArchive(scratch => {
        const path = join(scratch, 'observation.json');
        const original = verifyOsc52WriteRun(scratch).run;
        const run = structuredClone(original);
        run.settings.AllowOsc52ClipboardWrite = false;
        writeFileSync(path, JSON.stringify(run));
        assert.throws(() => verifyOsc52WriteRun(scratch), /disposable profile/);
        original.cleanup.peers.pop();
        writeFileSync(path, JSON.stringify(original));
        assert.throws(() => verifyOsc52WriteRun(scratch), /GTK peer cleanup/);
    });
});

test('a refused fixture performs no restore write and a failed key never dispatches OSC', async () => {
    const noAuthority = new Error('No verified X11 fixture');
    let peers = 0;
    const rejectUnexpected = () => assert.fail('Unexpected XTEST or OSC operation');
    await assert.rejects(() => observeClipboard({ cases: [] }, {
        peer: () => { peers++; throw noAuthority; }, key: rejectUnexpected, write: rejectUnexpected,
    }), noAuthority);
    assert.equal(peers, 1);

    const raw = retained(), [trial] = raw.cases;
    const failure = new Error('XTEST failed');
    const exhausted = new Error('Restoration failed');
    const calls = [];
    let baseline;
    await assert.rejects(() => observeClipboard({ ...raw, cases: [] }, {
        peer: (step, request, _owners, restoring) => {
            calls.push([request.kind, Boolean(restoring)]);
            if (restoring) throw exhausted;
            const original = request.kind === 'seed' ? trial.seed : trial.baselineRead;
            if (request.kind === 'seed') baseline = request.text;
            Object.assign(step, structuredClone(original));
            step.result.text = baseline;
        },
        key: () => { throw failure; }, write: rejectUnexpected,
    }), error => error instanceof AggregateError && error.errors[0] === failure && error.errors[1] === exhausted);
    assert.deepEqual(calls, [['seed', false], ['read', false], ['seed', true]]);
});
