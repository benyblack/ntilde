import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptEraseAttrsRegionCollector, eraseAttrsRegionAdapterId, eraseAttrsRegionSeams } from './erase-attrs-region-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => eraseAttrsRegionSeams.join('\n/* gap */\n') + '\n';

test('derives the erase-attributes extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptEraseAttrsRegionCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeEraseAttrsRegionAdapter = Object.freeze({\n  id: "${eraseAttrsRegionAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunEraseAttrsRegionTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["erase.el-with-attrs","erase.ed-scroll-region"]'));
    assert.equal(adaptEraseAttrsRegionCollector(source), derived);
});

test('the seams pin exactly the two registrations (with their cursor-query paths) and the helpers they use', () => {
    const el = eraseAttrsRegionSeams.find(seam => seam.includes('probe("erase.el-with-attrs"'));
    const ed = eraseAttrsRegionSeams.find(seam => seam.includes('probe("erase.ed-scroll-region"'));
    assert.ok(el && ed);
    for (const part of ['\\x1B[42m\\x1B[1;1HXXXXX\\x1B[1;1H', 'ctx.feed("\\x1B[K")', 'ctx.feed("\\x1B[0m")', 'after.bg?.r === before.bg.r', 'Colored X setup was not measured',
        'Erased cell background metadata unavailable', 'unmeasuredCellResult(pos, "erased cells or scrollback")']) {
        assert.ok(el.includes(part), part);
    }
    for (const part of ['\\x1B[1;1HKEEP!\\x1B[3;1HERASE\\x1B[3;10r\\x1B[3;1H', 'ctx.feed("\\x1B[J")', 'ctx.feed("\\x1B[r")', 'Need a measured 10x10 fixture',
        'Erase region seed or cursor setup failed', 'Preceding row or cell metadata was not preserved', 'unmeasuredCellResult(pos, "erased cells or scrollback")']) {
        assert.ok(ed.includes(part), part);
    }
    for (const helper of ['function isBlank(', 'function rowCells(', 'function parserStateResult(', 'function unmeasuredCellResult(']) {
        assert.ok(eraseAttrsRegionSeams.some(seam => seam.startsWith(helper)), helper);
    }
    // The other erase cases are different cases and are not seams of this batch.
    assert.ok(!eraseAttrsRegionSeams.some(seam => /erase\.(line|screen|character|selective)/.test(seam)), 'other erase cases are outside this batch');
    // The terminal paths never carry a verdict upstream: pinned so a later change that gave one a verdict is refused.
    for (const seam of [el, ed]) assert.ok(seam.includes('unmeasuredCellResult(pos, '), 'the cursor-query path is a pinned non-verdict');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of eraseAttrsRegionSeams) {
        assert.throws(() => adaptEraseAttrsRegionCollector(verbatim().split(seam).join('CHANGED')), /Pinned erase-attributes probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptEraseAttrsRegionCollector(verbatim() + seam + '\n'), /Pinned erase-attributes probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptEraseAttrsRegionCollector(''), /Pinned erase-attributes probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria, notes or terminal path were substituted', () => {
    const edits = [
        ['\\x1B[42m\\x1B[1;1HXXXXX', '\\x1B[41m\\x1B[1;1HXXXXX'],                                  // a different background
        ['XXXXX\\x1B[1;1H");\n          const before', 'XXXX\\x1B[1;1H");\n          const before'],     // a shorter seed
        ['ctx.feed("\\x1B[K");', 'ctx.feed("\\x1B[0K");'],                                         // explicit spelling replaced the omitted one
        ['before.char !== "X" || before.bg == null', 'before.char !== "X"'],                       // the measured-background gate removed
        ['after.bg?.r === before.bg.r', 'true'],                                                    // the resolved-RGB comparison removed
        ['ctx.feed("\\x1B[0m");', 'ctx.feed("");'],                                                 // the pinned restoration removed
        ['Colored X setup was not measured', 'setup'],
        ['Erased cell background metadata unavailable', 'unavailable'],
        ['\\x1B[1;1HKEEP!\\x1B[3;1HERASE', '\\x1B[1;1HKEEP!\\x1B[4;1HERASE'],
        ['\\x1B[3;10r\\x1B[3;1H");', '\\x1B[3;9r\\x1B[3;1H");'],                                    // different margins
        ['ctx.feed("\\x1B[J");', 'ctx.feed("\\x1B[0J");'],
        ['ctx.feed("\\x1B[r");', 'ctx.feed("");'],
        ['rows < 10 ||', 'rows < 3 ||'],
        ['before.cursor.y !== 2', 'before.cursor.y !== 3'],
        ['after.control.join("") === "KEEP!"', 'true'],
        ['after.target.every((char) => char !== null && isBlank(char))', 'true'],
        ['Preceding row or cell metadata was not preserved', 'preserved'],
        ['Erase region seed or cursor setup failed', 'failed'],
        ['return char === "" || char === " ";', 'return true;'],
        ['const char = ctx.getCell(row, col).char;', 'const char = "";'],
        ['unmeasuredCellResult(pos, "erased cells or scrollback")', '{ pass: true, observation: { outcome: "supported" } }'],        // a terminal path gaining a verdict
    ];
    for (const [from, to] of edits) {
        assert.ok(eraseAttrsRegionSeams.some(seam => seam.includes(from)), from);
        const mutated = eraseAttrsRegionSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptEraseAttrsRegionCollector(mutated), /Pinned erase-attributes probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeSavedResetAdapterLater = {};\n';
    const layered = adaptEraseAttrsRegionCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(eraseAttrsRegionAdapterId));
});

// The real pinned bundle, derived through all eighteen earlier layers and EXECUTED over synthetic observed cells: the pinned
// criteria must agree with the independent replay and contract. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const haveBundle = existsSync(bundleUrl);
test('the real pinned bundle derives through every layer and its termless criteria agree with the replay', { skip: haveBundle ? false : 'collector cache not installed' }, async () => {
    const { createHash } = await import('node:crypto');
    const { adaptCollector, upstreamBundleSha256 } = await import('./collector.mjs');
    const layerModules = ['grading', 'sgr-colors-grading', 'sgr-rgb-grading', 'sgr-attrs-grading', 'sgr-palette-grading', 'sgr-truecolor-grading',
        'sgr-underline-grading', 'sgr-underline-color-grading', 'sgr-reset-grading', 'sgr-closure-grading', 'erase-line-grading', 'char-edit-grading',
        'erase-display-grading', 'line-edit-grading', 'charsets-grading', 'scrollback-grading', 'saved-reset-grading'];
    const layers = await Promise.all(layerModules.map(name => import(`./${name}-collector.mjs`)
        .then(module => Object.entries(module).find(([key]) => /^adapt\w+Collector$/.test(key))[1])));
    const { replayEraseAttrsRegionPinned } = await import('./erase-attrs-region-grading.mjs');
    const { eraseAttrsRegionRecordingContext } = await import('./erase-attrs-region-collector.mjs');
    const { eraseAttrsRegionContracts } = await import('./erase-attrs-region-contracts.mjs');
    const { syntheticEraseAttrsRegionScreens, syntheticEraseAttrsRegionReads, textCells, blankRow, colorCell } = await import('./fixtures/erase-attrs-region-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    let text = adaptCollector(source.toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    for (const adapt of [...layers, adaptEraseAttrsRegionCollector]) text = adapt(text);
    // Written beside the bundle so its package imports resolve, exactly as the runner places derived bundles.
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-erar-test-${process.pid}.mjs`, import.meta.url));
    const green = { bg: { kind: 'palette', index: 2 } }, margins = { scrollTop: 2, scrollBottom: 9 };
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeEraseAttrsRegionAdapter.id, eraseAttrsRegionAdapterId);
        assert.deepEqual([...module.ntildeEraseAttrsRegionAdapter.probeIds], ['erase.el-with-attrs', 'erase.ed-scroll-region']);
        const variants = {
            'erase.el-with-attrs': [
                [() => {}, 'supported'],
                [reads => { reads.erased = reads.setup; }, 'unsupported'],                                                            // no-op
                [reads => { reads.erased = { cells: blankRow(0, { bg: { kind: 'palette', index: 4 } }), cursor: [0, 0] }; }, 'unsupported'], // wrong background
                [reads => { reads.erased = { cells: [], cursor: [0, 0] }; }, 'inconclusive'],                                            // default background: not measurable by the pinned probe
                [reads => { reads.erased = { cells: [[0, 0, colorCell('X', green)], ...blankRow(0, green, 1)], cursor: [0, 0] }; }, 'unsupported'],
                [reads => { reads.setup = { cells: textCells(0, 'XXXXX'), cursor: [0, 0] }; }, 'inconclusive'],                      // no measured background
                [reads => { reads.erased = { cells: blankRow(0, { bg: { kind: 'literal', rgb: { r: 0, g: 205, b: 0 } } }), cursor: [0, 0] }; }, 'supported'], // literal ARGB, same RGB
                [reads => { reads.erased = { cells: blankRow(0, { bg: { kind: 'literal', rgb: { r: 0, g: 205, b: 1 } } }), cursor: [0, 0] }; }, 'unsupported'],
                [reads => { reads.fill = { cells: [], cursor: [0, 0] }; }, 'supported'],                                               // damage the pinned probe cannot see
            ],
            'erase.ed-scroll-region': [
                [() => {}, 'supported'],
                [reads => { reads.erased = reads.setup; }, 'unsupported'],
                [reads => { reads.erased = { cells: [], cursor: [2, 0], buffer: margins }; }, 'inconclusive'],                          // KEEP! erased
                [reads => { reads.erased = { cells: [...textCells(0, 'KEEP!'), [2, 5, colorCell('Z')]], cursor: [2, 0], buffer: margins }; }, 'supported'], // column 5 is never read
                [reads => { reads.erased = { cells: [...textCells(0, 'KEEP!'), ...textCells(2, 'ERASE').slice(4)], cursor: [2, 0], buffer: margins }; }, 'unsupported'], // column 4 is the last cell read
                [reads => { reads.erased = { cells: [...textCells(0, 'KEEP!'), ...textCells(2, 'ERASE').slice(0, 1)], cursor: [2, 0], buffer: margins }; }, 'unsupported'],
                [reads => { reads.setup = { ...reads.setup, cursor: [2, 1] }; }, 'inconclusive'],
                [reads => { reads.setup = { cells: [...textCells(0, 'KEEP!'), ...textCells(2, 'ERAS')], cursor: [2, 0], buffer: margins }; }, 'inconclusive'],
                [reads => { reads.erased = { cells: reads.erased.cells, cursor: [9, 9], buffer: margins }; }, 'supported'],               // the pinned probe never reads the cursor after
                [reads => { reads.erased = { cells: reads.erased.cells, cursor: [2, 0] }; }, 'supported'],                              // margins reset: invisible to the pinned probe
            ],
        };
        for (const contract of eraseAttrsRegionContracts) {
            for (const [edit, expected] of variants[contract.caseId]) {
                const readsOf = id => { const reads = syntheticEraseAttrsRegionReads(id); if (id === contract.caseId) edit(reads); return reads; };
                const screens = syntheticEraseAttrsRegionScreens(contract.caseId, undefined, readsOf);
                const context = eraseAttrsRegionRecordingContext(contract, screens);
                const live = module.ntildeRunEraseAttrsRegionTermless(contract.caseId, context);
                const replay = replayEraseAttrsRegionPinned(contract, screens);
                assert.ok(context.complete, 'the pinned code issued every feed of one of its possible paths');
                assert.equal(live.observation.outcome, expected, `${contract.caseId}: ${edit}`);
                assert.equal(live.pass, replay.pass);
                assert.equal(live.response, replay.response);
                assert.equal(live.note ?? null, replay.note ?? null);
                assert.deepEqual(live.observation, replay.observation);
                assert.deepEqual(live.assertions ?? [], replay.assertions ?? []);
            }
        }
    } finally { rmSync(file, { force: true }); }
});
