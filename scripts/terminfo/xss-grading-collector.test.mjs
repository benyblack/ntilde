import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptXssCollector, xssAdapterId, xssSeams } from './xss-grading-collector.mjs';
import { xssCaseIds, xssContracts, replayXssPinned } from './xss-contracts.mjs';
import { xssRecordingContext, xssExpectedCalls } from './xss-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => xssSeams.join('\n/* gap */\n') + '\n';
const da = '\x1b[?62;4;22c';
const byId = Object.fromEntries(xssContracts.map(item => [item.caseId, item]));

test('derives the xterm SGR-stack extension by appending exports only', () => {
    const source = verbatim(), derived = adaptXssCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeXssAdapter = Object.freeze({\n  id: "${xssAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunXssTermless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunXssTerminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...xssCaseIds])));
});

test('the seams pin the two exact registrations and the left/right-margin layer', () => {
    const joined = xssSeams.join('\n');
    assert.ok(joined.includes('probe("modes.xtpushsgr"') && joined.includes('ctx.feedCapture("\\x1B[#{")') && joined.includes('ctx.feed("\\x1B[#}")'), 'the push bytes');
    assert.ok(joined.includes('probe("modes.xtpopsgr"') && joined.includes('ctx.feedCapture("\\x1B[#}")') && joined.includes('ctx.feed("\\x1B[#{")'), 'the pop bytes');
    assert.ok(joined.includes('queryCursorPosition') && joined.includes('DA1 responsiveness does not measure saved/restored stack state'));
    assert.ok(xssSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
    assert.ok(xssSeams.includes('export function ntildeRunLrMarginsTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of xssSeams) {
        assert.throws(() => adaptXssCollector(verbatim().split(seam).join('CHANGED')), /Pinned xterm SGR-stack probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptXssCollector(verbatim() + seam + '\n'), /Pinned xterm SGR-stack probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change a request, a criterion or a verdict', () => {
    for (const [from, to] of [['ctx.feedCapture("\\x1B[#{")', 'ctx.feedCapture("\\x1B[#}")'], ['probeResponse = ctx.feedCapture("\\x1B[c")', 'probeResponse = ctx.feedCapture("\\x1B[0c")'],
        ['pass: false,\n          response: popOut', 'pass: true,\n          response: popOut'], ['modes.xtpopsgr', 'modes.xtpopcolor'], ['reason: "insufficient-evidence"', 'reason: "no-response"']]) {
        assert.ok(xssSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptXssCollector(xssSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
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
    text = adaptXssCollector(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-xss-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeXssAdapter.probeIds], [...xssCaseIds]);
        for (const caseId of xssCaseIds) {
            const contract = byId[caseId], silent = { barrier: da, replies: [] };
            const termlessContext = xssRecordingContext(contract, 'termless', silent, [0, 0]);
            const { probeId, ...termless } = module.ntildeRunXssTermless(caseId, termlessContext);
            assert.equal(probeId, caseId);
            assert.equal(termlessContext.callCount, contract.pinnedScript.termless.length, `${caseId} makes exactly the pinned calls`);
            assert.deepEqual(termless, replayXssPinned(caseId, 'termless', { replies: '', barrier: da }), `${caseId} termless matches the replay`);
            const terminalContext = xssRecordingContext(contract, 'terminal', silent, [0, 0]);
            const { probeId: terminalId, ...terminal } = await module.ntildeRunXssTerminal(caseId, terminalContext);
            assert.equal(terminalId, caseId);
            assert.equal(terminalContext.callCount, contract.pinnedScript.terminal.length);
            assert.deepEqual(terminal, replayXssPinned(caseId, 'terminal', { position: { row: 1, col: 1 } }), `${caseId} terminal matches the replay`);
            // Any output from the stack operation is the pinned invalid-reply, never support.
            const noisy = { barrier: da, replies: ['\x1b[?0;0#Q'] };
            const noisyContext = xssRecordingContext(contract, 'termless', noisy, [0, 0]);
            const { probeId: noisyId, ...invalid } = module.ntildeRunXssTermless(caseId, noisyContext);
            assert.equal(noisyContext.callCount, xssExpectedCalls(contract, 'termless', noisy), `${caseId} returns early at the capture that carries output`);
            assert.equal(noisyId, caseId);
            assert.deepEqual(invalid, replayXssPinned(caseId, 'termless', { replies: '\x1b[?0;0#Q', barrier: da }));
            assert.equal(invalid.observation.reason, 'invalid-reply');
            // No cursor answer is the pinned no-response.
            const { probeId: lostId, ...lost } = await module.ntildeRunXssTerminal(caseId, xssRecordingContext(contract, 'terminal', silent, null));
            assert.equal(lostId, caseId);
            assert.deepEqual(lost, replayXssPinned(caseId, 'terminal', { position: null }));
        }
    } finally { rmSync(file, { force: true }); }
});
