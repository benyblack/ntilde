import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptSgrClosureCollector, sgrClosureAdapterId, sgrClosureSeams } from './sgr-closure-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => sgrClosureSeams.join('\n/* gap */\n') + '\n';

test('derives the closure extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptSgrClosureCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeSgrClosureAdapter = Object.freeze({\n  id: "${sgrClosureAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunSgrClosureTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["sgr.blink","sgr.overline","sgr.underline.color"]'));
    assert.equal(adaptSgrClosureCollector(source), derived);
});

test('the seams pin the three registrations, the sgrProbe body and notTestedResult, and no sibling id', () => {
    assert.ok(sgrClosureSeams.some(seam => seam.includes('sgrProbe("sgr.blink", "\\x1B[5m", (cell) => cell.blink === true)')));
    assert.ok(sgrClosureSeams.some(seam => seam.includes('cell.overline === undefined ? "cell.overline field not exposed" : null')));
    assert.ok(sgrClosureSeams.includes('    rgbUnderlineProbe("sgr.underline.color"),'));
    assert.ok(sgrClosureSeams.some(seam => seam.startsWith('function sgrProbe(')));
    assert.ok(sgrClosureSeams.some(seam => seam.startsWith('function notTestedResult(')));
    assert.ok(!sgrClosureSeams.some(seam => /underline-color-rgb|selective-reset|sgr\.(faint|hidden|inverse)/.test(seam)));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of sgrClosureSeams) {
        assert.throws(() => adaptSgrClosureCollector(verbatim().split(seam).join('CHANGED')), /Pinned SGR closure probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptSgrClosureCollector(verbatim() + seam + '\n'), /Pinned SGR closure probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptSgrClosureCollector(''), /Pinned SGR closure probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria, guard or notes were substituted', () => {
    const edits = [
        ['"\\x1B[5m", (cell) => cell.blink === true', '"\\x1B[6m", (cell) => cell.blink === true'],            // SGR 5 -> SGR 6
        ['(cell) => cell.blink === true', '(cell) => !!cell.blink'],
        ['"\\x1B[53m"', '"\\x1B[55m"'],
        ['cell.overline === undefined ? null : cell.overline === true', 'cell.overline === true'],             // false-vs-unavailable collapsed
        ['"cell.overline field not exposed"', '"not exposed"'],
        ['rgbUnderlineProbe("sgr.underline.color")', 'rgbUnderlineProbe("sgr.underline-color-rgb")'],         // sibling id
        ['ctx.feed(sequence + "X");', 'ctx.feed(sequence + "Y");'],
        ['const measured = cell.char === "X" && check(cell) === true;', 'const measured = true;'],
        ['reason: "no-semantic-observable"', 'reason: "other"'],
        ['No rendered X cell to evaluate', 'none'],
        ['a negative needs rendering review', 'ok'],
    ];
    for (const [from, to] of edits) {
        assert.ok(sgrClosureSeams.some(seam => seam.includes(from)), from);
        const mutated = sgrClosureSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptSgrClosureCollector(mutated), /Pinned SGR closure probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeSgrResetAdapter = {};\n';
    const layered = adaptSgrClosureCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(sgrClosureAdapterId));
});

// The real pinned bundle, derived through all eleven layers and EXECUTED over synthetic observed cells: the pinned
// code must agree with the independent replay. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through every layer and its sgrProbe/rgb probes agree with the replay', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const { createHash } = await import('node:crypto');
    const { adaptCollector, upstreamBundleSha256 } = await import('./collector.mjs');
    const layers = await Promise.all([import('./grading-collector.mjs'), import('./sgr-colors-grading-collector.mjs'), import('./sgr-rgb-grading-collector.mjs'),
        import('./sgr-attrs-grading-collector.mjs'), import('./sgr-palette-grading-collector.mjs'), import('./sgr-truecolor-grading-collector.mjs'),
        import('./sgr-underline-grading-collector.mjs'), import('./sgr-underline-color-grading-collector.mjs'), import('./sgr-reset-grading-collector.mjs')]);
    const adapters = [layers[0].adaptGradingCollector, layers[1].adaptSgrColorsCollector, layers[2].adaptSgrRgbCollector, layers[3].adaptSgrAttrsCollector,
        layers[4].adaptSgrPaletteCollector, layers[5].adaptSgrTruecolorCollector, layers[6].adaptSgrUnderlineCollector, layers[7].adaptSgrUnderlineColorCollector,
        layers[8].adaptSgrResetCollector, adaptSgrClosureCollector];
    const { replaySgrClosurePinned } = await import('./sgr-closure-grading.mjs');
    const { sgrClosureRecordingContext } = await import('./sgr-closure-collector.mjs');
    const { sgrClosureContracts } = await import('./sgr-closure-contracts.mjs');
    const { syntheticSgrClosureScreens, syntheticSgrClosureReads, styled, plainCell } = await import('./fixtures/sgr-closure-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl).toString();
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    let text = adaptCollector(source, new URL('./ownership.mjs', import.meta.url).pathname);
    for (const adapt of adapters) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-sgcl-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeSgrClosureAdapter.id, sgrClosureAdapterId);
        const variants = [
            [id => syntheticSgrClosureReads(id), 'supported'],
            [id => { const reads = syntheticSgrClosureReads(id); if (id !== 'sgr.underline.color') reads.groups[1] = [plainCell('X')]; else reads.groups[0][2] = styled('X', [], { style: 1 }); return reads; }, null],
        ];
        for (const contract of sgrClosureContracts) {
            for (const [readsOf, expected] of variants) {
                const screens = syntheticSgrClosureScreens(contract.caseId, undefined, readsOf);
                const live = module.ntildeRunSgrClosureTermless(contract.caseId, sgrClosureRecordingContext(contract, screens));
                const replay = replaySgrClosurePinned(contract, screens);
                if (expected) assert.equal(live.observation.outcome, expected, contract.caseId);
                assert.equal(live.pass, replay.pass, contract.caseId);
                assert.equal(live.response, replay.response, contract.caseId);
                assert.deepEqual(live.observation, replay.observation, contract.caseId);
                assert.deepEqual(live.assertions?.length ? live.assertions : null, replay.assertions?.length ? replay.assertions : null, contract.caseId);
            }
        }
    } finally { rmSync(file, { force: true }); }
});
