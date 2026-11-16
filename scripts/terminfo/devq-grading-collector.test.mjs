import assert from 'node:assert/strict';
import { test } from 'node:test';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptDevqCollector, devqAdapterId, devqSeams } from './devq-grading-collector.mjs';
import { devqCaseIds, devqContracts, replayDevqPinned } from './devq-contracts.mjs';
import { devqRecordingContext } from './devq-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => devqSeams.join('\n/* gap */\n') + '\n';
const da = '\x1b[?62;4;22c';
const st = '\x1b\\';
const byId = Object.fromEntries(devqContracts.map(item => [item.caseId, item]));

test('derives the device-query extension by appending exports only', () => {
    const source = verbatim(), derived = adaptDevqCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeDevqAdapter = Object.freeze({\n  id: "${devqAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunDevqTermless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunDevqTerminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...devqCaseIds])));
});

test('the seams pin the four exact registrations, the label definitions and the left/right-margin layer', () => {
    const joined = devqSeams.join('\n');
    assert.ok(joined.includes('id: "device.xtgetxres"') && joined.includes('query: "\\x1BP+Q7465726d4e616d65\\x1B\\\\"'), 'the exact XTGETXRES request bytes');
    assert.ok(joined.includes('valid: /\\x1bP1\\+R7465726d4e616d65=[0-9A-Fa-f]+\\x1b\\\\/i') && joined.includes('refusal: /\\x1bP0\\+R7465726d4e616d65\\x1b\\\\/i'));
    assert.ok(joined.includes('id: "device.xtreportcolors"') && joined.includes('valid: /\\x1b\\[[0-9;]*#Q/') && joined.includes('malformed: /\\x1b\\[[0-9;]*#|#Q/'), 'the no-? pattern');
    assert.ok(joined.includes('valid: /\\x1b\\]Ltest-icon(?:\\x07|\\x1b\\\\)/') && joined.includes('valid: /\\x1b\\]ltest-title(?:\\x07|\\x1b\\\\)/'));
    assert.ok(joined.includes('ctx.feed("\\x1B]1;test-icon\\x07")') && joined.includes('ctx.feed("\\x1B]2;test-title\\x07")'), 'the setup writes');
    assert.ok(devqSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
    assert.ok(devqSeams.includes('export function ntildeRunLrMarginsTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of devqSeams) {
        assert.throws(() => adaptDevqCollector(verbatim().split(seam).join('CHANGED')), /Pinned device-query probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptDevqCollector(verbatim() + seam + '\n'), /Pinned device-query probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change a request, a criterion or a setup', () => {
    for (const [from, to] of [['+Q7465726d4e616d65', '+Q5465726d4e616d65'], ['P0\\+R', 'P2\\+R'], ['#Q/,', '#R/,'], ['test-icon(?', 'test-ico(?'], ['ltest-title', 'ltest-titl'],
        ['ctx.feed("\\x1B]1;test-icon', 'ctx.feed("\\x1B]1;test-ico'], ['device.xtwinops-21', 'device.xtwinops-22'], ['"\\x1B[20t"', '"\\x1B[21t"']]) {
        assert.ok(devqSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptDevqCollector(devqSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

testWithPinnedCollector('the real pinned bundle derives through every layer and the pinned code runs over served replies', async bundleUrl => {
    const { adaptCollector } = await import('./collector.mjs');
    const { adaptGradingCollector } = await import('./grading-collector.mjs');
    const { layerChain } = await import('./osc133-receipt.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptGradingCollector(adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname));
    for (const [, adapt] of layerChain) text = adapt(text);
    text = adaptDevqCollector(text);
    const file = fileURLToPath(new URL(`./ntilde-dvq-test-${process.pid}.mjs`, bundleUrl));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeDevqAdapter.probeIds], [...devqCaseIds]);
        const served = {
            'device.xtgetxres': ['\x1bP1+R7465726d4e616d65=787465726D2D323536636F6C6F72' + st],
            'device.xtreportcolors': ['\x1b[?0;0#Q'],
            'device.xtwinops-20': ['\x1b]Ltest-icon' + st],
            'device.xtwinops-21': ['\x1b]ltest-title' + st],
        };
        for (const caseId of devqCaseIds) {
            const contract = byId[caseId], exchange = { barrier: da, replies: served[caseId] };
            const termlessContext = devqRecordingContext(contract, exchange);
            const { probeId, ...termless } = module.ntildeRunDevqTermless(caseId, termlessContext);
            assert.equal(probeId, caseId);
            assert.equal(termlessContext.fedCount, contract.pinnedFeeds.length, `${caseId} feeds exactly the pinned bytes`);
            assert.deepEqual(termless, replayDevqPinned(caseId, served[caseId].join('')), `${caseId} termless matches the replay`);
            const terminalContext = devqRecordingContext(contract, exchange);
            const { probeId: terminalId, ...terminal } = await module.ntildeRunDevqTerminal(caseId, terminalContext);
            assert.equal(terminalId, caseId);
            assert.deepEqual(terminal, replayDevqPinned(caseId, served[caseId].join('') + da), `${caseId} terminal matches the replay`);
        }
        // xterm's reply cannot satisfy the pinned XTREPORTCOLORS pattern: the pinned code says invalid-reply, not supported.
        const context = devqRecordingContext(byId['device.xtreportcolors'], { barrier: da, replies: ['\x1b[?0;0#Q'] });
        assert.equal(module.ntildeRunDevqTermless('device.xtreportcolors', context).observation.reason, 'invalid-reply');
        const unmarked = devqRecordingContext(byId['device.xtreportcolors'], { barrier: da, replies: ['\x1b[0;0#Q'] });
        assert.equal(module.ntildeRunDevqTermless('device.xtreportcolors', unmarked).observation.outcome, 'supported');
    } finally { rmSync(file, { force: true }); }
});

test('the served pinned contexts refuse any feed, write, read or query outside the pinned fixture', async () => {
    const plain = byId['device.xtgetxres'], withSetup = byId['device.xtwinops-20'];
    const exchange = { barrier: da, replies: ['\x1bP1+R7465726d4e616d65=41' + st] };
    const context = devqRecordingContext(plain, exchange);
    assert.throws(() => context.feed(plain.pinnedFeeds[0]), /must not plain-feed/);
    assert.throws(() => context.write(plain.pinnedFeeds[0]), /must not write/);
    assert.throws(() => context.getCell(), /must not read cells/);
    assert.equal(context.feedCapture(plain.pinnedFeeds[0]), exchange.replies[0]);
    assert.throws(() => context.feedCapture(plain.pinnedFeeds[0]), /captures out of order/);
    const asked = devqRecordingContext(plain, exchange);
    await assert.rejects(asked.queryWithSentinelOutcome('\x1bP+Q5\x1b\\', /x/), /differs from the contract/);
    const terminal = devqRecordingContext(plain, exchange);
    await terminal.queryWithSentinelOutcome(plain.pinnedFeeds[0], /x/);
    await assert.rejects(terminal.queryWithSentinelOutcome(plain.pinnedFeeds[0], /x/), /outside its fixture/, 'a second terminal query is refused');
    // A case with a pinned setup feeds it first and refuses a capture before it or a different setup.
    const setup = devqRecordingContext(withSetup, { barrier: da, replies: ['\x1b]Ltest-icon' + st] });
    assert.throws(() => setup.feedCapture(withSetup.pinnedFeeds[1]), /captures out of order/);
    assert.throws(() => setup.feed('\x1b]1;other\x07'), /differs from the contract/);
    setup.feed(withSetup.pinnedFeeds[0]);
    assert.throws(() => setup.feed(withSetup.pinnedFeeds[0]), /must not plain-feed/);
    assert.equal(setup.feedCapture(withSetup.pinnedFeeds[1]), '\x1b]Ltest-icon' + st);
    const written = devqRecordingContext(withSetup, { barrier: da, replies: [] });
    written.write(withSetup.pinnedFeeds[0]);
    assert.equal((await written.queryWithSentinelOutcome(withSetup.pinnedFeeds[1], /x/)).raw, da);
});
