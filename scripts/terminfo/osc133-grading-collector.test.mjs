import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptOsc133Collector, osc133AdapterId, osc133Seams } from './osc133-grading-collector.mjs';
import { osc133CaseIds, osc133Pinned } from './osc133-contracts.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => osc133Seams.join('\n/* gap */\n') + '\n';

test('derives the OSC 133 extension by appending exports only', () => {
    const source = verbatim(), derived = adaptOsc133Collector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeOsc133Adapter = Object.freeze({\n  id: "${osc133AdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunOsc133Termless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunOsc133Terminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...osc133CaseIds])));
});

test('the seams pin all four registrations with both paths, the shared helper and the left/right-margin layer', () => {
    for (const caseId of osc133CaseIds) {
        const seam = osc133Seams.find(item => item.includes(`...probe("${caseId}"`));
        assert.ok(seam, caseId);
        const feed = osc133Pinned[caseId].termlessFeeds[0].replace('\x1b', '\\x1B').replace('\x07', '\\x07');
        assert.ok(seam.includes(`ctx.feed("${feed}");`), `${caseId} termless fixture`);
        assert.ok(seam.includes(`ctx.write("${feed}");`), `${caseId} terminal fixture`);
        assert.ok(seam.includes('ctx.write("\\x1B[1;1H\\x1B[2K");') && seam.includes('const cell = ctx.getCell(0, 0);') && seam.includes('cell.char === "X"'));
        assert.ok(seam.includes('pos === null ? null : pos.col === 2') && seam.includes('termlessObservationEvidence: "consumed"'));
    }
    const helper = osc133Seams.find(item => item.startsWith('function promptConsumptionResult('));
    for (const part of ['pass: false,', 'outcome: "inconclusive"', 'shell prompt integration was not observed', 'evidence: consumed === null ? "query" : "consumed"']) assert.ok(helper.includes(part), part);
    assert.ok(osc133Seams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of osc133Seams) {
        assert.throws(() => adaptOsc133Collector(verbatim().split(seam).join('CHANGED')), /Pinned OSC 133 probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptOsc133Collector(verbatim() + seam + '\n'), /Pinned OSC 133 probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would gain a verdict or change its bytes', () => {
    for (const [from, to] of [['pass: false,', 'pass: true,'], ['outcome: "inconclusive"', 'outcome: "supported"'], ['cell.char === "X"', 'cell.char !== "X"'],
        ['pos.col === 2', 'pos.col === 1'], ['\\x1B]133;D;0\\x07X', '\\x1B]133;D;1\\x07X'], ['ctx.getCell(0, 0)', 'ctx.getCell(1, 0)']]) {
        assert.ok(osc133Seams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptOsc133Collector(osc133Seams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through every layer and the pinned code runs over served terminals', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const { adaptCollector } = await import('./collector.mjs');
    const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins'];
    const layers = await Promise.all(names.map(name => import(name === 'grading' ? './grading-collector.mjs' : './' + name + '-grading-collector.mjs')
        .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
    const { osc133TermlessContext, osc133TerminalContext, replayOsc133Pinned } = await import('./osc133-contracts.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    for (const adapt of [...layers, adaptOsc133Collector]) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-shi-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeOsc133Adapter.probeIds], [...osc133CaseIds]);
        const screens = [{ lines: ['X', ''], cursorRow: 0, cursorCol: 1 }, { lines: ['', ''], cursorRow: 0, cursorCol: 0 }, { lines: ['XY', ''], cursorRow: 0, cursorCol: 2 }];
        for (const screen of screens) for (const caseId of osc133CaseIds) {
            const termlessContext = osc133TermlessContext(caseId, screen), terminalContext = osc133TerminalContext(caseId, screen);
            const { probeId, ...termless } = module.ntildeRunOsc133Termless(caseId, termlessContext);
            const { probeId: terminalId, ...terminal } = await module.ntildeRunOsc133Terminal(caseId, terminalContext);
            assert.equal(probeId, caseId); assert.equal(terminalId, caseId);
            assert.equal(termlessContext.fedCount, 1); assert.equal(terminalContext.writtenCount, 2);
            const replay = replayOsc133Pinned(caseId, screen);
            assert.deepEqual(termless, replay.termless, `${caseId} termless`);
            assert.deepEqual(terminal, replay.terminal, `${caseId} terminal`);
            assert.equal(termless.observation.outcome, 'inconclusive', 'the pinned probe never decides');
            assert.equal(terminal.observation.outcome, 'inconclusive', 'the pinned probe never decides');
        }
    } finally { rmSync(file, { force: true }); }
});
