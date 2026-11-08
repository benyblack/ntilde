import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptSgrResetCollector, sgrResetAdapterId, sgrResetSeams } from './sgr-reset-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => sgrResetSeams.join('\n/* gap */\n') + '\n';

test('derives the selective-reset extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptSgrResetCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeSgrResetAdapter = Object.freeze({\n  id: "${sgrResetAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunSgrResetTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["sgr.selective-reset.underline","sgr.selective-reset.inverse"]'));
    assert.equal(adaptSgrResetCollector(source), derived);
});

test('the seams pin exactly the two registrations and the complete measuredReset helpers, nothing for other reset ids', () => {
    assert.ok(sgrResetSeams.some(seam => seam.includes('resetProbe("sgr.selective-reset.underline", "\\x1B[1;4m", "\\x1B[24m", ["underline"], ["bold"])')));
    assert.ok(sgrResetSeams.some(seam => seam.includes('resetProbe("sgr.selective-reset.inverse", "\\x1B[1;7m", "\\x1B[27m", ["inverse"], ["bold"])')));
    for (const helper of ['function measuredAttribute(', 'function measuredReset(', 'function resetProbe(']) {
        assert.ok(sgrResetSeams.some(seam => seam.startsWith(helper)), helper);
    }
    assert.ok(!sgrResetSeams.some(seam => /selective-reset\.(bold|italic)|"sgr\.reset"/.test(seam)), 'the bold, italic and full-reset cases are outside this batch');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of sgrResetSeams) {
        assert.throws(() => adaptSgrResetCollector(verbatim().split(seam).join('CHANGED')), /Pinned SGR selective-reset probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptSgrResetCollector(verbatim() + seam + '\n'), /Pinned SGR selective-reset probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptSgrResetCollector(''), /Pinned SGR selective-reset probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria or notes were substituted', () => {
    const edits = [
        ['"\\x1B[1;4m", "\\x1B[24m"', '"\\x1B[1;4m", "\\x1B[0m"'],            // SGR 24 swapped for SGR 0
        ['"\\x1B[1;7m", "\\x1B[27m"', '"\\x1B[1;7m", "\\x1B[28m"'],            // SGR 27 swapped
        ['["underline"], ["bold"]', '["underline"], ["bold", "dim"]'],
        ['["inverse"], ["bold"]', '["inverse"], []'],
        ['ctx.feed(`C${setup}X${reset}Y`);', 'ctx.feed(`C${setup}X${reset}Z`);'],       // fixture shape
        ['measuredAttribute(after, attribute) === null', 'measuredAttribute(after, attribute) === 0'],
        ['const cleared = set.every((attribute) => measuredAttribute(after, attribute) === false);', 'const cleared = true;'],
        ['const retained = preserve.every((attribute) => measuredAttribute(after, attribute) === true);', 'const retained = true;'],
        ['return value !== false && value !== null && value !== "none";', 'return true;'],
        ['Baseline, styled, or reset cell was not exposed', 'not exposed'],
        ['Neutral baseline, styled setup, or reset readback was not measured', 'not measured'],
        ['measured setup attributes clear on Y and unrelated attributes remain set', 'cleared'],
    ];
    for (const [from, to] of edits) {
        const hits = sgrResetSeams.filter(seam => seam.includes(from));
        assert.ok(hits.length >= 1, from);
        const mutated = sgrResetSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptSgrResetCollector(mutated), /Pinned SGR selective-reset probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeSgrUnderlineColorAdapter = {};\n';
    const layered = adaptSgrResetCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(sgrResetAdapterId));
});

// The real pinned bundle, derived through all ten layers and EXECUTED over synthetic observed cells: the pinned
// measuredReset code must agree with the independent replay and contract. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const haveBundle = existsSync(bundleUrl);
test('the real pinned bundle derives through every layer and its measuredReset agrees with the replay', { skip: haveBundle ? false : 'collector cache not installed' }, async () => {
    const { createHash } = await import('node:crypto');
    const { adaptCollector, upstreamBundleSha256 } = await import('./collector.mjs');
    const { adaptGradingCollector } = await import('./grading-collector.mjs');
    const { adaptSgrColorsCollector } = await import('./sgr-colors-grading-collector.mjs');
    const { adaptSgrRgbCollector } = await import('./sgr-rgb-grading-collector.mjs');
    const { adaptSgrAttrsCollector } = await import('./sgr-attrs-grading-collector.mjs');
    const { adaptSgrPaletteCollector } = await import('./sgr-palette-grading-collector.mjs');
    const { adaptSgrTruecolorCollector } = await import('./sgr-truecolor-grading-collector.mjs');
    const { adaptSgrUnderlineCollector } = await import('./sgr-underline-grading-collector.mjs');
    const { adaptSgrUnderlineColorCollector } = await import('./sgr-underline-color-grading-collector.mjs');
    const { replaySgrResetPinned } = await import('./sgr-reset-grading.mjs');
    const { sgrResetRecordingContext } = await import('./sgr-reset-collector.mjs');
    const { sgrResetContracts } = await import('./sgr-reset-contracts.mjs');
    const { syntheticSgrResetScreens, syntheticSgrResetReads, styled } = await import('./fixtures/sgr-reset-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    const sourceText = source.toString();
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    const owner = new URL('./ownership.mjs', import.meta.url).pathname;
    let text = adaptCollector(sourceText, owner);
    for (const adapt of [adaptGradingCollector, adaptSgrColorsCollector, adaptSgrRgbCollector, adaptSgrAttrsCollector,
        adaptSgrPaletteCollector, adaptSgrTruecolorCollector, adaptSgrUnderlineCollector, adaptSgrUnderlineColorCollector, adaptSgrResetCollector]) {
        text = adapt(text);
    }
    // Written beside the bundle so its package imports resolve, exactly as the runner places derived bundles.
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-sgsr-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeSgrResetAdapter.id, sgrResetAdapterId);
        const variants = [
            [id => syntheticSgrResetReads(id), 'supported'],
            [id => { const reads = syntheticSgrResetReads(id); reads.pinned[2] = id.endsWith('underline') ? styled('Y', ['bold'], { style: 1 }) : styled('Y', ['bold', 'inverse']); return reads; }, 'unsupported'],
            [id => { const reads = syntheticSgrResetReads(id); reads.pinned[2] = styled('Y', []); return reads; }, 'unsupported'],
            [id => { const reads = syntheticSgrResetReads(id); reads.pinned[1] = styled('X', ['bold']); return reads; }, 'inconclusive'],
        ];
        for (const contract of sgrResetContracts) {
            for (const [readsOf, expected] of variants) {
                const screens = syntheticSgrResetScreens(contract.caseId, undefined, readsOf);
                const live = module.ntildeRunSgrResetTermless(contract.caseId, sgrResetRecordingContext(contract, screens));
                const replay = replaySgrResetPinned(contract, screens);
                assert.equal(live.observation.outcome, expected, contract.caseId);
                assert.equal(live.pass, replay.pass);
                assert.equal(live.response, replay.response);
                assert.deepEqual(live.observation, replay.observation);
            }
        }
    } finally { rmSync(file, { force: true }); }
});
