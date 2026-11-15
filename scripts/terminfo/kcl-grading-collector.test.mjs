import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptKclCollector, kclAdapterId, kclSeams } from './kcl-grading-collector.mjs';
import { kclCaseIds, kclContracts, kclPinnedPattern, kclPinnedQuery, replayKclPinned } from './kcl-contracts.mjs';
import { kclRecordingContext } from './kcl-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => kclSeams.join('\n/* gap */\n') + '\n';
const da = '\x1b[?62;4;22c';
const [contract] = kclContracts;
const caseId = kclCaseIds[0];

test('derives the kitty color extension by appending exports only', () => {
    const source = verbatim(), derived = adaptKclCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeKclAdapter = Object.freeze({\n  id: "${kclAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunKclTermless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunKclTerminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...kclCaseIds])));
});

test('the seams pin the registration, both verdict-deciding helpers and the left/right-margin layer', () => {
    const joined = kclSeams.join('\n');
    assert.ok(joined.includes('probe("extensions.osc21-kitty-color"') && joined.includes('ctx.feedCapture("\\x1B]21;foreground=?\\x1B\\\\")') && joined.includes('ctx.queryWithSentinelOutcome("\\x1B]21;foreground=?\\x1B\\\\"'), 'the request bytes');
    assert.ok(joined.includes('function kittyForegroundResult(response) {') && joined.includes('function unansweredQuery(reply, note) {'));
    assert.ok(joined.includes('"foreground=")') && joined.includes('{1,4}'), 'the value criterion');
    assert.ok(kclSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
    assert.ok(kclSeams.includes('export function ntildeRunLrMarginsTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of kclSeams) {
        assert.throws(() => adaptKclCollector(verbatim().split(seam).join('CHANGED')), /Pinned kitty color probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptKclCollector(verbatim() + seam + '\n'), /Pinned kitty color probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change a request, a criterion or a verdict', () => {
    for (const [from, to] of [['ctx.feedCapture("\\x1B]21;foreground=?', 'ctx.feedCapture("\\x1B]21;background=?'], ['values.length === 1', 'values.length >= 1'], ['{1,4}', '{1,8}'],
        ['value === ""', 'value !== ""'], ['outcome: pass ? "supported" : "inconclusive"', 'outcome: pass ? "supported" : "unsupported"'], ['extensions.osc21-kitty-color', 'extensions.osc21-kitty-colour'],
        ['reason: reply.reason === "timeout" ? "timeout" : "no-response"', 'reason: "timeout"'], ['reply.match[0] ?? ""', 'reply.raw']]) {
        assert.ok(kclSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptKclCollector(kclSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
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
    text = adaptKclCollector(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-kcl-test-${process.pid}.mjs`, import.meta.url));
    const frames = ['\x1b]21;foreground=rgb:cc/cc/cc\x1b\\', '\x1b]21;foreground=\x1b\\', '\x07', '\x1b]21;foreground=rgb:1234/5678/9abc\x07', '\x1b]21;foreground=red\x1b\\', '\x1b]21;foreground=rgb:1/2\x1b\\',
        '\x1b]21;background=rgb:1/2/3\x1b\\', '\x1b]21;foreground=rgb:1/2/3;foreground=rgb:1/2/3\x1b\\', '\x1b]21;unknown=Zm9v\x1b\\', '\x1b]21;foreground=RGB:A/B/C\x1b\\', '\x1b]21;\x1b\\', '\x1b]21;foreground=rgb:1/2/3'];
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeKclAdapter.probeIds], [...kclCaseIds]);
        for (const reply of [null, ...frames]) {
            const exchange = reply === null ? { barrier: da, replies: [] } : { barrier: da, replies: [reply] };
            const termlessContext = kclRecordingContext(contract, exchange);
            const { probeId, ...termless } = module.ntildeRunKclTermless(caseId, termlessContext);
            assert.equal(probeId, caseId);
            assert.equal(termlessContext.fedCount, 1, 'the termless path makes exactly the pinned call');
            assert.equal(termlessContext.askedCount, 0);
            assert.deepEqual(termless, replayKclPinned(caseId, 'termless', { replies: reply ?? '' }), `termless over ${JSON.stringify(reply)}`);
            const terminalContext = kclRecordingContext(contract, exchange);
            const { probeId: terminalId, ...terminal } = await module.ntildeRunKclTerminal(caseId, terminalContext);
            assert.equal(terminalId, caseId);
            assert.equal(terminalContext.fedCount, 1);
            assert.equal(terminalContext.askedCount, 1);
            assert.deepEqual(terminal, replayKclPinned(caseId, 'terminal', { replies: reply ?? '', barrier: da }), `terminal over ${JSON.stringify(reply)}`);
            assert.notEqual(termless.observation.outcome, 'unsupported');
        }
        // The pinned request and frame pattern are exactly the contract's.
        const probe = await module.ntildeRunKclTerminal(caseId, { queryWithSentinelOutcome: async (text, pattern) => {
            assert.equal(text, kclPinnedQuery);
            assert.equal(pattern.source, kclPinnedPattern.source);
            assert.equal(pattern.flags, kclPinnedPattern.flags);
            return { reason: 'timeout', raw: '', match: null };
        } });
        assert.equal(probe.observation.reason, 'timeout', 'a real timeout is the pinned timeout, which the journal never produces');
    } finally { rmSync(file, { force: true }); }
});
