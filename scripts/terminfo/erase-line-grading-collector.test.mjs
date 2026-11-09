import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptEraseLineCollector, eraseLineAdapterId, eraseLineSeams } from './erase-line-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => eraseLineSeams.join('\n/* gap */\n') + '\n';

test('derives the erase-in-line extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptEraseLineCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeEraseLineAdapter = Object.freeze({\n  id: "${eraseLineAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunEraseLineTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["erase.line.right","erase.line.left","erase.line.all"]'));
    assert.equal(adaptEraseLineCollector(source), derived);
});

test('the seams pin exactly the three registrations, the termless helpers and the capture-only pixel helper', () => {
    assert.ok(eraseLineSeams.some(seam => seam.includes('probe("erase.line.right"') && seam.includes('"\\x1B[K"') && seam.includes('false, 2)')));
    assert.ok(eraseLineSeams.some(seam => seam.includes('probe("erase.line.left"') && seam.includes('"\\x1B[1K"') && seam.includes('false, 2)')));
    assert.ok(eraseLineSeams.some(seam => seam.includes('probe("erase.line.all"') && seam.includes('"\\x1B[2K"') && seam.includes('KEEP!\\x1B[1;3H')));
    for (const helper of ['function isBlank(', 'function rowCells(', 'function eraseRowResult(', 'async function captureEraseFixture(']) {
        assert.ok(eraseLineSeams.some(seam => seam.startsWith(helper)), helper);
    }
    // The pixel path is pinned too, so a later upstream change that gave it a verdict would be refused.
    const pixel = eraseLineSeams.find(seam => seam.startsWith('async function captureEraseFixture('));
    for (const part of ['outcome: "inconclusive"', 'reason: "insufficient-evidence"', 'evidence: "pixels"']) assert.ok(pixel.includes(part), part);
    assert.ok(pixel.includes('pass: false'));
    assert.ok(pixel.includes('capture-only assertion; no erase support judgment'));
    assert.ok(!eraseLineSeams.some(seam => /erase\.(screen|display|characters)/.test(seam)), 'other erase cases are outside this batch');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of eraseLineSeams) {
        assert.throws(() => adaptEraseLineCollector(verbatim().split(seam).join('CHANGED')), /Pinned erase-in-line probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptEraseLineCollector(verbatim() + seam + '\n'), /Pinned erase-in-line probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptEraseLineCollector(''), /Pinned erase-in-line probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria or notes were substituted', () => {
    const edits = [
        ['"\\x1B[K", ["A", "B"', '"\\x1B[0K", ["A", "B"'],                       // omitted spelling replaced by explicit 0
        ['"\\x1B[1K", ["blank"', '"\\x1B[0K", ["blank"'],                         // EL 1 swapped for EL 0
        ['"\\x1B[2K", ["blank"', '"\\x1B[3K", ["blank"'],                         // EL 2 swapped
        ['["A", "B", "blank", "blank", "blank"]', '["A", "B", "C", "blank", "blank"]'],
        ['["blank", "blank", "blank", "D", "E"]', '["blank", "blank", "C", "D", "E"]'],
        ['KEEP!\\x1B[1;3H', 'KEEP!\\x1B[1;4H'],                                   // setup cursor moved
        ['false, 2)', 'false, 3)'],                                               // expected cursor column
        ['ctx.feed(setup);', 'ctx.feed(setup + "x");'],
        ['ctx.feed(sequence);', 'ctx.feed("\\x1B[2J");'],
        ['const neighborPreserved = !adjacentRow || neighborAfter?.join("") === "KEEP!";', 'const neighborPreserved = true;'],
        ['const supported = exact && neighborPreserved;', 'const supported = true;'],
        ['return char === "" || char === " ";', 'return true;'],
        ['Erase fixture, cursor setup, or cell metadata unavailable', 'unavailable'],
        ['Measured geometry cannot fit the erase fixture', 'too small'],
        ['pass: false,\n      response: observed,', 'pass: true,\n      response: observed,'],           // the pixel path gaining a verdict
        ['capture-only assertion; no erase support judgment', 'verdict'],
    ];
    for (const [from, to] of edits) {
        const hits = eraseLineSeams.filter(seam => seam.includes(from));
        assert.ok(hits.length >= 1, from);
        const mutated = eraseLineSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptEraseLineCollector(mutated), /Pinned erase-in-line probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeSgrClosureAdapterLater = {};\n';
    const layered = adaptEraseLineCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(eraseLineAdapterId));
});

// The real pinned bundle, derived through all eleven layers and EXECUTED over synthetic observed cells: the pinned
// eraseRowResult code must agree with the independent replay and contract. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const haveBundle = existsSync(bundleUrl);
test('the real pinned bundle derives through every layer and its eraseRowResult agrees with the replay', { skip: haveBundle ? false : 'collector cache not installed' }, async () => {
    const { createHash } = await import('node:crypto');
    const { adaptCollector, upstreamBundleSha256 } = await import('./collector.mjs');
    const layers = await Promise.all([
        import('./grading-collector.mjs').then(m => m.adaptGradingCollector),
        import('./sgr-colors-grading-collector.mjs').then(m => m.adaptSgrColorsCollector),
        import('./sgr-rgb-grading-collector.mjs').then(m => m.adaptSgrRgbCollector),
        import('./sgr-attrs-grading-collector.mjs').then(m => m.adaptSgrAttrsCollector),
        import('./sgr-palette-grading-collector.mjs').then(m => m.adaptSgrPaletteCollector),
        import('./sgr-truecolor-grading-collector.mjs').then(m => m.adaptSgrTruecolorCollector),
        import('./sgr-underline-grading-collector.mjs').then(m => m.adaptSgrUnderlineCollector),
        import('./sgr-underline-color-grading-collector.mjs').then(m => m.adaptSgrUnderlineColorCollector),
        import('./sgr-reset-grading-collector.mjs').then(m => m.adaptSgrResetCollector),
        import('./sgr-closure-grading-collector.mjs').then(m => m.adaptSgrClosureCollector),
    ]);
    const { replayEraseLinePinned } = await import('./erase-line-grading.mjs');
    const { eraseLineRecordingContext } = await import('./erase-line-collector.mjs');
    const { eraseLineContracts } = await import('./erase-line-contracts.mjs');
    const { syntheticEraseLineScreens, syntheticEraseLineReads, rowOf } = await import('./fixtures/erase-line-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    const owner = new URL('./ownership.mjs', import.meta.url).pathname;
    let text = adaptCollector(source.toString(), owner);
    for (const adapt of [...layers, adaptEraseLineCollector]) text = adapt(text);
    // Written beside the bundle so its package imports resolve, exactly as the runner places derived bundles.
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-erln-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeEraseLineAdapter.id, eraseLineAdapterId);
        assert.deepEqual([...module.ntildeEraseLineAdapter.probeIds], ['erase.line.right', 'erase.line.left', 'erase.line.all']);
        const modes = { 'erase.line.right': 'AB   ', 'erase.line.left': '   DE', 'erase.line.all': '     ' };
        const variants = [
            [() => ({}), 'supported'],
            [reads => { reads.erased = reads.setup; }, 'unsupported'],                                    // no-op
            [(reads, id) => { reads.erased = { rows: [rowOf(id === 'erase.line.left' ? 'AB   ' : '   DE'), ...(id === 'erase.line.all' ? [rowOf('KEEP!')] : [])], cursor: [0, 2] }; }, 'unsupported'], // wrong direction
            [reads => { reads.setup = { ...reads.setup, cursor: [0, 3] }; }, id => (id === 'erase.line.all' ? 'supported' : 'inconclusive')], // the pinned all case never reads the cursor
            [reads => { reads.setup = { rows: [rowOf('ABCDX'), ...reads.setup.rows.slice(1)], cursor: [0, 2] }; }, 'inconclusive'],
        ];
        for (const contract of eraseLineContracts) {
            for (const [edit, expectedOf] of variants) {
                const readsOf = id => { const reads = syntheticEraseLineReads(id); if (id === contract.caseId) edit(reads, id); return reads; };
                const expected = typeof expectedOf === 'function' ? expectedOf(contract.caseId) : expectedOf;
                const screens = syntheticEraseLineScreens(contract.caseId, undefined, readsOf);
                const context = eraseLineRecordingContext(contract, screens);
                const live = module.ntildeRunEraseLineTermless(contract.caseId, context);
                const replay = replayEraseLinePinned(contract, screens);
                assert.equal(context.fedCount, 2);
                assert.equal(live.observation.outcome, expected, `${contract.caseId} ${modes[contract.caseId]}`);
                assert.equal(live.pass, replay.pass);
                assert.equal(live.response, replay.response);
                assert.equal(live.note ?? null, replay.note ?? null);
                assert.deepEqual(live.observation, replay.observation);
                assert.deepEqual(live.assertions ?? [], replay.assertions ?? []);
            }
        }
    } finally { rmSync(file, { force: true }); }
});
