import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { osc633ScopeV2, legacyScopeV2, osc633ScopeV3, legacyScopeV3, historicLegacyScope, historicOsc633Scope, integratedLegacyScope, integratedOsc633Scope, isLegacyScope, isOsc633Scope, legacyScope, osc633Scope, runPredecessorExtractor, validateCollectionScope, verifyScopePredecessor } from './collection-scope.mjs';
import { completeOwnedReceipt } from './receipt.mjs';

test('only the fixed scope is admitted by matching context, parent proof, policy and absence of excluded evidence', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-scope-'));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v65', osc633SuiteId: 'ntilde-owned-osc633-graded-v1', collectionScope: structuredClone(osc633Scope) };
    const proof = { collectionScope: structuredClone(osc633Scope) };
    const evidence = { osc633Evidence: {} };
    try {
        assert.equal(validateCollectionScope(root, {}, {}, {}), false);
        assert.equal(validateCollectionScope(root, evidence, context, proof), true);
        for (const alter of [
            () => [{ ...context, collectionScope: undefined }, proof, evidence],
            () => [context, {}, evidence],
            () => [{ ...context, sourceSnapshotPolicy: 'readonly-owned-source-graph-v61' }, proof, evidence],
            () => [{ ...context, collectionScope: { ...osc633Scope, omittedSuites: ['notify'] } }, proof, evidence],
            () => [context, proof, {}],
            () => [context, proof, { ...evidence, osc9Evidence: {} }],
            () => [context, proof, { ...evidence, notifyEvidence: {} }],
        ]) {
            const [c, p, e] = alter();
            assert.throws(() => validateCollectionScope(root, e, c, p));
        }
        for (const file of ['osn-run.json', 'osn-snapshots.jsonl', 'ntf-run.json', 'ntf-unavailable.json']) {
            writeFileSync(join(root, file), '');
            assert.throws(() => validateCollectionScope(root, evidence, context, proof), /scoped collection/);
            rmSync(join(root, file));
        }
        assert.equal(isOsc633Scope({ ...osc633Scope, deadlineMs: 300001 }), false);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('each scope generation is bound to the one source policy it was collected under, so published v62 runs stay valid', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-scope-gen-'));
    const scoped = (scope, policy) => [root, { osc633Evidence: {} }, { sourceSnapshotPolicy: policy, osc633SuiteId: 'ntilde-owned-osc633-graded-v1', collectionScope: structuredClone(scope) }, { collectionScope: structuredClone(scope) }];
    const legacy = (scope, policy) => [root, {}, { sourceSnapshotPolicy: policy, collectionScope: structuredClone(scope) }, { collectionScope: structuredClone(scope) }];
    try {
        const reorder = ([dir, evidence, context, proof]) => validateCollectionScope(dir, evidence, context, proof);
        assert.equal(reorder(scoped(osc633ScopeV2, 'readonly-owned-source-graph-v62')), true);
        assert.equal(reorder(scoped(osc633Scope, 'readonly-owned-source-graph-v65')), true);
        assert.equal(reorder(scoped(osc633Scope, 'readonly-owned-source-graph-v69')), true);
        assert.throws(() => reorder(scoped(osc633ScopeV2, 'readonly-owned-source-graph-v65')), /scoped collection/);
        assert.throws(() => reorder(scoped(osc633Scope, 'readonly-owned-source-graph-v62')), /scoped collection/);
        assert.equal(reorder(legacy(legacyScopeV2, 'readonly-owned-source-graph-v62')), true);
        assert.equal(reorder(legacy(legacyScope, 'readonly-owned-source-graph-v65')), true);
        assert.equal(reorder(legacy(legacyScope, 'readonly-owned-source-graph-v69')), true);
        assert.throws(() => reorder(legacy(legacyScopeV2, 'readonly-owned-source-graph-v65')), /scoped collection/);
        assert.throws(() => reorder(legacy(legacyScope, 'readonly-owned-source-graph-v62')), /scoped collection/);
        assert.equal(osc633ScopeV2.id.endsWith('-v2'), true); assert.equal(osc633Scope.id.endsWith('-v5'), true);
        assert.deepEqual(osc633Scope.predecessor, osc633ScopeV2.predecessor);
        assert.equal(isOsc633Scope(osc633ScopeV2) && isLegacyScope(legacyScopeV2), true);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('the default selection preserves all prior suites and explicitly excludes only OSC 633', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-legacy-scope-'));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v65', collectionScope: structuredClone(legacyScope) };
    const proof = { collectionScope: structuredClone(legacyScope) };
    try {
        assert.equal(isLegacyScope(context.collectionScope), true);
        assert.equal(validateCollectionScope(root, { osc9Evidence: {}, notifyEvidence: {} }, context, proof), true);
        assert.throws(() => validateCollectionScope(root, { osc633Evidence: {} }, context, proof), /scoped collection/);
        assert.throws(() => validateCollectionScope(root, {}, { ...context, osc633SuiteId: 'x' }, proof), /scoped collection/);
        assert.throws(() => validateCollectionScope(root, {}, context, { collectionScope: osc633Scope }), /scoped collection/);
        writeFileSync(join(root, 'vs633-run.json'), '');
        assert.throws(() => validateCollectionScope(root, {}, context, proof), /scoped collection/);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('the integrated v62 selections retain their own exact identities while the new selections require v65', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-integrated-scope-'));
    try {
        for (const scope of [integratedLegacyScope, integratedOsc633Scope]) {
            const evidence = isLegacyScope(scope) ? {} : { osc633Evidence: {} };
            const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v62', collectionScope: scope,
                ...(isLegacyScope(scope) ? {} : { osc633SuiteId: 'ntilde-owned-osc633-graded-v1' }) };
            assert.equal(validateCollectionScope(root, evidence, context, { collectionScope: scope }), true);
            assert.throws(() => validateCollectionScope(root, evidence, { ...context, sourceSnapshotPolicy: 'readonly-owned-source-graph-v65' }, { collectionScope: scope }));
        }
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('historical v61 scope refuses SGR-pixel evidence that belongs to the other v61 graph', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-historical-scope-'));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v61', osc633SuiteId: 'ntilde-owned-osc633-graded-v1', collectionScope: historicOsc633Scope };
    const proof = { collectionScope: historicOsc633Scope }, evidence = { osc633Evidence: {} };
    try {
        assert.equal(validateCollectionScope(root, evidence, context, proof), true);
        assert.throws(() => validateCollectionScope(root, { ...evidence, pxmEvidence: {} }, context, proof), /scoped collection/);
        writeFileSync(join(root, 'pxm-run.json'), '');
        assert.throws(() => validateCollectionScope(root, evidence, context, proof), /scoped collection/);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('a stalled predecessor extractor is killed and extraction fails closed', () => {
    let called = false;
    const stalled = (_python, args, options) => {
        called = true;
        assert.deepEqual(args, ['-c', 'script', 'archive', 'root']);
        assert.equal(options.timeout, 30000);
        assert.equal(options.killSignal, 'SIGKILL');
        const result = spawnSync(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { ...options, timeout: 150 });
        assert.equal(result.error?.code, 'ETIMEDOUT');
        assert.equal(result.status, null);
        return result;
    };
    assert.throws(() => runPredecessorExtractor(stalled, 'python', 'script', 'archive', 'root'), /archive extraction failed/);
    assert.equal(called, true);
    assert.throws(() => runPredecessorExtractor(() => ({ status: 0, error: new Error('spawn failed') }), 'python', '', '', ''), /archive extraction failed/);
});

test('the pinned full predecessor reissues its actual receipt; modified archive bytes are refused', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-scope-predecessor-test-'));
    try {
        const archive = join(root, osc633Scope.predecessor.file);
        copyFileSync(new URL('../../docs/qa/terminfo/2026-11-16-sgr-pixel-mouse/owned-run.zip', import.meta.url), archive);
        assert.doesNotThrow(() => verifyScopePredecessor(root, completeOwnedReceipt));
        writeFileSync(archive, 'changed');
        assert.throws(() => verifyScopePredecessor(root, completeOwnedReceipt), /archive changed/);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('the animation-only scopes are exact, distinct, and only the compose suite runs in both kinds of tab', async () => {
    const { directComposeScope, persistentAnimationScope, persistentComposeScope, persistentRestoreScope, isAnimationOnlyScope, isDirectComposeScope, isPersistentAnimationScope } = await import('./collection-scope.mjs');
    assert.equal(directComposeScope.persistence, 'Off'); assert.equal(persistentComposeScope.persistence, 'KeepOnClose');
    assert.equal(directComposeScope.suiteVersion, persistentComposeScope.suiteVersion);
    assert.notEqual(directComposeScope.id, persistentComposeScope.id);
    for (const scope of [directComposeScope, persistentComposeScope, persistentRestoreScope, persistentAnimationScope]) assert.equal(isAnimationOnlyScope(scope), true, scope.id);
    // The ordinary-tab collection is animation-only but not a persistent one: the persistent predicate must not claim it, nor may a lookalike pass.
    assert.equal(isDirectComposeScope(directComposeScope), true); assert.equal(isPersistentAnimationScope(directComposeScope), false);
    assert.equal(isDirectComposeScope(persistentComposeScope), false);
    assert.equal(isAnimationOnlyScope({ ...directComposeScope, persistence: 'KeepOnClose' }), false);
    assert.equal(isAnimationOnlyScope({ ...directComposeScope, deadlineMs: directComposeScope.deadlineMs + 1 }), false);
    assert.equal(isAnimationOnlyScope(undefined), false);
});

test('the placeholder grid scopes are exact, distinct from the earlier placeholder ones, and one runs in each kind of tab', async () => {
    const { directPlaceholderGridScope, persistentPlaceholderGridScope, directPlaceholdersScope, persistentPlaceholdersScope, isAnimationOnlyScope, isDirectPlaceholderGridScope, isPersistentPlaceholderGridScope, isPersistentAnimationScope, isDirectPlaceholdersScope } = await import('./collection-scope.mjs');
    assert.equal(directPlaceholderGridScope.persistence, 'Off'); assert.equal(persistentPlaceholderGridScope.persistence, 'KeepOnClose');
    assert.equal(directPlaceholderGridScope.suiteVersion, 6); assert.equal(persistentPlaceholderGridScope.suiteVersion, 6);
    assert.equal(new Set([directPlaceholderGridScope.id, persistentPlaceholderGridScope.id, directPlaceholdersScope.id, persistentPlaceholdersScope.id]).size, 4);
    for (const scope of [directPlaceholderGridScope, persistentPlaceholderGridScope]) assert.equal(isAnimationOnlyScope(scope), true, scope.id);
    assert.equal(isPersistentAnimationScope(persistentPlaceholderGridScope), true); assert.equal(isPersistentAnimationScope(directPlaceholderGridScope), false);
    assert.equal(isDirectPlaceholderGridScope(directPlaceholderGridScope), true); assert.equal(isDirectPlaceholderGridScope(directPlaceholdersScope), false); assert.equal(isDirectPlaceholdersScope(directPlaceholderGridScope), false);
    assert.equal(isPersistentPlaceholderGridScope(persistentPlaceholderGridScope), true); assert.equal(isPersistentPlaceholderGridScope(persistentPlaceholdersScope), false);
    assert.equal(isAnimationOnlyScope({ ...directPlaceholderGridScope, suiteVersion: 5 }), false);
    assert.equal(isAnimationOnlyScope({ ...directPlaceholderGridScope, persistence: 'KeepOnClose' }), false);
});

test('the placeholder scopes are exact, distinct, and one runs in each kind of tab', async () => {
    const scopes = await import('./collection-scope.mjs');
    const { directPlaceholdersScope, persistentPlaceholdersScope, directComposeScope, persistentComposeScope, isAnimationOnlyScope, isDirectPlaceholdersScope, isPersistentPlaceholdersScope, isPersistentAnimationScope } = scopes;
    assert.equal(directPlaceholdersScope.persistence, 'Off'); assert.equal(persistentPlaceholdersScope.persistence, 'KeepOnClose');
    assert.equal(directPlaceholdersScope.suiteVersion, 5); assert.equal(persistentPlaceholdersScope.suiteVersion, 5);
    assert.deepEqual(directPlaceholdersScope.suites, persistentPlaceholdersScope.suites); assert.notEqual(directPlaceholdersScope.id, persistentPlaceholdersScope.id);
    assert.equal(new Set([directPlaceholdersScope.id, persistentPlaceholdersScope.id, directComposeScope.id, persistentComposeScope.id]).size, 4);
    for (const scope of [directPlaceholdersScope, persistentPlaceholdersScope]) assert.equal(isAnimationOnlyScope(scope), true, scope.id);
    assert.equal(isPersistentAnimationScope(persistentPlaceholdersScope), true); assert.equal(isPersistentAnimationScope(directPlaceholdersScope), false);
    assert.equal(isDirectPlaceholdersScope(directPlaceholdersScope), true); assert.equal(isDirectPlaceholdersScope(persistentPlaceholdersScope), false); assert.equal(isDirectPlaceholdersScope(directComposeScope), false);
    assert.equal(isPersistentPlaceholdersScope(persistentPlaceholdersScope), true); assert.equal(isPersistentPlaceholdersScope(persistentComposeScope), false);
    assert.equal(isAnimationOnlyScope({ ...directPlaceholdersScope, persistence: 'KeepOnClose' }), false);
    assert.equal(isAnimationOnlyScope({ ...persistentPlaceholdersScope, suiteVersion: 4 }), false);
    assert.equal(isAnimationOnlyScope({ ...directPlaceholdersScope, deadlineMs: directPlaceholdersScope.deadlineMs + 1 }), false);
});
