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
    assert.ok(adapted.includes('    if (options.ownedTerminal?.clipboard && options.ownedTerminal.clipboard.profile !== "default"\n'
        + '        && (options.ownedTerminal.clipboard.profile !== "ntilde-private-app-v1" || ["extensions.osc52-clipboard", "extensions.osc52-write", "extensions.osc52-read"].includes(probe.id))) {'));
    assert.ok(adapted.includes('batch.rawReplies["collector.ntildePrivateClipboard"]'));
    assert.ok(adapted.includes('await clipboard.finish()'));
    assert.ok(adapted.includes('permissions: null'));
    const syntax = spawnSync(process.execPath, ['--check', '--input-type=module'], { input: adapted, encoding: 'utf8' });
    assert.equal(syntax.status, 0, syntax.stderr);
});

test('private derivation rejects unknown options instead of silently broadening the owner', () => {
    assert.throws(() => adaptCollector(original, ownerPath, { privateClipboardModulePath: workerPath, allowRead: true }), /private|option/i);
});

import { collectionSource, collectionSourceSha256 } from './fixtures/private-clipboard-collection.mjs';
/** Evaluate only the current generator's pinned function with fake dependencies.
 * The original function must equal the reviewed repository literal first; no
 * archived module, package/dependency, PTY or clipboard implementation executes.
 */
function generatedCollection({ operation, dispose = async () => {} } = {}) {
    assert.equal(createHash('sha256').update(collectionSource).digest('hex'), collectionSourceSha256);
    assert.ok(original.includes(collectionSource));
    const generated = adaptCollector(original, ownerPath, { privateClipboardModulePath: workerPath });
    const start = generated.indexOf('async function collectProbeRun(options = {}) {'), end = generated.indexOf('\nfunction register(', start);
    const fn = generated.slice(start, end), owner = { geometryAtGrant: { status: 'measured', rows: 40, cols: 140 },
        clipboard: { profile: 'ntilde-private-app-v1', summary: 'private-summary' }, summary: 'owner-summary', dispose };
    const dependencies = {
        process: { env: {}, stdout: {} }, randomBytes: () => ({ toString: () => 'a'.repeat(32) }),
        detectTerminal: () => ({ name: 'ntilde', version: '0.13.0', os: 'macos', osVersion: 'synthetic' }),
        getTrustedSuiteReceipt: () => ({ manifest: { probeHash: 'synthetic' }, collectorRevision: 'synthetic' }),
        createNtildeOwnedTerminal: async () => owner,
        withRawMode: operation ?? (async work => work()), COMPLETE_XTVERSION: null,
        queryWithSentinelOutcome: async sequence => ({ match: sequence.includes('18t') ? ['frame', '40', '140'] : null, raw: '', rawBase64: '', reason: 'reply' }),
        resolveMeasuredAppVersion: () => '0.13.0', drainStdin: async () => {},
        runProbeBatch: async () => ({ rawReplies: {}, assertions: [], screenshotRefs: [], observations: [], suiteComplete: false, ungradedDiagnostics: {} }),
    };
    const create = new Function(...Object.keys(dependencies), fn + '\nreturn collectProbeRun;');
    return () => create(...Object.values(dependencies))({ ntildeOwner: {}, out: {} });
}
test('actual generated private collection never impersonates native fixture provenance or read permissions', async () => {
    const raw = await generatedCollection()();
    assert.equal(raw.target.permissions, null); assert.equal(raw.rawReplies['collector.clipboardFixture'], undefined);
    assert.equal(raw.rawReplies['collector.ntildePrivateClipboard'], 'private-summary');
});
for (const primary of [new Error('stock collector failed'), undefined]) test('generated private collection preserves primary and finalization failures, primary=' + typeof primary, async () => {
    const cleanup = new Error('private finalization failed');
    const collect = generatedCollection({ operation: async () => { throw primary; }, dispose: async () => { throw cleanup; } });
    await assert.rejects(collect(), error => error instanceof AggregateError && error.errors[0] === primary && error.errors[1] === cleanup);
});

for (const primary of [new Error('original collector failed'), undefined]) test('generated collection preserves an unaccompanied primary rejection, type=' + typeof primary, async () => {
    let disposed = false;
    await assert.rejects(generatedCollection({ operation: async () => { throw primary; }, dispose: async () => { disposed = true; } })(), error => error === primary);
    assert.equal(disposed, true);
});
test('generated collection preserves an unaccompanied finalization rejection', async () => {
    const cleanup = new Error('finalization alone failed');
    await assert.rejects(generatedCollection({ dispose: async () => { throw cleanup; } })(), error => error === cleanup);
});
