import assert from 'node:assert/strict';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { replayClipboard } from './modern-x11-osc52-read.mjs';
import { replayPinned } from './osc52-read-pinned.mjs';
import { buildPublication, publish, verifyRun, withMeasurement } from './osc52-read-results.mjs';

const hex = text => Buffer.from(text, 'latin1').toString('hex');
const denial = '1b5d35323b633b1b5c';
const frame = text => hex('\x1b]52;c;' + btoa(text) + '\x07');
const measured = () => withMeasurement(scratch => verifyRun(scratch).run);

test('the publication rederives the exact read-only callback and the independent denial verdict', async () => {
    await publish(true);
    const { report } = await buildPublication();
    const [row] = report.cases;
    assert.equal(row.exchange.queryHex, '1b5d35323b633b3f07');
    assert.equal(row.exchange.rawHex, denial + row.exchange.cprHex);
    assert.equal(row.pinned.response, '\x1b]52;c;\x1b\\');
    assert.deepEqual(row.pinned.observation, { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query' });
    assert.equal(row.contractResult.outcome, 'unsupported');
    assert.equal(row.denial.outcome, 'supported');
    assert.equal(row.stock.outcome, 'inconclusive');
    assert.equal(row.before, row.after);
    assert.equal(row.baseline, row.restored);
    assert.notEqual(row.before, row.baseline);
    assert.equal(report.measurement.clipboardWriteEnabled, false);
});

test('the unchanged callback requires the independently seeded nonce', async () => {
    const run = measured();
    const [trial] = run.cases;
    const [{ exchange }] = replayClipboard(run);
    const play = replyHex => replayPinned(trial.uuid, { ...exchange, replyHex });
    assert.equal((await play(frame(trial.nonce))).observation.outcome, 'supported');
    assert.equal((await play(frame(trial.baseline))).observation.reason, 'insufficient-evidence');
    assert.equal((await play('')).observation.reason, 'no-response');
    assert.equal((await play(hex('\x1b]52;c;a===\x07'))).observation.reason, 'invalid-reply');
    await assert.rejects(replayPinned(trial.uuid, { ...exchange, queryHex: exchange.queryHex.slice(0, -2) }));
    await assert.rejects(replayPinned(trial.uuid, exchange, Buffer.from('foreign bundle')));
});

test('the full read window distinguishes denial from other PTY observations', () => {
    const run = measured();
    const [trial] = run.cases;
    const [{ exchange: { cprHex } }] = replayClipboard(run);
    const outcome = rawHex => {
        trial.query.exchange.rawHex = rawHex;
        return replayClipboard(run)[0].exchange.kind;
    };

    assert.equal(outcome(cprHex), 'silence');
    assert.equal(outcome(cprHex + '1b5d35323b633b07'), 'empty-denial');
    assert.equal(outcome(frame(trial.nonce) + cprHex), 'seeded-nonce');
    assert.equal(outcome(frame(trial.baseline) + cprHex), 'stale-data');
    assert.equal(outcome(frame('other synthetic value') + cprHex), 'other-value');
    assert.equal(outcome(denial), 'delivery-failure');
    assert.equal(outcome(denial + cprHex + cprHex), 'delivery-failure');
    for (const raw of [denial.slice(0, -2) + cprHex, denial + denial + cprHex, denial.slice(0, -4) + cprHex + denial.slice(-4),
        cprHex + hex('\x1b]52;c;a===\x07'), denial + cprHex + '0a']) {
        assert.equal(outcome(raw), 'malformed');
    }
    trial.query.exchange.lastReadAt = (BigInt(trial.query.exchange.started) + 1_000_000_000n).toString();
    assert.equal(outcome(denial + cprHex), 'late-reply');
});

test('the read replay rejects altered custody, sequencing, window and restoration', () => {
    const original = measured();
    const mutations = [
        [trial => { trial.afterRead.result.text = trial.baseline; }, /clipboard readback/],
        [trial => { trial.afterRead = structuredClone(trial.beforeRead); }, /independent processes/],
        [trial => { trial.afterRead.before.at = trial.query.exchange.started; }, /stale or incomplete GTK/],
        [trial => { trial.beforeRead.after.ownerWindow++; }, /unattributed/],
        [trial => { trial.query.after.peerPid++; }, /ownership, focus or identity/],
        [trial => { trial.query.exchange.writeHex = '1b5d35323b633b3f1b5c'; }, /read window/],
        [trial => { trial.query.exchange.finished = (BigInt(trial.query.exchange.started) + 500_000_000n).toString(); }, /capture deadline/],
        [trial => { trial.query.exchange.lastReadAt = trial.query.exchange.started; }, /stale OSC 52 reply timestamp/],
        [trial => { trial.restoreRead.result.text = trial.nonce; }, /clipboard readback/],
        [(_trial, run) => { run.terminal.restored[0]++; }, /kernel terminal restoration/],
    ];
    for (const [mutate, reason] of mutations) {
        const run = structuredClone(original);
        mutate(run.cases[0], run);
        assert.throws(() => replayClipboard(run), reason);
    }
});

test('the common X11 receipt binds all peers, the worker and the sealed source set', () => {
    withMeasurement(scratch => {
        const path = join(scratch, 'observation.json');
        const run = verifyRun(scratch).run;
        run.cleanup.peers.splice(4, 1);
        writeFileSync(path, JSON.stringify(run));
        assert.throws(() => verifyRun(scratch), /GTK peer cleanup/);

        const source = join(scratch, 'sources', 'modern-x11-osc52-read.mjs');
        writeFileSync(source, readFileSync(source, 'utf8') + '\n');
        assert.throws(() => verifyRun(scratch), /changed X11 collector source/);

        const failure = join(scratch, 'failure.json');
        assert.equal(existsSync(failure), false);
        writeFileSync(failure, '{}');
        assert.throws(() => verifyRun(scratch), /collection failed/);
    });
});
