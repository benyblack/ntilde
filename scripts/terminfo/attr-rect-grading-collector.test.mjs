import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptAttrRectCollector, attrRectAdapterId, attrRectSeams } from './attr-rect-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => attrRectSeams.join('\n/* gap */\n') + '\n';

test('derives the attribute-rectangle extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptAttrRectCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeAttrRectAdapter = Object.freeze({\n  id: "${attrRectAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunAttrRectTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["editing.deccara","editing.decrara","editing.decsace"]'));
    assert.equal(adaptAttrRectCollector(source), derived);
});

test('the seams pin exactly the three registrations, their helpers and the terminal paths that never carry a verdict', () => {
    const has = (...parts) => attrRectSeams.some(seam => parts.every(part => seam.includes(part)));
    assert.ok(has('probe("editing.deccara"', '\\x1B[1;1;3;5;7$r', 'DECCARA character or outside attribute control changed', 'Cursor response does not measure'.slice(0, 0)));
    assert.ok(has('probe("editing.decrara"', '\\x1B[1;1;3;5;7$t', 'DECRARA character or outside attribute control changed'));
    assert.ok(has('probe("editing.decsace"', '\\x1B[1;1H\\x1B[2*x', 'Sequence consumption does not measure attribute-change extent', 'DECSACE was not attempted'));
    for (const helper of ['function parserStateResult(', 'function unmeasuredCellResult(']) assert.ok(attrRectSeams.some(seam => seam.startsWith(helper)), helper);
    // The other rectangle operations and the checksum query are different cases and stay unpinned here.
    for (const other of ['"editing.deccra"', '"editing.decera"', '"editing.decfra"', '"editing.decsera"', '"editing.decrqcra"', '"editing.sl"', '"editing.sr"']) {
        assert.ok(!attrRectSeams.some(seam => seam.includes(`probe(${other}`)), other);
    }
    // Both attribute operations pin their terminal path, which ends in a cursor query and never returns a verdict.
    for (const id of ['editing.deccara', 'editing.decrara']) {
        const seam = attrRectSeams.find(item => item.includes(`probe("${id}"`));
        const terminal = seam.slice(seam.indexOf('async (ctx)'));
        assert.ok(terminal.includes('unmeasuredCellResult(pos, "edited cells or attributes")'), id);
        assert.ok(!/outcome: "supported"|outcome: "unsupported"/.test(terminal), `${id} terminal path never carries a verdict`);
    }
    // The DECSACE termless path is inconclusive by design: no supported or unsupported outcome can appear in it.
    const decsace = attrRectSeams.find(item => item.includes('probe("editing.decsace"'));
    assert.ok(!/outcome: "supported"|outcome: "unsupported"/.test(decsace), 'DECSACE never carries a verdict');
    assert.ok(decsace.includes('evidence: "consumed"'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of attrRectSeams) {
        assert.throws(() => adaptAttrRectCollector(verbatim().split(seam).join('CHANGED')), /Pinned attribute-rectangle probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptAttrRectCollector(verbatim() + seam + '\n'), /Pinned attribute-rectangle probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptAttrRectCollector(''), /Pinned attribute-rectangle probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria or notes were substituted', () => {
    const edits = [
        ['ctx.feed("\\x1B[1;1HaaaaaZ\\x1B[2;1HbbbbbY\\x1B[3;1HcccccX");', 'ctx.feed("\\x1B[1;1HaaaaaZ\\x1B[2;1HbbbbbY\\x1B[3;1HcccccW");'],
        ['ctx.feed("\\x1B[1;1;3;5;7$r");', 'ctx.feed("\\x1B[1;1;3;5;7$t");'],                                     // DECCARA bytes replaced by DECRARA's
        ['ctx.feed("\\x1B[1;1;3;5;7$t");', 'ctx.feed("\\x1B[1;1;3;5;7$r");'],
        ['ctx.feed("\\x1B[1;1;3;5;7$r");', 'ctx.feed("\\x1B[1;1;3;5;1$r");'],                                    // a different selector
        ['ctx.feed("\\x1B[1;1;3;5;7$r");', 'ctx.feed("\\x1B[1;1;3;4;7$r");'],                                    // a different right border
        ['ctx.feed("\\x1B[1;1H\\x1B[7maaaaa\\x1B[0mZ', 'ctx.feed("\\x1B[1;1H\\x1B[1maaaaa\\x1B[0mZ'],
        ['&& !line[5]?.inverse);\n        return parserStateResult(controlsValid ? after.every((line) => line.slice(0, 5).every((cell) => cell.inverse))', '&& true);\n        return parserStateResult(controlsValid ? after.every((line) => line.slice(0, 5).every((cell) => cell.inverse))'],
        ['line.slice(0, 5).every((cell) => cell.inverse)) : null', 'line.slice(0, 4).every((cell) => cell.inverse)) : null'],
        ['line.slice(0, 5).every((cell) => !cell.inverse)) : null', 'line.slice(0, 5).every((cell) => cell.inverse)) : null'],
        ['DECCARA character or outside attribute control changed', 'changed'],
        ['DECRARA character or outside attribute control changed', 'changed'],
        ['DECCARA character and attribute seed was not measured', 'unmeasured'],
        ['DECRARA inverse seed was not measured', 'unmeasured'],
        ['DECCARA cursor setup failed', 'failed'],
        ['Need a measured 3x6 fixture', 'Need geometry'],
        ['ctx.feed("\\x1B[1;1H\\x1B[2*x");', 'ctx.feed("\\x1B[1;1H\\x1B[0*x");'],                                   // the DECSACE pinned value
        ['note: "Sequence consumption does not measure attribute-change extent"', 'note: "measured"'],
        ['observation: { outcome: "inconclusive", evidence: "consumed", reason: "insufficient-evidence" }', 'observation: { outcome: "supported", evidence: "consumed", reason: "insufficient-evidence" }'],
        ['pass: false,\n        response: JSON.stringify({ text })', 'pass: true,\n        response: JSON.stringify({ text })'],
        ['DECSACE was not attempted; this probe does not measure attribute-change extent', 'measured'],
        ['edited cells or attributes', 'cells'],
    ];
    for (const [from, to] of edits) {
        const hits = attrRectSeams.filter(seam => seam.includes(from));
        assert.ok(hits.length >= 1, from);
        const mutated = attrRectSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptAttrRectCollector(mutated), /Pinned attribute-rectangle probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeEraseAttrsRegionAdapterLater = {};\n';
    const layered = adaptAttrRectCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(attrRectAdapterId));
});

// The real pinned bundle, derived through all nineteen layers and EXECUTED over synthetic observed screens: the pinned code must agree with
// the independent replay and contract. Skipped when the collector cache is absent.
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
    ]);
    const { replayAttrRectPinned } = await import('./attr-rect-grading.mjs');
    const { attrRectRecordingContext } = await import('./attr-rect-collector.mjs');
    const { attrRectContracts, attrRectOracle } = await import('./attr-rect-contracts.mjs');
    const { syntheticAttrRectScreens, syntheticAttrRectReads } = await import('./fixtures/attr-rect-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    const owner = new URL('./ownership.mjs', import.meta.url).pathname;
    let text = adaptCollector(source.toString(), owner);
    for (const adapt of [...layers, adaptAttrRectCollector]) text = adapt(text);
    // Written beside the bundle so its package imports resolve, exactly as the runner places derived bundles.
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-atrc-test-${process.pid}.mjs`, import.meta.url));
    const { applyOp, pinnedC, pinnedR } = attrRectOracle;
    const op = (reverse, rectangle) => applyOp(reverse ? pinnedR.grid : pinnedC.grid, { top: 1, left: 1, bottom: 3, right: 5, selectors: [7], reverse, rectangle });
    const variants = reverse => {
        const seedGrid = reverse ? pinnedR.grid : pinnedC.grid;
        return [
            [() => ({}), 'inconclusive'],                                                                              // the VT510 stream: the flank control fails
            [reads => { reads.edited = { grid: op(reverse, true), cursor: [0, 0] }; }, 'supported'],                      // the rectangle: the pinned criterion holds
            [reads => { reads.edited = { grid: seedGrid, cursor: [0, 0] }; }, 'unsupported'],                              // a no-op is a measured failure
            [reads => { // one row missed: its columns 1..5 keep the seeded attributes
                const grid = new Map(op(reverse, true));
                for (let col = 0; col < 5; col++) grid.set(`1,${col}`, { ...grid.get(`1,${col}`), attrs: reverse ? ['inverse'] : [] });
                reads.edited = { grid, cursor: [0, 0] };
            }, 'unsupported'],
            [reads => { reads.seed = { grid: new Map(), cursor: [2, 6] }; }, 'inconclusive'],                              // seed not measured
            [reads => { reads.cursor = { grid: seedGrid, cursor: [1, 0] }; }, 'inconclusive'],                             // cursor setup failed
            [reads => { const grid = new Map(op(reverse, true)); grid.set('0,0', { char: 'Q', attrs: reverse ? [] : ['inverse'] }); reads.edited = { grid, cursor: [0, 0] }; }, 'inconclusive'], // text changed
        ];
    };
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeAttrRectAdapter.id, attrRectAdapterId);
        assert.deepEqual([...module.ntildeAttrRectAdapter.probeIds], ['editing.deccara', 'editing.decrara', 'editing.decsace']);
        for (const contract of attrRectContracts) {
            const edits = contract.family === 'extent' ? [[() => ({}), 'inconclusive'], [reads => { reads.pinned = { grid: new Map(), cursor: [3, 3] }; }, 'inconclusive']]
                : variants(contract.family === 'reverse');
            for (const [edit, expected] of edits) {
                const readsOf = id => { const reads = syntheticAttrRectReads(id); if (id === contract.caseId) edit(reads); return reads; };
                const screens = syntheticAttrRectScreens(contract.caseId, undefined, readsOf);
                const context = attrRectRecordingContext(contract, screens);
                const live = module.ntildeRunAttrRectTermless(contract.caseId, context);
                const replay = replayAttrRectPinned(contract, screens);
                assert.equal(live.observation.outcome, expected, `${contract.caseId} ${edit}`);
                assert.equal(live.pass, replay.pass);
                assert.equal(live.response, replay.response);
                assert.equal(live.note ?? null, replay.note ?? null);
                assert.deepEqual(live.observation, replay.observation);
                assert.deepEqual(live.assertions ?? [], replay.assertions ?? []);
                assert.ok(context.fedCount >= 1 && context.fedCount <= contract.pinnedFeeds.length);
            }
            if (contract.rectanglePinnedReads) {
                // The labeled experiment: the same pinned code over the rectangle reads satisfies the pinned criterion.
                const screens = syntheticAttrRectScreens(contract.caseId);
                const context = attrRectRecordingContext(contract, screens, contract.rectanglePinnedReads);
                const live = module.ntildeRunAttrRectTermless(contract.caseId, context);
                assert.equal(live.observation.outcome, 'supported');
                assert.deepEqual(live.observation, replayAttrRectPinned(contract, screens, contract.rectanglePinnedReads).observation);
                assert.equal(context.fedCount, 3);
            }
        }
    } finally { rmSync(file, { force: true }); }
});
