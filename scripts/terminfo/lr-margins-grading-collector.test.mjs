import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptLrMarginsCollector, lrMarginsAdapterId, lrMarginsSeams } from './lr-margins-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => lrMarginsSeams.join('\n/* gap */\n') + '\n';

test('derives the left/right-margin extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptLrMarginsCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeLrMarginsAdapter = Object.freeze({\n  id: "${lrMarginsAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunLrMarginsTermless(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify(['modes.left-right-margin'])));
});

test('the seams pin the registration whose termless and terminal paths are both inconclusive, parserStateResult and the previous layer', () => {
    const registration = lrMarginsSeams.find(seam => seam.includes('probe("modes.left-right-margin"'));
    for (const part of ['ctx.feed("\\x1B[?69h");', 'ctx.getMode("leftRightMargin") === true', 'ctx.feed("\\x1B[?69l");', 'return parserStateResult(null,',
        'outcome: "inconclusive"', 'evidence: "none"']) assert.ok(registration.includes(part), part);
    assert.ok(lrMarginsSeams.some(seam => seam.startsWith('function parserStateResult(')));
    assert.ok(lrMarginsSeams.includes('export const ntildeRectChecksumAdapter = Object.freeze({'), 'derived from the rectangle-checksum layer');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of lrMarginsSeams) {
        assert.throws(() => adaptLrMarginsCollector(verbatim().split(seam).join('CHANGED')), /Pinned left\/right-margin probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptLrMarginsCollector(verbatim() + seam + '\n'), /Pinned left\/right-margin probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would gain a verdict or change its bytes', () => {
    for (const [from, to] of [['return parserStateResult(null,', 'return parserStateResult(pass,'], ['ctx.feed("\\x1B[?69h");', 'ctx.feed("\\x1B[?69h\\x1B[3;7s");'],
        ['outcome: "inconclusive",\n        reason', 'outcome: "supported",\n        reason']]) {
        assert.ok(lrMarginsSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptLrMarginsCollector(lrMarginsSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through every layer and its code agrees with the replay', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const { adaptCollector } = await import('./collector.mjs');
    const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum'];
    const layers = await Promise.all(names.map(name => import(name === 'grading' ? './grading-collector.mjs' : './' + name + '-grading-collector.mjs')
        .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
    const { replayLrMarginsPinned } = await import('./lr-margins-grading.mjs');
    const { lrMarginsRecordingContext } = await import('./lr-margins-collector.mjs');
    const { lrMarginsContracts } = await import('./lr-margins-contracts.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    for (const adapt of [...layers, adaptLrMarginsCollector]) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-lrmm-test-${process.pid}.mjs`, import.meta.url));
    const contract = lrMarginsContracts[0];
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        for (const mode of [true, false]) {
            const screens = contract.roles.map(() => ({ horizontalMargins: { leftRightMarginMode: mode, leftMargin: 0, rightMargin: 59 } }));
            const context = lrMarginsRecordingContext(contract, screens);
            const { probeId, ...live } = module.ntildeRunLrMarginsTermless(contract.caseId, context);
            assert.equal(probeId, contract.caseId);
            assert.equal(context.fedCount, 2);
            assert.deepEqual(live, replayLrMarginsPinned(contract, screens));
            assert.equal(live.observation.outcome, 'inconclusive', 'the pinned probe never decides');
            assert.equal(live.response, JSON.stringify({ mode }));
        }
    } finally { rmSync(file, { force: true }); }
});
