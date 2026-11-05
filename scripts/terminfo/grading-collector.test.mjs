import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';
import { adaptGradingCollector, gradingAdapterId } from './grading-collector.mjs';
import { adaptCollector } from './collector.mjs';
import { asciiSgrCaseIds } from './ascii-sgr-contracts.mjs';

// A minimal stand-in for the owned-adapter output that still carries every
// seam the derivation binds to. CI has no collector installation; the live
// tests below exercise the real bundle when it is present.
const seams = [
    'probe("text.basic", (ctx) => {\n        ctx.feed("\\x1B[2J\\x1B[H");',
    '"Hello appears after a measured empty control"',
    'probe("text.overwrite", (ctx) => {\n        ctx.feed("AB");',
    '"C overwrites A while B remains"',
    'sgrProbe("sgr.bold", "\\x1B[1m", (cell) => cell.bold === true)',
    'sgrProbe("sgr.italic", "\\x1B[3m", (cell) => cell.italic === true)',
    'var init_src = __esm(() => {',
];
// Template literals keep every seam byte verbatim while staying valid modules.
const syntheticOwnedAdapter = seams.map((seam, index) => `const seam${index} = \`${seam}\`;`).join('\n')
    + '\nexport async function collectNtildeRun(assertion) {\n'
    + '  return collectProbeRun({ ntildeOwner: assertion, out: process.stdout });\n}\n';

const check = source => {
    const syntax = spawnSync(process.execPath, ['--check', '--input-type=module'], { input: source, encoding: 'utf8' });
    assert.equal(syntax.status, 0, syntax.stderr);
};

test('derives the grading extension from a valid owned adapter without rewriting it', () => {
    const derived = adaptGradingCollector(syntheticOwnedAdapter);
    check(derived);
    assert.ok(derived.startsWith(syntheticOwnedAdapter));
    assert.ok(derived.includes(`export const ntildeGradingAdapter = Object.freeze({\n  id: "${gradingAdapterId}"`));
    assert.ok(derived.includes(`probeIds: ${JSON.stringify([...asciiSgrCaseIds])}`));
    assert.ok(derived.includes('export function ntildeRunTermless(probeId, ctx)'));
});

for (const [name, mutate] of [
    ['a changed text.basic fixture', s => s.replace(seams[0], 'probe("text.basic", (ctx) => {\n        ctx.feed("\\x1B[3J\\x1B[H");')],
    ['a changed bold criterion', s => s.replace('(cell) => cell.bold === true', '(cell) => cell.bright === true')],
    ['a changed italic criterion', s => s.replace('(cell) => cell.italic === true', '(cell) => cell.slanted === true')],
    ['a removed overwrite control', s => s.replace(seams[2], 'probe("text.overwrite", (ctx) => {\n        ctx.feed("ABCD");')],
    ['a removed criterion string', s => s.replace(seams[1], '"Hello appears"')],
    ['a missing lazy initializer', s => s.replace(seams[6], 'var init_src = () => { };')],
]) test('refuses derivation after ' + name, () => {
    assert.throws(() => adaptGradingCollector(mutate(syntheticOwnedAdapter)), /seam changed|Expected the existing owned collector/);
});

test('refuses a source that is not an owned-adapter derivation', () => {
    const upstream = seams.join('\n');
    assert.throws(() => adaptGradingCollector(upstream), /Expected the existing owned collector/);
    assert.throws(() => adaptGradingCollector('program.parse();'), /seam changed/);
});

const bundle = fileURLToPath(new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url));

test('derives from the real pinned bundle and grades a live-shaped observation', { skip: !existsSync(bundle) }, async () => {
    const owned = adaptCollector(readFileSync(bundle, 'utf8'), fileURLToPath(new URL('./ownership.mjs', import.meta.url)));
    const derived = adaptGradingCollector(owned);
    check(derived);
    // Everything upstream of the appended extension stays byte-identical.
    assert.ok(derived.startsWith(owned));
    // Keep the derived module beside the bundle so its npm imports resolve.
    const path = fileURLToPath(new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/.grading-derive-test.mjs', import.meta.url));
    const { writeFileSync, rmSync } = await import('node:fs');
    writeFileSync(path, derived);
    try {
        const module = await import(pathToFileURL(path).href);
        assert.deepEqual([...module.ntildeGradingAdapter.probeIds], [...asciiSgrCaseIds]);
        // The pinned text.basic probe grades a real observed screen pair.
        const screens = [
            { lines: ['', '', ''], observed: true },
            { lines: ['Hello', '', ''], observed: true },
        ];
        let read = 0;
        const result = module.ntildeRunTermless('text.basic', {
            feed() {},
            getText() { return screens[read++].lines.join('\n'); },
            getCell() { throw new Error('unexpected'); },
        });
        assert.equal(result.observation.outcome, 'supported');
        assert.equal(result.pass, true);
        assert.equal(result.observation.evidence, 'parser-state');
        assert.deepEqual(result.assertions, [{ kind: 'positive',
            expected: 'Hello appears after a measured empty control',
            observed: JSON.stringify({ before: screens[0].lines.join('\n'), after: screens[1].lines.join('\n') }) }]);
        // The pinned control path stays inconclusive when the control fails.
        const controlFailed = module.ntildeRunTermless('text.basic', {
            feed() {}, getText() { return 'Hello'; }, getCell() { throw new Error('unexpected'); },
        });
        assert.equal(controlFailed.observation.outcome, 'inconclusive');
        assert.equal(controlFailed.observation.reason, 'insufficient-evidence');
        assert.equal(controlFailed.observation.note, 'Control already contained Hello');
    } finally { rmSync(path); }
});
