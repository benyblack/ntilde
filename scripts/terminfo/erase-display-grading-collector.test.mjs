import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptEraseDisplayCollector, eraseDisplayAdapterId, eraseDisplaySeams } from './erase-display-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => eraseDisplaySeams.join('\n/* gap */\n') + '\n';

test('derives the erase-in-display extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptEraseDisplayCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeEraseDisplayAdapter = Object.freeze({\n  id: "${eraseDisplayAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunEraseDisplayTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["erase.screen.below","erase.screen.above","erase.screen.all"]'));
    assert.equal(adaptEraseDisplayCollector(source), derived);
});

test('the seams pin exactly the three registrations, the termless helpers and the capture-only pixel helper', () => {
    assert.ok(eraseDisplaySeams.some(seam => seam.includes('probe("erase.screen.below"') && seam.includes('"\\x1B[0J"') && seam.includes('["AAAAA", "BB   ", "     "]')));
    assert.ok(eraseDisplaySeams.some(seam => seam.includes('probe("erase.screen.above"') && seam.includes('"\\x1B[1J"') && seam.includes('["     ", "   BB", "CCCCC"]')));
    assert.ok(eraseDisplaySeams.some(seam => seam.includes('probe("erase.screen.all"') && seam.includes('"\\x1B[2J"') && seam.includes('["     ", "     ", "     "]')));
    for (const helper of ['function isBlank(', 'function rowCells(', 'function eraseScreenResult(', 'async function captureEraseFixture(']) {
        assert.ok(eraseDisplaySeams.some(seam => seam.startsWith(helper)), helper);
    }
    const pixel = eraseDisplaySeams.find(seam => seam.startsWith('async function captureEraseFixture('));
    for (const part of ['outcome: "inconclusive"', 'reason: "insufficient-evidence"', 'evidence: "pixels"', 'pass: false', 'capture-only assertion; no erase support judgment']) {
        assert.ok(pixel.includes(part), part);
    }
    // ED 3 (saved lines) is a different case and is not a seam of this batch.
    assert.ok(!eraseDisplaySeams.some(seam => seam.includes('erase.screen.scrollback') || seam.includes('x1B[3J')), 'ED 3 is outside this batch');
    assert.ok(!eraseDisplaySeams.some(seam => /erase\.(line|display|characters)\./.test(seam)));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of eraseDisplaySeams) {
        assert.throws(() => adaptEraseDisplayCollector(verbatim().split(seam).join('CHANGED')), /Pinned erase-in-display probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptEraseDisplayCollector(verbatim() + seam + '\n'), /Pinned erase-in-display probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptEraseDisplayCollector(''), /Pinned erase-in-display probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria or notes were substituted', () => {
    const edits = [
        ['"\\x1B[0J", ["AAAAA"', '"\\x1B[J", ["AAAAA"'],                                // explicit 0 replaced by the omitted spelling
        ['"\\x1B[1J", ["     "', '"\\x1B[0J", ["     "'],                               // ED 1 swapped for ED 0
        ['"\\x1B[2J", ["     "', '"\\x1B[3J", ["     "'],                               // ED 2 swapped for ED 3
        ['["AAAAA", "BB   ", "     "]', '["AAAAA", "BBB  ", "     "]'],
        ['["     ", "   BB", "CCCCC"]', '["     ", "  BBB", "CCCCC"]'],
        ['\\x1B[1;1HAAAAA\\x1B[${middle + 1};1HBBBBB', '\\x1B[1;1HAAAAA\\x1B[${middle + 2};1HBBBBB'],
        ['const middle = Math.floor(lines / 2);', 'const middle = Math.floor(lines / 3);'],
        ['cursorBefore.x === 2 && cursorBefore.y === middle', 'cursorBefore.x === 3 && cursorBefore.y === middle'],
        ['const preservedCursor = cursorAfter.x === cursorBefore.x && cursorAfter.y === cursorBefore.y;', 'const preservedCursor = true;'],
        ['const supported = cellsMatch && preservedCursor;', 'const supported = true;'],
        ['ctx.feed(sequence);', 'ctx.feed("\\x1B[2J");'],
        ['return char === "" || char === " ";', 'return true;'],
        ['Screen fixture, cursor setup, or cell readback unavailable', 'unavailable'],
        ['Screen fixture needs at least three measured rows and six columns', 'too small'],
        ['pass: false,\n      response: observed,', 'pass: true,\n      response: observed,'],           // the pixel path gaining a verdict
        ['capture-only assertion; no erase support judgment', 'verdict'],
    ];
    for (const [from, to] of edits) {
        assert.ok(eraseDisplaySeams.some(seam => seam.includes(from)), from);
        const mutated = eraseDisplaySeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptEraseDisplayCollector(mutated), /Pinned erase-in-display probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeEraseLineAdapterLater = {};\n';
    const layered = adaptEraseDisplayCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(eraseDisplayAdapterId));
});

// The real pinned bundle, derived through all twelve layers and EXECUTED over synthetic observed cells: the pinned
// eraseScreenResult code must agree with the independent replay and contract. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const haveBundle = existsSync(bundleUrl);
test('the real pinned bundle derives through every layer and its eraseScreenResult agrees with the replay', { skip: haveBundle ? false : 'collector cache not installed' }, async () => {
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
        import('./erase-line-grading-collector.mjs').then(m => m.adaptEraseLineCollector),
    ]);
    const { replayEraseDisplayPinned } = await import('./erase-display-grading.mjs');
    const { eraseDisplayRecordingContext } = await import('./erase-display-collector.mjs');
    const { eraseDisplayContracts } = await import('./erase-display-contracts.mjs');
    const { syntheticEraseDisplayScreens, syntheticEraseDisplayReads, textCells, survivors } = await import('./fixtures/erase-display-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    const owner = new URL('./ownership.mjs', import.meta.url).pathname;
    let text = adaptCollector(source.toString(), owner);
    for (const adapt of [...layers, adaptEraseDisplayCollector]) text = adapt(text);
    // Written beside the bundle so its package imports resolve, exactly as the runner places derived bundles.
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-erdi-test-${process.pid}.mjs`, import.meta.url));
    const seed = () => [...textCells(0, 'AAAAA'), ...textCells(20, 'BBBBB'), ...textCells(39, 'CCCCC')];
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeEraseDisplayAdapter.id, eraseDisplayAdapterId);
        assert.deepEqual([...module.ntildeEraseDisplayAdapter.probeIds], ['erase.screen.below', 'erase.screen.above', 'erase.screen.all']);
        const variants = [
            [() => ({}), 'supported'],
            [reads => { reads.erased = reads.setup; }, 'unsupported'],                                      // no-op
            [(reads, id) => { reads.erased = { cells: survivors(seed(), id === 'erase.screen.above' ? 0 : 1, [20, 2]), cursor: [20, 2] }; }, 'unsupported'], // wrong direction
            [reads => { reads.erased = { ...reads.erased, cursor: [0, 0] }; }, 'unsupported'],               // cursor homed (the pinned probe reads the cursor after)
            [reads => { reads.setup = { ...reads.setup, cursor: [20, 3] }; }, 'inconclusive'],
            [reads => { reads.setup = { cells: textCells(0, 'AAAAX'), cursor: [20, 2] }; }, 'inconclusive'],
            [reads => { reads.wide = { cells: [], cursor: [20, 4] }; }, 'supported'], // damage the pinned probe cannot see
        ];
        for (const contract of eraseDisplayContracts) {
            for (const [edit, expectedOf] of variants) {
                const readsOf = id => { const reads = syntheticEraseDisplayReads(id); if (id === contract.caseId) edit(reads, id); return reads; };
                const expected = typeof expectedOf === 'function' ? expectedOf(contract.caseId) : expectedOf;
                const screens = syntheticEraseDisplayScreens(contract.caseId, undefined, readsOf);
                const context = eraseDisplayRecordingContext(contract, screens);
                const live = module.ntildeRunEraseDisplayTermless(contract.caseId, context);
                const replay = replayEraseDisplayPinned(contract, screens);
                assert.equal(context.fedCount, 2);
                assert.equal(live.observation.outcome, expected, contract.caseId);
                assert.equal(live.pass, replay.pass);
                assert.equal(live.response, replay.response);
                assert.equal(live.note ?? null, replay.note ?? null);
                assert.deepEqual(live.observation, replay.observation);
                assert.deepEqual(live.assertions ?? [], replay.assertions ?? []);
            }
        }
    } finally { rmSync(file, { force: true }); }
});
