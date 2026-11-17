import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, writeFileSync, rmSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptCollector } from './collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { layerChain } from './osc133-receipt.mjs';
import { adaptOsc22Collector, osc22Seams } from './osc22-grading-collector.mjs';
import { osc22CaseId, osc22PinnedWrites } from './osc22-contracts.mjs';

test('OSC 22 derivation only appends and rejects changed or repeated pinned seams', () => {
    const source = osc22Seams.join('\n');
    assert.ok(adaptOsc22Collector(source).startsWith(source));
    for (const seam of osc22Seams) {
        assert.throws(() => adaptOsc22Collector(source.replace(seam, 'changed')));
        assert.throws(() => adaptOsc22Collector(source + seam));
    }
});

testWithPinnedCollector('exact pinned OSC 22 has no termless path and never grades pointer shape from CPR', async bundleUrl => {
    let source = adaptGradingCollector(adaptCollector(readFileSync(bundleUrl, 'utf8'), fileURLToPath(new URL('./ownership.mjs', import.meta.url))));
    for (const [, adapt] of layerChain) source = adapt(source);
    const file = new URL(`./ntilde-osc22-test-${process.pid}.mjs`, bundleUrl);
    try {
        writeFileSync(file, adaptOsc22Collector(source));
        const module = await import(file.href);
        assert.equal(module.ntildeOsc22HasTermless(osc22CaseId), false);
        for (const position of [null, { row: 1, col: 1 }, { row: 7, col: 20 }]) {
            const writes = []; let queries = 0;
            const context = new Proxy({ write(value) { writes.push(value); },
                async queryCursorPosition() { queries++; return position; } }, { get(target, key) {
                assert.ok(Object.hasOwn(target, key), `unexpected pinned read: ${String(key)}`); return target[key];
            } });
            const result = await module.ntildeRunOsc22Terminal(osc22CaseId, context);
            assert.deepEqual(writes, [...osc22PinnedWrites]); assert.equal(queries, 1);
            assert.equal(result.pass, false); assert.equal(result.observation.outcome, 'inconclusive');
            assert.equal(result.observation.reason, position ? 'insufficient-evidence' : 'no-response');
        }
    } finally { rmSync(file, { force: true }); }
});
