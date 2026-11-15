import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptXcsCollector, xcsAdapterId, xcsSeams } from './xcs-grading-collector.mjs';
import { xcsCaseIds, xcsContracts, replayXcsPinned } from './xcs-contracts.mjs';
import { xcsRecordingContext } from './xcs-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => xcsSeams.join('\n/* gap */\n') + '\n';
const da = '\x1b[?62;4;22c';
const byId = Object.fromEntries(xcsContracts.map(item => [item.caseId, item]));

test('derives the xterm color-stack extension by appending exports only', () => {
    const source = verbatim(), derived = adaptXcsCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeXcsAdapter = Object.freeze({\n  id: "${xcsAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunXcsTermless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunXcsTerminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...xcsCaseIds])));
});

test('the seams pin the two exact registrations and the left/right-margin layer', () => {
    const joined = xcsSeams.join('\n');
    assert.ok(joined.includes('probe("modes.xtpushcolors"') && joined.includes('ctx.feedCapture("\\x1B[#P")') && joined.includes('ctx.feed("\\x1B[#Q")'), 'the push bytes');
    assert.ok(joined.includes('probe("modes.xtpopcolors"') && joined.includes('ctx.feedCapture("\\x1B[#Q")') && joined.includes('ctx.feed("\\x1B[#P")'), 'the pop bytes');
    assert.ok(joined.includes('queryCursorPosition') && joined.includes('DA1 responsiveness does not measure saved/restored stack state'));
    assert.ok(xcsSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
    assert.ok(xcsSeams.includes('export function ntildeRunLrMarginsTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of xcsSeams) {
        assert.throws(() => adaptXcsCollector(verbatim().split(seam).join('CHANGED')), /Pinned xterm color-stack probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptXcsCollector(verbatim() + seam + '\n'), /Pinned xterm color-stack probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change a request, a criterion or a verdict', () => {
    for (const [from, to] of [['ctx.feedCapture("\\x1B[#P")', 'ctx.feedCapture("\\x1B[#Q")'], ['probeResponse = ctx.feedCapture("\\x1B[c")', 'probeResponse = ctx.feedCapture("\\x1B[0c")'],
        ['pass: false,\n          response: popOut', 'pass: true,\n          response: popOut'], ['modes.xtpopcolors', 'modes.xtpopcolor'], ['reason: "insufficient-evidence"', 'reason: "no-response"']]) {
        assert.ok(xcsSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptXcsCollector(xcsSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through every layer and the pinned code runs over served bytes', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const { adaptCollector } = await import('./collector.mjs');
    const { adaptGradingCollector } = await import('./grading-collector.mjs');
    const { layerChain } = await import('./osc133-receipt.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptGradingCollector(adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname));
    for (const [, adapt] of layerChain) text = adapt(text);
    text = adaptXcsCollector(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-xcs-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeXcsAdapter.probeIds], [...xcsCaseIds]);
        for (const caseId of xcsCaseIds) {
            const contract = byId[caseId], silent = { barrier: da, replies: [] };
            const termlessContext = xcsRecordingContext(contract, 'termless', silent, [0, 0]);
            const { probeId, ...termless } = module.ntildeRunXcsTermless(caseId, termlessContext);
            assert.equal(probeId, caseId);
            assert.equal(termlessContext.callCount, contract.pinnedScript.termless.length, `${caseId} makes exactly the pinned calls`);
            assert.deepEqual(termless, replayXcsPinned(caseId, 'termless', { replies: '', barrier: da }), `${caseId} termless matches the replay`);
            const terminalContext = xcsRecordingContext(contract, 'terminal', silent, [0, 0]);
            const { probeId: terminalId, ...terminal } = await module.ntildeRunXcsTerminal(caseId, terminalContext);
            assert.equal(terminalId, caseId);
            assert.equal(terminalContext.callCount, contract.pinnedScript.terminal.length);
            assert.deepEqual(terminal, replayXcsPinned(caseId, 'terminal', { position: { row: 1, col: 1 } }), `${caseId} terminal matches the replay`);
            // Any output from the stack operation is the pinned invalid-reply, never support.
            const noisy = { barrier: da, replies: ['\x1b[?0;0#Q'] };
            const { probeId: noisyId, ...invalid } = module.ntildeRunXcsTermless(caseId, xcsRecordingContext(contract, 'termless', noisy, [0, 0]));
            assert.equal(noisyId, caseId);
            assert.deepEqual(invalid, replayXcsPinned(caseId, 'termless', { replies: '\x1b[?0;0#Q', barrier: da }));
            assert.equal(invalid.observation.reason, 'invalid-reply');
            // No cursor answer is the pinned no-response.
            const { probeId: lostId, ...lost } = await module.ntildeRunXcsTerminal(caseId, xcsRecordingContext(contract, 'terminal', silent, null));
            assert.equal(lostId, caseId);
            assert.deepEqual(lost, replayXcsPinned(caseId, 'terminal', { position: null }));
        }
    } finally { rmSync(file, { force: true }); }
});
