import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { snapshotHash } from './agent-observer.mjs';
import { buildModernModesReport, collectModernModes, modernModesSuiteId } from './modern-modes.mjs';
import { checkModernModesEvidence, deriveModernModesEvidence, includesModernModesEvidence } from './modern-modes-receipt.mjs';
import { terminalFence } from './terminal-fence.mjs';

const runId = 'a'.repeat(32);
const nonce = 'NTILDE_MMOD_' + runId;
const da = '\x1b[c', daReply = '\x1b[?64;1c';
const leave = '\x1b[?47l';
const query = mode => `\x1b[?${mode}$p`;
const rawReply = (mode, pm) => pm === null ? '' : `\x1b[?${mode};${pm}$y`;
const transition = mode => `\x1b[?${mode}h` + query(mode) + da + `\x1b[?${mode}l` + query(mode) + da;
const screen = alternateScreen => ({ rows: 12, cols: 48, lines: [alternateScreen ? nonce : 'main', ...Array(11).fill('')],
    cursorRow: 0, cursorCol: alternateScreen ? nonce.length : 4, cursorVisible: true,
    bufferState: { scrollTop: 0, scrollBottom: 11, originMode: false, autoWrap: true, insertMode: false, pendingWrap: false, alternateScreen, scrollbackLines: 0 },
    history: { startLine: 0, totalLines: 0, lines: [] } });

function fixture({ initial = [2, 2], enabled = [1, 1], restored = [2, 2], override = {} } = {}) {
    const expected = new Map([
        ['\x1b[?47h\x1b[2J\x1b[H' + nonce + da, daReply],
        ['\x1b[2J\x1b[H' + leave + da, daReply],
        ['\x1b[?2026l' + leave + da, daReply],
        ['\x1b[?2026l' + leave, ''],
    ]);
    for (const [index, mode] of [2026, 2031].entries()) {
        expected.set(query(mode) + da, rawReply(mode, initial[index]) + daReply);
        if (initial[index] === 2) expected.set(transition(mode), rawReply(mode, enabled[index]) + daReply + rawReply(mode, restored[index]) + daReply);
    }
    const input = Object.assign(new EventEmitter(), { setRawMode() {}, resume() {}, isPaused: () => false });
    const frames = [], requests = [], attempts = [];
    const fence = terminalFence(input, { write(bytes, callback) {
        requests.push(bytes);
        assert.ok(expected.has(bytes), 'unexpected write ' + JSON.stringify(bytes));
        const response = Object.hasOwn(override, bytes) ? override[bytes] : expected.get(bytes);
        callback();
        if (response) {
            // Exercise fragmentation before the first complete response.
            input.emit('data', Buffer.from(response.slice(0, 2)));
            input.emit('data', Buffer.from(response.slice(2)));
        }
    } }, 100);
    const collected = collectModernModes({ config: { runId }, fence, retainFailed: run => attempts.push(run), owner(kind, barrier = null, extra = {}) {
        const value = screen(!['mmod-start', 'mmod-finish'].includes(kind));
        frames.push({ kind, barrier, index: frames.length, paneId: 'b62422e1-5645-4510-8f1a-530e9ea8f42e', screen: value,
            sha256: snapshotHash(value), request: extra.requestBytes ?? null,
            ...(extra.response === undefined ? {} : { response: extra.response }) });
        return { screen: value };
    } }).finally(() => fence.dispose());
    return { collected, frames, requests, attempts };
}

test('a single ordered set/query/reset/query write records the exact bytes for each mode and restores both', async () => {
    const { collected, frames, requests } = fixture();
    const report = buildModernModesReport(frames, await collected, runId);
    assert.deepEqual(requests.slice(1, -1), [query(2026) + da, transition(2026), query(2031) + da, transition(2031)]);
    assert.deepEqual(report.cases.map(item => [item.id, item.conclusion, item.exchanges.flatMap(step => step.replies.map(reply => reply.reply))]), [
        ['modes.synchronized-output', 'pass', [rawReply(2026, 2), rawReply(2026, 1), rawReply(2026, 2)]],
        ['modes.color-scheme-reporting', 'pass', [rawReply(2031, 2), rawReply(2031, 1), rawReply(2031, 2)]],
    ]);
});

for (const [name, states, verdicts] of [
    ['a still-reset report', { enabled: [2, 2] }, ['inconclusive', 'fail']],
    ['an absent set-state reply', { enabled: [null, null] }, ['inconclusive', 'inconclusive']],
    ['initially set reports', { initial: [1, 3] }, ['pass', 'pass']],
    ['initially unavailable reports', { initial: [0, 4] }, ['fail', 'fail']],
    ['initial silence', { initial: [null, null] }, ['inconclusive', 'inconclusive']],
]) test(name + ' follows the App criterion', async () => {
    const { collected, frames, requests } = fixture(states);
    const result = buildModernModesReport(frames, await collected, runId);
    assert.deepEqual(result.cases.map(item => item.conclusion), verdicts);
    if (states.initial) assert.deepEqual(requests.slice(1, -1), [query(2026) + da, query(2031) + da]);
});

for (const [name, reply, expectedError] of [
    ['malformed', '\x1b[?2026;9$y' + daReply, /malformed or mismatched/],
    ['other mode', rawReply(2031, 1) + daReply, /malformed or mismatched/],
    ['duplicate', rawReply(2026, 1).repeat(2) + daReply, /malformed or mismatched/],
    ['timed out after a partial transition', rawReply(2026, 1) + daReply, /timed out/],
]) test(name + ' cannot qualify the mode and triggers bounded cleanup', async () => {
    const { collected, requests, attempts } = fixture({ override: { [transition(2026)]: reply } });
    await assert.rejects(collected, expectedError);
    assert.equal(requests.at(-1), '\x1b[?2026l' + leave);
    assert.equal(attempts[0].exchanges.at(-1).response, reply);
    assert.equal(attempts[0].cleanup.request, requests.at(-1));
});

test('an incorrect final state requires cleanup and invalidates the attempt', async () => {
    const { collected, requests, attempts } = fixture({ restored: [1, 2] });
    await assert.rejects(collected, /restoration was not verified/);
    assert.equal(requests.at(-1), '\x1b[?2026l' + leave + da);
    assert.equal(attempts[0].cleanup.barrier, daReply);
});

for (const [name, mutate, message] of [
    ['a missing exchange', (_frames, run) => run.exchanges.splice(1, 1), /Incomplete Modern modes/],
    ['a stale worker reply', (frames, run) => { run.exchanges[1].response = frames[2].response; }, /unexpected exchange bytes/],
    ['a stale nonce', (_frames, run) => { run.runId = 'b'.repeat(32); }, /run identity/],
    ['an expired synchronized exchange', (_frames, run) => { run.exchanges[1].elapsedMs = 200; }, /exceeded 200 ms/],
    ['a failed retained restoration', (frames, run) => {
        frames[3].response = run.exchanges[1].response = rawReply(2026, 1) + daReply + daReply;
    }, /restoration was not verified/],
]) test('rederivation rejects ' + name, async () => {
    const { collected, frames } = fixture();
    const run = await collected;
    mutate(frames, run);
    assert.throws(() => buildModernModesReport(frames, run, runId), message);
});

test('current receipt requires the exact mode artifacts from the same pane and refuses a failed attempt', async () => {
    const { collected, frames } = fixture();
    const run = await collected;
    const output = mkdtempSync(join(tmpdir(), 'modern-modes-receipt-'));
    try {
        const policy = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v74' };
        assert.equal(includesModernModesEvidence(output, {}, policy), true);
        assert.equal(includesModernModesEvidence(output, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v73' }), false);
        const context = { ...policy, modernModesSuiteId };
        const evidence = { modernCursorEvidence: {} };
        assert.throws(() => checkModernModesEvidence(output, evidence, runId, context), /binding missing/);
        writeFileSync(join(output, 'mmod-run.json'), JSON.stringify(run) + '\n');
        writeFileSync(join(output, 'mmod-snapshots.jsonl'), frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        const { report, binding } = deriveModernModesEvidence(output, runId);
        evidence.modernModesEvidence = binding;
        writeFileSync(join(output, 'mmod-observations.json'), JSON.stringify(report, null, 2) + '\n');
        writeFileSync(join(output, 'cell-observations.json'), JSON.stringify({ snapshots: [frames[0]] }));
        checkModernModesEvidence(output, evidence, runId, context);
        writeFileSync(join(output, 'cell-observations.json'), JSON.stringify({ snapshots: [{ ...frames[0], paneId: 'wrong' }] }));
        assert.throws(() => checkModernModesEvidence(output, evidence, runId, context), /another pane or grid/);
        writeFileSync(join(output, 'mmod-failed-exchanges.json'), '{}\n');
        assert.throws(() => checkModernModesEvidence(output, evidence, runId, context), /failed observation/);
    } finally { rmSync(output, { recursive: true, force: true }); }
});
