import assert from 'node:assert/strict';
import { test } from 'node:test';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptCapsCollector, capsAdapterId, capsSeams } from './caps-grading-collector.mjs';
import { capsCaseIds, capsContracts, capsPinnedPattern, capsPinnedQuery, capsExpectedReply, capsDeclaredFeatures, replayCapsPinned, capsSilentPayloads } from './caps-contracts.mjs';
import { requirePinnedAgreement } from './graded-plan.mjs';
import { syntheticCapsEvidence, syntheticCapsGrade } from './fixtures/caps-evidence.mjs';
import { buildCapsGradingReport } from './caps-grading.mjs';
import { capsRecordingContext } from './caps-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => capsSeams.join('\n/* gap */\n') + '\n';
const da = '\x1b[?62;4;22c';
const [contract] = capsContracts;
const caseId = capsCaseIds[0];

test('derives the feature-reporting extension by appending exports only', () => {
    const source = verbatim(), derived = adaptCapsCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeCapsAdapter = Object.freeze({\n  id: "${capsAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunCapsTermless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunCapsTerminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...capsCaseIds])));
});

test('the seams pin the registration, the reply helper that decides the verdict, the factory and the left/right-margin layer', () => {
    const joined = capsSeams.join('\n');
    assert.ok(joined.includes('oscSimpleQueryProbe("extensions.osc1337-capabilities"'), 'the registration');
    assert.ok(joined.includes('"\\x1B]1337;RequestCapabilities\\x07"'), 'the request bytes');
    assert.ok(joined.includes('function oscReplyResult(raw, frame, expected, prefix) {'));
    assert.ok(joined.includes('[^\\x07\\x1b]*'), 'the frame criterion accepts any feature string');
    assert.ok(joined.includes('function oscSimpleQueryProbe(id, query, pattern, prefix, expected) {'), 'the factory');
    assert.ok(capsSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
    assert.ok(capsSeams.includes('export function ntildeRunLrMarginsTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of capsSeams) {
        assert.throws(() => adaptCapsCollector(verbatim().split(seam).join('CHANGED')), /Pinned feature-reporting probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptCapsCollector(verbatim() + seam + '\n'), /Pinned feature-reporting probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change a request, a criterion or a verdict', () => {
    for (const [from, to] of [['"\\x1B]1337;RequestCapabilities\\x07"', '"\\x1B]1337;RequestCapabilities\\x1b\\\\"'], ['[^\\x07\\x1b]*', '[^\\x07]*'],
        ['"Complete OSC 1337 Capabilities reply"', '"Complete OSC 1337 capability reply"'], ['extensions.osc1337-capabilities', 'extensions.osc1337-capabilites'],
        ['outcome: "supported"', 'outcome: "unsupported"'], ['prefix.test(raw)', 'prefix.ignore'], ['Capabilities=', 'capabilities=']]) {
        assert.ok(capsSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptCapsCollector(capsSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

test('the contracted declaration is the specification-table transcription with Cw, and the silent payloads are bounded', () => {
    // Written independently here: the effect contract's demand must equal this literal, not the parser's constant.
    assert.equal(capsExpectedReply, '\x1b]1337;Capabilities=T3CwLrMSc7UTs3BFGsGoSyHNoP\x1b\\');
    assert.equal(capsDeclaredFeatures, 'T3CwLrMSc7UTs3BFGsGoSyHNoP');
    assert.ok(capsSilentPayloads.includes('1337;Capabilities=') === false, 'the exact payload is a request, never listed silent');
    assert.equal(capsSilentPayloads.length, 6);
});

test('the synthetic fixture is supported and a changed reply is refused as unsupported', () => {
    assert.deepEqual(syntheticCapsGrade(), { outcome: 'supported' });
    const dropped = syntheticCapsGrade(capsContracts[0].caseId, undefined, (caseId, role, read) => (role === 'pinned' ? { ...read, replies: [] } : null));
    assert.deepEqual(dropped, { outcome: 'unsupported', note: 'The pinned request was not answered' });
    const wrong = syntheticCapsGrade(capsContracts[0].caseId, undefined, (caseId, role, read) => (role === 'pinned' ? { ...read, replies: ['\x1b]1337;Capabilities=T3\x07'] } : null));
    assert.equal(wrong.outcome, 'unsupported');
});

test('the state-unchanged rule refuses an exchange that mutated the screen', () => {
    const moved = syntheticCapsGrade(capsContracts[0].caseId, undefined, (caseId, role, read) => {
        if (role !== 'pinned') return null;
        const screen = structuredClone(read.screen);
        screen.lines[0] = 'changed';
        return { ...read, screen };
    });
    assert.equal(moved.outcome, 'unsupported');
    assert.match(moved.note, /changed the screen/);
});

testWithPinnedCollector('the real pinned bundle derives through the layers and the pinned code runs over served bytes', async bundleUrl => {
    const { adaptCollector } = await import('./collector.mjs');
    const { adaptGradingCollector } = await import('./grading-collector.mjs');
    const { layerChain } = await import('./osc133-receipt.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptGradingCollector(adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname));
    for (const [, adapt] of layerChain) text = adapt(text);
    text = adaptCapsCollector(text);
    const file = fileURLToPath(new URL(`./ntilde-caps-test-${process.pid}.mjs`, bundleUrl));
    const frames = ['\x1b]1337;Capabilities=T3CwLrMSc7UTs3BFGsGoSyHNoP\x1b\\', '\x1b]1337;Capabilities=T3CwLrMSc7UTs3BFGsGoSyHNoP\x07', '\x1b]1337;Capabilities=whatever\x07',
        '\x1b]1337;Capabilities=', '\x1b]1337;Cap', '\x1b]1337;x\x07tail'];
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeCapsAdapter.probeIds], [...capsCaseIds]);
        for (const reply of [null, ...frames]) {
            const exchange = reply === null ? { barrier: da, replies: [] } : { barrier: da, replies: [reply] };
            const termlessContext = capsRecordingContext(contract, exchange);
            const { probeId, ...termless } = module.ntildeRunCapsTermless(caseId, termlessContext);
            assert.equal(probeId, caseId);
            assert.equal(termlessContext.fedCount, 1, 'the termless path makes exactly the pinned call');
            assert.equal(termlessContext.askedCount, 0);
            assert.deepEqual(termless, replayCapsPinned(caseId, 'termless', { replies: reply ?? '' }), `termless over ${JSON.stringify(reply)}`);
            const terminalContext = capsRecordingContext(contract, exchange);
            const { probeId: terminalId, ...terminal } = await module.ntildeRunCapsTerminal(caseId, terminalContext);
            assert.equal(terminalId, caseId);
            assert.equal(terminalContext.fedCount, 1);
            assert.equal(terminalContext.askedCount, 1);
            assert.deepEqual(terminal, replayCapsPinned(caseId, 'terminal', { replies: reply ?? '', barrier: da }), `terminal over ${JSON.stringify(reply)}`);
            // deepEqual ignores object key order; the production pinned-agreement check serializes it.
            assert.doesNotThrow(() => requirePinnedAgreement(contract, termless, replayCapsPinned(caseId, 'termless', { replies: reply ?? '' }), 'real-adapter'));
            assert.doesNotThrow(() => requirePinnedAgreement(contract, terminal, replayCapsPinned(caseId, 'terminal', { replies: reply ?? '', barrier: da }), 'real-adapter'));
            if (reply === null) {
                const evidence = syntheticCapsEvidence({ mutate: (id, role, read) => role === 'pinned' ? { ...read, replies: exchange.replies } : null });
                evidence.run.cases[0].pinned = { termless: { probeId: caseId, ...termless }, terminal: { probeId: caseId, ...terminal } };
                const report = buildCapsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
                assert.equal(report.cases[0].conclusion.outcome, 'inconclusive');
                assert.equal(report.cases[0].conclusion.reason, 'no-response');
                assert.equal(report.cases[0].effectContract.outcome, 'unsupported');
            }
            assert.notEqual(termless.observation.outcome, 'unsupported');
        }
        // The pinned request and frame pattern are exactly the contract's.
        const probe = await module.ntildeRunCapsTerminal(caseId, { queryWithSentinelOutcome: async (text, pattern) => {
            assert.equal(text, capsPinnedQuery);
            assert.equal(pattern.source, capsPinnedPattern.source);
            assert.equal(pattern.flags, capsPinnedPattern.flags);
            return { reason: 'timeout', raw: '', match: null };
        } });
        assert.equal(probe.observation.reason, 'no-response', 'a real timeout resolves through oscReplyResult, which the journal never produces');
    } finally { rmSync(file, { force: true }); }
});
