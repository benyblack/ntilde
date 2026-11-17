import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { capturedCellSizeForkV70Files, capturedV70SourceFiles, capturedV71SourceFiles, ownedSourceFiles,
    isCellSizePolicy63, isKittyAnimationPolicy63 } from './owned-sources.mjs';
import { retainedInventory } from './metadata-receipt.mjs';
import { includesCellSizeEvidence } from './cell-size-receipt.mjs';
import { includesAnimationEvidence } from './kitty-animation-receipt.mjs';
import { historicOsc633Scope, osc633ScopeV2, osc633ScopeV3, osc633Scope, historicLegacyScope, legacyScopeV2,
    legacyScopeV3, legacyScope, validateCollectionScope } from './collection-scope.mjs';

const policy = version => 'readonly-owned-source-graph-v' + version;
const graph = { cellSizeContractsSourceSha256: 'a'.repeat(64), kittyAnimationInputsSourceSha256: 'b'.repeat(64) };
const cell = () => ({ sourceSnapshotPolicy: policy(63), cellSizeContractsSourceSha256: graph.cellSizeContractsSourceSha256 });
const kitty = () => ({ sourceSnapshotPolicy: policy(63), kittyAnimationInputsSourceSha256: graph.kittyAnimationInputsSourceSha256 });

test('both historical v63 graphs remain distinct from the complete432-input integrated graph', () => {
    assert.equal(Object.keys(capturedV70SourceFiles).length, 420);
    assert.equal(Object.keys(capturedCellSizeForkV70Files).length, 425);
    assert.equal(Object.keys(capturedV71SourceFiles).length, 432);
    assert.deepEqual(ownedSourceFiles, capturedV71SourceFiles);
    assert.equal(retainedInventory('', cell()), capturedCellSizeForkV70Files);
    assert.equal(retainedInventory('', kitty()), capturedV70SourceFiles);
    assert.equal(retainedInventory('', { sourceSnapshotPolicy: policy(64) }), capturedV71SourceFiles);
});

for (const [name, mutate] of Object.entries({
    missing: context => delete context.cellSizeContractsSourceSha256,
    null: context => context.cellSizeContractsSourceSha256 = null,
    short: context => context.cellSizeContractsSourceSha256 = 'a',
    both: context => context.kittyAnimationInputsSourceSha256 = graph.kittyAnimationInputsSourceSha256,
    'opposite null': context => context.kittyAnimationInputsSourceSha256 = null,
    'opposite empty': context => context.kittyAnimationInputsSourceSha256 = '',
})) test('historical fork discrimination refuses ' + name, () => {
    const context = cell(); mutate(context);
    assert.equal(isCellSizePolicy63(context), false);
    assert.equal(isKittyAnimationPolicy63(context), false);
    assert.throws(() => retainedInventory('', context), /ambiguous historical/);
});

test('only a valid opposite historical graph exempts its absent family; current64 requires both after stripping', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-source-forks-'));
    try {
        const flags = context => [includesCellSizeEvidence(root, {}, context), includesAnimationEvidence(root, {}, context, {})];
        assert.deepEqual(flags(cell()), [true, false]);
        assert.deepEqual(flags(kitty()), [false, true]);
        assert.deepEqual(flags({ sourceSnapshotPolicy: policy(63) }), [true, true]);
        assert.deepEqual(flags({ sourceSnapshotPolicy: policy(64) }), [true, true]);
        assert.deepEqual(flags({ ...cell(), sourceSnapshotPolicy: policy(64) }), [true, true]);
        assert.deepEqual(flags({ ...kitty(), sourceSnapshotPolicy: policy(64) }), [true, true]);
        assert.deepEqual(flags({ ...kitty(), cellSizeFrameSourceSha256: 'c'.repeat(64) }), [true, true]);
        assert.deepEqual(flags({ ...kitty(), cellSizeParserReferenceSha256: 'c'.repeat(64) }), [true, true]);
        assert.deepEqual(flags({ ...cell(), kittyAnimationCasesSourceSha256: 'c'.repeat(64) }), [true, true]);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('every frozen selection stays bound to its own source policy, including identical v3 fork selections', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-scope-forks-'));
    try {
        const selected = [[historicOsc633Scope, 61], [osc633ScopeV2, 62], [osc633ScopeV3, 63], [osc633Scope, 64],
            [historicLegacyScope, 61], [legacyScopeV2, 62], [legacyScopeV3, 63], [legacyScope, 64]];
        for (const [scope, version] of selected) for (const candidate of [61, 62, 63, 64]) {
            const legacy = scope.omittedSuites.includes('osc633');
            const context = { sourceSnapshotPolicy: policy(candidate), collectionScope: scope,
                ...(legacy ? {} : { osc633SuiteId: 'ntilde-owned-osc633-graded-v1' }) };
            const evidence = legacy ? {} : { osc633Evidence: {} };
            if (version === candidate) assert.equal(validateCollectionScope(root, evidence, context, { collectionScope: scope }), true);
            else assert.throws(() => validateCollectionScope(root, evidence, context, { collectionScope: scope }), /scoped collection/);
        }
    } finally { rmSync(root, { recursive: true, force: true }); }
});
