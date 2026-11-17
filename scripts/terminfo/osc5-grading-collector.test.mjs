import assert from 'node:assert/strict';
import { test } from 'node:test';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptOsc5Collector, osc5AdapterId, osc5Seams } from './osc5-grading-collector.mjs';
import { osc5CaseIds, osc5Contracts, osc5PinnedPattern, osc5PinnedQuery, replayOsc5Pinned } from './osc5-contracts.mjs';
import { osc5RecordingContext } from './osc5-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => osc5Seams.join('\n/* gap */\n') + '\n';
const da = '\x1b[?62;4;22c';
const [contract] = osc5Contracts;
const caseId = osc5CaseIds[0];

test('derives the special-color extension by appending exports only', () => {
    const source = verbatim(), derived = adaptOsc5Collector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeOsc5Adapter = Object.freeze({\n  id: "${osc5AdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunOsc5Termless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunOsc5Terminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...osc5CaseIds])));
});

test('the seams pin the registration, the reply helper that decides the verdict, the factory and the left/right-margin layer', () => {
    const joined = osc5Seams.join('\n');
    assert.ok(joined.includes('probe("extensions.osc5-special-color"') || joined.includes('oscSimpleQueryProbe("extensions.osc5-special-color"'), 'the registration');
    assert.ok(joined.includes('"\\x1B]5;0;?\\x07"'), 'the request bytes');
    assert.ok(joined.includes('function oscReplyResult(raw, frame, expected, prefix) {'));
    assert.ok(joined.includes('{1,4}'), 'the frame criterion');
    assert.ok(joined.includes('function oscSimpleQueryProbe(id, query, pattern, prefix, expected) {'), 'the factory');
    assert.ok(osc5Seams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
    assert.ok(osc5Seams.includes('export function ntildeRunLrMarginsTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of osc5Seams) {
        assert.throws(() => adaptOsc5Collector(verbatim().split(seam).join('CHANGED')), /Pinned special-color probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptOsc5Collector(verbatim() + seam + '\n'), /Pinned special-color probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change a request, a criterion or a verdict', () => {
    for (const [from, to] of [['"\\x1B]5;0;?\\x07"', '"\\x1B]5;1;?\\x07"'], ['{1,4}', '{1,8}'], ['"Complete OSC 5 index-0 color reply"', '"Complete OSC 5 index-1 color reply"'],
        ['extensions.osc5-special-color', 'extensions.osc5-special-colour'], ['outcome: "supported"', 'outcome: "unsupported"'], ['prefix.test(raw)', 'prefix.ignore'],
        ['rgb:[0-9a-f]', 'rgb:[0-9a-f][0-9a-f]']]) {
        assert.ok(osc5Seams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptOsc5Collector(osc5Seams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

testWithPinnedCollector('the real pinned bundle derives through every layer and the pinned code runs over served bytes', async bundleUrl => {
    const { adaptCollector } = await import('./collector.mjs');
    const { adaptGradingCollector } = await import('./grading-collector.mjs');
    const { layerChain } = await import('./osc133-receipt.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptGradingCollector(adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname));
    for (const [, adapt] of layerChain) text = adapt(text);
    text = adaptOsc5Collector(text);
    const file = fileURLToPath(new URL(`./ntilde-osc5-test-${process.pid}.mjs`, bundleUrl));
    const frames = ['\x1b]5;0;rgb:cc/cc/cc\x1b\\', '\x1b]5;0;rgb:cccc/cccc/cccc\x07', '\x1b]5;0;rgb:1234/5678/9abc\x1b\\', '\x1b]5;0;RGB:1/2/3\x07', '\x1b]5;0;rgb:1/2\x1b\\',
        '\x1b]5;0;\x1b\\', '\x1b]5;0;', '\x1b]5;1;rgb:1/2/3\x1b\\', '\x1b]5;0;rgb:1/2/3'];
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeOsc5Adapter.probeIds], [...osc5CaseIds]);
        for (const reply of [null, ...frames]) {
            const exchange = reply === null ? { barrier: da, replies: [] } : { barrier: da, replies: [reply] };
            const termlessContext = osc5RecordingContext(contract, exchange);
            const { probeId, ...termless } = module.ntildeRunOsc5Termless(caseId, termlessContext);
            assert.equal(probeId, caseId);
            assert.equal(termlessContext.fedCount, 1, 'the termless path makes exactly the pinned call');
            assert.equal(termlessContext.askedCount, 0);
            assert.deepEqual(termless, replayOsc5Pinned(caseId, 'termless', { replies: reply ?? '' }), `termless over ${JSON.stringify(reply)}`);
            const terminalContext = osc5RecordingContext(contract, exchange);
            const { probeId: terminalId, ...terminal } = await module.ntildeRunOsc5Terminal(caseId, terminalContext);
            assert.equal(terminalId, caseId);
            assert.equal(terminalContext.fedCount, 1);
            assert.equal(terminalContext.askedCount, 1);
            assert.deepEqual(terminal, replayOsc5Pinned(caseId, 'terminal', { replies: reply ?? '', barrier: da }), `terminal over ${JSON.stringify(reply)}`);
            assert.notEqual(termless.observation.outcome, 'unsupported');
        }
        // The pinned request and frame pattern are exactly the contract's.
        const probe = await module.ntildeRunOsc5Terminal(caseId, { queryWithSentinelOutcome: async (text, pattern) => {
            assert.equal(text, osc5PinnedQuery);
            assert.equal(pattern.source, osc5PinnedPattern.source);
            assert.equal(pattern.flags, osc5PinnedPattern.flags);
            return { reason: 'timeout', raw: '', match: null };
        } });
        assert.equal(probe.observation.reason, 'no-response', 'a real timeout resolves through oscReplyResult, which the journal never produces');
    } finally { rmSync(file, { force: true }); }
});
