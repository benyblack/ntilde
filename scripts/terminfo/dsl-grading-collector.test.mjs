import assert from 'node:assert/strict';
import { test } from 'node:test';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptDslCollector, dslAdapterId, dslSeams } from './dsl-grading-collector.mjs';
import { dslCaseIds, dslContracts, replayDslTermless, replayDslTerminal } from './dsl-contracts.mjs';
import { requirePinnedAgreement } from './graded-plan.mjs';
import { syntheticDslEvidence } from './fixtures/dsl-evidence.mjs';
import { buildDslGradingReport } from './dsl-grading.mjs';
import { dslRecordingContext, dslTerminalContext } from './dsl-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => dslSeams.join('\n/* gap */\n') + '\n';
const da = '\x1b[?62;4;22c';
const four = '\x1b[?4;0$y';

test('derives the DECSCLM extension by appending exports only', () => {
    const source = verbatim(), derived = adaptDslCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeDslAdapter = Object.freeze({\n  id: "${dslAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunDslTermless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunDslTerminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...dslCaseIds])));
});

test('the seams pin the registration, the helpers that decide each verdict and the left/right-margin layer', () => {
    const joined = dslSeams.join('\n');
    assert.ok(joined.includes('decrpmModeProbe("modes.decsclm", 4, (ctx) => {'), 'the registration');
    assert.ok(joined.includes('ctx.feed("\\x1B[?4h");') && joined.includes('ctx.feedCapture("\\x1B[?4$p");') && joined.includes('ctx.feed("\\x1B[?4l");'), 'the termless feeds');
    assert.ok(joined.includes('return parserStateResult(null, "DECSCLM changes scroll timing", { response }, "DECRPM state does not measure scroll timing");'), 'the always-inconclusive termless result');
    assert.ok(joined.includes('function decrpmModeProbe(id, modeNum, termlessFn) {') && joined.includes('decrpmResult(await ctx.queryMode(modeNum), modeNum)'), 'the terminal path');
    assert.ok(joined.includes('function decrpmResult(state, modeNum) {') && joined.includes('reason: "insufficient-evidence"'), 'the terminal verdict');
    assert.ok(joined.includes('function parserStateResult(pass, expected, state, note) {') && joined.includes('outcome: pass === null ? "inconclusive"'), 'the termless verdict');
    assert.ok(dslSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
    assert.ok(dslSeams.includes('export function ntildeRunLrMarginsTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of dslSeams) {
        assert.throws(() => adaptDslCollector(verbatim().split(seam).join('CHANGED')), /Pinned decsclm probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptDslCollector(verbatim() + seam + '\n'), /Pinned decsclm probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change a request, a criterion or a verdict', () => {
    for (const [from, to] of [['ctx.feed("\\x1B[?4h");', 'ctx.feed("\\x1B[?4l");'], ['ctx.feedCapture("\\x1B[?4$p");', 'ctx.feedCapture("\\x1B[?3$p");'], ['"modes.decsclm", 4,', '"modes.decsclm", 3,'],
        ['parserStateResult(null, "DECSCLM', 'parserStateResult(true, "DECSCLM'], ['decrpmResult(await ctx.queryMode(modeNum), modeNum)', 'decrpmResult(await ctx.queryMode(3), modeNum)'],
        ['reason: "insufficient-evidence"', 'reason: "no-response"'], ['pass === null ? "inconclusive"', 'pass === null ? "supported"'], ['if (state === "unknown") {', 'if (state !== "unknown") {']]) {
        assert.ok(dslSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptDslCollector(dslSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
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
    text = adaptDslCollector(text);
    const file = fileURLToPath(new URL(`./ntilde-dsl-test-${process.pid}.mjs`, bundleUrl));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeDslAdapter.probeIds], [...dslCaseIds]);
        const [contract] = dslContracts, caseId = contract.caseId;
        // The pinned termless code never reads the reply: every reply (and none) gives the same inconclusive result as the replay.
        for (const reply of [four, '\x1b[?4;1$y', '\x1b[?4;2$y', '\x1b[?4;0$y\x1b[?3;0$y', '']) {
            const served = dslRecordingContext(contract, { barrier: da, replies: reply === '' ? [] : [reply] });
            const { probeId, ...termless } = module.ntildeRunDslTermless(caseId, served.context);
            assert.equal(probeId, caseId);
            assert.equal(served.callCount, served.expectedCalls, 'the termless path makes exactly the pinned calls');
            assert.deepEqual(termless, replayDslTermless(caseId, reply), `termless over ${JSON.stringify(reply)}`);
            // deepEqual ignores object key order; the production pinned-agreement check serializes it.
            assert.doesNotThrow(() => requirePinnedAgreement(contract, termless, replayDslTermless(caseId, reply), 'real-adapter'));
            assert.equal(termless.observation.outcome, 'inconclusive');
        }
        // The terminal path over every served mode state: only the not-recognized one is replayable without a width measurement.
        for (const replies of [[four], []]) {
            const terminalContext = dslTerminalContext(contract, { barrier: da, replies });
            const { probeId, ...terminal } = await module.ntildeRunDslTerminal(caseId, terminalContext.context);
            assert.equal(probeId, caseId);
            assert.equal(terminalContext.askedCount, 1, 'the terminal path makes exactly one mode query');
            assert.deepEqual(terminal, replayDslTerminal(caseId, replies));
            assert.doesNotThrow(() => requirePinnedAgreement(contract, terminal, replayDslTerminal(caseId, replies), 'real-adapter'));
            assert.equal(terminal.observation.outcome, 'inconclusive');
        }
        for (const status of [1, 2]) {
            const recognised = await module.ntildeRunDslTerminal(caseId, dslTerminalContext(contract, { barrier: da, replies: [`\x1b[?4;${status}$y`] }).context);
            assert.equal(recognised.observation.outcome, 'supported', 'the real pinned terminal path would pass on a DECRPM acknowledgement alone: the replay refuses to serve it');
            assert.throws(() => replayDslTerminal(caseId, [`\x1b[?4;${status}$y`]), /would pass without any scroll timing/);
        }
        // The real bundle's results build the published report: pinned inconclusive on both paths, effect unsupported.
        const evidence = syntheticDslEvidence();
        for (const item of evidence.run.cases) {
            const each = dslContracts.find(entry => entry.caseId === item.caseId);
            const termlessRead = item.reads[each.pinnedRead], terminalRead = item.reads[each.pinnedTerminalRead];
            const { probeId, ...termless } = module.ntildeRunDslTermless(item.caseId, dslRecordingContext(each, termlessRead.report).context);
            const { probeId: terminalId, ...terminal } = await module.ntildeRunDslTerminal(item.caseId, dslTerminalContext(each, terminalRead.report).context);
            item.pinned = { termless: { probeId, ...termless }, terminal: { probeId: terminalId, ...terminal } };
        }
        const report = buildDslGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { replayModule: module });
        for (const item of report.cases) {
            assert.equal(item.conclusion.outcome, 'inconclusive');
            assert.equal(item.conclusion.reason, 'insufficient-evidence');
            assert.equal(item.effectContract.outcome, 'unsupported');
        }
    } finally { rmSync(file, { force: true }); }
});
