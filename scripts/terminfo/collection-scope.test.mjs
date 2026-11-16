import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { historicOsc633Scope, isLegacyScope, isOsc633Scope, legacyScope, osc633Scope, runPredecessorExtractor, validateCollectionScope, verifyScopePredecessor } from './collection-scope.mjs';
import { completeOwnedReceipt } from './receipt.mjs';

test('only the fixed scope is admitted by matching context, parent proof, policy and absence of excluded evidence', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-scope-'));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v62', osc633SuiteId: 'ntilde-owned-osc633-graded-v1', collectionScope: structuredClone(osc633Scope) };
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

test('the default selection preserves all prior suites and explicitly excludes only OSC 633', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-legacy-scope-'));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v62', collectionScope: structuredClone(legacyScope) };
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
