import assert from 'node:assert/strict';
import { test } from 'node:test';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptDclCollector, dclAdapterId, dclSeams } from './dcl-grading-collector.mjs';
import { dclCaseIds, dclContracts, replayDclTermless, replayDclTerminal } from './dcl-contracts.mjs';
import { requirePinnedAgreement } from './graded-plan.mjs';
import { syntheticDclEvidence } from './fixtures/dcl-evidence.mjs';
import { buildDclGradingReport } from './dcl-grading.mjs';
import { dclRecordingContext, dclTerminalContext } from './dcl-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => dclSeams.join('\n/* gap */\n') + '\n';
const da = '\x1b[?62;4;22c';
const three = '\x1b[?3;0$y';

test('derives the DECCOLM extension by appending exports only', () => {
    const source = verbatim(), derived = adaptDclCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeDclAdapter = Object.freeze({\n  id: "${dclAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunDclTermless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunDclTerminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...dclCaseIds])));
});

test('the seams pin the registration, the helpers that decide each verdict and the left/right-margin layer', () => {
    const joined = dclSeams.join('\n');
    assert.ok(joined.includes('decrpmModeProbe("modes.deccolm", 3, (ctx) => {'), 'the registration');
    assert.ok(joined.includes('ctx.feed("\\x1B[?3h");') && joined.includes('ctx.feedCapture("\\x1B[?3$p");') && joined.includes('ctx.feed("\\x1B[?3l");'), 'the termless feeds');
    assert.ok(joined.includes('parserStateResult(null, "DECCOLM switches the measured grid width"'), 'the inconclusive termless result');
    assert.ok(joined.includes('function decrpmModeProbe(id, modeNum, termlessFn) {') && joined.includes('decrpmResult(await ctx.queryMode(modeNum), modeNum)'), 'the terminal path');
    assert.ok(joined.includes('function decrpmResult(state, modeNum) {') && joined.includes('reason: "insufficient-evidence"'), 'the terminal verdict');
    assert.ok(joined.includes('function parserStateResult(pass, expected, state, note) {') && joined.includes('outcome: pass === null ? "inconclusive"'), 'the termless verdict');
    assert.ok(dclSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
    assert.ok(dclSeams.includes('export function ntildeRunLrMarginsTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of dclSeams) {
        assert.throws(() => adaptDclCollector(verbatim().split(seam).join('CHANGED')), /Pinned deccolm probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptDclCollector(verbatim() + seam + '\n'), /Pinned deccolm probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change a request, a criterion or a verdict', () => {
    for (const [from, to] of [['ctx.feed("\\x1B[?3h");', 'ctx.feed("\\x1B[?3l");'], ['ctx.feedCapture("\\x1B[?3$p");', 'ctx.feedCapture("\\x1B[?4$p");'], ['"modes.deccolm", 3,', '"modes.deccolm", 4,'],
        ['parserStateResult(null, "DECCOLM', 'parserStateResult(true, "DECCOLM'], ['decrpmResult(await ctx.queryMode(modeNum), modeNum)', 'decrpmResult(await ctx.queryMode(3), modeNum)'],
        ['reason: "insufficient-evidence"', 'reason: "no-response"'], ['pass === null ? "inconclusive"', 'pass === null ? "supported"'], ['if (state === "unknown") {', 'if (state !== "unknown") {']]) {
        assert.ok(dclSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptDclCollector(dclSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
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
    text = adaptDclCollector(text);
    const file = fileURLToPath(new URL(`./ntilde-dcl-test-${process.pid}.mjs`, bundleUrl));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeDclAdapter.probeIds], [...dclCaseIds]);
        const [contract] = dclContracts, caseId = contract.caseId;
        // Replies the pinned termless code can finish on: a recognised state, not recognized, and (separately) none, which makes it continue to a text probe the recording context refuses.
        for (const [reply, cols] of [[three, 60], ['\x1b[?3;1$y', 60], ['\x1b[?3;2$y', 132], ['\x1b[?3;0$y\x1b[?40;0$y', 77]]) {
            const served = dclRecordingContext(contract, { barrier: da, replies: [reply] }, cols);
            const { probeId, ...termless } = module.ntildeRunDclTermless(caseId, served.context);
            assert.equal(probeId, caseId);
            assert.equal(served.callCount, served.expectedCalls, 'the termless path makes exactly the pinned calls');
            assert.deepEqual(termless, replayDclTermless(caseId, reply, cols), `termless over ${JSON.stringify(reply)}`);
            // deepEqual ignores object key order; the production pinned-agreement check serializes it.
            assert.doesNotThrow(() => requirePinnedAgreement(contract, termless, replayDclTermless(caseId, reply, cols), 'real-adapter'));
            assert.equal(termless.observation.outcome, 'inconclusive');
        }
        const unanswered = dclRecordingContext(contract, { barrier: da, replies: [] }, 60);
        assert.throws(() => module.ntildeRunDclTermless(caseId, unanswered.context), /Unexpected pinned context access|differs from the contract/, 'a missing reply makes the real code continue to calls the contract never serves');
        assert.throws(() => replayDclTermless(caseId, '', 60), /was not answered/);
        // The terminal path over every served mode state: only the not-recognized one is replayable without a width measurement.
        for (const replies of [[three], []]) {
            const terminalContext = dclTerminalContext(contract, { barrier: da, replies });
            const { probeId, ...terminal } = await module.ntildeRunDclTerminal(caseId, terminalContext.context);
            assert.equal(probeId, caseId);
            assert.equal(terminalContext.askedCount, 1, 'the terminal path makes exactly one mode query');
            assert.deepEqual(terminal, replayDclTerminal(caseId, replies));
            assert.doesNotThrow(() => requirePinnedAgreement(contract, terminal, replayDclTerminal(caseId, replies), 'real-adapter'));
            assert.equal(terminal.observation.outcome, 'inconclusive');
        }
        for (const status of [1, 2]) {
            const recognised = await module.ntildeRunDclTerminal(caseId, dclTerminalContext(contract, { barrier: da, replies: [`\x1b[?3;${status}$y`] }).context);
            assert.equal(recognised.observation.outcome, 'supported', 'the real pinned terminal path would pass on a DECRPM acknowledgement alone: the replay refuses to serve it');
            assert.throws(() => replayDclTerminal(caseId, [`\x1b[?3;${status}$y`]), /would pass without a width measurement/);
        }
        // The real bundle's results build the published report: pinned inconclusive on both paths, effect unsupported.
        const evidence = syntheticDclEvidence();
        for (const item of evidence.run.cases) {
            const each = dclContracts.find(entry => entry.caseId === item.caseId);
            const termlessRead = item.reads[each.pinnedRead], terminalRead = item.reads[each.pinnedTerminalRead];
            const { probeId, ...termless } = module.ntildeRunDclTermless(item.caseId, dclRecordingContext(each, termlessRead.report, termlessRead.size[1]).context);
            const { probeId: terminalId, ...terminal } = await module.ntildeRunDclTerminal(item.caseId, dclTerminalContext(each, terminalRead.report).context);
            item.pinned = { termless: { probeId, ...termless }, terminal: { probeId: terminalId, ...terminal } };
        }
        const report = buildDclGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { replayModule: module });
        for (const item of report.cases) {
            assert.equal(item.conclusion.outcome, 'inconclusive');
            assert.equal(item.conclusion.reason, 'insufficient-evidence');
            assert.equal(item.effectContract.outcome, 'unsupported');
        }
    } finally { rmSync(file, { force: true }); }
});
