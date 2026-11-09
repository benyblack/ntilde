import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptRectEditCollector, rectEditAdapterId, rectEditSeams } from './rect-edit-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => rectEditSeams.join('\n/* gap */\n') + '\n';

test('derives the rectangle-editing extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptRectEditCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeRectEditAdapter = Object.freeze({\n  id: "${rectEditAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunRectEditTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["editing.decfra","editing.decera","editing.deccra"]'));
    assert.equal(adaptRectEditCollector(source), derived);
});

test('the seams pin exactly the three registrations, the termless helpers and the never-a-verdict pixel helper', () => {
    const fra = rectEditSeams.find(seam => seam.includes('probe("editing.decfra"'));
    const era = rectEditSeams.find(seam => seam.includes('probe("editing.decera"'));
    const cra = rectEditSeams.find(seam => seam.includes('probe("editing.deccra"'));
    assert.ok(fra.includes('ctx.feed("\\x1B[88;1;1;3;5$x");') && fra.includes('ctx.feed("\\x1B[1;1H");') && fra.includes('ctx.write("\\x1B[88;1;1;3;5$x");'));
    assert.ok(era.includes('ctx.feed("\\x1B[1;1;3;5$z");') && era.includes('ctx.feed("\\x1B[1;1H");') && era.includes('ctx.write("\\x1B[1;1;3;5$z");'));
    assert.ok(cra.includes('ctx.feed("\\x1B[1;1;2;5;1;5;10$v");') && cra.includes('ctx.feed("\\x1B[1;1H");') && cra.includes('ctx.write("\\x1B[1;1;2;5;1;5;10$v");'));
    for (const helper of ['function parserStateResult(', 'function isBlank(', 'function unmeasuredCellResult(']) {
        assert.ok(rectEditSeams.some(seam => seam.startsWith(helper)), helper);
    }
    // The pixel path is pinned too, so a later upstream change that gave it a verdict would be refused.
    const pixel = rectEditSeams.find(seam => seam.startsWith('function unmeasuredCellResult('));
    for (const part of ['pass: false', 'outcome: "inconclusive"', 'reason: position ? "insufficient-evidence" : "no-response"']) assert.ok(pixel.includes(part), part);
    for (const seam of [fra, era, cra]) assert.ok(seam.includes('return unmeasuredCellResult(pos, "edited cells or attributes");'));
    assert.ok(!rectEditSeams.some(seam => /editing\.(decsera|deccara|decrara|decsace|insert-lines|delete-lines|insert-chars|delete-chars)/.test(seam)), 'other editing cases are outside this batch');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of rectEditSeams) {
        assert.throws(() => adaptRectEditCollector(verbatim().split(seam).join('CHANGED')), /Pinned rectangle-editing probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptRectEditCollector(verbatim() + seam + '\n'), /Pinned rectangle-editing probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptRectEditCollector(''), /Pinned rectangle-editing probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria or notes were substituted', () => {
    const edits = [
        ['ctx.feed("\\x1B[88;1;1;3;5$x");', 'ctx.feed("\\x1B[88;1;1;3;4$x");'],                       // DECFRA rectangle changed
        ['ctx.feed("\\x1B[88;1;1;3;5$x");', 'ctx.feed("\\x1B[89;1;1;3;5$x");'],                       // DECFRA fill character changed
        ['ctx.feed("\\x1B[1;1;3;5$z");', 'ctx.feed("\\x1B[1;1;3;4$z");'],                             // DECERA rectangle changed
        ['ctx.feed("\\x1B[1;1;2;5;1;5;10$v");', 'ctx.feed("\\x1B[1;1;2;5;1;5;11$v");'],               // DECCRA destination changed
        ['ctx.feed("\\x1B[1;1;2;5;1;5;10$v");', 'ctx.feed("\\x1B[1;1;2;5;1;6;10$v");'],
        ['ctx.feed("\\x1B[1;1H");', 'ctx.feed("\\x1B[2;1H");'],                                      // cursor setup moved
        ['setup.x !== 0 || setup.y !== 0', 'setup.x !== 0 || setup.y !== 1'],
        ['after.every((line) => line.slice(0, 5) === "XXXXX")', 'true'],
        ['after.every((line) => line.slice(0, 5).every(isBlank))', 'true'],
        ['after[4]?.slice(9) === "ABCDE" && after[5]?.slice(9) === "FGHIJ"', 'true'],
        ['const controlsValid = after.every((line, row) => line[5] === before[row]?.[5]);', 'const controlsValid = true;'],
        ['const seeds = ["aaaaaZ", "bbbbbY", "cccccX"];', 'const seeds = ["aaaaaZ", "bbbbbY", "cccccW"];'],
        ['return char === "" || char === " ";', 'return true;'],
        ['Need a measured 3x6 fixture', 'too small'],
        ['Need a measured 6x14 fixture', 'too small'],
        ['DECFRA seed was not measured', 'seed'],
        ['DECERA cursor setup failed', 'cursor'],
        ['DECCRA source or outside control changed', 'control'],
        ['pass: false,\n    ...position', 'pass: true,\n    ...position'],                              // the pixel path gaining a verdict
        ['evidence: "query"', 'evidence: "parser-state"'],
    ];
    for (const [from, to] of edits) {
        const hits = rectEditSeams.filter(seam => seam.includes(from));
        assert.ok(hits.length >= 1, from);
        const mutated = rectEditSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptRectEditCollector(mutated), /Pinned rectangle-editing probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeEraseCharsHistoryAdapterLater = {};\n';
    const layered = adaptRectEditCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(rectEditAdapterId));
});

// The real pinned bundle, derived through all twenty-two earlier layers and EXECUTED over synthetic observed cells: the pinned
// termless code must agree with the independent replay and contract. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const haveBundle = existsSync(bundleUrl);
test('the real pinned bundle derives through every layer and its DECFRA, DECERA and DECCRA code agrees with the replay', { skip: haveBundle ? false : 'collector cache not installed' }, async () => {
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
    const { replayRectEditPinned } = await import('./rect-edit-grading.mjs');
    const { rectEditRecordingContext } = await import('./rect-edit-collector.mjs');
    const { rectEditContracts } = await import('./rect-edit-contracts.mjs');
    const { syntheticRectEditScreens, syntheticRectEditReads, rowsOfText } = await import('./fixtures/rect-edit-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    const owner = new URL('./ownership.mjs', import.meta.url).pathname;
    let text = adaptCollector(source.toString(), owner);
    for (const adapt of [...layers, adaptRectEditCollector]) text = adapt(text);
    // Written beside the bundle so its package imports resolve, exactly as the runner places derived bundles.
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-rect-test-${process.pid}.mjs`, import.meta.url));
    const rows = (reads, role, texts, cursor) => { reads[role] = { ...reads[role], rows: rowsOfText(texts), ...(cursor ? { cursor } : {}) }; };
    const seeds = { 'editing.decfra': ['aaaaaZ', 'bbbbbY', 'cccccW'], 'editing.decera': ['aaaaaZ', 'bbbbbY', 'cccccX'],
        'editing.deccra': ['ABCDE.........', 'FGHIJ.........', '..............', '..............', '.........12345', '.........67890'] };
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeRectEditAdapter.id, rectEditAdapterId);
        assert.deepEqual([...module.ntildeRectEditAdapter.probeIds], ['editing.decfra', 'editing.decera', 'editing.deccra']);
        const variants = [
            [() => {}, 'supported'],
            [reads => { reads.edited = reads.setup; }, 'unsupported'],                                       // no-op
            [(reads, id) => rows(reads, 'edited', id === 'editing.decfra' ? seeds[id].map(line => `     ${line[5]}`) : id === 'editing.decera' ? seeds[id].map(line => `XXXXX${line[5]}`)
                : [...seeds[id].slice(0, 4), '.........ABCDE', '.........FGHI.']), 'unsupported'],  // blanked instead of filled, filled instead of erased, a short copy
            [reads => { reads.edited = { ...reads.edited, cursor: [3, 7] }; }, 'supported'],                  // the pinned probe never reads the cursor after the edit
            [reads => { reads.setup = { ...reads.setup, cursor: [1, 3] }; }, 'inconclusive'],
            [(reads, id) => rows(reads, 'before', [...seeds[id].slice(0, 2), 'zzzzzzzz']), 'inconclusive'],
            [(reads, id) => rows(reads, 'edited', seeds[id].map((line, row) => (row === 0 ? `${line.slice(0, 5)}Q` : line))), 'inconclusive'], // the flank control changed
        ];
        for (const contract of rectEditContracts) {
            for (const [edit, expected] of variants) {
                const readsOf = id => { const reads = syntheticRectEditReads(id); if (id === contract.caseId) edit(reads, id); return reads; };
                const screens = syntheticRectEditScreens(contract.caseId, undefined, readsOf);
                const context = rectEditRecordingContext(contract, screens);
                const live = module.ntildeRunRectEditTermless(contract.caseId, context);
                const replay = replayRectEditPinned(contract, screens);
                // The pinned probe stops early on an unmeasured seed (1 feed) or a wrong setup cursor (2 feeds); decided verdicts replay all three.
                assert.ok(context.fedCount >= 1 && context.fedCount <= 3);
                if (expected !== 'inconclusive') assert.equal(context.fedCount, 3);
                assert.equal(live.observation.outcome, expected, `${contract.caseId} ${live.response}`);
                assert.equal(live.pass, replay.pass);
                assert.equal(live.response, replay.response);
                assert.equal(live.note ?? null, replay.note ?? null);
                assert.deepEqual(live.observation, replay.observation);
                assert.deepEqual(live.assertions ?? [], replay.assertions ?? []);
            }
        }
        // The pinned geometry guard: too few columns or rows reads as inconclusive in both executions, before any feed.
        for (const contract of rectEditContracts) {
            for (const small of [{ cols: contract.mode === 'copy' ? 13 : 5 }, { rows: contract.mode === 'copy' ? 5 : 2 }]) {
                const screens = syntheticRectEditScreens(contract.caseId).map(screen => ({ ...screen, ...small }));
                const replay = replayRectEditPinned(contract, screens);
                assert.equal(replay.observation.outcome, 'inconclusive');
                assert.match(replay.note, /Need a measured (3x6|6x14) fixture/);
                const context = rectEditRecordingContext(contract, screens);
                const live = module.ntildeRunRectEditTermless(contract.caseId, context);
                assert.equal(context.fedCount, 0);
                assert.equal(live.response, replay.response);
                assert.equal(live.note, replay.note);
            }
        }
    } finally { rmSync(file, { force: true }); }
});
