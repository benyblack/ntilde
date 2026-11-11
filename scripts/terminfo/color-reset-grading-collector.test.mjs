import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptColorResetCollector, colorResetAdapterId, colorResetSeams } from './color-reset-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => colorResetSeams.join('\n/* gap */\n') + '\n';

test('derives the runtime-color reset extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptColorResetCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeColorResetAdapter = Object.freeze({\n  id: "${colorResetAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunColorResetTermless(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify(['extensions.osc104-reset-palette', 'extensions.osc110-reset-fg', 'extensions.osc111-reset-bg', 'extensions.osc112-reset-cursor'])));
});

test('the seams pin the four registrations (termless colorResetProbe, terminal unmeasuredStateEffect), the probe, its helpers and the previous layer', () => {
    const registrations = colorResetSeams.filter(seam => seam.startsWith('    probe("extensions.osc1'));
    assert.deepEqual(registrations.map(seam => seam.match(/colorResetProbe\(([^)]*)\)/)[1]), ['4, 104, 0', '10, 110', '11, 111', '12, 112']);
    for (const seam of registrations) assert.ok(seam.includes('() => Promise.resolve(unmeasuredStateEffect('), 'the terminal path is inconclusive by design');
    const probe = colorResetSeams.find(seam => seam.startsWith('function colorResetProbe('));
    for (const part of ['ctx.feedCapture(query)', 'probeForeground(original)', 'sameRgb2(restored, original)', 'finally {']) assert.ok(probe.includes(part), part);
    for (const name of ['sameRgb2', 'probeForeground', 'oscReplyResult', 'unmeasuredStateEffect']) assert.ok(colorResetSeams.some(seam => seam.startsWith(`function ${name}(`)), name);
    assert.ok(colorResetSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of colorResetSeams) {
        assert.throws(() => adaptColorResetCollector(verbatim().split(seam).join('CHANGED')), /Pinned runtime-color reset probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptColorResetCollector(verbatim() + seam + '\n'), /Pinned runtime-color reset probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change its bytes, its criterion or the terminal path', () => {
    for (const [from, to] of [['rgb:aa/bb/cc', 'rgb:aa/bb/dd'], ['sameRgb2(restored, original)', 'sameRgb2(restored, changed)'], ['evidence: \\"none\\"', 'evidence: \\"query\\"'],
        ['unmeasuredStateEffect(\\"OSC 110 foreground reset\\")', 'unmeasuredStateEffect(\\"OSC 110 reset\\")'], ['colorResetProbe(12, 112)', 'colorResetProbe(12, 117)']]) {
        const changed = colorResetSeams.map(seam => seam.replace(from.replaceAll('\\"', '"'), to.replaceAll('\\"', '"')));
        assert.notDeepEqual(changed, colorResetSeams, from);
        assert.throws(() => adaptColorResetCollector(changed.join('\n/* gap */\n')), /seam changed/, from);
    }
});

const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through every layer; its code agrees with the replay for every case and a terminal path never touches the terminal', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const { adaptCollector } = await import('./collector.mjs');
    const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins'];
    const layers = await Promise.all(names.map(name => import(name === 'grading' ? './grading-collector.mjs' : './' + name + '-grading-collector.mjs')
        .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
    const { replayColorResetPinned } = await import('./color-reset-grading.mjs');
    const { colorResetRecordingContext } = await import('./color-reset-collector.mjs');
    const { colorResetContracts } = await import('./color-reset-contracts.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    for (const adapt of [...layers, adaptColorResetCollector]) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-crst-test-${process.pid}.mjs`, import.meta.url));
    const reply = (code, value) => ({ raw: `\x1b]${code};rgb:${value}\x1b\\` });
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        for (const [index, contract] of colorResetContracts.entries()) {
            const code = ['4;0', '10', '11', '12'][index];
            const original = 'c0c0/c0c0/c0c0', requested = 'aaaa/bbbb/cccc';
            for (const replies of [[original, requested, original], [original, requested, '1111/2222/3333'], [original, original, original]].map(set => set.map(value => reply(code, value)))) {
                const context = colorResetRecordingContext(contract, replies);
                const { probeId, ...live } = module.ntildeRunColorResetTermless(contract.caseId, context);
                assert.equal(probeId, contract.caseId);
                assert.equal(context.fedCount, 5);
                assert.deepEqual(live, replayColorResetPinned(contract, replies));
            }
            const none = colorResetRecordingContext(contract, [null, null, null]);
            assert.equal(module.ntildeRunColorResetTermless(contract.caseId, none).observation.outcome, 'inconclusive');
            assert.equal(none.fedCount, 1, 'a missing original reply stops the pinned probe after its first capture');
        }
    } finally { rmSync(file, { force: true }); }
});
