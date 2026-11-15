import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { adaptSelectionColorsCollector, selectionColorsAdapterId, selectionColorsSeams } from './selection-colors-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => selectionColorsSeams.join('\n/* gap */\n') + '\n';

test('derives the selection-color extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptSelectionColorsCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeSelectionColorsAdapter = Object.freeze({\n  id: "${selectionColorsAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunSelectionColorsTermless(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify(['extensions.osc17-highlight-bg', 'extensions.osc19-highlight-fg',
        'extensions.osc117-reset-highlight-bg', 'extensions.osc119-reset-highlight-fg'])));
});

test('the seams pin the four pinned registrations verbatim, the shared probe factory body and the helpers the verdicts run through', () => {
    const resetSeams = selectionColorsSeams.filter(seam => seam.includes('...probe("extensions.osc117-reset-highlight-bg"') || seam.includes('...probe("extensions.osc119-reset-highlight-fg"'));
    assert.equal(resetSeams.length, 2);
    for (const seam of resetSeams) {
        assert.ok(seam.includes('notTestedResult("highlight'), 'the termless path reports no semantic observable');
        assert.ok(seam.includes('unverifiedEffect(pos ?'), 'the terminal path is inconclusive by construction');
        assert.ok(seam.includes('termlessObservationEvidence: "parser-state"'));
    }
    const registrations = selectionColorsSeams.filter(seam => seam.startsWith('    oscColorQueryProbe("extensions.osc'));
    assert.deepEqual(registrations.map(seam => seam.match(/oscColorQueryProbe\("extensions\.(osc\d+[^"]*)", (\d+)\)/).slice(1).join('=')),
        ['osc17-highlight-bg=17', 'osc19-highlight-fg=19']);
    const factory = selectionColorsSeams.find(seam => seam.startsWith('function oscColorQueryProbe('));
    for (const part of ['ctx.feedCapture(querySeq)', 'queryWithSentinelOutcome(querySeq, pattern)', 'termlessObservationEvidence: "query"']) {
        assert.ok(factory.includes(part), part);
    }
    for (const name of ['queryOnly', 'notTestedResult', 'unverifiedEffect']) {
        assert.ok(selectionColorsSeams.some(seam => seam.startsWith(`function ${name}(`)), name);
    }
    assert.ok(selectionColorsSeams.includes('function oscReplyResult(raw, frame, expected, prefix) {'), 'the shared reply grader is re-checked');
    assert.ok(selectionColorsSeams.includes('export const ntildeOscTitleAdapter = Object.freeze({'), 'derived from the OSC title layer');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of selectionColorsSeams) {
        assert.throws(() => adaptSelectionColorsCollector(verbatim().split(seam).join('CHANGED')), /Pinned selection-color probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptSelectionColorsCollector(verbatim() + seam + '\n'), /Pinned selection-color probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change its bytes, its criterion or a terminal path', () => {
    for (const [from, to] of [
        ['notTestedResult(\\"highlight background color\\", { cell })', 'notTestedResult(\\"highlight bg\\", { cell })'],
        ['\\x1B]117\\x07X', '\\x1B]117\\x07Y'],
        ['evidence: \\"consumed\\"', 'evidence: \\"none\\"'],
        ['oscColorQueryProbe(\\"extensions.osc17-highlight-bg\\", 17)', 'oscColorQueryProbe(\\"extensions.osc17-highlight-bg\\", 18)'],
        ['Complete OSC ${oscCode} color reply', 'OSC ${oscCode} reply'],
        ['termWrites: \\"query\\"', 'termWrites: \\"behavior\\"'],
    ]) {
        const changed = selectionColorsSeams.map(seam => seam.replace(from.replaceAll('\\"', '"'), to.replaceAll('\\"', '"')));
        assert.notDeepEqual(changed, selectionColorsSeams, from);
        assert.throws(() => adaptSelectionColorsCollector(changed.join('\n/* gap */\n')), /seam changed/, from);
    }
});

const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through every layer to this one, and its live code agrees with the replay ports', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const { adaptCollector } = await import('./collector.mjs');
    const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins', 'title-stack', 'osc-title'];
    const layers = await Promise.all(names.map(name => import(name === 'grading' ? './grading-collector.mjs' : './' + name + '-grading-collector.mjs')
        .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
    const { replaySelectionColorQuery, replaySelectionColorResetTermless } = await import('./selection-colors-grading.mjs');
    const { selectionColorsRecordingContext, pinnedCellAt } = await import('./selection-colors-collector.mjs');
    const { selectionColorsContracts } = await import('./selection-colors-contracts.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL } = await import('node:url');
    let text = adaptCollector(readFileSync(bundleUrl).toString(), fileURLToPath(new URL('./ownership.mjs', import.meta.url)));
    for (const adapt of [...layers, adaptSelectionColorsCollector]) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-selc-test-${process.pid}.mjs`, import.meta.url));
    writeFileSync(file, text, { flag: 'wx' });
    try {
        const live = await import(pathToFileURL(file).href);
        for (const contract of selectionColorsContracts) {
            if (contract.isResetCase) {
                const screen = { lines: ['X', ''], cols: 60, rows: 13 };
                const replay = replaySelectionColorResetTermless(contract, screen);
                const pinned = live.ntildeRunSelectionColorsTermless(contract.caseId, selectionColorsRecordingContext(contract, [], screen));
                assert.equal(pinned.pass, replay.pass);
                assert.equal(pinned.response, replay.response);
                assert.equal(pinned.notTested.reason, 'no-semantic-observable');
            } else {
                const reply = { raw: `\x1b]${contract.code};rgb:aaaa/bbbb/cccc\x1b\\`, channels16: [43690, 48059, 52428] };
                const replay = replaySelectionColorQuery(contract, [reply]);
                const pinned = live.ntildeRunSelectionColorsTermless(contract.caseId, selectionColorsRecordingContext(contract, [reply], null));
                assert.equal(pinned.pass, true);
                assert.deepEqual(pinned.observation, replay.observation);
                assert.equal(pinned.assertions[0].observed, replay.assertions[0].observed);
                // The absent reply grades no-response exactly as the replay does.
                const absent = live.ntildeRunSelectionColorsTermless(contract.caseId, selectionColorsRecordingContext(contract, [null], null));
                const absentReplay = replaySelectionColorQuery(contract, [null]);
                assert.deepEqual(absent.observation, absentReplay.observation);
            }
        }
        assert.equal(pinnedCellAt({ lines: ['X'] }, 0, 0).char, 'X');
    } finally { rmSync(file, { force: true }); }
});
