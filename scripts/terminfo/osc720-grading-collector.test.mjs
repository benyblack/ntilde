import assert from 'node:assert/strict';
import { test } from 'node:test';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptOsc720Collector, osc720AdapterId, osc720Seams, seamFunction, seamRegistration } from './osc720-grading-collector.mjs';
import { osc720CaseId, osc720CaseIds, osc720Pinned } from './osc720-contracts.mjs';
import { osc720TerminalContext, osc720TermlessContext, replayOsc720Pinned } from './osc720-collector.mjs';
import { replayOsc720TerminalStatic, replayOsc720TermlessStatic } from './osc720-grading.mjs';
import { requirePinnedAgreement } from './graded-plan.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => osc720Seams.join('\n/* gap */\n') + '\n';
const facts = (offset, scrollback = 30) => ({ rows: 24, viewport: { scrollOffset: offset, maxScrollOffset: scrollback }, scrollbackLines: scrollback, cursor: [23, 0] });

test('derives the OSC 720 extension by appending exports only', () => {
    const source = verbatim(), derived = adaptOsc720Collector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeOsc720Adapter = Object.freeze({\n  id: "${osc720AdapterId}"`));
    for (const name of ['ntildeOsc720HasTermless(probeId)', 'ntildeRunOsc720Termless(probeId, ctx)', 'ntildeRunOsc720Terminal(probeId, ctx)']) assert.ok(derived.includes(name), name);
    assert.ok(derived.includes(JSON.stringify([...osc720CaseIds])));
});

test('the seams pin the viewport criterion, the bare request, the terminal path and the left/right-margin layer', () => {
    assert.ok(seamFunction.includes('ctx.feed(`scroll-${i}\\r\n`)') && seamFunction.includes('i < 30'));
    assert.ok(seamFunction.includes('ctx.feed("\\x1B]720\\x07")'), 'the bare request has no count');
    assert.ok(seamFunction.includes('after.viewportOffset < before.viewportOffset'));
    assert.ok(seamFunction.includes('before.totalLines <= before.screenLines || before.viewportOffset <= 0'));
    assert.ok(seamRegistration.includes('probe("extensions.osc720-scroll-up", osc720ScrollProbe()'));
    assert.ok(seamRegistration.includes('ctx.write("\\x1B[1;1H\\x1B[2K")') && seamRegistration.includes('unverifiedEffect('));
    assert.ok(osc720Seams.includes('export const ntildeLrMarginsAdapter = Object.freeze({') && osc720Seams.includes('export function ntildeRunLrMarginsTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated, or a criterion would change', () => {
    for (const seam of osc720Seams) {
        assert.throws(() => adaptOsc720Collector(verbatim().split(seam).join('CHANGED')), /Pinned OSC 720 probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptOsc720Collector(verbatim() + seam + '\n'), /Pinned OSC 720 probe seam changed/, seam.slice(0, 40));
    }
    for (const [from, to] of [['i < 30', 'i < 31'], ['\\x1B]720\\x07', '\\x1B]720;3\\x07'], ['after.viewportOffset < before.viewportOffset', 'after.viewportOffset > before.viewportOffset'],
        ['unverifiedEffect(', 'verifiedEffect(']]) {
        assert.ok(osc720Seams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptOsc720Collector(osc720Seams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

test('the parent-built contexts serve only what the pinned code legitimately uses', () => {
    const termless = osc720TermlessContext(24, facts(0), facts(0));
    assert.throws(() => termless.context.write('x'), /Unexpected pinned context access/);
    termless.context.getScrollback(); termless.context.getScrollback();
    assert.throws(() => termless.context.getScrollback(), /more than twice/);
    assert.throws(() => osc720TerminalContext([0, 0]).context.feed('x'), /Unexpected pinned context access/);
});

testWithPinnedCollector('the real pinned bundle derives through every layer and the pinned code replays over served observations', async bundleUrl => {
    const { adaptCollector } = await import('./collector.mjs');
    const { adaptGradingCollector } = await import('./grading-collector.mjs');
    const { layerChain } = await import('./osc133-receipt.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptGradingCollector(adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname));
    for (const [, adapt] of layerChain) text = adapt(text);
    text = adaptOsc720Collector(text);
    const file = fileURLToPath(new URL(`./ntilde-osc720-test-${process.pid}.mjs`, bundleUrl));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeOsc720Adapter.probeIds], [...osc720CaseIds]);
        assert.equal(module.ntildeOsc720HasTermless(osc720CaseId), true);
        for (const [before, after] of [[facts(0), facts(0)], [facts(0), facts(5)], [facts(0), { ...facts(0, 0) }], [facts(0, 0), facts(0, 0)], [facts(30), facts(30)], [facts(4), facts(2)]]) {
            const served = osc720TermlessContext(24, before, after);
            const { probeId, ...termless } = module.ntildeRunOsc720Termless(osc720CaseId, served.context);
            assert.equal(probeId, osc720CaseId);
            const expected = replayOsc720TermlessStatic(24, before, after);
            // The guard returns before the bare request when there is no scrollback viewport to measure.
            assert.deepEqual(served.calls, expected.observation.reason === 'insufficient-evidence' ? osc720Pinned.termless.feeds.slice(0, 30) : osc720Pinned.termless.feeds, 'the termless path makes exactly the pinned feeds');
            assert.deepEqual(termless, expected);
            // deepEqual ignores key order; the production pinned-agreement check serializes it.
            assert.doesNotThrow(() => requirePinnedAgreement({ caseId: osc720CaseId }, termless, expected, 'real-adapter'));
            assert.equal(JSON.stringify(termless), JSON.stringify(expected));
        }
        const terminal = osc720TerminalContext([3, 4]);
        const { probeId: terminalId, ...result } = await module.ntildeRunOsc720Terminal(osc720CaseId, terminal.context);
        assert.equal(terminalId, osc720CaseId);
        assert.deepEqual(terminal.writes, osc720Pinned.terminal.writes);
        assert.equal(JSON.stringify(result), JSON.stringify(replayOsc720TerminalStatic()));
        const noAnswer = await module.ntildeRunOsc720Terminal(osc720CaseId, { write() {}, queryCursorPosition: async () => null });
        assert.equal(noAnswer.observation.outcome, 'inconclusive', 'no cursor answer is still inconclusive and never a verdict');
        const replay = await replayOsc720Pinned(module, [facts(0), facts(0)]);
        assert.equal(replay.termless.observation.outcome, 'unsupported');
        assert.equal(replay.terminal.observation.outcome, 'inconclusive');
    } finally { rmSync(file, { force: true }); }
});
