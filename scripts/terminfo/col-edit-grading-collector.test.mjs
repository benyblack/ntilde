import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptColEditCollector, colEditAdapterId, colEditSeams } from './col-edit-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => colEditSeams.join('\n/* gap */\n') + '\n';
const IDS = ['editing.sl', 'editing.sr', 'editing.decic', 'editing.decdc'];

test('derives the column-editing extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptColEditCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeColEditAdapter = Object.freeze({\n  id: "${colEditAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunColEditTermless(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify(IDS)));
    assert.equal(adaptColEditCollector(source), derived);
});

test('the seams pin exactly the four registrations, the termless helpers, the never-a-verdict pixel helper and the previous layer', () => {
    const [sl, sr, ic, dc] = IDS.map(id => colEditSeams.find(seam => seam.includes(`probe("${id}"`)));
    assert.ok(sl.includes('ctx.feed("\\x1B[2 @");') && sl.includes('ctx.feed("\\x1B[1;1H");') && sl.includes('ctx.write("\\x1B[2 @");'));
    assert.ok(sr.includes('ctx.feed("\\x1B[2 A");') && sr.includes('ctx.feed("\\x1B[1;1H");') && sr.includes('ctx.write("\\x1B[2 A");'));
    assert.ok(ic.includes('ctx.feed("\\x1B[2\'}");') && ic.includes('ctx.feed("\\x1B[2;3H");') && ic.includes('ctx.write("\\x1B[2\'}");'));
    assert.ok(dc.includes('ctx.feed("\\x1B[2\'~");') && dc.includes('ctx.feed("\\x1B[2;3H");') && dc.includes('ctx.write("\\x1B[2\'~");'));
    for (const helper of ['function parserStateResult(', 'function isBlank(', 'function unmeasuredCellResult(']) {
        assert.ok(colEditSeams.some(seam => seam.startsWith(helper)), helper);
    }
    const pixel = colEditSeams.find(seam => seam.startsWith('function unmeasuredCellResult('));
    for (const part of ['pass: false', 'outcome: "inconclusive"', 'reason: position ? "insufficient-evidence" : "no-response"']) assert.ok(pixel.includes(part), part);
    for (const seam of [sl, sr, ic, dc]) assert.ok(seam.includes('return unmeasuredCellResult(pos, "edited cells or attributes");'));
    assert.ok(colEditSeams.includes('export const ntildeRectEditAdapter = Object.freeze({'), 'derived from the rectangle-editing layer');
    assert.ok(!colEditSeams.some(seam => /modes\.left-right-margin|editing\.(decrqcra|decfra|decera|deccra|decsera)/.test(seam)), 'other cases are outside this batch');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of colEditSeams) {
        assert.throws(() => adaptColEditCollector(verbatim().split(seam).join('CHANGED')), /Pinned column-editing probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptColEditCollector(verbatim() + seam + '\n'), /Pinned column-editing probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptColEditCollector(''), /Pinned column-editing probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria or notes were substituted', () => {
    const edits = [
        ['ctx.feed("\\x1B[2 @");', 'ctx.feed("\\x1B[3 @");'],                                         // SL count changed
        ['ctx.feed("\\x1B[2 A");', 'ctx.feed("\\x1B[2A");'],                                           // SR became CUU
        ['ctx.feed("\\x1B[2\'}");', 'ctx.feed("\\x1B[2@");'],                                          // DECIC became ICH
        ['ctx.feed("\\x1B[2\'~");', 'ctx.feed("\\x1B[2P");'],                                          // DECDC became DCH
        ['ctx.feed("\\x1B[2;3H");', 'ctx.feed("\\x1B[1;3H");'],                                        // the DECIC/DECDC setup moved
        ['setup.x !== 2 || setup.y !== 1', 'setup.x !== 2 || setup.y !== 0'],
        ['col + 2 < ctx.cols ? char === before[row]?.[col + 2] : isBlank(char)', 'true'],
        ['col < 2 ? isBlank(char) : char === before[row]?.[col - 2]', 'true'],
        ['const seeds = ["ABCDEFGHI", "JKLMNOPQR"];', 'const seeds = ["ABCDEFGHI", "JKLMNOPQRS"];'],
        ['const measuredWidth = Math.min(ctx.cols, 11);', 'const measuredWidth = Math.min(ctx.cols, 9);'],
        ['line.slice(0, 2).every((char, col) => char === before[row]?.[col])', 'true'],
        ['return char === "" || char === " ";', 'return true;'],
        ['Need a measured 2x9 fixture', 'too small'],
        ['Need a measured 3x8 fixture', 'too small'],
        ['SR seed was not measured', 'seed'],
        ['DECIC cursor setup failed', 'cursor'],
        ['DECDC unchanged-prefix control changed', 'control'],
        ['pass: false,\n    ...position', 'pass: true,\n    ...position'],                              // the pixel path gaining a verdict
        ['evidence: "query"', 'evidence: "parser-state"'],
    ];
    for (const [from, to] of edits) {
        const hits = colEditSeams.filter(seam => seam.includes(from));
        assert.ok(hits.length >= 1, from);
        const mutated = colEditSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptColEditCollector(mutated), /Pinned column-editing probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeRectEditAdapterLater = {};\n';
    const layered = adaptColEditCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(colEditAdapterId));
});

// The real pinned bundle, derived through all twenty-four earlier layers and EXECUTED over synthetic observed cells: the pinned
// termless code must agree with the independent replay. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const haveBundle = existsSync(bundleUrl);
test('the real pinned bundle derives through every layer and its SL, SR, DECIC and DECDC code agrees with the replay', { skip: haveBundle ? false : 'collector cache not installed' }, async () => {
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
        import('./selective-erase-grading-collector.mjs').then(m => m.adaptSelectiveEraseCollector),
        import('./rect-edit-grading-collector.mjs').then(m => m.adaptRectEditCollector),
    ]);
    const { replayColEditPinned } = await import('./col-edit-grading.mjs');
    const { colEditRecordingContext, expectedFedCount } = await import('./col-edit-collector.mjs');
    const { colEditContracts } = await import('./col-edit-contracts.mjs');
    const { syntheticColEditScreens, syntheticColEditReads, plainCell } = await import('./fixtures/col-edit-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    const owner = new URL('./ownership.mjs', import.meta.url).pathname;
    let text = adaptCollector(source.toString(), owner);
    for (const adapt of [...layers, adaptColEditCollector]) text = adapt(text);
    // Written beside the bundle so its package imports resolve, exactly as the runner places derived bundles.
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-cole-test-${process.pid}.mjs`, import.meta.url));
    const rows = (reads, role, texts, cursor) => { reads[role] = { ...reads[role], rows: texts.map(line => [...line].map(char => plainCell(char))), ...(cursor ? { cursor } : {}) }; };
    const seeds = { 'editing.sl': ['ABCDEFGHIJK', 'JKLMNOPQRST'], 'editing.sr': ['ABCDEFGHI', 'JKLMNOPQR'],
        'editing.decic': ['ABCDEFGH', 'IJKLMNOP', 'QRSTUVWX'], 'editing.decdc': ['ABCDEFGHIJ', 'IJKLMNOPQR', 'QRSTUVWXab'] };
    // The edit applied only to the cursor row (ICH/DCH-like): SL/SR on row 1, DECIC/DECDC on row 2.
    const cursorRowOnly = { 'editing.sl': ['CDEFGHIJK', 'JKLMNOPQRST'], 'editing.sr': ['  ABCDEFGHI', 'JKLMNOPQR'],
        'editing.decic': ['ABCDEFGH', 'IJ  KLMNOP', 'QRSTUVWX'], 'editing.decdc': ['ABCDEFGH', 'IJMNOPQR', 'QRSTUVWX'] };
    // An off-by-one count: one column instead of two.
    const byOne = { 'editing.sl': ['BCDEFGHIJK', 'KLMNOPQRST'], 'editing.sr': [' ABCDEFGHI', ' JKLMNOPQR'],
        'editing.decic': ['AB CDEFGH', 'IJ KLMNOP', 'QR STUVWX'], 'editing.decdc': ['ABDEFGHIJ', 'IJLMNOPQR', 'QRTUVWXab'] };
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeColEditAdapter.id, colEditAdapterId);
        assert.deepEqual([...module.ntildeColEditAdapter.probeIds], IDS);
        const variants = [
            [() => {}, 'supported'],
            [reads => { reads.edited = { ...reads.setup }; }, 'unsupported'],                              // no-op
            [(reads, id) => rows(reads, 'edited', byOne[id]), 'unsupported'],                              // off-by-one count
            [(reads, id) => rows(reads, 'edited', cursorRowOnly[id]), 'unsupported'],                      // only the cursor row moved
            [reads => { reads.edited = { ...reads.edited, cursor: [3, 7] }; }, 'supported'],               // the pinned probe never reads the cursor after the edit
            [reads => { reads.setup = { ...reads.setup, cursor: [2, 4] }; }, 'inconclusive'],
            [(reads, id) => rows(reads, 'before', [...seeds[id].slice(0, 1), 'zzzzzzzz']), 'inconclusive'],
        ];
        for (const contract of colEditContracts) {
            for (const [edit, expected] of variants) {
                // DECIC/DECDC's unchanged-prefix control catches an only-cursor-row edit as a verdict too, never a pass.
                const readsOf = id => { const reads = syntheticColEditReads(id); if (id === contract.caseId) edit(reads, id); return reads; };
                const screens = syntheticColEditScreens(contract.caseId, undefined, readsOf);
                const context = colEditRecordingContext(contract, screens);
                const live = module.ntildeRunColEditTermless(contract.caseId, context);
                const replay = replayColEditPinned(contract, screens);
                assert.equal(context.fedCount, expectedFedCount(contract, live));
                assert.equal(live.observation.outcome, expected, `${contract.caseId} ${live.response}`);
                assert.equal(live.pass, replay.pass);
                assert.equal(live.response, replay.response);
                assert.equal(live.note ?? null, replay.note ?? null);
                assert.deepEqual(live.observation, replay.observation);
                assert.deepEqual(live.assertions ?? [], replay.assertions ?? []);
            }
            // DECIC/DECDC only: a changed unchanged-prefix column is inconclusive (the pinned control), SL/SR grade it unsupported.
            const prefix = id => { const reads = syntheticColEditReads(id); if (id === contract.caseId) {
                const after = { 'editing.sl': ['ZDEFGHIJK', 'LMNOPQRST'], 'editing.sr': ['Z ABCDEFGHI', '  JKLMNOPQR'],
                    'editing.decic': ['ZB  CDEFGH', 'IJ  KLMNOP', 'QR  STUVWX'], 'editing.decdc': ['ZBEFGHIJ', 'IJMNOPQR', 'QRUVWXab'] }[id];
                rows(reads, 'edited', after); } return reads; };
            const screens = syntheticColEditScreens(contract.caseId, undefined, prefix);
            const live = module.ntildeRunColEditTermless(contract.caseId, colEditRecordingContext(contract, screens));
            assert.equal(live.observation.outcome, contract.mode === 'insert' || contract.mode === 'delete' ? 'inconclusive' : 'unsupported', contract.caseId);
            assert.deepEqual(live.observation, replayColEditPinned(contract, screens).observation);
        }
        // The pinned geometry guard: too few columns or rows reads as inconclusive in both executions, before any feed.
        for (const contract of colEditContracts) {
            const atCursor = contract.mode === 'insert' || contract.mode === 'delete';
            for (const small of [{ cols: atCursor ? 7 : 8 }, { rows: atCursor ? 2 : 1 }]) {
                const screens = syntheticColEditScreens(contract.caseId).map(screen => ({ ...screen, ...small }));
                const replay = replayColEditPinned(contract, screens);
                assert.equal(replay.observation.outcome, 'inconclusive');
                assert.match(replay.note, /Need a measured (2x9|3x8) fixture/);
                const context = colEditRecordingContext(contract, screens);
                const live = module.ntildeRunColEditTermless(contract.caseId, context);
                assert.equal(context.fedCount, 0);
                assert.equal(live.response, replay.response);
                assert.equal(live.note, replay.note);
            }
        }
    } finally { rmSync(file, { force: true }); }
});
