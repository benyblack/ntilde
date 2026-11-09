import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptSavedResetCollector, savedResetAdapterId, savedResetSeams } from './saved-reset-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => savedResetSeams.join('\n/* gap */\n') + '\n';

test('derives the saved-state/reset extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptSavedResetCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeSavedResetAdapter = Object.freeze({\n  id: "${savedResetAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunSavedResetTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["modes.alt-screen.exit","modes.altscreen-47","modes.altscreen-1047","reset.sgr","reset.decaln"]'));
    assert.equal(adaptSavedResetCollector(source), derived);
});

test('the seams pin exactly the five registrations, their helpers and the terminal paths that never carry a verdict', () => {
    const has = (...parts) => savedResetSeams.some(seam => parts.every(part => seam.includes(part)));
    assert.ok(has('probe("modes.alt-screen.exit"', 'altScreen1049Roundtrip(ctx, "exit")', 'Cursor responsiveness after exit does not measure the restored buffer'));
    assert.ok(has('probe("modes.altscreen-47"', '?47h', 'Mode metadata does not measure the buffer'));
    assert.ok(has('probe("modes.altscreen-1047"', '?1047h', 'DECRPM status does not measure alternate-buffer clearing'));
    assert.ok(has('probe("reset.sgr"', '\\x1B[1;3;7mX\\x1B[0mY', 'Styled control or reset cell was not measured'));
    assert.ok(has('probe("reset.decaln"', '\\x1B#8', 'capture-only assertion; glyphs and full-grid coverage must be independently reviewed'));
    for (const helper of ['function parserStateResult(', 'function readAlt1049Span(', 'function altScreen1049Roundtrip(']) {
        assert.ok(savedResetSeams.some(seam => seam.startsWith(helper)), helper);
    }
    assert.ok(has('var ALT_1049_SEED_A = "PRIMARY-A", ALT_1049_SEED_B = "PRIMARY-B", ALT_1049_MARK = "ALT-MARK"'));
    // The sibling and the out-of-scope cases are different cases and stay unpinned here.
    for (const other of ['"sgr.reset"', '"reset.method"', '"reset.ris"', '"scrollback.alt-screen"', '"modes.alt-screen.enter"', '"modes.altscreen-1048"', '"modes.altscreen-1049"']) {
        assert.ok(!savedResetSeams.some(seam => seam.includes(`probe(${other}`)), other);
    }
    // The pixel and query paths are pinned too, so a later upstream change that gave one a verdict would be refused.
    const decaln = savedResetSeams.find(seam => seam.includes('probe("reset.decaln"'));
    for (const part of ['reason: "insufficient-evidence"', 'evidence: "pixels"', 'pass: false']) assert.ok(decaln.includes(part), part);
    assert.ok(!/outcome: "supported"|outcome: "unsupported"/.test(decaln.slice(decaln.indexOf('async (ctx)'))), 'the pixel path never carries a verdict');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of savedResetSeams) {
        assert.throws(() => adaptSavedResetCollector(verbatim().split(seam).join('CHANGED')), /Pinned saved-state probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptSavedResetCollector(verbatim() + seam + '\n'), /Pinned saved-state probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptSavedResetCollector(''), /Pinned saved-state probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria or notes were substituted', () => {
    const edits = [
        ['ALT_1049_SEED_A = "PRIMARY-A"', 'ALT_1049_SEED_A = "PRIMARY-X"'],
        ['ctx.feed("\\x1B[5;7H");', 'ctx.feed("\\x1B[5;8H");'],
        ['ctx.feed("\\x1B[?1049h");', 'ctx.feed("\\x1B[?1047h");'],
        ['ctx.feed("\\x1B[?1049l");\n    let restoredA;', 'ctx.feed("\\x1B[?47l");\n    let restoredA;'],
        ['before.x !== 6 || before.y !== 4', 'before.x !== 7 || before.y !== 4'],
        ['if (failures.length === 0)\n      return parserStateResult(true,', 'if (failures.length >= 0)\n      return parserStateResult(true,'],
        ['return parserStateResult(null, expected, state, "1049 roundtrip failed: "', 'return parserStateResult(false, expected, state, "1049 roundtrip failed: "'],
        ['"Entry did not clear the seeded primary spans; exit cannot be attributed"', '"cleared"'],
        ['ctx.feed("\\x1B[?47h");', 'ctx.feed("\\x1B[?47l");'],
        ['return parserStateResult(null, "?47 swaps the visible screen buffer"', 'return parserStateResult(true, "?47 swaps the visible screen buffer"'],
        ['return parserStateResult(null, "?1047 swaps and clears the visible alternate buffer"', 'return parserStateResult(entered && exited, "?1047 swaps and clears the visible alternate buffer"'],
        ['ctx.feed("\\x1B[1;3;7mX\\x1B[0mY");', 'ctx.feed("\\x1B[1;3;4mX\\x1B[0mY");'],                // the sibling sgr.reset fixture
        ['!after.bold && !after.italic && !after.inverse', '!after.bold && !after.italic'],
        ['!before.bold || !before.italic || !before.inverse', '!before.bold || !before.italic'],
        ['ctx.feed("\\x1B#8");', 'ctx.feed("\\x1B#8\\x1B[H");'],
        ['cell.char === "E"', 'cell.char !== ""'],
        ['getCell(0, 0);\n        return parserStateResult', 'getCell(0, 1);\n        return parserStateResult'],
        ['capture-only assertion; glyphs and full-grid coverage must be independently reviewed', 'verdict'],
        ['Cursor responsiveness after exit does not measure the restored buffer', 'measured'],
        ['DECRPM status does not measure alternate-buffer clearing', 'measured'],
        ['Cursor advance does not measure SGR styling after reset', 'measured'],
        ['Mode metadata does not measure the buffer', 'measured'],
        ['Styled control or reset cell was not measured', 'unmeasured'],
        ['pass: false,\n          response: JSON.stringify(pos),', 'pass: true,\n          response: JSON.stringify(pos),'],
    ];
    for (const [from, to] of edits) {
        const hits = savedResetSeams.filter(seam => seam.includes(from));
        assert.ok(hits.length >= 1, from);
        const mutated = savedResetSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptSavedResetCollector(mutated), /Pinned saved-state probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeLineEditAdapterLater = {};\n';
    const layered = adaptSavedResetCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(savedResetAdapterId));
});

// The real pinned bundle, derived through all fifteen layers and EXECUTED over synthetic observed screens: the pinned
// code must agree with the independent replay and contract. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const haveBundle = existsSync(bundleUrl);
test('the real pinned bundle derives through every layer and its pinned probes agree with the replay', { skip: haveBundle ? false : 'collector cache not installed' }, async () => {
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
        import('./char-edit-grading-collector.mjs').then(m => m.adaptCharEditCollector),
        import('./erase-display-grading-collector.mjs').then(m => m.adaptEraseDisplayCollector),
        import('./line-edit-grading-collector.mjs').then(m => m.adaptLineEditCollector),
    ]);
    const { replaySavedResetPinned } = await import('./saved-reset-grading.mjs');
    const { savedResetRecordingContext } = await import('./saved-reset-collector.mjs');
    const { savedResetContracts } = await import('./saved-reset-contracts.mjs');
    const { syntheticSavedResetScreens, syntheticSavedResetReads, put } = await import('./fixtures/saved-reset-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    const owner = new URL('./ownership.mjs', import.meta.url).pathname;
    let text = adaptCollector(source.toString(), owner);
    for (const adapt of [...layers, adaptSavedResetCollector]) text = adapt(text);
    // Written beside the bundle so its package imports resolve, exactly as the runner places derived bundles.
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-svrs-test-${process.pid}.mjs`, import.meta.url));
    const primary = { cells: [...put(0, 0, 'PRIMARY-A'), ...put(2, 0, 'PRIMARY-B')], cursor: [4, 6] };
    const styled = { attrs: ['bold', 'italic', 'inverse'] };
    const pair = (x, y) => ({ cells: [...put(0, 0, 'X', x), ...put(0, 1, 'Y', y)], cursor: [0, 2] });
    const variants = {
        'modes.alt-screen.exit': [
            [() => ({}), 'supported'],
            [reads => { reads.exited = { cells: [], cursor: [4, 6] }; }, 'inconclusive'],                       // contents not restored
            [reads => { reads.exited = { ...primary, cursor: [0, 0] }; }, 'inconclusive'],                      // cursor not restored
            [reads => { reads.exited = { cells: [...put(0, 0, 'ALT-MARK'), ...put(2, 0, 'PRIMARY-B')], cursor: [4, 6] }; }, 'inconclusive'],
            [reads => { reads.entered = { cells: put(0, 0, 'PRIMARY-A'), cursor: [0, 0], alt: true }; }, 'inconclusive'],
            [reads => { reads.seed = { ...primary, cursor: [4, 7] }; }, 'inconclusive'],
            [reads => { reads.marked = { cells: [], cursor: [0, 0], alt: true }; }, 'inconclusive'],
        ],
        'modes.altscreen-47': [
            [() => ({}), 'inconclusive'], [reads => { reads.pinnedEntered = { cells: [], cursor: [0, 0] }; }, 'inconclusive'],
            [reads => { reads.pinnedExited = { cells: [], cursor: [0, 0], alt: true }; }, 'inconclusive'],
        ],
        'modes.altscreen-1047': [
            [() => ({}), 'inconclusive'], [reads => { reads.pinnedEntered = { cells: [], cursor: [0, 0] }; }, 'inconclusive'],
        ],
        'reset.sgr': [
            [() => ({}), 'supported'],
            [reads => { reads.pinned = pair(styled, { attrs: ['bold'] }); }, 'unsupported'],
            [reads => { reads.pinned = pair(styled, { attrs: ['italic'] }); }, 'unsupported'],
            [reads => { reads.pinned = pair(styled, { attrs: ['inverse'] }); }, 'unsupported'],
            [reads => { reads.pinned = pair({ attrs: ['bold', 'italic'] }, {}); }, 'inconclusive'],
            [reads => { reads.pinned = pair(styled, { attrs: ['underline'], underlineStyle: 1 }); }, 'supported'], // the pinned probe never reads underline
        ],
        'reset.decaln': [
            [() => ({}), 'supported'], [reads => { reads.pinned = { cells: [], cursor: [0, 0] }; }, 'unsupported'],
            [reads => { reads.pinned = { cells: put(0, 0, 'E'), cursor: [0, 0] }; }, 'supported'],                 // the pinned probe reads only cell 0,0
        ],
    };
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeSavedResetAdapter.id, savedResetAdapterId);
        assert.deepEqual([...module.ntildeSavedResetAdapter.probeIds], ['modes.alt-screen.exit', 'modes.altscreen-47', 'modes.altscreen-1047', 'reset.sgr', 'reset.decaln']);
        for (const contract of savedResetContracts) {
            for (const [edit, expected] of variants[contract.caseId]) {
                const readsOf = id => { const reads = syntheticSavedResetReads(id); if (id === contract.caseId) edit(reads); return reads; };
                const screens = syntheticSavedResetScreens(contract.caseId, undefined, readsOf);
                const context = savedResetRecordingContext(contract, screens);
                const live = module.ntildeRunSavedResetTermless(contract.caseId, context);
                const replay = replaySavedResetPinned(contract, screens);
                assert.equal(live.observation.outcome, expected, `${contract.caseId} ${edit}`);
                assert.equal(live.pass, replay.pass);
                assert.equal(live.response, replay.response);
                assert.equal(live.note ?? null, replay.note ?? null);
                assert.deepEqual(live.observation, replay.observation);
                assert.deepEqual(live.assertions ?? [], replay.assertions ?? []);
                assert.ok(context.fedCount >= 1 && context.fedCount <= contract.pinnedFeeds.length);
            }
        }
        // A fully passing exit fixture feeds every pinned write, including the trailing ESC[?1049l ESC[0m.
        const exit = savedResetContracts[0];
        const full = savedResetRecordingContext(exit, syntheticSavedResetScreens(exit.caseId));
        module.ntildeRunSavedResetTermless(exit.caseId, full);
        assert.equal(full.fedCount, 7);
    } finally { rmSync(file, { force: true }); }
});
