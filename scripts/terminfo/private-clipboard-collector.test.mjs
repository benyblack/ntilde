import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { adaptCollector, upstreamBundleSha256 } from './collector.mjs';
import { preparePrivateClipboardSource } from './private-clipboard-source.mjs';
import { historicalArchive } from './fixtures/captured-sources.mjs';

// Recover data from an immutable archive, never execute archive modules. The
// independent upstream digest is authority for the recovered original bytes.
import { archived, original } from './fixtures/private-clipboard-upstream.mjs';
const ownerPath = '/synthetic/ownership.mjs', workerPath = '/synthetic/private-clipboard-worker.mjs';

test('ordinary adaptation remains byte-identical after the path-only import line to frozen v11', () => {
    const adapted = adaptCollector(original, ownerPath);
    assert.equal(adapted.slice(adapted.indexOf('\n') + 1), archived.slice(archived.indexOf('\n') + 1));
    assert.equal(adapted.includes('await fixture.beforeOsc52Write(nonce)'), false);
});

test('explicit private adaptation gates original Q11 bytes, binds exact probe ID and closes before separate summary', () => {
    const adapted = adaptCollector(original, ownerPath, { privateClipboardModulePath: workerPath });
    const prepared = preparePrivateClipboardSource(original).source;
    assert.ok(adapted.includes(prepared.slice(prepared.indexOf('function liveClipboardNotTested() {'),
        prepared.indexOf('var sixelDa1Reply, foregroundQuery'))));
    assert.ok(adapted.includes('createPrivateClipboardWorker'));
    assert.ok(adapted.includes('clipboard.withClipboardFixture(probe.id, work,'));
    assert.ok(adapted.includes('batch.rawReplies["collector.ntildePrivateClipboard"]'));
    assert.ok(adapted.includes('await clipboard.finish()'));
    assert.ok(adapted.includes('permissions: null'));
    const syntax = spawnSync(process.execPath, ['--check', '--input-type=module'], { input: adapted, encoding: 'utf8' });
    assert.equal(syntax.status, 0, syntax.stderr);
});

test('private derivation rejects unknown options instead of silently broadening the owner', () => {
    assert.throws(() => adaptCollector(original, ownerPath, { privateClipboardModulePath: workerPath, allowRead: true }), /private|option/i);
});
