import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptReflowCollector, asciiReflowAdapterId, asciiReflowSeams } from './ascii-reflow-grading-collector.mjs';
import { adaptOscTitleCollector } from './osc-title-grading-collector.mjs';
import { asciiReflowCaseIds } from './ascii-reflow-contracts.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => asciiReflowSeams.join('\n/* gap */\n') + '\n';

test('derives the ASCII reflow extension by appending exports only', () => {
    const source = verbatim(), derived = adaptReflowCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeReflowAdapter = Object.freeze({\n  id: "${asciiReflowAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunReflowTermless(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...asciiReflowCaseIds])));
});

test('the seam pins the pinned reflow registration with both always-inconclusive arms', () => {
    const [registration, parserStateResult, ...predecessor] = asciiReflowSeams;
    assert.ok(registration.includes('probe("extensions.reflow", (ctx) => {'));
    assert.ok(registration.includes('JSON.stringify({ declaredReflow: ctx.capabilities.reflow === true })'));
    assert.ok(registration.includes('outcome: "inconclusive", reason: "insufficient-evidence", evidence: "legacy"'));
    assert.ok(registration.includes('evidence: "none"'));
    assert.ok(registration.includes('No controlled resize and cell-content readback to measure reflow'));
    assert.equal((registration.match(/const note = /g) ?? []).length, 2);
    assert.ok(parserStateResult.startsWith('function parserStateResult(pass, expected, state, note) {'));
    assert.ok(predecessor.includes('export const ntildeOscTitleAdapter = Object.freeze({'), 'derived from the OSC-title layer');
    assert.ok(predecessor.includes('export function ntildeRunOscTitleTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of asciiReflowSeams) {
        assert.throws(() => adaptReflowCollector(verbatim().split(seam).join('CHANGED')), /seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptReflowCollector(verbatim() + seam + '\n'), /seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change its bytes, its capability read or its verdict', () => {
    for (const [from, to] of [['ctx.capabilities.reflow === true', 'ctx.capabilities.reflow !== true'],
        ['"legacy"', '"parser-state"'], ['pass: false', 'pass: true'],
        ['No controlled resize and cell-content readback to measure reflow', 'A controlled resize readback exists'],
        ['insufficient-evidence', 'declared-capability']]) {
        assert.ok(asciiReflowSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptReflowCollector(asciiReflowSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through the linear chain and the pinned reflow code runs over the served capability',
    { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const { adaptCollector } = await import('./collector.mjs');
    const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins', 'title-stack'];
    const layers = await Promise.all(names.map(name => import(name === 'grading' ? './grading-collector.mjs' : './' + name + '-grading-collector.mjs')
        .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    for (const adapt of [...layers, adaptOscTitleCollector, adaptReflowCollector]) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-rfl-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeReflowAdapter.probeIds], [...asciiReflowCaseIds]);
        const read = [];
        const capabilities = new Proxy({}, { get: (target, key) => { read.push(String(key)); return undefined; } });
        const { probeId, ...result } = module.ntildeRunReflowTermless('extensions.reflow', { capabilities });
        assert.equal(probeId, 'extensions.reflow');
        assert.deepEqual(read, ['reflow']);
        assert.deepEqual(result, { pass: false, response: '{"declaredReflow":false}',
            note: 'No controlled resize and cell-content readback to measure reflow',
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy',
                note: 'No controlled resize and cell-content readback to measure reflow' } });
        assert.notEqual(result.observation.outcome, 'unsupported', 'the pinned probe never returns unsupported');
    } finally { rmSync(file, { force: true }); }
});
