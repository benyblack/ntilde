import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';
import { adaptSgrColorsCollector, sgrColorsAdapterId } from './sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { adaptCollector } from './collector.mjs';
import { sgrColorsCaseIds, sgrColorsContracts, sgrColorsCellAt } from './sgr-colors-contracts.mjs';
import { replaySgrColorsPinned } from './sgr-colors-grading.mjs';
import { syntheticSgrColorsScreen } from './fixtures/sgr-colors-evidence.mjs';

// A minimal stand-in for the first grading extension that still carries every
// seam this derivation binds to. CI has no collector installation; the live
// tests below exercise the real layered derivation when the bundle is present.
const seams = [
    'namedColorProbe("sgr.fg.standard", "fg", 31, 34)',
    'namedColorProbe("sgr.bg.standard", "bg", 41, 44)',
    'resetProbe("sgr.selective-reset.bold", "\\x1B[1;2;3m", "\\x1B[22m", ["bold", "dim"], ["italic"])',
    'resetProbe("sgr.selective-reset.italic", "\\x1B[1;3m", "\\x1B[23m", ["italic"], ["bold"])',
    'resetProbe("sgr.reset", "\\x1B[1;3;4m", "\\x1B[0m", ["bold", "italic", "underline"])',
    'function measuredReset(ctx, id, setup, reset, set, preserve) {',
    'function namedColorProbe(id, channel, firstCode, secondCode) {',
    'the two named colors select distinct exposed ',
    'measured setup attributes clear on Y and unrelated attributes remain set',
];
const syntheticFirstExtension = seams.map((seam, index) => `const seam${index} = \`${seam}\`;`).join('\n')
    + '\nexport const ntildeGradingAdapter = Object.freeze({\n  id: "ntilde-owned-ascii-sgr-grading-v1"\n});\n';

const check = source => {
    const syntax = spawnSync(process.execPath, ['--check', '--input-type=module'], { input: source, encoding: 'utf8' });
    assert.equal(syntax.status, 0, syntax.stderr);
};

test('derives the layered extension from the first grading extension without rewriting it', () => {
    const derived = adaptSgrColorsCollector(syntheticFirstExtension);
    check(derived);
    assert.ok(derived.startsWith(syntheticFirstExtension));
    assert.ok(derived.includes(`export const ntildeSgrColorsAdapter = Object.freeze({\n  id: "${sgrColorsAdapterId}"`));
    assert.ok(derived.includes(`probeIds: ${JSON.stringify([...sgrColorsCaseIds])}`));
    assert.ok(derived.includes('export function ntildeRunSgrColorsTermless(probeId, ctx)'));
});

for (const [name, mutate] of [
    ['a changed standard-fg definition', s => s.replace('"fg", 31, 34', '"fg", 31, 35')],
    ['a changed selective-reset fixture', s => s.replace('"\\x1B[1;2;3m", "\\x1B[22m"', '"\\x1B[1;2;3m", "\\x1B[22;24m"')],
    ['a changed reset attribute list', s => s.replace('"\\x1B[0m", ["bold", "italic", "underline"]', '"\\x1B[0m", ["bold", "italic"]')],
    ['a removed criterion string', s => s.replace('unrelated attributes remain set', 'unrelated attributes remain')],
    ['a missing factory', s => s.replace('function namedColorProbe(id, channel, firstCode, secondCode) {', 'function namedColor(id, channel) {')],
]) test('refuses derivation after ' + name, () => {
    assert.throws(() => adaptSgrColorsCollector(mutate(syntheticFirstExtension)), /seam changed|Expected the first ASCII\/SGR grading extension/);
});

test('refuses a source that is not the first grading extension', () => {
    assert.throws(() => adaptSgrColorsCollector(seams.join('\n')), /Expected the first ASCII\/SGR grading extension/);
    assert.throws(() => adaptSgrColorsCollector('export async function collectNtildeRun() {}'), /seam changed/);
});

const bundle = fileURLToPath(new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url));

test('derives from the real pinned bundle and executes the five pinned probes over live-shaped cells', { skip: !existsSync(bundle) }, async () => {
    const owned = adaptCollector(readFileSync(bundle, 'utf8'), fileURLToPath(new URL('./ownership.mjs', import.meta.url)));
    const first = adaptGradingCollector(owned);
    const derived = adaptSgrColorsCollector(first);
    check(derived);
    assert.ok(derived.startsWith(first));
    // Keep the derived module beside the bundle so its npm imports resolve.
    const path = fileURLToPath(new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/.sgrcolors-derive-test.mjs', import.meta.url));
    writeFileSync(path, derived);
    try {
        const module = await import(pathToFileURL(path).href);
        assert.deepEqual([...module.ntildeSgrColorsAdapter.probeIds], [...sgrColorsCaseIds]);
        for (const contract of sgrColorsContracts) {
            const screen = syntheticSgrColorsScreen(contract.caseId);
            const ctx = {
                feed(text) { assert.equal(typeof text, 'string'); },
                getText() { throw new Error('unexpected text read'); },
                getCell: (row, col) => sgrColorsCellAt(screen, row, col),
            };
            // The REAL pinned termless code over the projected cells.
            const live = module.ntildeRunSgrColorsTermless(contract.caseId, ctx);
            const replayed = replaySgrColorsPinned(contract, [screen]);
            assert.equal(live.pass, replayed.pass, contract.caseId);
            assert.equal(live.response, replayed.response, contract.caseId);
            assert.equal(live.observation.outcome, replayed.observation.outcome, contract.caseId);
            assert.equal(JSON.stringify(live.assertions ?? null), JSON.stringify(replayed.assertions?.length ? replayed.assertions : null), contract.caseId);
            if (contract.pinnedExpectation === 'inapplicable') {
                assert.equal(live.observation.outcome, 'inconclusive');
                assert.equal(live.observation.note, contract.pinnedControlNote);
            } else {
                assert.equal(live.observation.outcome, 'supported', contract.caseId);
            }
        }
    } finally { rmSync(path); }
});
