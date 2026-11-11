import assert from 'node:assert/strict';
import { test } from 'node:test';
import { collectRoutedInputPinned } from './routed-input-collector.mjs';
import { fakeGradingModule, syntheticRoutedInputEvidence } from './fixtures/routed-input-evidence.mjs';

const inputs = () => {
    const evidence = syntheticRoutedInputEvidence(), controls = {};
    for (const frame of evidence.frames) if (frame.kind === 'input-control') controls[frame.caseId] = frame.capture.hex;
    return { geometry: { rows: 12, cols: 120 }, controls, restored: evidence.frames.at(-1).capture.hex, gradingModule: fakeGradingModule };
};
test('the pinned probes run over replies the child itself received and journal what they were served', () => {
    const run = collectRoutedInputPinned(inputs());
    assert.deepEqual(run.cases.map(item => item.caseId), ['input.x10-mouse', 'input.button-event-mouse', 'modes.application-keypad']);
    assert.deepEqual(run.cases[2].served, { on: true, off: false, sources: ['input.keypad.application.0', 'input.keypad.restore.deckpnm.enter'] });
    assert.deepEqual(run.cases[0].served, { currentMode: false, sources: ['restored'] });
    assert.deepEqual(run.cases[2].feeds, ['\x1b=', '\x1b>']);
});
test('a pinned body that feeds or reads more than its contract is refused', () => {
    const extra = { ...fakeGradingModule, ntildeRunRoutedInputTermless: (probeId, ctx) => { ctx.feed('\x1b[?1000h'); return fakeGradingModule.ntildeRunRoutedInputTermless(probeId, ctx); } };
    assert.throws(() => collectRoutedInputPinned({ ...inputs(), gradingModule: extra }), /differs from the contract/);
    const reads = { ...fakeGradingModule, ntildeRunRoutedInputTermless: (probeId, ctx) => ({ probeId, ...{ pass: false, response: String(ctx.getMode('altScreen')) } }) };
    assert.throws(() => collectRoutedInputPinned({ ...inputs(), gradingModule: reads }), /uncontracted mode/);
    const lazy = { ...fakeGradingModule, ntildeRunRoutedInputTermless: probeId => ({ probeId, pass: false, response: '{}' }) };
    assert.throws(() => collectRoutedInputPinned({ ...inputs(), gradingModule: lazy }), /complete fixture/);
});
test('an adapter that does not name the contracted probes is refused', () => {
    assert.throws(() => collectRoutedInputPinned({ ...inputs(), gradingModule: { ...fakeGradingModule, ntildeRoutedInputAdapter: { id: 'x', probeIds: ['modes.application-keypad'] } } }), /does not name/);
});
