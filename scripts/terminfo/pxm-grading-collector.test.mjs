import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptPxmCollector, pxmAdapterId, pxmSeams } from './pxm-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => pxmSeams.join('\n/* gap */\n') + '\n';

test('derives the SGR-pixel extension by appending exports only', () => {
    const source = verbatim(), derived = adaptPxmCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildePxmAdapter = Object.freeze({\n  id: "${pxmAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunPxmTermless(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify(['input.pixel-mouse'])));
});

test('the seams pin the pixel registration, the mouseInputProbe helper, the absent-event note and the previous layer', () => {
    const helper = pxmSeams.find(seam => seam.startsWith('function mouseInputProbe('));
    for (const part of ['ctx.getMode(modeName) === true', 'outcome: "inconclusive"', 'evidence: "legacy"', 'evidence: "none"']) assert.ok(helper.includes(part), part);
    assert.ok(pxmSeams.includes('    mouseInputProbe("input.pixel-mouse", "pixelMouse"),'));
    assert.ok(pxmSeams.some(seam => seam.startsWith('var absentEventNote = "No generated input event and encoded report were validated"')));
    assert.ok(pxmSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of pxmSeams) {
        assert.throws(() => adaptPxmCollector(verbatim().split(seam).join('CHANGED')), /Pinned SGR-pixel probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptPxmCollector(verbatim() + seam + '\n'), /Pinned SGR-pixel probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would gain a verdict or change its mode read', () => {
    for (const [from, to] of [['outcome: "inconclusive",\n      reason', 'outcome: "supported",\n      reason'], ['ctx.getMode(modeName) === true', 'ctx.getMode(modeName) === false'],
        ['"pixelMouse"', '"mouseTracking"']]) {
        assert.ok(pxmSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptPxmCollector(pxmSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through every layer and the pinned code runs over served child replies', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const { adaptCollector } = await import('./collector.mjs');
    const { adaptGradingCollector } = await import('./grading-collector.mjs');
    const names = ['sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins'];
    const layers = await Promise.all(names.map(name => import('./' + name + '-grading-collector.mjs')
        .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
    const { replayPxmPinned, pxmRecordingContext, pxmCaseIds } = await import('./pxm-contracts.mjs');
    const { collectPxmPinned } = await import('./pxm-collector.mjs');
    const { syntheticPxmEvidence } = await import('./fixtures/pxm-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    text = adaptGradingCollector(text);
    for (const adapt of [...layers, adaptPxmCollector]) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-pxm-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildePxmAdapter.probeIds], pxmCaseIds);
        for (const served of [{ currentMode: false }, { currentMode: true }, { currentMode: undefined }]) {
            for (const caseId of pxmCaseIds) {
                const context = pxmRecordingContext(caseId, served);
                const { probeId, ...live } = module.ntildeRunPxmTermless(caseId, context);
                assert.equal(probeId, caseId);
                assert.deepEqual(live, replayPxmPinned(caseId, served), caseId);
                assert.equal(live.observation?.outcome, 'inconclusive', 'the pinned probe never decides');
                assert.equal(context.fedCount, 0);
            }
        }
        const evidence = syntheticPxmEvidence(), controls = {};
        for (const frame of evidence.frames) if (frame.kind === 'input-control') controls[frame.caseId] = frame.capture.hex;
        const run = collectPxmPinned({ geometry: { rows: 30, cols: 120 }, controls, restored: evidence.frames.at(-1).capture.hex, gradingModule: module });
        assert.deepEqual(run, evidence.run, 'the real pinned code and the synthetic transliteration agree on the same child replies');
    } finally { rmSync(file, { force: true }); }
});
