import assert from 'node:assert/strict';
import { test } from 'node:test';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptMexCollector, mexAdapterId, mexSeams } from './mex-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => mexSeams.join('\n/* gap */\n') + '\n';

test('derives the extended-mouse extension by appending exports only', () => {
    const source = verbatim(), derived = adaptMexCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeMexAdapter = Object.freeze({\n  id: "${mexAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunMexTermless(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify(['modes.utf8-mouse-1005', 'input.urxvt-mouse'])));
});

test('the seams pin the 1005 registration with its DECRQM capture, the urxvt registration, the helpers and the previous layer', () => {
    const utf8 = mexSeams.find(seam => seam.includes('decrpmModeProbe("modes.utf8-mouse-1005"'));
    for (const part of ['ctx.feed("\\x1B[?1005h");', 'ctx.feedCapture("\\x1B[?1005$p")', 'ctx.feed("\\x1B[?1005l");', 'response.includes("$y")',
        'notTestedResult("encoded UTF-8 mouse events", { response })', 'notTestedResult("encoded UTF-8 mouse events", { responsive: ok })']) assert.ok(utf8.includes(part), part);
    const decrpm = mexSeams.find(seam => seam.startsWith('function decrpmModeProbe('));
    for (const part of ['termless: termlessFn', 'decrpmResult(await ctx.queryMode(modeNum), modeNum)']) assert.ok(decrpm.includes(part), part);
    const helper = mexSeams.find(seam => seam.startsWith('function mouseInputProbe('));
    for (const part of ['ctx.getMode(modeName) === true', 'outcome: "inconclusive"', 'evidence: "legacy"', 'evidence: "none"']) assert.ok(helper.includes(part), part);
    assert.ok(mexSeams.includes('    mouseInputProbe("input.urxvt-mouse", "mouseTracking"),'));
    assert.ok(mexSeams.some(seam => seam.startsWith('function notTestedResult(')));
    assert.ok(mexSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of mexSeams) {
        assert.throws(() => adaptMexCollector(verbatim().split(seam).join('CHANGED')), /Pinned extended-mouse probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptMexCollector(verbatim() + seam + '\n'), /Pinned extended-mouse probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would gain a verdict or change its bytes', () => {
    for (const [from, to] of [['notTestedResult("encoded UTF-8 mouse events", { response })', 'parserStateResult(true, "encoded UTF-8 mouse events", { response })'],
        ['ctx.feed("\\x1B[?1005h");', 'ctx.feed("\\x1B[?1005h\\x1B[?1000h");'], ['ctx.feedCapture("\\x1B[?1005$p")', 'ctx.feedCapture("\\x1B[?1015$p")'],
        ['outcome: "inconclusive",\n      reason', 'outcome: "supported",\n      reason'], ['ctx.getMode(modeName) === true', 'ctx.getMode(modeName) === false']]) {
        assert.ok(mexSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptMexCollector(mexSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

testWithPinnedCollector('the real pinned bundle derives through every layer and the pinned code runs over served child replies', async bundleUrl => {
    const { adaptCollector } = await import('./collector.mjs');
    const { adaptGradingCollector } = await import('./grading-collector.mjs');
    const names = ['sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins'];
    const layers = await Promise.all(names.map(name => import('./' + name + '-grading-collector.mjs')
        .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
    const { replayMexPinned, mexRecordingContext, mexCaseIds, servedMex } = await import('./mex-contracts.mjs');
    const { collectMexPinned } = await import('./mex-collector.mjs');
    const { syntheticMexEvidence } = await import('./fixtures/mex-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    text = adaptGradingCollector(text);
    for (const adapt of [...layers, adaptMexCollector]) text = adapt(text);
    const file = fileURLToPath(new URL(`./ntilde-mex-test-${process.pid}.mjs`, bundleUrl));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeMexAdapter.probeIds], mexCaseIds);
        for (const served of [{ response: '\x1b[?1005;1$y', currentMode: false }, { response: '\x1b[?1005;2$y', currentMode: true }, { response: '\x1b[?1005;0$y', currentMode: undefined }]) {
            for (const caseId of mexCaseIds) {
                const context = mexRecordingContext(caseId, served);
                const { probeId, ...live } = module.ntildeRunMexTermless(caseId, context);
                assert.equal(probeId, caseId);
                assert.deepEqual(live, replayMexPinned(caseId, served), caseId);
                assert.equal(live.observation?.outcome ?? 'inconclusive', 'inconclusive', 'the pinned probe never decides');
            }
        }
        const evidence = syntheticMexEvidence();
        const controls = {};
        for (const frame of evidence.frames) if (frame.kind === 'input-control') controls[frame.caseId] = frame.capture.hex;
        const run = collectMexPinned({ geometry: { rows: 12, cols: 120 }, controls, restored: evidence.frames.at(-1).capture.hex, gradingModule: module });
        assert.deepEqual(run, evidence.run, 'the real pinned code and the synthetic transliteration agree on the same child replies');
        assert.equal(servedMex('modes.utf8-mouse-1005', controls, '').response, '\x1b[?1005;1$y');
    } finally { rmSync(file, { force: true }); }
});
