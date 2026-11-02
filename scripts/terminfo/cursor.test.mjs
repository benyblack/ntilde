import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildCursorReport, collectCursor, pristineCursorBaseline } from './cursor.mjs';
import { rehash, runId, stateFrames, stateScreen } from './fixtures/state-frames.mjs';

const mode = (style, blink, isDefault = false) => ({ style, blink, isDefault });
const initial = () => stateScreen('', [], { control: '', mode: mode(2, true, true) });
const control = (value = mode(0, false), visible = true) => stateScreen('CURSOR', [], { col: 2, visible, mode: value });
const samples = [
    ['cursor.visibility', [['\x1b[?25l', control(mode(0, false), false)], ['\x1b[?25h', control()]]],
    ...[[0, 0, true], [1, 0, true], [2, 0, false], [3, 2, true], [4, 2, false], [5, 1, true], [6, 1, false]]
        .map(([value, shape, blink]) => [`cursor.style.${value}`, [[`\x1b[${value} q`, control(mode(shape, blink))]]]),
    ['cursor.style.invalid', [['\x1b[7 q', control()]]],
];
const frames = () => stateFrames('cursor', 'NTILDE_CS_', initial(), samples, () => control(),
    nonce => stateScreen(nonce, [], { control: '', mode: mode(2, true, true) }));

test('nine cursor experiments derive only from stored modes and complete viewport state', () => {
    const report = buildCursorReport(frames(), runId);
    assert.equal(report.snapshots.length, 22);
    assert.deepEqual(report.observations.map(value => value.featureId), samples.map(([id]) => id));
    assert.ok(report.observations.every(value => value.result === 'passed'));
});

for (const [name, change, result] of [
    ['visibility did not change', f => { f[3].screen.cursorVisible = true; }, 'failed'],
    ['wrong style', f => { f[6].screen.cursorMode.style = 1; }, 'failed'],
    ['wrong control mode', f => { f[2].screen.cursorMode.blink = true; }, 'inconclusive'],
]) test(name, () => {
    const input = frames(); change(input); rehash(input);
    const index = name === 'wrong style' ? 1 : 0;
    assert.equal(buildCursorReport(input, runId).observations[index].result, result);
});

test('unpristine baseline, missing modes and failed mode restoration are rejected', () => {
    for (const mutate of [f => { f[0].screen.lines[0] = 'other content'; }, f => { delete f[3].screen.cursorMode; },
        f => { f.at(-1).screen.cursorMode.isDefault = false; }]) {
        const input = frames(); mutate(input); rehash(input); assert.throws(() => buildCursorReport(input, runId));
    }
    assert.equal(pristineCursorBaseline(initial()), true);
    assert.equal(pristineCursorBaseline({ ...initial(), cursorVisible: false }), false);
});

test('cursor writes require pristine ownership and always attempt pristine DECSTR cleanup after entry', async () => {
    const writes = [], reads = [];
    await collectCursor({ config: { runId }, owner: async kind => { reads.push(kind); return { screen: initial() }; },
        fence: { write: async value => { writes.push(value); return '\x1b[?62;4;22c'; } } });
    assert.ok(writes[0].endsWith('NTILDE_CS_' + runId));
    assert.equal(reads.filter(kind => kind === 'cursor-start').length, 1);
    let offset = 1;
    for (const [, steps] of samples) { assert.ok(writes[offset++].includes('\x1b[2 q\x1b[?25h'));
        assert.deepEqual(writes.slice(offset, offset + steps.length), steps.map(([sequence]) => sequence)); offset += steps.length; }
    assert.equal(writes.at(-1), '\x1b[?1049l\x1b[!p');
    const blocked = [];
    await assert.rejects(collectCursor({ config: { runId }, owner: async () => ({ screen: control() }),
        fence: { write: async value => blocked.push(value) } }), /pristine/);
    assert.deepEqual(blocked, []);
});

test('known initial budget refusal performs no reset, unrelated errors remain fatal', async () => {
    const writes = [];
    const result = await collectCursor({ config: { runId }, owner: async () => { throw Object.assign(new Error('budget'), { code: 'screenDetailsUnavailable' }); },
        fence: { write: async value => writes.push(value) } });
    assert.equal(result.status, 'unavailable'); assert.deepEqual(writes, []);
    await assert.rejects(collectCursor({ config: { runId }, owner: async () => { throw new Error('wrong pane'); } }), /wrong pane/);
});
