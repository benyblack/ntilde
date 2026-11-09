import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptLineEditCollector, lineEditAdapterId, lineEditSeams } from './line-edit-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => lineEditSeams.join('\n/* gap */\n') + '\n';

test('derives the line-editing extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptLineEditCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeLineEditAdapter = Object.freeze({\n  id: "${lineEditAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunLineEditTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["editing.insert-lines","editing.delete-lines"]'));
    assert.equal(adaptLineEditCollector(source), derived);
});

test('the seams pin exactly the two registrations, the termless helpers and the never-a-verdict pixel helper', () => {
    const il = lineEditSeams.find(seam => seam.includes('probe("editing.insert-lines"'));
    const dl = lineEditSeams.find(seam => seam.includes('probe("editing.delete-lines"'));
    assert.ok(il.includes('ctx.feed("\\x1B[1L");') && il.includes('ctx.feed("\\x1B[2;1H");') && il.includes('ctx.write("\\x1B[3;5H");'));
    assert.ok(dl.includes('ctx.feed("\\x1B[1M");') && dl.includes('ctx.feed("\\x1B[2;1H");') && dl.includes('ctx.write("\\x1B[3;5H");'));
    for (const helper of ['function parserStateResult(', 'function isBlank(', 'function unmeasuredCellResult(']) {
        assert.ok(lineEditSeams.some(seam => seam.startsWith(helper)), helper);
    }
    // The pixel path is pinned too, so a later upstream change that gave it a verdict would be refused.
    const pixel = lineEditSeams.find(seam => seam.startsWith('function unmeasuredCellResult('));
    for (const part of ['pass: false', 'outcome: "inconclusive"', 'reason: position ? "insufficient-evidence" : "no-response"']) assert.ok(pixel.includes(part), part);
    assert.ok(il.includes('return unmeasuredCellResult(pos, "edited cells or attributes");'));
    assert.ok(dl.includes('return unmeasuredCellResult(pos, "edited cells or attributes");'));
    assert.ok(!lineEditSeams.some(seam => /editing\.(insert-chars|delete-chars|sl|sr|decic|decdc|repeat-char)/.test(seam)), 'other editing cases are outside this batch');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of lineEditSeams) {
        assert.throws(() => adaptLineEditCollector(verbatim().split(seam).join('CHANGED')), /Pinned line-editing probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptLineEditCollector(verbatim() + seam + '\n'), /Pinned line-editing probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptLineEditCollector(''), /Pinned line-editing probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria or notes were substituted', () => {
    const edits = [
        ['ctx.feed("\\x1B[1L");', 'ctx.feed("\\x1B[L");'],                                        // explicit count replaced by the omitted spelling
        ['ctx.feed("\\x1B[1L");', 'ctx.feed("\\x1B[1M");'],                                        // IL swapped for DL
        ['ctx.feed("\\x1B[1M");', 'ctx.feed("\\x1B[2M");'],                                        // count changed
        ['ctx.feed("\\x1B[1M");', 'ctx.feed("\\x1B[1L");'],                                        // DL swapped for IL
        ['const seed = ["AAAAA", "BBBBB", "CCCCC", "DDDDD"];', 'const seed = ["AAAAA", "BBBBB", "CCCCC", "EEEEE"];'],
        ['ctx.feed("\\x1B[2;1H");', 'ctx.feed("\\x1B[3;1H");'],                                    // cursor setup moved
        ['setup.x !== 0 || setup.y !== 1', 'setup.x !== 0 || setup.y !== 2'],
        ['after[1]?.every(isBlank) === true && after[2]?.join("") === "BBBBB" && after[3]?.join("") === "CCCCC"', 'true'],
        ['after[1]?.join("") === "CCCCC" && after[2]?.join("") === "DDDDD"', 'true'],
        ['const controlsValid = after[0]?.join("") === "AAAAA";', 'const controlsValid = true;'],
        ['return char === "" || char === " ";', 'return true;'],
        ['Need six measured columns and four measured screen lines for IL', 'too small'],
        ['IL cursor setup did not reach row 2 column 1', 'cursor'],
        ['DL source rows were not measured before the edit', 'seed'],
        ['pass: false,\n    ...position', 'pass: true,\n    ...position'],                            // the pixel path gaining a verdict
        ['evidence: "query"', 'evidence: "parser-state"'],
    ];
    for (const [from, to] of edits) {
        const hits = lineEditSeams.filter(seam => seam.includes(from));
        assert.ok(hits.length >= 1, from);
        const mutated = lineEditSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptLineEditCollector(mutated), /Pinned line-editing probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeEraseLineAdapterLater = {};\n';
    const layered = adaptLineEditCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(lineEditAdapterId));
});

// The real pinned bundle, derived through all twelve layers and EXECUTED over synthetic observed cells: the pinned
// termless code must agree with the independent replay and contract. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const haveBundle = existsSync(bundleUrl);
test('the real pinned bundle derives through every layer and its IL and DL code agrees with the replay', { skip: haveBundle ? false : 'collector cache not installed' }, async () => {
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
    const { replayLineEditPinned } = await import('./line-edit-grading.mjs');
    const { lineEditRecordingContext } = await import('./line-edit-collector.mjs');
    const { lineEditContracts } = await import('./line-edit-contracts.mjs');
    const { syntheticLineEditScreens, syntheticLineEditReads, rowsOfText } = await import('./fixtures/line-edit-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    const owner = new URL('./ownership.mjs', import.meta.url).pathname;
    let text = adaptCollector(source.toString(), owner);
    for (const adapt of [...layers, adaptLineEditCollector]) text = adapt(text);
    // Written beside the bundle so its package imports resolve, exactly as the runner places derived bundles.
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-ledt-test-${process.pid}.mjs`, import.meta.url));
    const rows = (reads, role, texts, cursor) => { reads[role] = { ...reads[role], rows: rowsOfText(texts), ...(cursor ? { cursor } : {}) }; };
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeLineEditAdapter.id, lineEditAdapterId);
        assert.deepEqual([...module.ntildeLineEditAdapter.probeIds], ['editing.insert-lines', 'editing.delete-lines']);
        const variants = [
            [() => {}, 'supported'],
            [reads => { reads.edited = reads.setup; }, 'unsupported'],                                       // no-op
            [(reads, id) => rows(reads, 'edited', id === 'editing.insert-lines' ? ['AAAAA', 'CCCCC', 'DDDDD'] : ['AAAAA', '', 'BBBBB', 'CCCCC', 'DDDDD']), 'unsupported'], // reversed
            [reads => { reads.edited = { ...reads.edited, cursor: [3, 7] }; }, 'supported'],                  // the pinned probe never reads the cursor after the edit
            [reads => { reads.setup = { ...reads.setup, cursor: [1, 3] }; }, 'inconclusive'],
            [reads => rows(reads, 'before', ['AAAAA', 'BBBBB', 'CCCCC', 'DDDDX']), 'inconclusive'],
            [reads => rows(reads, 'edited', ['ZZZZZ', 'BBBBB', 'CCCCC', 'DDDDD']), 'inconclusive'],          // the first-row control changed
        ];
        for (const contract of lineEditContracts) {
            for (const [edit, expected] of variants) {
                const readsOf = id => { const reads = syntheticLineEditReads(id); if (id === contract.caseId) edit(reads, id); return reads; };
                const screens = syntheticLineEditScreens(contract.caseId, undefined, readsOf);
                const context = lineEditRecordingContext(contract, screens);
                const live = module.ntildeRunLineEditTermless(contract.caseId, context);
                const replay = replayLineEditPinned(contract, screens);
                // The pinned probe stops early on an unmeasured seed (4 feeds) or a wrong setup cursor (5 feeds); decided verdicts replay all six.
                assert.ok(context.fedCount >= 4 && context.fedCount <= 6);
                if (expected !== 'inconclusive') assert.equal(context.fedCount, 6);
                assert.equal(live.observation.outcome, expected, `${contract.caseId} ${live.response}`);
                assert.equal(live.pass, replay.pass);
                assert.equal(live.response, replay.response);
                assert.equal(live.note ?? null, replay.note ?? null);
                assert.deepEqual(live.observation, replay.observation);
                assert.deepEqual(live.assertions ?? [], replay.assertions ?? []);
            }
        }
        // The pinned geometry guard: too few columns reads as inconclusive in both executions.
        for (const contract of lineEditContracts) {
            const screens = syntheticLineEditScreens(contract.caseId).map(screen => ({ ...screen, cols: 5 }));
            const replay = replayLineEditPinned(contract, screens);
            assert.equal(replay.observation.outcome, 'inconclusive');
            assert.match(replay.note, /Need six measured columns and four measured screen lines/);
        }
    } finally { rmSync(file, { force: true }); }
});
