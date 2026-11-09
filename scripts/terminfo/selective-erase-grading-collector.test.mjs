import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptSelectiveEraseCollector, selectiveEraseAdapterId, selectiveEraseSeams } from './selective-erase-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => selectiveEraseSeams.join('\n/* gap */\n') + '\n';

test('derives the selective-erasure extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptSelectiveEraseCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeSelectiveEraseAdapter = Object.freeze({\n  id: "${selectiveEraseAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunSelectiveEraseTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["erase.selective","editing.decsera"]'));
    assert.equal(adaptSelectiveEraseCollector(source), derived);
});

test('the seams pin exactly the two registrations, their helpers and the terminal paths that never carry a verdict', () => {
    const has = (...parts) => selectiveEraseSeams.some(seam => parts.every(part => seam.includes(part)));
    assert.ok(has('probe("erase.selective"', 'selectiveEraseResult(ctx, "\\x1B[?2J", false)', 'ctx.write("\\x1B[?2J")', 'unmeasuredCellResult(pos, "erased cells or scrollback")'));
    assert.ok(has('probe("editing.decsera"', 'selectiveEraseResult(ctx, "\\x1B[1;1;1;5${", true)', 'ctx.write("\\x1B[1;1;3;5${")', 'unmeasuredCellResult(pos, "edited cells or attributes")'));
    assert.ok(has('function selectiveEraseResult(', 'ctx.feed(\'\\x1B[1;1H\\x1B[1"qP\\x1B[0"qABCDZ\')', 'Protection or outside-rectangle control failed; cannot attribute selective erase',
        "ctx.feed('\\x1B[0\"q')"));
    for (const helper of ['function parserStateResult(', 'function selectiveEraseResult(', 'function unmeasuredCellResult(']) {
        assert.ok(selectiveEraseSeams.some(seam => seam.startsWith(helper)), helper);
    }
    // The sibling and the out-of-scope cases are different cases and stay unpinned here.
    for (const other of ['"erase.screen.below"', '"erase.screen.all"', '"erase.screen.scrollback"', '"editing.decera"', '"editing.deccra"', '"erase.el-with-attrs"', '"editing.decfra"']) {
        assert.ok(!selectiveEraseSeams.some(seam => seam.includes(`probe(${other}`)), other);
    }
    // The terminal paths are pinned too, so a later upstream change that gave one a verdict would be refused.
    const unmeasured = selectiveEraseSeams.find(seam => seam.startsWith('function unmeasuredCellResult('));
    assert.match(unmeasured, /outcome: "inconclusive"/);
    assert.ok(!/outcome: "supported"|outcome: "unsupported"|pass: true/.test(unmeasured), 'the terminal helper never carries a verdict');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of selectiveEraseSeams) {
        assert.throws(() => adaptSelectiveEraseCollector(verbatim().split(seam).join('CHANGED')), /Pinned selective-erase probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptSelectiveEraseCollector(verbatim() + seam + '\n'), /Pinned selective-erase probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptSelectiveEraseCollector(''), /Pinned selective-erase probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria or notes were substituted', () => {
    const edits = [
        ['ctx.feed(\'\\x1B[1;1H\\x1B[1"qP\\x1B[0"qABCDZ\');', 'ctx.feed(\'\\x1B[1;1H\\x1B[1"qP\\x1B[0"qABCDY\');'],           // the seed bytes
        ['ctx.feed(\'\\x1B[1;1H\\x1B[1"qP\\x1B[0"qABCDZ\');', 'ctx.feed(\'\\x1B[1;1H\\x1B[0"qP\\x1B[0"qABCDZ\');'],           // protection never switched on
        ['if (before.join("") !== "PABCDZ") {', 'if (before.join("") === "") {'],
        ['const controlsValid = after[0] === "P" && (!rectangular || after[5] === "Z");', 'const controlsValid = true;'],
        ['const controlsValid = after[0] === "P" && (!rectangular || after[5] === "Z");', 'const controlsValid = after[0] === "P";'],
        ['after.slice(1, rectangular ? 5 : 6).every(isBlank)', 'after.slice(1, rectangular ? 4 : 6).every(isBlank)'],
        ['after.slice(1, rectangular ? 5 : 6).every(isBlank)', 'after.slice(2, rectangular ? 5 : 6).every(isBlank)'],
        ['ctx.cols < 6', 'ctx.cols < 3'],
        ["ctx.feed('\\x1B[0\"q');", "ctx.feed('\\x1B[1\"q');"],                                                                // the finalizer
        ['"Protection or outside-rectangle control failed; cannot attribute selective erase"', '"unsupported"'],
        ['"Selective erase seed was not measured"', '"measured"'],
        ['"Need six measured columns"', '"measured"'],
        ['selectiveEraseResult(ctx, "\\x1B[?2J", false)', 'selectiveEraseResult(ctx, "\\x1B[?3J", false)'],                      // wrong sequence
        ['selectiveEraseResult(ctx, "\\x1B[?2J", false)', 'selectiveEraseResult(ctx, "\\x1B[?2J", true)'],                        // wrong extent
        ['selectiveEraseResult(ctx, "\\x1B[1;1;1;5${", true)', 'selectiveEraseResult(ctx, "\\x1B[1;1;1;4${", true)'],
        ['selectiveEraseResult(ctx, "\\x1B[1;1;1;5${", true)', 'selectiveEraseResult(ctx, "\\x1B[1;1;1;5${", false)'],
        ['unmeasuredCellResult(pos, "erased cells or scrollback")', 'parserStateResult(true, "x", {})'],                          // a terminal path gaining a verdict
        ['unmeasuredCellResult(pos, "edited cells or attributes")', 'parserStateResult(true, "x", {})'],
        ['outcome: "inconclusive",\n      reason: position ? "insufficient-evidence" : "no-response",', 'outcome: "supported",\n      reason: position ? "insufficient-evidence" : "no-response",'],
        ['pass: false,\n    ...position && { response:', 'pass: true,\n    ...position && { response:'],
        ['outcome: pass === null ? "inconclusive" : pass ? "supported" : "unsupported",', 'outcome: "supported",'],
    ];
    for (const [from, to] of edits) {
        const hits = selectiveEraseSeams.filter(seam => seam.includes(from));
        assert.ok(hits.length >= 1, from);
        const mutated = selectiveEraseSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptSelectiveEraseCollector(mutated), /Pinned selective-erase probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeEraseCharsHistoryAdapterLater = {};\n';
    const layered = adaptSelectiveEraseCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(selectiveEraseAdapterId));
});

// The real pinned bundle, derived through all twenty-two layers and EXECUTED over synthetic observed screens: the pinned
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
        import('./charsets-grading-collector.mjs').then(m => m.adaptCharsetsCollector),
        import('./scrollback-grading-collector.mjs').then(m => m.adaptScrollbackCollector),
        import('./saved-reset-grading-collector.mjs').then(m => m.adaptSavedResetCollector),
        import('./erase-attrs-region-grading-collector.mjs').then(m => m.adaptEraseAttrsRegionCollector),
        import('./attr-rect-grading-collector.mjs').then(m => m.adaptAttrRectCollector),
        import('./cursor-qual-grading-collector.mjs').then(m => m.adaptCursorQualCollector),
        import('./erase-chars-history-grading-collector.mjs').then(m => m.adaptEraseCharsHistoryCollector),
    ]);
    const { replaySelectiveErasePinned } = await import('./selective-erase-grading.mjs');
    const { selectiveEraseRecordingContext, expectedPinnedFeeds } = await import('./selective-erase-collector.mjs');
    const { selectiveEraseContracts } = await import('./selective-erase-contracts.mjs');
    const { syntheticSelectiveEraseScreens, syntheticSelectiveEraseReads, seedCells, guarded, open } = await import('./fixtures/selective-erase-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    const owner = new URL('./ownership.mjs', import.meta.url).pathname;
    let text = adaptCollector(source.toString(), owner);
    for (const adapt of [...layers, adaptSelectiveEraseCollector]) text = adapt(text);
    // Written beside the bundle so its package imports resolve, exactly as the runner places derived bundles.
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-sels-test-${process.pid}.mjs`, import.meta.url));
    const seed = seedCells(), seedCursor = [0, 6];
    const variants = {
        'erase.selective': [
            [() => ({}), 'supported'],
            [reads => { reads.erased = { cells: [], cursor: seedCursor }; }, 'inconclusive'],                                   // P lost
            [reads => { reads.erased = { cells: [open(0, 0, 'Q')], cursor: seedCursor }; }, 'inconclusive'],                     // P replaced
            [reads => { reads.erased = reads.seeded; }, 'unsupported'],                                                          // no-op
            [reads => { reads.erased = { cells: [guarded(0, 0, 'P'), open(0, 5, 'Z')], cursor: seedCursor }; }, 'unsupported'],  // Z kept
            [reads => { reads.erased = { cells: [guarded(0, 0, 'P'), open(0, 3, 'C')], cursor: seedCursor }; }, 'unsupported'],
            [reads => { reads.seeded = { cells: seed.slice(0, 5), cursor: seedCursor }; }, 'inconclusive'],                      // seed unreadable
            [reads => { reads.erased = { cells: [open(0, 0, 'P')], cursor: seedCursor }; }, 'supported'],                        // the pinned probe never reads protection
            [reads => { reads.erased = { cells: [guarded(0, 0, 'P')], cursor: [0, 0] }; }, 'supported'],                         // ... or the cursor
        ],
        'editing.decsera': [
            [() => ({}), 'supported'],
            [reads => { reads.erased = { cells: [guarded(0, 0, 'P')], cursor: seedCursor }; }, 'inconclusive'],                 // Z lost
            [reads => { reads.erased = { cells: [open(0, 5, 'Z')], cursor: seedCursor }; }, 'inconclusive'],                     // P lost
            [reads => { reads.erased = reads.seeded; }, 'unsupported'],
            [reads => { reads.erased = { cells: [guarded(0, 0, 'P'), open(0, 4, 'D'), open(0, 5, 'Z')], cursor: seedCursor }; }, 'unsupported'],
            [reads => { reads.erased = { cells: [guarded(0, 0, 'P'), open(0, 5, 'Z')], cursor: [3, 3] }; }, 'supported'],
        ],
    };
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeSelectiveEraseAdapter.id, selectiveEraseAdapterId);
        assert.deepEqual([...module.ntildeSelectiveEraseAdapter.probeIds], ['erase.selective', 'editing.decsera']);
        for (const contract of selectiveEraseContracts) {
            for (const [edit, expected] of variants[contract.caseId]) {
                const readsOf = id => { const reads = syntheticSelectiveEraseReads(id); if (id === contract.caseId) edit(reads); return reads; };
                const screens = syntheticSelectiveEraseScreens(contract.caseId, undefined, readsOf);
                const context = selectiveEraseRecordingContext(contract, screens);
                const live = module.ntildeRunSelectiveEraseTermless(contract.caseId, context);
                const replay = replaySelectiveErasePinned(contract, screens);
                assert.equal(live.observation.outcome, expected, `${contract.caseId} ${edit}`);
                assert.equal(live.pass, replay.pass);
                assert.equal(live.response, replay.response);
                assert.equal(live.note ?? null, replay.note ?? null);
                assert.deepEqual(live.observation, replay.observation);
                assert.deepEqual(live.assertions ?? [], replay.assertions ?? []);
                assert.deepEqual(context.feeds, expectedPinnedFeeds(contract, live));
            }
        }
        // The pinned terminal paths (cursor-query only) never carry a verdict in the real bundle either.
        const sed = module.ntildeRunSelectiveEraseTermless;
        assert.equal(typeof sed, 'function');
    } finally { rmSync(file, { force: true }); }
});
