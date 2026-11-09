import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import { adaptScrollbackCollector, scrollbackAdapterId, scrollbackSeams, pinnedScrollbackSection, pinnedScrollbackSectionSha256 } from './scrollback-grading-collector.mjs';

const { sectionStart, sectionEnd, helperSeams } = scrollbackSeams;
const sha = text => createHash('sha256').update(text).digest('hex');
const body = 'function tooSmall2() {}\nvar scrollbackProbes;\n';
const sectionDigest = sha(sectionStart + body);
/** A synthetic source: the section, then every helper seam exactly once. */
const verbatim = (section = body) => [sectionStart + section + sectionEnd, ...helperSeams].join('\n/* gap */\n') + '\n';
const adapt = (source, digest = sectionDigest) => adaptScrollbackCollector(source, digest);

test('derives the scrolling/history extension by appending exports only', () => {
    const source = verbatim();
    const derived = adapt(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeScrollbackAdapter = Object.freeze({\n  id: "${scrollbackAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunScrollbackTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["scrollback.accumulate","scrollback.total-lines","scrollback.scroll-up","scrollback.reverse-index","scrollback.scroll-down","scrollback.set-region","scrollback.alt-screen","scrollback.decstbm","scrollback.decstbm-reset"]'));
    assert.equal(adapt(source), derived);
});

test('the pinned section is delimited by the scrollback and charsets markers and pinned by one digest', () => {
    assert.equal(sectionStart, '// ../probe-defs/src/scrollback.ts\n');
    assert.equal(sectionEnd, '// ../probe-defs/src/charsets.ts\n');
    assert.match(pinnedScrollbackSectionSha256, /^[a-f\d]{64}$/);
    assert.equal(pinnedScrollbackSection(verbatim()), sectionStart + body);
    assert.throws(() => adaptScrollbackCollector(verbatim()), /Pinned scrollback probes changed/, 'the production digest rejects a synthetic section');
});

test('refuses derivation when the section, its markers or any helper seam changed, repeated or vanished', () => {
    for (const seam of helperSeams) {
        assert.throws(() => adapt(verbatim().split(seam).join('CHANGED')), /helper seam changed/, seam.slice(0, 40));
        assert.throws(() => adapt(verbatim() + seam + '\n'), /helper seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adapt(verbatim().replace(sectionStart, 'x')), /section seam changed/);
    assert.throws(() => adapt(verbatim() + sectionStart), /section seam changed/);
    assert.throws(() => adapt(verbatim().replace(sectionEnd, 'x')), /section seam changed/);
    assert.throws(() => adapt(verbatim() + sectionEnd), /section seam changed/);
    assert.throws(() => adapt(''), /helper seam changed/);
    assert.throws(() => adapt(verbatim(body + 'tampered')), /Pinned scrollback probes changed/);
    assert.throws(() => adapt(verbatim(body.replace('tooSmall2', 'tooSmall3'))), /Pinned scrollback probes changed/);
});

test('the helper seams are the pinned isBlank and parserStateResult plus the retained charsets exports', () => {
    assert.ok(helperSeams.some(seam => seam.startsWith('function isBlank(')));
    assert.ok(helperSeams.some(seam => seam.startsWith('function parserStateResult(') && seam.includes('outcome: pass === null ? "inconclusive"')));
    assert.ok(helperSeams.includes('export const ntildeCharsetsAdapter = Object.freeze({'));
    assert.ok(helperSeams.includes('export function ntildeRunCharsetsTermless(probeId, ctx) {'));
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeLaterAdapter = {};\n';
    const layered = adapt(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(scrollbackAdapterId));
});

// The real pinned bundle, derived through all sixteen layers and EXECUTED over synthetic observed states: the pinned
// probe code must agree with the independent replay and the contract. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const haveBundle = existsSync(bundleUrl);
test('the real pinned bundle derives through every layer and its nine probes agree with the replay', { skip: haveBundle ? false : 'collector cache not installed' }, async () => {
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
    ]);
    const { replayScrollbackPinned } = await import('./scrollback-grading.mjs');
    const { scrollbackRecordingContext } = await import('./scrollback-collector.mjs');
    const { syntheticScrollbackContracts, syntheticScrollbackScreens } = await import('./fixtures/scrollback-evidence.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(sha(source), upstreamBundleSha256);
    const owner = new URL('./ownership.mjs', import.meta.url).pathname;
    let text = adaptCollector(source.toString(), owner);
    for (const adapt_ of [...layers, adaptScrollbackCollector]) text = adapt_(text);
    // Written beside the bundle so its package imports resolve, exactly as the runner places derived bundles.
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-scrl-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeScrollbackAdapter.id, scrollbackAdapterId);
        const contracts = syntheticScrollbackContracts();
        assert.deepEqual([...module.ntildeScrollbackAdapter.probeIds], contracts.map(contract => contract.caseId));
        const outcome = (contract, edit) => {
            const screens = syntheticScrollbackScreens(contract, undefined, edit);
            const context = scrollbackRecordingContext(contract, screens);
            const live = module.ntildeRunScrollbackTermless(contract.caseId, context);
            const replay = replayScrollbackPinned(contract, screens);
            assert.ok(context.complete, contract.caseId);
            assert.equal(live.pass, replay.pass, contract.caseId);
            assert.equal(live.response, replay.response, contract.caseId);
            assert.equal(live.note ?? null, replay.note ?? null, contract.caseId);
            assert.deepEqual(live.observation, replay.observation, contract.caseId);
            assert.deepEqual(live.assertions ?? [], replay.assertions ?? [], contract.caseId);
            return live;
        };
        const expectedFaithful = { 'scrollback.set-region': 'inconclusive', 'scrollback.alt-screen': 'inconclusive', 'scrollback.decstbm-reset': 'inconclusive' };
        for (const contract of contracts) {
            const live = outcome(contract);
            assert.equal(live.observation.outcome, expectedFaithful[contract.caseId] ?? 'supported', contract.caseId);
            if (!contract.decides) assert.equal(live.pass, false, `${contract.caseId} is null by construction`);
        }
        const byId = id => contracts.find(contract => contract.caseId === id);
        const edit = (role, change) => (name, state) => { if (name === role) change(state); return state; };
        // Deciding probes: a no-op or wrong state is unsupported or inconclusive in the pinned code itself.
        assert.equal(outcome(byId('scrollback.scroll-up'), edit('after', state => { state.lines[1] = 'B'; state.lines[2] = 'C'; state.lines[3] = 'D'; })).observation.outcome, 'unsupported');
        assert.equal(outcome(byId('scrollback.scroll-up'), edit('after', state => { state.lines = ['A', '', 'B', 'C', 'E', ...state.lines.slice(5)]; })).observation.outcome, 'inconclusive');
        assert.equal(outcome(byId('scrollback.scroll-up'), edit('seed', state => { state.history = ['stale']; })).observation.outcome, 'inconclusive');
        assert.equal(outcome(byId('scrollback.reverse-index'), edit('after', state => { state.lines[0] = 'A'; })).observation.outcome, 'unsupported');
        assert.equal(outcome(byId('scrollback.scroll-down'), edit('after', state => { state.lines[0] = 'LINE1'; })).observation.outcome, 'unsupported');
        assert.equal(outcome(byId('scrollback.decstbm'), edit('after', state => { state.lines[0] = ''; })).observation.outcome, 'unsupported');
        assert.equal(outcome(byId('scrollback.decstbm'), edit('innerBefore', state => { state.lines[2] = ''; })).observation.outcome, 'inconclusive');
        assert.equal(outcome(byId('scrollback.accumulate'), edit('final', state => { state.history = []; })).observation.outcome, 'unsupported');
        assert.equal(outcome(byId('scrollback.total-lines'), edit('final', state => { state.history = []; })).observation.outcome, 'unsupported');
        // The pinned total-lines definition is rows plus history, so the pinned code sees growth exactly when history grew.
        const final = outcome(byId('scrollback.total-lines'));
        assert.match(final.response, /"baseline":\{"screenLines":16,"totalLines":16,"viewportY":0\}/);
        assert.match(final.response, /"scroll":\{"screenLines":16,"totalLines":27,"viewportY":0\}/);
    } finally { rmSync(file, { force: true }); }
});
