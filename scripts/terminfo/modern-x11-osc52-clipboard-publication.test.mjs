import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { modernRoot, serializeModern } from './modern-results.mjs';
import { id, replayClipboard } from './modern-x11-osc52-clipboard.mjs';
import {
    buildOsc52ClipboardPublication, osc52ClipboardPaths, publication, readableOsc52Clipboard, verifyOsc52ClipboardRun,
} from './modern-x11-osc52-clipboard-results.mjs';

function withArchive(check) {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-osc52-roundtrip-test-'));
    try {
        const directory = join(modernRoot, publication);
        const manifest = JSON.parse(readFileSync(join(directory, 'manifest.json')));
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        return check(scratch);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

const retained = () => withArchive(scratch => verifyOsc52ClipboardRun(scratch).run);
const frameHex = nonce => Buffer.from('\x1b]52;c;' + Buffer.from(nonce).toString('base64') + '\x1b\\').toString('hex');
const cpr = '1b5b313b343352';
const denial = '1b5d35323b633b1b5c';

test('rederive the write and empty denial while preserving the other 68 frozen rows', () => {
    const report = buildOsc52ClipboardPublication();
    const previous = JSON.parse(readFileSync(join(modernRoot, report.predecessor.publication, 'report.json')));
    const index = report.rows.findIndex(row => row.id === id);
    const { observed, requirement, stockVerdict } = report.rows[index];
    assert.deepEqual(report.rows.with(index, previous.rows[index]), previous.rows);
    assert.equal(requirement, previous.rows[index].requirement);
    assert.equal(stockVerdict, previous.rows[index].stockVerdict);
    assert.equal(observed.keyHex, '61');
    assert.equal(observed.write.conclusion, 'pass');
    assert.equal(observed.reads.at(-1).text, observed.nonce);
    assert.equal(observed.read.outcome, 'empty-denial');
    assert.notEqual(observed.read.nonce, observed.nonce);
    assert.deepEqual(observed.read.attempts, [{ rawHex: denial + cpr, outcome: 'empty-denial', replyHex: denial }]);
    assert.equal(observed.restored, observed.baseline);
    assert.equal(observed.conclusion, 'inconclusive');
    assert.deepEqual(report.totals, { pass: 66, fail: 0, inconclusive: 3 });
    const paths = osc52ClipboardPaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableOsc52Clipboard(report));
});

function shiftRestoration(run, trial) {
    const later = value => (BigInt(value) + 4_000_000_000n).toString();
    for (const step of [trial.restoreSeed, trial.restoreRead]) {
        step.before.at = later(step.before.at);
        step.dispatched = later(step.dispatched);
        step.result.started = later(step.result.started);
        step.result.finished = later(step.result.finished);
        step.after.at = later(step.after.at);
    }
    run.terminal.at = later(run.terminal.at);
}

function setAttemptTimes(sample, start, duration) {
    const at = delta => (start + delta * 1_000_000n).toString();
    sample.before.at = at(1n);
    sample.exchange.started = at(2n);
    sample.exchange.lastReadAt = at(2n + duration / 2n);
    sample.exchange.finished = at(2n + duration);
    sample.after.at = at(3n + duration);
    sample.decidedAt = at(4n + duration);
}

test('grade exact, silent, malformed and other replies from the raw PTY bytes', () => {
    const run = retained(), [trial] = run.cases, [sample] = trial.queries;
    shiftRestoration(run, trial);
    setAttemptTimes(sample, BigInt(trial.queryStarted), 1000n);
    const scenarios = [
        [cpr + frameHex(trial.queryNonce), 'seeded-nonce', 'pass'],
        [cpr, 'silence', 'inconclusive'],
        [denial, 'unfenced', 'inconclusive'],
        [cpr + denial + denial, 'invalid-reply', 'inconclusive'],
        [cpr + denial.slice(0, -2), 'invalid-reply', 'inconclusive'],
        [cpr + Buffer.from('\x1b]52;c;Zh==\x07').toString('hex'), 'invalid-reply', 'inconclusive'],
        [cpr + frameHex('another value'), 'other-value', 'inconclusive'],
    ];
    for (const [rawHex, outcome, conclusion] of scenarios) {
        sample.exchange.rawHex = rawHex;
        const [result] = replayClipboard(run);
        assert.equal(result.read.outcome, outcome);
        assert.equal(result.conclusion, conclusion);
    }
});

test('retry only the earlier write and grade the last reply by the write deadline', () => {
    const run = retained(), [trial] = run.cases, [first] = trial.queries;
    const start = BigInt(trial.queryStarted);
    shiftRestoration(run, trial);
    setAttemptTimes(first, start, 25n);
    first.exchange.rawHex = frameHex(trial.nonce) + cpr;
    assert.throws(() => replayClipboard(run), /missing or extra OSC 52 stale-nonce retry/);

    const second = structuredClone(first);
    setAttemptTimes(second, BigInt(first.decidedAt) + 10n, 20n);
    second.exchange.rawHex = frameHex(trial.queryNonce) + cpr;
    trial.queries.push(second);
    assert.throws(() => replayClipboard(run), /before the poll interval/);
    setAttemptTimes(second, BigInt(first.decidedAt) + 25_000_000n, 20n);
    assert.equal(replayClipboard(run)[0].conclusion, 'pass');

    setAttemptTimes(second, start + 970_000_000n, 29n);
    second.exchange.rawHex = first.exchange.rawHex;
    const [stale] = replayClipboard(run);
    assert.equal(stale.read.outcome, 'own-write');
    assert.equal(stale.conclusion, 'inconclusive');

    setAttemptTimes(second, start + 900_000_000n, 160n);
    second.exchange.rawHex = frameHex(trial.queryNonce) + cpr;
    second.exchange.lastReadAt = (start + 1_010_000_000n).toString();
    const [late] = replayClipboard(run);
    assert.equal(late.read.outcome, 'retry-deadline');
    assert.equal(late.conclusion, 'inconclusive');
    assert.equal(late.read.attempts.at(-1).rawHex, second.exchange.rawHex);

    setAttemptTimes(second, start + 1_000_000_000n, 2n);
    second.exchange.rawHex = '';
    second.exchange.lastReadAt = null;
    assert.throws(() => replayClipboard(run), /retry sent after the deadline/);
    second.exchange.writeHex = '';
    assert.equal(replayClipboard(run)[0].read.outcome, 'retry-deadline');

    first.exchange.rawHex = denial + cpr;
    assert.throws(() => replayClipboard(run), /missing or extra OSC 52 stale-nonce retry/);
});

test('refuse a reused nonce, detached ownership, changed query or missing restoration', () => {
    const raw = retained();
    const tampering = [
        ['synthetic read nonce', trial => { trial.queryNonce = trial.nonce; }],
        ['independent read nonce', trial => { trial.querySeedRead.result.text = trial.nonce; }],
        ['X11 ownership or focus', trial => { trial.queries[0].after.ownerWindow++; }],
        ['query window', trial => { trial.queries[0].exchange.writeHex = '1b5d35323b703b3f07'; }],
        ['restoration', trial => { trial.restoreRead.result.text = trial.queryNonce; }],
    ];
    for (const [reason, change] of tampering) {
        const run = structuredClone(raw);
        change(run.cases[0]);
        assert.throws(() => replayClipboard(run), new RegExp(reason));
    }
});

test('refuse a recorded grade that disagrees with the bytes or a missing peer exit', () => {
    withArchive(scratch => {
        const path = join(scratch, 'observation.json');
        const original = verifyOsc52ClipboardRun(scratch).run;
        const rewritten = structuredClone(original);
        rewritten.results[0].conclusion = 'pass';
        writeFileSync(path, JSON.stringify(rewritten));
        assert.throws(() => verifyOsc52ClipboardRun(scratch), /recorded grading does not rederive/);
        original.cleanup.peers.splice(3, 1);
        writeFileSync(path, JSON.stringify(original));
        assert.throws(() => verifyOsc52ClipboardRun(scratch), /GTK peer cleanup/);
    });
});
