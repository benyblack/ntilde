import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptCharEditCollector, charEditAdapterId, charEditSeams } from './char-edit-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => charEditSeams.join('\n/* gap */\n') + '\n';

test('derives the character-editing extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptCharEditCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeCharEditAdapter = Object.freeze({\n  id: "${charEditAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunCharEditTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["editing.insert-chars","editing.delete-chars","editing.repeat-char"]'));
    assert.equal(adaptCharEditCollector(source), derived);
});

test('the seams pin exactly the three registrations (termless and query paths) and the three helpers', () => {
    const find = id => charEditSeams.find(seam => seam.includes(`probe("${id}"`));
    assert.ok(find('editing.insert-chars').includes('ctx.feed("\\x1B[1@");') && find('editing.insert-chars').includes('"CDEZQ"'));
    assert.ok(find('editing.delete-chars').includes('ctx.feed("\\x1B[1P");') && find('editing.delete-chars').includes('"DEZQH"'));
    assert.ok(find('editing.repeat-char').includes('ctx.feed("\\x1B[3b");') && find('editing.repeat-char').includes('"XXXX"'));
    for (const helper of ['function isBlank(', 'function parserStateResult(', 'function unmeasuredCellResult(']) {
        assert.ok(charEditSeams.some(seam => seam.startsWith(helper)), helper);
    }
    // The cursor-query path is pinned too, so a later upstream change that gave it a cell verdict would be refused.
    for (const id of ['editing.insert-chars', 'editing.delete-chars', 'editing.repeat-char']) {
        assert.ok(find(id).includes('queryCursorPosition') && find(id).includes('unmeasuredCellResult(pos, "edited cells or attributes")'), id);
    }
    const unmeasured = charEditSeams.find(seam => seam.startsWith('function unmeasuredCellResult('));
    for (const part of ['pass: false', 'outcome: "inconclusive"', 'evidence: "query"']) assert.ok(unmeasured.includes(part), part);
    assert.ok(!charEditSeams.some(seam => /editing\.(insert-lines|delete-lines|decic|decdc|decfra)/.test(seam.slice(0, 80))), 'other editing cases are outside this batch');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of charEditSeams) {
        assert.throws(() => adaptCharEditCollector(verbatim().split(seam).join('CHANGED')), /Pinned character-editing probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptCharEditCollector(verbatim() + seam + '\n'), /Pinned character-editing probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptCharEditCollector(''), /Pinned character-editing probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria or notes were substituted', () => {
    const edits = [
        ['ctx.feed("\\x1B[1@");', 'ctx.feed("\\x1B[@");'],                         // explicit 1 replaced by the omitted spelling
        ['ctx.feed("\\x1B[1P");', 'ctx.feed("\\x1B[P");'],
        ['ctx.feed("\\x1B[3b");', 'ctx.feed("\\x1B[2b");'],                         // REP count
        ['ABCDEZQH', 'ABCDEZQX'],
        ['AX   Z', 'AX  YZ'],
        ['"CDEZQ"', '"CDEZH"'],
        ['"DEZQH"', '"DEZQ"'],
        ['"XXXX"', '"XXX"'],
        ['ctx.feed("\\x1B[1;3H");', 'ctx.feed("\\x1B[1;4H");'],
        ['setup.x !== 2 || setup.y !== 0', 'false'],
        ['const controlsValid = after[0] === "A" && after[1] === "B";', 'const controlsValid = true;'],
        ['const controlsValid = after[0] === "A" && after[5] === "Z";', 'const controlsValid = true;'],
        ['return char === "" || char === " ";', 'return true;'],
        ['Need eight measured columns for the ICH fixture', 'too small'],
        ['REP flank control changed during the edit', 'changed'],
        ['Cursor response does not measure ${feature}', 'verdict'],                  // the query path gaining a cell verdict
        ['pass: pass === true,', 'pass: true,'],
        ['evidence: "query"', 'evidence: "parser-state"'],
    ];
    for (const [from, to] of edits) {
        const hits = charEditSeams.filter(seam => seam.includes(from));
        assert.ok(hits.length >= 1, from);
        const mutated = charEditSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptCharEditCollector(mutated), /Pinned character-editing probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeEraseLineAdapterLater = {};\n';
    const layered = adaptCharEditCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(charEditAdapterId));
});

// The real pinned bundle, derived through all layers and EXECUTED over synthetic observed cells: the pinned probe
// code must agree with the independent replay and contract. Skipped when the collector cache is absent.
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
    ]);
    const { replayCharEditPinned } = await import('./char-edit-grading.mjs');
    const { charEditRecordingContext } = await import('./char-edit-collector.mjs');
    const { charEditContracts } = await import('./char-edit-contracts.mjs');
    const { syntheticCharEditScreens, syntheticCharEditReads, rowOf, repeated, shifted } = await import('./fixtures/char-edit-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    const owner = new URL('./ownership.mjs', import.meta.url).pathname;
    let text = adaptCollector(source.toString(), owner);
    for (const adapt of [...layers, adaptCharEditCollector]) text = adapt(text);
    // Written beside the bundle so its package imports resolve, exactly as the runner places derived bundles.
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-ched-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeCharEditAdapter.id, charEditAdapterId);
        assert.deepEqual([...module.ntildeCharEditAdapter.probeIds], ['editing.insert-chars', 'editing.delete-chars', 'editing.repeat-char']);
        const variants = [
            [() => ({}), 'supported'],
            [(reads, contract) => { const role = contract.feedRoles.at(-1); reads[role] = contract.family === 'repeat' ? reads.before : reads.cursor; }, 'unsupported'], // no-op
            [(reads, contract) => { const role = contract.feedRoles.at(-1);
                reads[role] = contract.family === 'repeat' ? { rows: [rowOf(repeated(1))], cursor: [0, 3] }
                    : { rows: [rowOf(shifted(contract.op === 'ich' ? 'dch' : 'ich', 'ABCDEZQH', 2, 1))], cursor: [0, 2] }; }, 'unsupported'], // reversed shift / wrong count
            [(reads, contract) => { reads[contract.feedRoles[0]] = { rows: [rowOf(contract.family === 'repeat' ? 'AX  YZ' : 'ABCDEZQX')], cursor: [0, 8] }; }, 'inconclusive'],
            [(reads, contract) => { reads[contract.feedRoles[1]] = { ...reads[contract.feedRoles[1]], cursor: [0, 4] }; }, 'inconclusive'],
            [(reads, contract) => { const role = contract.feedRoles.at(-1);
                reads[role] = { rows: [rowOf(contract.family === 'repeat' ? 'BXXXXZ' : 'BB CDEZQH')], cursor: [0, contract.family === 'repeat' ? 5 : 2] }; }, 'inconclusive'], // flank/prefix control changed
        ];
        for (const contract of charEditContracts) {
            for (const [edit, expected] of variants) {
                const readsOf = id => { const reads = syntheticCharEditReads(id); if (id === contract.caseId) edit(reads, contract); return reads; };
                const screens = syntheticCharEditScreens(contract.caseId, undefined, readsOf);
                const context = charEditRecordingContext(contract, screens);
                const live = module.ntildeRunCharEditTermless(contract.caseId, context);
                const replay = replayCharEditPinned(contract, screens);
                if (expected === 'inconclusive') assert.ok(context.fedCount >= 1 && context.fedCount <= contract.pinnedFeeds.length);
                else assert.equal(context.fedCount, contract.pinnedFeeds.length);
                assert.equal(live.observation.outcome, expected, `${contract.caseId} ${live.response}`);
                assert.equal(live.pass, replay.pass);
                assert.equal(live.response, replay.response);
                assert.equal(live.note ?? null, replay.note ?? null);
                assert.deepEqual(live.observation, replay.observation);
                assert.deepEqual(live.assertions ?? [], replay.assertions ?? []);
            }
        }
    } finally { rmSync(file, { force: true }); }
});
