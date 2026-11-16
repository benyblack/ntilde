import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptXsvCollector, xsvAdapterId, xsvSeams } from './xsv-grading-collector.mjs';
import { xsvCaseIds, xsvContracts, replayXsvPinned } from './xsv-contracts.mjs';
import { xsvRecordingContext, xsvExpectedCalls } from './xsv-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => xsvSeams.join('\n/* gap */\n') + '\n';
const da = '\x1b[?62;4;22c';
const byId = Object.fromEntries(xsvContracts.map(item => [item.caseId, item]));

test('derives the xterm saved-mode extension by appending exports only', () => {
    const source = verbatim(), derived = adaptXsvCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeXsvAdapter = Object.freeze({\n  id: "${xsvAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunXsvTermless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunXsvTerminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...xsvCaseIds])));
});

test('the seams pin the two exact registrations and the left/right-margin layer', () => {
    const joined = xsvSeams.join('\n');
    assert.ok(joined.includes('probe("modes.xtsave"') && joined.includes('ctx.feedCapture("\\x1B[?7s")') && joined.includes('ctx.feed("\\x1B[?7r")'), 'the save bytes');
    assert.ok(joined.includes('probe("modes.xtrestore"') && joined.includes('ctx.feedCapture("\\x1B[?7r")') && joined.includes('ctx.feed("\\x1B[?7s")'), 'the restore bytes');
    assert.ok(joined.includes('queryCursorPosition') && joined.includes('DA1 responsiveness does not measure saved/restored stack state'));
    assert.ok(xsvSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
    assert.ok(xsvSeams.includes('export function ntildeRunLrMarginsTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of xsvSeams) {
        assert.throws(() => adaptXsvCollector(verbatim().split(seam).join('CHANGED')), /Pinned xterm saved-mode probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptXsvCollector(verbatim() + seam + '\n'), /Pinned xterm saved-mode probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change a request, a criterion or a verdict', () => {
    for (const [from, to] of [['ctx.feedCapture("\\x1B[?7s")', 'ctx.feedCapture("\\x1B[?7r")'], ['probeResponse = ctx.feedCapture("\\x1B[c")', 'probeResponse = ctx.feedCapture("\\x1B[0c")'],
        ['pass: false,\n          response: restoreOut', 'pass: true,\n          response: restoreOut'], ['modes.xtrestore', 'modes.xtrestorecolor'], ['reason: "insufficient-evidence"', 'reason: "no-response"']]) {
        assert.ok(xsvSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptXsvCollector(xsvSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
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
    text = adaptXsvCollector(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-xsv-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeXsvAdapter.probeIds], [...xsvCaseIds]);
        for (const caseId of xsvCaseIds) {
            const contract = byId[caseId], silent = { barrier: da, replies: [] };
            const termlessContext = xsvRecordingContext(contract, 'termless', silent, [0, 0]);
            const { probeId, ...termless } = module.ntildeRunXsvTermless(caseId, termlessContext);
            assert.equal(probeId, caseId);
            assert.equal(termlessContext.callCount, contract.pinnedScript.termless.length, `${caseId} makes exactly the pinned calls`);
            assert.deepEqual(termless, replayXsvPinned(caseId, 'termless', { replies: '', barrier: da }), `${caseId} termless matches the replay`);
            const terminalContext = xsvRecordingContext(contract, 'terminal', silent, [0, 0]);
            const { probeId: terminalId, ...terminal } = await module.ntildeRunXsvTerminal(caseId, terminalContext);
            assert.equal(terminalId, caseId);
            assert.equal(terminalContext.callCount, contract.pinnedScript.terminal.length);
            assert.deepEqual(terminal, replayXsvPinned(caseId, 'terminal', { position: { row: 1, col: 1 } }), `${caseId} terminal matches the replay`);
            // Any output from the save or restore is the pinned invalid-reply, never support.
            const noisy = { barrier: da, replies: ['\x1b[?0;0#Q'] };
            const noisyContext = xsvRecordingContext(contract, 'termless', noisy, [0, 0]);
            const { probeId: noisyId, ...invalid } = module.ntildeRunXsvTermless(caseId, noisyContext);
            assert.equal(noisyContext.callCount, xsvExpectedCalls(contract, 'termless', noisy), `${caseId} returns early at the capture that carries output`);
            assert.equal(noisyId, caseId);
            assert.deepEqual(invalid, replayXsvPinned(caseId, 'termless', { replies: '\x1b[?0;0#Q', barrier: da }));
            assert.equal(invalid.observation.reason, 'invalid-reply');
            // No cursor answer is the pinned no-response.
            const { probeId: lostId, ...lost } = await module.ntildeRunXsvTerminal(caseId, xsvRecordingContext(contract, 'terminal', silent, null));
            assert.equal(lostId, caseId);
            assert.deepEqual(lost, replayXsvPinned(caseId, 'terminal', { position: null }));
        }
    } finally { rmSync(file, { force: true }); }
});
