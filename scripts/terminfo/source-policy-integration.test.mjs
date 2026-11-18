import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { capturedCellSizeForkV70Files, capturedV70SourceFiles, capturedV71SourceFiles, capturedV72SourceFiles, capturedV73SourceFiles, capturedV74SourceFiles, capturedV75SourceFiles, capturedV76SourceFiles,
    capturedOsc5ForkV63Files, capturedOsc5ForkV64Files, ownedSourceFiles,
    isCellSizePolicy63, isKittyAnimationPolicy63, isOsc5Policy63 } from './owned-sources.mjs';
import { retainedInventory } from './metadata-receipt.mjs';
import { includesCellSizeEvidence } from './cell-size-receipt.mjs';
import { includesAnimationEvidence } from './kitty-animation-receipt.mjs';
import { includesOsc5Evidence } from './osc5-receipt.mjs';
import { historicOsc633Scope, osc633ScopeV2, osc633ScopeV3, osc633ScopeV4, osc633Scope, historicLegacyScope, legacyScopeV2,
    legacyScopeV3, legacyScopeV4, legacyScope, osc5LegacyScopeV4, osc5Osc633ScopeV4, validateCollectionScope } from './collection-scope.mjs';

const policy = version => 'readonly-owned-source-graph-v' + version;
const fields = { cellSizeContractsSourceSha256: 'a'.repeat(64), kittyAnimationInputsSourceSha256: 'b'.repeat(64), osc5ContractsSourceSha256: 'c'.repeat(64) };
const cell = () => ({ sourceSnapshotPolicy: policy(63), cellSizeContractsSourceSha256: fields.cellSizeContractsSourceSha256 });
const kitty = () => ({ sourceSnapshotPolicy: policy(63), kittyAnimationInputsSourceSha256: fields.kittyAnimationInputsSourceSha256 });
const osc5 = () => ({ sourceSnapshotPolicy: policy(63), osc5ContractsSourceSha256: fields.osc5ContractsSourceSha256, collectionScope: legacyScopeV2 });
const cell64 = () => ({ ...cell(), ...kitty(), sourceSnapshotPolicy: policy(64), collectionScope: legacyScopeV4 });
const osc564 = () => ({ ...osc5(), ...kitty(), sourceSnapshotPolicy: policy(64), collectionScope: osc5LegacyScopeV4 });

test('frozen graphs remain distinct from the live455-input v68 graph', () => {
    for (const [graph, count] of [[capturedV70SourceFiles, 420], [capturedCellSizeForkV70Files, 425], [capturedOsc5ForkV63Files, 420],
        [capturedV71SourceFiles, 432], [capturedOsc5ForkV64Files, 427], [capturedV72SourceFiles, 439], [capturedV73SourceFiles, 444], [capturedV74SourceFiles, 450]]) assert.equal(Object.keys(graph).length, count);
    assert.deepEqual(ownedSourceFiles, capturedV76SourceFiles);
    for (const [context, graph] of [[cell(), capturedCellSizeForkV70Files], [kitty(), capturedV70SourceFiles], [osc5(), capturedOsc5ForkV63Files],
        [cell64(), capturedV71SourceFiles], [osc564(), capturedOsc5ForkV64Files], [{ sourceSnapshotPolicy: policy(65) }, capturedV72SourceFiles], [{ sourceSnapshotPolicy: policy(66) }, capturedV73SourceFiles], [{ sourceSnapshotPolicy: policy(67) }, capturedV74SourceFiles], [{ sourceSnapshotPolicy: policy(68) }, capturedV75SourceFiles], [{ sourceSnapshotPolicy: policy(69) }, capturedV76SourceFiles]]) {
        assert.equal(retainedInventory('', context), graph);
    }
});

for (const make of [cell, kitty, osc5]) {
    const own = Object.keys(make()).find(key => key.endsWith('Sha256'));
    for (const [label, mutate] of Object.entries({
        missing: context => delete context[own],
        null: context => context[own] = null,
        short: context => context[own] = 'a',
        opposite: context => Object.assign(context, fields),
        'opposite null': context => context[own.startsWith('cellSize') ? 'osc5ContractsSourceSha256' : 'cellSizeContractsSourceSha256'] = null,
        'other opposite marker': context => context[own.startsWith('kitty') ? 'osc5BundleSha256' : 'kittyAnimationCasesSourceSha256'] = '',
    })) test(`historical v63 ${own} fork refuses ${label} markers`, () => {
        const context = make(); mutate(context);
        assert.equal(isCellSizePolicy63(context), false);
        assert.equal(isKittyAnimationPolicy63(context), false);
        assert.equal(isOsc5Policy63(context), false);
        assert.throws(() => retainedInventory('', context), /ambiguous historical/);
    });
}

test('v64 fork selection requires both valid families and the exact historical deadline', () => {
    for (const make of [cell64, osc564]) {
        const context = make();
        assert.throws(() => retainedInventory('', { ...context, collectionScope: { ...context.collectionScope, deadlineMs: 123 } }), /ambiguous historical/);
        assert.throws(() => retainedInventory('', { ...context, kittyAnimationInputsSourceSha256: null }), /ambiguous historical/);
        assert.throws(() => retainedInventory('', { ...context, ...fields }), /ambiguous historical/);
    }
    assert.throws(() => retainedInventory('', { ...cell64(), collectionScope: osc5LegacyScopeV4 }), /ambiguous historical/);
    assert.throws(() => retainedInventory('', { ...osc564(), collectionScope: legacyScopeV4 }), /ambiguous historical/);
    assert.throws(() => retainedInventory('', { sourceSnapshotPolicy: policy(64) }), /ambiguous historical/);
});

test('historical family exemptions never hide declarations or artifacts; live65 requires all three after stripping', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-source-forks-'));
    const flags = context => [includesCellSizeEvidence(root, {}, context), includesAnimationEvidence(root, {}, context, {}), includesOsc5Evidence(root, {}, context)];
    try {
        assert.deepEqual(flags(cell()), [true, false, false]);
        assert.deepEqual(flags(kitty()), [false, true, false]);
        assert.deepEqual(flags(osc5()), [false, false, true]);
        assert.deepEqual(flags(cell64()), [true, true, false]);
        assert.deepEqual(flags(osc564()), [false, true, true]);
        for (const version of [63, 64, 65]) assert.deepEqual(flags({ sourceSnapshotPolicy: policy(version) }), [true, true, true]);
        for (const make of [cell64, osc564]) assert.deepEqual(flags({ ...make(), sourceSnapshotPolicy: policy(65) }), [true, true, true]);
        for (const [make, file, position] of [[osc564, 'cell-size-run.json', 0], [osc5, 'animation-snapshots.jsonl', 1], [cell64, 'osc5-run.json', 2]]) {
            writeFileSync(join(root, file), '');
            assert.equal(flags(make())[position], true);
            rmSync(join(root, file));
        }
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('every exact frozen selection stays bound to its policy while live V5 uses65', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-scope-forks-'));
    const selected = [[historicOsc633Scope, 61, {}], [osc633ScopeV2, 62, {}], [osc633ScopeV3, 63, cell()], [osc633ScopeV4, 64, cell64()], [osc633Scope, 65, {}],
        [historicLegacyScope, 61, {}], [legacyScopeV2, 62, {}], [legacyScopeV3, 63, kitty()], [legacyScopeV4, 64, cell64()], [legacyScope, 65, {}],
        [legacyScopeV2, 63, osc5()], [osc5LegacyScopeV4, 64, osc564()], [osc5Osc633ScopeV4, 64, osc564()]];
    try {
        for (const [scope, version, fields] of selected) for (const candidate of [61, 62, 63, 64, 65]) {
            const legacy = scope.omittedSuites.includes('osc633');
            const context = { ...fields, sourceSnapshotPolicy: policy(candidate), collectionScope: scope,
                ...(legacy ? {} : { osc633SuiteId: 'ntilde-owned-osc633-graded-v1' }) };
            const evidence = legacy ? {} : { osc633Evidence: {} };
            if (version === candidate) assert.equal(validateCollectionScope(root, evidence, context, { collectionScope: scope }), true);
            else assert.throws(() => validateCollectionScope(root, evidence, context, { collectionScope: scope }), /scoped collection/);
        }
    } finally { rmSync(root, { recursive: true, force: true }); }
});
