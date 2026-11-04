import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { collectInput } from './input-collector.mjs';
import { terminalFence } from './terminal-fence.mjs';
import { makeInputCases } from './input-cases.mjs';
import { inputFinalRestore, inputModeQueries, inputModeReplyHex, defaultInputModes } from './input-state.mjs';
import { initialInputScreen } from './fixtures/input-journal.mjs';
class Input extends EventEmitter {
    isRaw = false; paused = true;
    isPaused() { return this.paused; } setRawMode(raw) { this.isRaw = raw; }
    pause() { this.paused = true; } resume() { this.paused = false; }
}
async function fixture(action, { controlBad = false, refuse = null, late = false } = {}) {
    const outputDir = mkdtempSync(join(tmpdir(), 'nt-input-worker-'));
    const input = new Input(), calls = [], writes = [], cases = makeInputCases(12, 120);
    let next = 0;
    const output = { write(value, callback) {
        writes.push(value); callback();
        const challenge = /^\x1b\[\?(\d+)\$p\x1b\[\?(\d+)\$p$/.exec(value);
        if (challenge) queueMicrotask(() => input.emit('data', Buffer.from(challenge.slice(1).map(n => `\x1b[?${n};0$y`).join(''))));
        else if (value.endsWith(inputModeQueries)) {
            const modes = value.includes(inputFinalRestore) ? defaultInputModes : cases[next].modes;
            queueMicrotask(() => input.emit('data', Buffer.from(controlBad ? '' : inputModeReplyHex(modes), 'hex')));
        }
    } };
    const fence = terminalFence(input, output);
    const owner = async (kind, _, extra) => {
        calls.push({ kind, extra });
        if (kind === refuse) throw Object.assign(new Error('typed refusal after start'), { code: 'inputGeometryUnavailable' });
        if (kind === 'input-start') return { screen: initialInputScreen() };
        if (kind === 'input-apply') input.emit('data', Buffer.from(cases[next].expectedHex, 'hex'));
        if (kind === 'input-read') next++;
        if (kind === 'input-finish' && late) input.emit('data', Buffer.from('human'));
        if (kind === 'input-seal' && extra.captureStatus.failed) throw new Error('failed raw seal');
        return {};
    };
    try { await action({ input, output, fence, owner, calls, writes, config: { runId: 'a'.repeat(32), output: outputDir }, outputDir }); }
    finally { fence.dispose(); rmSync(outputDir, { recursive: true, force: true }); }
}
test('worker executes only39 source-selected events after mode controls, restores state and seals80 raw attempts', async () => fixture(async f => {
    await collectInput(f);
    const actions = f.calls.filter(x => x.kind === 'input-apply');
    assert.equal(actions.length, 39); assert.ok(actions.every(x => Object.keys(x.extra).join() === 'caseId'));
    const seal = f.calls.at(-1); assert.equal(seal.kind, 'input-seal'); assert.equal(seal.extra.captureStatus.attempts, 80);
    assert.equal(seal.extra.captureStatus.failed, false); assert.equal(f.input.listenerCount('data'), 0);
    assert.equal(f.input.isRaw, true); assert.equal(f.input.paused, false);
    const controlWrites = f.writes.filter(x => x.endsWith(inputModeQueries));
    assert.equal(controlWrites.length, 40);
    // The coalesced hover repeats only read-only mode queries, preserving the prior view/mode/cell.
    assert.equal(controlWrites.at(-2), inputModeQueries);
}));
test('failed independent mode control cannot cause even one generated event', async () => fixture(async f => {
    await assert.rejects(collectInput(f), /mode control/);
    assert.ok(!f.calls.some(x => x.kind === 'input-apply'));
    const failure = JSON.parse(readFileSync(join(f.outputDir, 'input-raw-failure.json')));
    assert.equal(failure.attempts, 2); assert.ok(failure.lastAttempt.completed);
}, { controlBad: true }));
test('only an initial refusal skips the fixture; a started refusal retains failure and never retries', async () => {
    await fixture(async f => {
        assert.deepEqual(await collectInput(f), { status: 'unavailable', reason: 'inputGeometryUnavailable' });
        assert.equal(f.writes.length, 0); assert.equal(f.input.listenerCount('data'), 1);
    }, { refuse: 'input-start' });
    await fixture(async f => {
        await assert.rejects(collectInput(f));
        assert.equal(f.calls.filter(x => x.kind === 'input-apply').length, 1);
        assert.ok(readFileSync(join(f.outputDir, 'input-raw-failure.json')).length);
        assert.ok(!f.calls.some(x => x.kind === 'input-finish'));
    }, { refuse: 'input-apply' });
});
test('late input after final capture is bound to the failed seal, never a completed sample', async () => fixture(async f => {
    await assert.rejects(collectInput(f), /failed raw seal/);
    assert.equal(f.calls.at(-1).extra.captureStatus.failed, true);
    const failure = JSON.parse(readFileSync(join(f.outputDir, 'input-raw-failure.json')));
    assert.equal(failure.failed, true); assert.match(failure.lastAttempt.error, /Unexpected/);
}, { late: true }));
