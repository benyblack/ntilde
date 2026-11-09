import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptCharsetsCollector, charsetsAdapterId, charsetsSeams } from './charsets-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => charsetsSeams.join('\n/* gap */\n') + '\n';

test('derives the charsets extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptCharsetsCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeCharsetsAdapter = Object.freeze({\n  id: "${charsetsAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunCharsetsTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["charsets.utf8","charsets.dec-special","charsets.dec-line-drawing","charsets.g0-g1-switching"]'));
    assert.equal(adaptCharsetsCollector(source), derived);
});

test('the seams pin exactly the four registrations, their capture-only and query paths, and the shared result helper', () => {
    for (const id of ['charsets.dec-special', 'charsets.utf8', 'charsets.g0-g1-switching', 'charsets.dec-line-drawing']) {
        assert.equal(charsetsSeams.filter(seam => seam.includes(`probe("${id}"`)).length, 1, id);
    }
    assert.ok(charsetsSeams.some(seam => seam.startsWith('function parserStateResult(')));
    assert.ok(charsetsSeams.every(seam => !/probe\("(?!charsets\.)/.test(seam)), 'no other registered case is pinned');
    // The capture-only and query paths never carry a verdict; pinning them makes an upstream change that gave them one refuse derivation.
    const special = charsetsSeams.find(seam => seam.includes('probe("charsets.dec-special"'));
    for (const part of ['evidence: "pixels"', 'outcome: "inconclusive"', 'Sampled row 3, column 2 horizontal-line appearance']) assert.ok(special.includes(part), part);
    for (const seam of charsetsSeams.filter(item => item.includes('probe("charsets.'))) {
        assert.ok(seam.includes('evidence: "consumed"'), 'the cursor-query path is consumed-bytes evidence only');
        assert.ok(seam.includes('Cursor movement does not verify charset glyph rendering or mapping'));
    }
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of charsetsSeams) {
        assert.throws(() => adaptCharsetsCollector(verbatim().split(seam).join('CHANGED')), /Pinned charsets probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptCharsetsCollector(verbatim() + seam + '\n'), /Pinned charsets probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptCharsetsCollector(''), /Pinned charsets probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria, expected cells or notes were substituted', () => {
    const edits = [
        ['ctx.feed("\\x1B(0q\\x1B(Bq");', 'ctx.feed("\\x1B(0q\\x1B(Bx");'],                       // dec-special: restored source character changed
        ['ctx.feed("\\x1B(0q\\x1B(Bq");', 'ctx.feed("\\x1B(0q");'],                               // dec-special: restoration through output dropped
        ['after[1] === "\\u2500"', 'after[1] === "q"'],                                           // dec-special: expected mapping
        ['ctx.feed("\\x0F\\x1B(B\\x1B)B\\x1B[1;1H\\x1B[2Kqqq");', 'ctx.feed("\\x1B[1;1H\\x1B[2Kqqq");'], // dec-special: charset reset preamble dropped
        ['ctx.feed("\\x1B(0jklmqx\\x1B(B");', 'ctx.feed("\\x1B(0jklmq\\x1B(B");'],                   // dec-line-drawing: bytes
        ['after.slice(0, 6).join("") === "\\u2518\\u2510\\u250C\\u2514\\u2500\\u2502"', 'after.slice(0, 6).join("") === "\\u2518\\u2510\\u250C\\u2514\\u2500\\u2500"'],
        ['ctx.feed("\\x1B(B\\x1B)0l\\x0El\\x0Fl");', 'ctx.feed("\\x1B(B\\x1B)0l\\x0Fl\\x0El");'],       // g0-g1: SO/SI order swapped
        ['after[2] === "\\u250C"', 'after[2] === "l"'],                                           // g0-g1: expected mapping
        ['ctx.feed("\\xE9");', 'ctx.feed("e\\u0301");'],                                          // utf8: decomposed instead of precomposed
        ['ctx.feed("\\u4E16");', 'ctx.feed("\\u4E17");'],                                         // utf8: wrong ideograph
        ['after[2] === "\\u4E16"', 'after[2] === "\\u4E17"'],
        ['Axy?Z', 'Axyz?Z'],
        ['ASCII control changed after Unicode samples', 'changed'],
        ['Need at least five measured columns for Unicode controls', 'too small'],
        ['pass: pass === true,', 'pass: true,'],                                                  // the shared helper
        ['evidence: "consumed"', 'evidence: "parser-state"'],                                     // the query path gaining a verdict class
        ['evidence: "pixels"', 'evidence: "parser-state"'],
    ];
    for (const [from, to] of edits) {
        assert.ok(charsetsSeams.some(seam => seam.includes(from)), from);
        const mutated = charsetsSeams.map(seam => seam.split(from).join(to)).join('\n/* gap */\n');
        assert.throws(() => adaptCharsetsCollector(mutated), /Pinned charsets probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeCharEditAdapterLater = {};\n';
    const layered = adaptCharsetsCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(charsetsAdapterId));
});

// The real pinned bundle, derived through all thirteen layers and EXECUTED over synthetic observed cells: the pinned probe
// code must agree with the independent replay and contract. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const haveBundle = existsSync(bundleUrl);
const layers = async () => Promise.all([
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
]);
/** The real layered bundle written beside the pinned one so its package imports resolve, as the runner places derived bundles. */
async function realModule() {
    const { createHash } = await import('node:crypto');
    const { adaptCollector, upstreamBundleSha256 } = await import('./collector.mjs');
    const { writeFileSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    let text = adaptCollector(source.toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    for (const adapt of [...await layers(), adaptCharsetsCollector]) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-chrs-test-${process.pid}-${Math.random().toString(16).slice(2)}.mjs`, import.meta.url));
    writeFileSync(file, text);
    return { file, module: await import(pathToFileURL(file).href) };
}

test('the real pinned bundle derives through every layer and its pinned probes agree with the replay and the contract over synthetic cells', { skip: haveBundle ? false : 'collector cache not installed' }, async () => {
    const { rmSync } = await import('node:fs');
    const { replayCharsetsPinned } = await import('./charsets-grading.mjs');
    const { charsetsRecordingContext, requirePinnedReplay } = await import('./charsets-collector.mjs');
    const { charsetsContracts, charsetsObservation, gradeCharsetsCase } = await import('./charsets-contracts.mjs');
    const { syntheticCharsetsReads, syntheticCharsetsScreens } = await import('./fixtures/charsets-evidence.mjs');
    const { file, module } = await realModule();
    try {
        assert.equal(module.ntildeCharsetsAdapter.id, charsetsAdapterId);
        assert.deepEqual([...module.ntildeCharsetsAdapter.probeIds], ['charsets.utf8', 'charsets.dec-special', 'charsets.dec-line-drawing', 'charsets.g0-g1-switching']);
        const variants = [
            [() => {}, () => 'supported', () => 'supported'],
            [reads => { reads.target = { ...reads.seed, cursor: reads.target.cursor }; }, () => 'unsupported', () => 'unsupported'],                     // nothing mapped or decoded
            [reads => { reads.seed = { rows: ['zzzzzzz'], cursor: reads.seed.cursor }; }, () => 'inconclusive', () => 'inconclusive'],                       // unusable seed
            [reads => { reads.witnessAscii = { ...reads.witnessAscii, rows: [reads.witnessAscii.rows[0], '┘┐┌└─│┌'] }; }, () => 'supported', () => 'inconclusive'], // stale state only the contract sees
            [reads => { reads.target.cursor = [0, 0]; }, () => 'supported', () => 'unsupported'],                                                        // cursor only the contract sees
        ];
        for (const contract of charsetsContracts) {
            for (const [edit, pinnedOf, contractOf] of variants) {
                const readsOf = id => { const reads = syntheticCharsetsReads(id); if (id === contract.caseId) edit(reads); return reads; };
                const screens = syntheticCharsetsScreens(contract.caseId, readsOf);
                const context = charsetsRecordingContext(contract, screens);
                const live = module.ntildeRunCharsetsTermless(contract.caseId, context);
                const replay = replayCharsetsPinned(contract, screens);
                assert.doesNotThrow(() => requirePinnedReplay(contract, context, live), contract.caseId);
                assert.equal(live.observation.outcome, pinnedOf(), `${contract.caseId} pinned`);
                assert.equal(live.pass, replay.pass);
                assert.equal(live.response, replay.response);
                assert.equal(live.note ?? null, replay.note ?? null);
                assert.deepEqual(live.observation, replay.observation);
                assert.deepEqual(live.assertions ?? [], replay.assertions ?? []);
                assert.equal(gradeCharsetsCase(contract, charsetsObservation(contract, screens)).outcome, contractOf(), `${contract.caseId} contract`);
                // The pinned restoration bytes ran whenever anything was fed.
                assert.equal(context.finalized, contract.pinnedFinally !== null && context.fedCount > 0);
            }
        }
    } finally { rmSync(file, { force: true }); }
});

test('the driver replays each plan byte-exactly through a fence, journals one read per group and the live pinned code grades the journal', { skip: haveBundle ? false : 'collector cache not installed' }, async () => {
    const { rmSync } = await import('node:fs');
    const { collectCharsetsGrading } = await import('./charsets-collector.mjs');
    const { charsetsContracts, charsetsReset } = await import('./charsets-contracts.mjs');
    const { syntheticCharsetsEvidence } = await import('./fixtures/charsets-evidence.mjs');
    const { buildCharsetsGradingReport } = await import('./charsets-grading.mjs');
    const { file, module } = await realModule();
    try {
        const runId = 'e'.repeat(32);
        const evidence = syntheticCharsetsEvidence(runId);
        const writes = [], kinds = [];
        let next = 0;
        const fence = { async write(bytes) { writes.push(bytes); return '\x1b[?62;4;22c'; } };
        const owner = async (kind) => { kinds.push(kind); return { screen: evidence.frames[next++].screen }; };
        const run = await collectCharsetsGrading({ config: { runId }, fence, owner, gradingModule: module });
        assert.equal(kinds[0], 'chrs-start');
        assert.equal(kinds[1], 'chrs-bind');
        assert.equal(kinds.at(-1), 'chrs-finish');
        assert.equal(writes[0], '\x1b[?1049h' + charsetsReset + 'NTILDE_CS_' + runId);
        assert.equal(writes.at(-1), charsetsReset + '\x1b[?1049l');
        const planned = charsetsContracts.flatMap(contract => contract.plan.filter(step => step.feed).map(step => step.feed));
        assert.deepEqual(writes.slice(1, -1), planned);
        assert.deepEqual(run.cases.map(item => item.caseId), charsetsContracts.map(contract => contract.caseId));
        assert.deepEqual(run.cases.map(item => item.pinned.observation.outcome), ['supported', 'supported', 'supported', 'supported']);
        const adapter = { ...evidence.adapter, bundleSha256: 'a'.repeat(64) };
        const report = buildCharsetsGradingReport(evidence.frames, run, runId, adapter, { replayModule: module });
        assert.deepEqual(report.cases.map(item => item.conclusion.outcome), ['supported', 'supported', 'supported', 'supported']);
    } finally { rmSync(file, { force: true }); }
});
