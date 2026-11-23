import assert from 'node:assert/strict';
import { test } from 'node:test';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptPclCollector, pclAdapterId, pclSeams } from './pcl-grading-collector.mjs';
import { pclCaseIds, pclContracts, pclPinned, replayPclTermless, replayPclTerminal } from './pcl-contracts.mjs';
import { requirePinnedAgreement } from './graded-plan.mjs';
import { syntheticPclEvidence } from './fixtures/pcl-evidence.mjs';
import { buildPclGradingReport } from './pcl-grading.mjs';
import { pclRecordingContext, pclTerminalContext } from './pcl-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => pclSeams.join('\n/* gap */\n') + '\n';
const da = '\x1b[?62;4;22c';

test('derives the pointer-color extension by appending exports only', () => {
    const source = verbatim(), derived = adaptPclCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildePclAdapter = Object.freeze({\n  id: "${pclAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunPclTermless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunPclTerminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...pclCaseIds])));
});

test('the seams pin both registrations, the helpers that decide each verdict, the color-reset factory and the left/right-margin layer', () => {
    const joined = pclSeams.join('\n');
    assert.ok(joined.includes('probe("extensions.osc113-reset-pointer-fg", colorResetProbe(13, 113)'), 'the foreground registration');
    assert.ok(joined.includes('probe("extensions.osc114-reset-pointer-bg", colorResetProbe(14, 114)'), 'the background registration');
    assert.ok(joined.includes('ctx.write("\\x1B]113\\x07");') && joined.includes('ctx.write("\\x1B]114\\x07");'), 'the terminal-path reset writes');
    assert.ok(joined.includes('function oscReplyResult(raw, frame, expected, prefix) {'));
    assert.ok(joined.includes('function unverifiedEffect(note, response) {') && joined.includes('evidence: "consumed"'));
    assert.ok(joined.includes('function colorResetProbe(setCode, resetCode, index) {') && joined.includes('const originalRaw = ctx.feedCapture(query);'), 'the termless factory');
    assert.ok(pclSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
    assert.ok(pclSeams.includes('export function ntildeRunLrMarginsTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of pclSeams) {
        assert.throws(() => adaptPclCollector(verbatim().split(seam).join('CHANGED')), /Pinned pointer-color probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptPclCollector(verbatim() + seam + '\n'), /Pinned pointer-color probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change a request, a criterion or a verdict', () => {
    for (const [from, to] of [['colorResetProbe(13, 113)', 'colorResetProbe(13, 114)'], ['extensions.osc114-reset-pointer-bg', 'extensions.osc114-reset-pointer-fg'],
        ['ctx.write("\\x1B]113\\x07");', 'ctx.write("\\x1B]113\\x1b\\\\");'], ['evidence: "consumed"', 'evidence: "behavior"'], ['prefix.test(raw)', 'prefix.ignore'],
        ['const originalRaw = ctx.feedCapture(query);', 'const originalRaw = ctx.feed(query);'], ['if (!original) {', 'if (original) {']]) {
        assert.ok(pclSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptPclCollector(pclSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

testWithPinnedCollector('the real pinned bundle derives through the layers and the pinned code runs over served bytes', async bundleUrl => {
    const { adaptCollector } = await import('./collector.mjs');
    const { adaptGradingCollector } = await import('./grading-collector.mjs');
    const { layerChain } = await import('./osc133-receipt.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptGradingCollector(adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname));
    for (const [, adapt] of layerChain) text = adapt(text);
    text = adaptPclCollector(text);
    const file = fileURLToPath(new URL(`./ntilde-pcl-test-${process.pid}.mjs`, bundleUrl));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildePclAdapter.probeIds], [...pclCaseIds]);
        for (const contract of pclContracts) {
            const caseId = contract.caseId, pinned = pclPinned[caseId];
            // Replies the pinned code can finish on: none, a bare prefix, and (separately) a complete color, which makes it continue and which the recording context refuses.
            for (const reply of [null, `\x1b]${pinned.setCode};`, `\x1b]${pinned.setCode};garbage\x07`]) {
                const exchange = { barrier: da, replies: reply === null ? [] : [reply] };
                const termlessContext = pclRecordingContext(contract, exchange);
                const { probeId, ...termless } = module.ntildeRunPclTermless(caseId, termlessContext);
                assert.equal(probeId, caseId);
                assert.equal(termlessContext.fedCount, 1, 'the termless path makes exactly the pinned call');
                assert.deepEqual(termless, replayPclTermless(caseId, reply ?? ''), `termless over ${JSON.stringify(reply)}`);
                // deepEqual ignores object key order; the production pinned-agreement check serializes it.
                assert.doesNotThrow(() => requirePinnedAgreement(contract, termless, replayPclTermless(caseId, reply ?? ''), 'real-adapter'));
                assert.notEqual(termless.observation.outcome, 'unsupported');
            }
            for (const cursor of [[0, 0], [4, 7]]) {
                const terminalContext = pclTerminalContext(caseId, cursor);
                const { probeId, ...terminal } = await module.ntildeRunPclTerminal(caseId, terminalContext.context);
                assert.equal(probeId, caseId);
                assert.deepEqual(terminalContext.writes, terminalContext.expectedWrites, 'the terminal path makes exactly the pinned writes');
                assert.deepEqual(terminal, replayPclTerminal(caseId, cursor));
                assert.doesNotThrow(() => requirePinnedAgreement(contract, terminal, replayPclTerminal(caseId, cursor), 'real-adapter'));
            }
            // A complete reply makes the real pinned code issue its continuation (a plain feed), which the contract context refuses instead of inventing the readback.
            assert.throws(() => module.ntildeRunPclTermless(caseId, pclRecordingContext(contract, { barrier: da, replies: [`\x1b]${pinned.setCode};rgb:aa/bb/cc\x07`] })), /must not plain-feed/);
            assert.throws(() => replayPclTermless(caseId, `\x1b]${pinned.setCode};rgb:aa/bb/cc\x07`), /pinned code would continue/);
        }
        // The real bundle's results build the published report: pinned inconclusive on both paths, effect unsupported.
        const evidence = syntheticPclEvidence();
        for (const item of evidence.run.cases) {
            const contract = pclContracts.find(entry => entry.caseId === item.caseId);
            const { probeId, ...termless } = module.ntildeRunPclTermless(item.caseId, pclRecordingContext(contract, { barrier: da, replies: [] }));
            const { probeId: terminalId, ...terminal } = await module.ntildeRunPclTerminal(item.caseId, pclTerminalContext(item.caseId, [0, 0]).context);
            item.pinned = { termless: { probeId, ...termless }, terminal: { probeId: terminalId, ...terminal } };
        }
        const report = buildPclGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
        for (const item of report.cases) {
            assert.equal(item.conclusion.outcome, 'inconclusive');
            assert.equal(item.conclusion.reason, 'no-response');
            assert.equal(item.effectContract.outcome, 'unsupported');
        }
    } finally { rmSync(file, { force: true }); }
});
