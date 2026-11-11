import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptRoutedInputCollector, routedInputAdapterId, routedInputSeams } from './routed-input-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => routedInputSeams.join('\n/* gap */\n') + '\n';

test('derives the routed-input extension by appending exports only', () => {
    const source = verbatim(), derived = adaptRoutedInputCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeRoutedInputAdapter = Object.freeze({\n  id: "${routedInputAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunRoutedInputTermless(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify(['input.x10-mouse', 'input.button-event-mouse', 'modes.application-keypad'])));
});

test('the seams pin both mouse registrations, the keypad registration with its terminal path, the helpers and the previous layer', () => {
    const keypad = routedInputSeams.find(seam => seam.includes('probe("modes.application-keypad"'));
    for (const part of ['ctx.feed("\\x1B=");', 'ctx.getMode("applicationKeypad") === true', 'ctx.feed("\\x1B>");', 'ctx.getMode("applicationKeypad") === false',
        'notTestedResult("keypad input"', 'outcome: "inconclusive"', 'evidence: "none"']) assert.ok(keypad.includes(part), part);
    const helper = routedInputSeams.find(seam => seam.startsWith('function mouseInputProbe('));
    for (const part of ['ctx.getMode(modeName) === true', 'outcome: "inconclusive"', 'evidence: "legacy"', 'evidence: "none"']) assert.ok(helper.includes(part), part);
    assert.ok(routedInputSeams.includes('    mouseInputProbe("input.x10-mouse", "mouseTracking"),'));
    assert.ok(routedInputSeams.includes('    mouseInputProbe("input.button-event-mouse", "mouseTracking")'));
    assert.ok(routedInputSeams.some(seam => seam.startsWith('function notTestedResult(')));
    assert.ok(routedInputSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of routedInputSeams) {
        assert.throws(() => adaptRoutedInputCollector(verbatim().split(seam).join('CHANGED')), /Pinned routed-input probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptRoutedInputCollector(verbatim() + seam + '\n'), /Pinned routed-input probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would gain a verdict or change its bytes', () => {
    for (const [from, to] of [['notTestedResult("keypad input"', 'parserStateResult(true, "keypad input"'], ['ctx.feed("\\x1B=");', 'ctx.feed("\\x1B=\\x1B[?66h");'],
        ['outcome: "inconclusive",\n      reason', 'outcome: "supported",\n      reason'], ['ctx.getMode(modeName) === true', 'ctx.getMode(modeName) === false']]) {
        assert.ok(routedInputSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptRoutedInputCollector(routedInputSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through every layer and the pinned code runs over served modes', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const { adaptCollector } = await import('./collector.mjs');
    const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins'];
    const layers = await Promise.all(names.map(name => import(name === 'grading' ? './grading-collector.mjs' : './' + name + '-grading-collector.mjs')
        .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
    const { replayRoutedInputPinned, routedInputRecordingContext, routedInputCaseIds, servedModes } = await import('./routed-input-contracts.mjs');
    const { collectRoutedInputPinned } = await import('./routed-input-collector.mjs');
    const { syntheticRoutedInputEvidence } = await import('./fixtures/routed-input-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    for (const adapt of [...layers, adaptRoutedInputCollector]) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-rin-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeRoutedInputAdapter.probeIds], routedInputCaseIds);
        for (const served of [{ on: true, off: false, currentMode: false }, { on: false, off: true, currentMode: true }, { on: undefined, off: undefined, currentMode: undefined }]) {
            for (const caseId of routedInputCaseIds) {
                const context = routedInputRecordingContext(caseId, served);
                const { probeId, ...live } = module.ntildeRunRoutedInputTermless(caseId, context);
                assert.equal(probeId, caseId);
                assert.deepEqual(live, replayRoutedInputPinned(caseId, served), caseId);
                assert.equal(live.observation?.outcome ?? 'inconclusive', 'inconclusive', 'the pinned probe never decides');
            }
        }
        const evidence = syntheticRoutedInputEvidence();
        const controls = {};
        for (const frame of evidence.frames) if (frame.kind === 'input-control') controls[frame.caseId] = frame.capture.hex;
        const run = collectRoutedInputPinned({ geometry: { rows: 12, cols: 120 }, controls, restored: evidence.frames.at(-1).capture.hex, gradingModule: module });
        assert.deepEqual(run, evidence.run, 'the real pinned code and the synthetic transliteration agree on the same child replies');
        assert.equal(servedModes('modes.application-keypad', controls, '', undefined).on, true);
    } finally { rmSync(file, { force: true }); }
});
