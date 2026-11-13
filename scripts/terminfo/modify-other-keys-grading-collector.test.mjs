import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptModifyOtherKeysCollector, modifyOtherKeysAdapterId, modifyOtherKeysSeams } from './modify-other-keys-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => modifyOtherKeysSeams.join('\n/* gap */\n') + '\n';

test('derives the modifyOtherKeys extension by appending exports only', () => {
    const source = verbatim(), derived = adaptModifyOtherKeysCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeModifyOtherKeysAdapter = Object.freeze({\n  id: "${modifyOtherKeysAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunModifyOtherKeysTermless(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify(['input.modify-other-keys'])));
});

test('the seams pin the input.modify-other-keys registration and the previous layer', () => {
    const registration = modifyOtherKeysSeams.find(seam => seam.includes('probe("input.modify-other-keys"'));
    for (const part of ['declaredModifyOtherKeys', 'ctx.capabilities.extensions.has("modifyOtherKeys")', 'outcome: "inconclusive"',
        'reason: "insufficient-evidence"', 'evidence: "legacy"', 'evidence: "none"']) assert.ok(registration.includes(part), part);
    assert.ok(!registration.includes('probe("input.modify-other-keys-3"'), 'the level-3 probe is never a seam of this layer');
    assert.ok(modifyOtherKeysSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
    assert.ok(modifyOtherKeysSeams.includes('export function ntildeRunLrMarginsTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of modifyOtherKeysSeams) {
        assert.throws(() => adaptModifyOtherKeysCollector(verbatim().split(seam).join('CHANGED')), /Pinned modifyOtherKeys probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptModifyOtherKeysCollector(verbatim() + seam + '\n'), /Pinned modifyOtherKeys probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would gain a verdict or change its bytes', () => {
    for (const [from, to] of [['declaredModifyOtherKeys', 'declaredSomethingElse'],
        ['outcome: "inconclusive"', 'outcome: "supported"'],
        ['pass: false', 'pass: true']]) {
        assert.ok(modifyOtherKeysSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptModifyOtherKeysCollector(modifyOtherKeysSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through every layer and the pinned code runs over the served declaration', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const { adaptCollector } = await import('./collector.mjs');
    const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins'];
    const layers = await Promise.all(names.map(name => import(name === 'grading' ? './grading-collector.mjs' : './' + name + '-grading-collector.mjs')
        .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
    const { replayModifyOtherKeysPinned, modifyOtherKeysRecordingContext, servedDeclaration, modifyOtherKeysCaseIds } = await import('./modify-other-keys-contracts.mjs');
    const { collectModifyOtherKeysPinned } = await import('./modify-other-keys-collector.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    for (const adapt of [...layers, adaptModifyOtherKeysCollector]) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-mok-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeModifyOtherKeysAdapter.probeIds], modifyOtherKeysCaseIds);
        for (const served of [{ declaredModifyOtherKeys: false }, { declaredModifyOtherKeys: true }, { declaredModifyOtherKeys: undefined }]) {
            const context = modifyOtherKeysRecordingContext(served);
            const { probeId, ...live } = module.ntildeRunModifyOtherKeysTermless('input.modify-other-keys', context);
            assert.equal(probeId, 'input.modify-other-keys');
            assert.deepEqual(live, replayModifyOtherKeysPinned(served));
            assert.equal(live.observation?.outcome, 'inconclusive', 'the pinned probe never decides');
        }
        // The worker's collection over a child refusal reply replays the pinned code exactly once.
        const controls = { 'input.mok.declaration.xtgettcap': Buffer.from('\x1bP0+r\x1b\\' + '\x1b[?66;2$y\x1b[?9;2$y', 'latin1').toString('hex') };
        const run = collectModifyOtherKeysPinned({ geometry: { rows: 12, cols: 120 }, controls, gradingModule: module });
        assert.deepEqual(run.probeIds, ['input.modify-other-keys']);
        assert.deepEqual(run.cases[0].served, { declaredModifyOtherKeys: false, source: 'child-xtgettcap-modifyOtherKeys' });
        assert.equal(run.cases[0].pinned.response, '{"declaredModifyOtherKeys":false}');
    } finally { rmSync(file, { force: true }); }
});
