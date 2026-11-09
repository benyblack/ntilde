import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptEraseCharsHistoryCollector, eraseCharsHistoryAdapterId, eraseCharsHistorySeams } from './erase-chars-history-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => eraseCharsHistorySeams.join('\n/* gap */\n') + '\n';

test('derives the erase-character / saved-lines extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptEraseCharsHistoryCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeEraseCharsHistoryAdapter = Object.freeze({\n  id: "${eraseCharsHistoryAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunEraseCharsHistoryTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["erase.character","erase.screen.scrollback"]'));
    assert.equal(adaptEraseCharsHistoryCollector(source), derived);
});

test('the seams pin exactly the two registrations (termless and query paths), eraseRowResult and the four helpers', () => {
    const find = id => eraseCharsHistorySeams.find(seam => seam.includes(`probe("${id}"`));
    const character = find('erase.character'), history = find('erase.screen.scrollback');
    assert.ok(character.includes('"ABCDE\\x1B[1G", "\\x1B[3X", ["blank", "blank", "blank", "D", "E"], false, 0'));
    assert.ok(character.includes('ctx.write("\\x1B[2X");') && character.includes('unmeasuredCellResult(pos, "erased cells or scrollback")'));
    assert.ok(history.includes('ctx.feed("\\x1B[3J");') && history.includes('after.totalLines === rows'));
    assert.ok(history.includes('ctx.write("\\x1B[3J");') && history.includes('unmeasuredCellResult(pos, "erased cells or scrollback")'));
    assert.ok(eraseCharsHistorySeams.some(seam => seam.startsWith('function eraseRowResult(')));
    for (const helper of ['function isBlank(', 'function rowCells(', 'function parserStateResult(', 'function unmeasuredCellResult(']) {
        assert.ok(eraseCharsHistorySeams.some(seam => seam.startsWith(helper)), helper);
    }
    // The cursor-query path is pinned too, so a later upstream change that gave it a verdict would be refused.
    const unmeasured = eraseCharsHistorySeams.find(seam => seam.startsWith('function unmeasuredCellResult('));
    for (const part of ['pass: false', 'outcome: "inconclusive"', 'evidence: "query"']) assert.ok(unmeasured.includes(part), part);
    // The other erase registrations and eraseScreenResult are different cases.
    assert.ok(!eraseCharsHistorySeams.some(seam => /probe\("erase\.(screen\.(below|above|all)|line|selective|el-with-attrs|ed-scroll-region)/.test(seam.slice(0, 90))));
    assert.ok(!eraseCharsHistorySeams.some(seam => seam.startsWith('function eraseScreenResult(')));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of eraseCharsHistorySeams) {
        assert.throws(() => adaptEraseCharsHistoryCollector(verbatim().split(seam).join('CHANGED')), /Pinned erase-character\/saved-lines probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptEraseCharsHistoryCollector(verbatim() + seam + '\n'), /Pinned erase-character\/saved-lines probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptEraseCharsHistoryCollector(''), /Pinned erase-character\/saved-lines probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria or notes were substituted', () => {
    const edits = [
        ['"ABCDE\\x1B[1G", "\\x1B[3X"', '"ABCDE\\x1B[1G", "\\x1B[1X"'],             // explicit 3 replaced by another count
        ['"ABCDE\\x1B[1G"', '"ABCDEF\\x1B[1G"'],
        ['["blank", "blank", "blank", "D", "E"]', '["blank", "blank", "C", "D", "E"]'],
        ['false, 0)', 'false, 1)'],
        ['ctx.feed("\\x1B[3J");', 'ctx.feed("\\x1B[2J");'],                       // ED 3 replaced by ED 2
        ['ctx.feed(`\\r\n`.repeat(rows + 2));', 'ctx.feed(`\\r\n`.repeat(rows));'],
        ['after.totalLines === rows', 'after.totalLines <= rows'],
        ['before.totalLines <= rows', 'before.totalLines < rows'],
        ['rows < 1 || rows > 1000', 'rows < 1'],
        ['No measured scrollback to erase', 'nothing'],
        ['const fixtureReady = before.join("") === "ABCDE"', 'const fixtureReady = true'],
        ['const exact = expected.every(', 'const exact = true || expected.every('],
        ['const supported = exact && neighborPreserved;', 'const supported = true;'],
        ['return char === "" || char === " ";', 'return true;'],
        ['Cursor response does not measure ${feature}', 'verdict'],                  // the query path gaining a cell verdict
        ['pass: pass === true,', 'pass: true,'],
        ['evidence: "query"', 'evidence: "parser-state"'],
    ];
    for (const [from, to] of edits) {
        const hits = eraseCharsHistorySeams.filter(seam => seam.includes(from));
        assert.ok(hits.length >= 1, from);
        const mutated = eraseCharsHistorySeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptEraseCharsHistoryCollector(mutated), /Pinned erase-character\/saved-lines probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeAttrRectAdapterLater = {};\n';
    const layered = adaptEraseCharsHistoryCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(eraseCharsHistoryAdapterId));
});

// The real pinned bundle, derived through all layers and EXECUTED over synthetic observed screens: the pinned probe code must agree
// with the independent replay and contract. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const haveBundle = existsSync(bundleUrl);
test('the real pinned bundle derives through every layer and its termless probes agree with the replay', { skip: haveBundle ? false : 'collector cache not installed' }, async () => {
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
    ]);
    const { replayEraseCharsHistoryPinned } = await import('./erase-chars-history-grading.mjs');
    const { eraseCharsHistoryRecordingContext } = await import('./erase-chars-history-collector.mjs');
    const { syntheticEraseCharsHistoryContracts, syntheticEraseCharsHistoryScreens, erasedRow, shiftedRow, echRows } = await import('./fixtures/erase-chars-history-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    const owner = new URL('./ownership.mjs', import.meta.url).pathname;
    let text = adaptCollector(source.toString(), owner);
    for (const adapt of [...layers, adaptEraseCharsHistoryCollector]) text = adapt(text);
    // Written beside the bundle so its package imports resolve, exactly as the runner places derived bundles.
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-echh-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeEraseCharsHistoryAdapter.id, eraseCharsHistoryAdapterId);
        assert.deepEqual([...module.ntildeEraseCharsHistoryAdapter.probeIds], ['erase.character', 'erase.screen.scrollback']);
        const [ech, ed3] = syntheticEraseCharsHistoryContracts();
        const variants = [
            [ech, 'supported', () => undefined],
            [ech, 'unsupported', (role, state) => { if (role === 'erased') state.lines[0] = 'ABCDE'; }],                          // no-op
            [ech, 'unsupported', (role, state) => { if (role === 'erased') state.lines[0] = shiftedRow('ABCDE', 0, 3); }],       // DCH instead of ECH
            [ech, 'unsupported', (role, state) => { if (role === 'erased') state.lines[0] = erasedRow('ABCDE', 0, 2); }],        // wrong count
            [ech, 'inconclusive', (role, state) => { if (role === 'seed') state.lines[0] = 'ABCXE'; }],                           // seed changed
            [ech, 'inconclusive', (role, state) => { if (role === 'seed') state.cursor = [0, 3]; }],                             // wrong setup cursor
            [ed3, 'supported', () => undefined],
            [ed3, 'unsupported', (role, state) => { if (role === 'after') state.history = ['', '', '']; }],                      // history kept
            [ed3, 'inconclusive', (role, state) => { if (role === 'before') state.history = []; }],                              // nothing to erase
        ];
        for (const [contract, expected, edit] of variants) {
            const screens = syntheticEraseCharsHistoryScreens(contract, undefined, (role, state) => { edit(role, state); return state; });
            const context = eraseCharsHistoryRecordingContext(contract, screens);
            const live = module.ntildeRunEraseCharsHistoryTermless(contract.caseId, context);
            const replay = replayEraseCharsHistoryPinned(contract, screens);
            if (expected === 'inconclusive') assert.ok(context.fedCount <= contract.pinnedFeeds.length);
            else assert.equal(context.fedCount, contract.pinnedFeeds.length);
            assert.equal(live.observation.outcome, expected, `${contract.caseId} ${live.response}`);
            assert.equal(live.pass, replay.pass);
            assert.equal(live.response, replay.response);
            assert.equal(live.note ?? null, replay.note ?? null);
            assert.deepEqual(live.observation, replay.observation);
            assert.deepEqual(live.assertions ?? [], replay.assertions ?? []);
        }
        assert.equal(echRows, 16);
    } finally { rmSync(file, { force: true }); }
});
