// A narrowly selected owned collection. It is not a full-run receipt: the
// omitted, time-consuming notification suites remain in their own historical
// receipts and are independently rederived by the publication.
import { isOsc5Policy63, isOsc5AnimationPolicy64, isCellSizeAnimationPolicy64 } from './owned-sources.mjs';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { lstatSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { isDeepStrictEqual } from 'node:util';

export const historicOsc633Scope = Object.freeze({
    id: 'owned-osc633-without-notification-suites-v1',
    omittedSuites: Object.freeze(['osc9', 'notify']),
    deadlineMs: 300000,
    predecessor: Object.freeze({ file: 'scope-predecessor.zip', sha256: '5531e5b1f110db1dc4b4db0d99d1a1b1f9e66700b916e49850cf19f403fe7833',
        runId: '55f0ba462cd3cc6fbc315a0e5fad5126', sourcePolicy: 'readonly-owned-source-graph-v60' }),
});

export const integratedOsc633Scope = Object.freeze({
    id: 'owned-osc633-without-notification-suites-v2', omittedSuites: Object.freeze(['osc9', 'notify']), deadlineMs: 300000,
    predecessor: Object.freeze({ file: 'scope-predecessor.zip', sha256: 'a3ef48571fc95de3e7c54f8c3d4d924d85b4ead8c7f8f6b959b11414a9d8e820',
        runId: 'b45702ad11ea1f0ea46eeb4908206155', sourcePolicy: 'readonly-owned-source-graph-v61' }),
});
export const historicLegacyScope = Object.freeze({ id: 'owned-pre-osc633-full-selection-v1', omittedSuites: Object.freeze(['osc633']), deadlineMs: 300000 });
export const integratedLegacyScope = Object.freeze({ id: 'owned-pre-osc633-full-selection-v2', omittedSuites: Object.freeze(['osc633']), deadlineMs: 300000 });

export const osc633ScopeV3 = Object.freeze({ ...integratedOsc633Scope, id: 'owned-osc633-without-notification-suites-v3' });
export const legacyScopeV3 = Object.freeze({ ...integratedLegacyScope, id: 'owned-pre-osc633-full-selection-v3' });

// Both names designate the same frozen v2 objects used by the two branches.
export const osc633ScopeV2 = integratedOsc633Scope;
export const legacyScopeV2 = integratedLegacyScope;
export const osc633ScopeV4 = Object.freeze({ ...osc633ScopeV3, id: 'owned-osc633-without-notification-suites-v4' });
export const legacyScopeV4 = Object.freeze({ ...legacyScopeV3, id: 'owned-pre-osc633-full-selection-v4' });

export const osc5Osc633ScopeV4 = Object.freeze({ ...osc633ScopeV4, deadlineMs: 600000 });
export const osc5LegacyScopeV4 = Object.freeze({ ...legacyScopeV4, deadlineMs: 600000 });
export const osc633Scope = Object.freeze({ ...osc633ScopeV4, id: 'owned-osc633-without-notification-suites-v5', deadlineMs: 600000 });
export const legacyScope = Object.freeze({ ...legacyScopeV4, id: 'owned-pre-osc633-full-selection-v5', deadlineMs: 600000 });
/**
 * Not a full-run selection: the Kitty animation suite alone, collected in a persistent (mux-backed, KeepOnClose) tab whose worker is the
 * multiplexer daemon's child. It has its own receipt (persistent-animation-receipt.json), so no full-receipt scope rule applies to it.
 */
export const persistentAnimationScope = Object.freeze({ id: 'owned-kitty-animation-persistent-v1', persistence: 'KeepOnClose', suites: Object.freeze(['kitty-animation']), deadlineMs: 300000 });
/**
 * The restore collection (#1030): the same persistent tab, but the runner terminates the App with SIGTERM (not a graceful quit) and starts it again on the same persistent session between stages, and the animation suite
 * read is the version 3 restore suite. It has its own receipt, like the live-tab collection.
 */
export const persistentRestoreScope = Object.freeze({ id: 'owned-kitty-animation-persistent-restore-v1', persistence: 'KeepOnClose', suites: Object.freeze(['kitty-animation-restore']), suiteVersion: 3, deadlineMs: 600000 });
export const isPersistentRestoreScope = value => JSON.stringify(value) === JSON.stringify(persistentRestoreScope);
/**
 * The frame-composition collection (#1027): the same persistent tab, no relaunch, and the animation suite read is the version 4 compose suite (`a=c`, persistent image policy 4).
 * It has its own receipt, like the other persistent collections.
 */
export const persistentComposeScope = Object.freeze({ id: 'owned-kitty-animation-persistent-compose-v1', persistence: 'KeepOnClose', suites: Object.freeze(['kitty-animation-compose']), suiteVersion: 4, deadlineMs: 300000 });
export const isPersistentComposeScope = value => JSON.stringify(value) === JSON.stringify(persistentComposeScope);
/**
 * The same composition suite in an ordinary (non-persistent) tab (#1027): no multiplexer daemon, the worker is the App's direct child as in a full collection, and `a=c` runs on the
 * App's own parser, where the root's pixels are read back through the host decoder. It has its own receipt, like the other animation-only collections.
 */
export const directComposeScope = Object.freeze({ id: 'owned-kitty-animation-direct-compose-v1', persistence: 'Off', suites: Object.freeze(['kitty-animation-compose']), suiteVersion: 4, deadlineMs: 300000 });
export const isDirectComposeScope = value => JSON.stringify(value) === JSON.stringify(directComposeScope);
/**
 * The Unicode placeholder collections (#989): the version 5 placeholder suite alone, in a persistent tab (image policy 5, a multiplexer daemon hosting the worker) and in an ordinary tab (no daemon, the worker the App's
 * own child). Each has its own receipt, like the other animation-only collections.
 */
export const persistentPlaceholdersScope = Object.freeze({ id: 'owned-kitty-placeholders-persistent-v1', persistence: 'KeepOnClose', suites: Object.freeze(['kitty-placeholders']), suiteVersion: 5, deadlineMs: 300000 });
export const directPlaceholdersScope = Object.freeze({ id: 'owned-kitty-placeholders-direct-v1', persistence: 'Off', suites: Object.freeze(['kitty-placeholders']), suiteVersion: 5, deadlineMs: 300000 });
/** The placeholder grid collections (#989): suite version 6, a 4 x 2 grid of cells, in a persistent tab and in an ordinary one, each with its own receipt. */
export const persistentPlaceholderGridScope = Object.freeze({ id: 'owned-kitty-placeholder-grid-persistent-v1', persistence: 'KeepOnClose', suites: Object.freeze(['kitty-placeholder-grid']), suiteVersion: 6, deadlineMs: 300000 });
export const directPlaceholderGridScope = Object.freeze({ id: 'owned-kitty-placeholder-grid-direct-v1', persistence: 'Off', suites: Object.freeze(['kitty-placeholder-grid']), suiteVersion: 6, deadlineMs: 300000 });
export const isPersistentPlaceholderGridScope = value => JSON.stringify(value) === JSON.stringify(persistentPlaceholderGridScope);
export const isDirectPlaceholderGridScope = value => JSON.stringify(value) === JSON.stringify(directPlaceholderGridScope);
export const isPersistentPlaceholdersScope = value => JSON.stringify(value) === JSON.stringify(persistentPlaceholdersScope);
export const isDirectPlaceholdersScope = value => JSON.stringify(value) === JSON.stringify(directPlaceholdersScope);
export const isPersistentAnimationScope = value => JSON.stringify(value) === JSON.stringify(persistentAnimationScope) || isPersistentRestoreScope(value) || isPersistentComposeScope(value) || isPersistentPlaceholdersScope(value) || isPersistentPlaceholderGridScope(value);

const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
/** Every animation-only collection: the persistent ones and the ordinary-tab composition and placeholder ones. */
export const isAnimationOnlyScope = value => isPersistentAnimationScope(value) || isDirectComposeScope(value) || isDirectPlaceholdersScope(value) || isDirectPlaceholderGridScope(value);
export const isOsc633Scope = value => same(value, osc633Scope) || same(value, osc633ScopeV4) || same(value, osc5Osc633ScopeV4) || same(value, osc633ScopeV3) || same(value, integratedOsc633Scope) || same(value, historicOsc633Scope);
export const isLegacyScope = value => same(value, legacyScope) || same(value, legacyScopeV4) || same(value, osc5LegacyScopeV4) || same(value, legacyScopeV3) || same(value, integratedLegacyScope) || same(value, historicLegacyScope);
export const isHistoricOsc633Scope = value => same(value, historicOsc633Scope) || same(value, historicLegacyScope);
export const isOsc5ForkScope = context => isOsc5Policy63(context) && same(context.collectionScope, legacyScopeV2);
export const isOsc5V64Scope = context => isOsc5AnimationPolicy64(context)
    && (same(context.collectionScope, osc5LegacyScopeV4) || same(context.collectionScope, osc5Osc633ScopeV4));
export const isCellSizeV64Scope = context => isCellSizeAnimationPolicy64(context)
    && (same(context.collectionScope, legacyScopeV4) || same(context.collectionScope, osc633ScopeV4));
const scopePolicyOk = context => {
    const scope = context.collectionScope, policy = context.sourceSnapshotPolicy;
    if (isHistoricOsc633Scope(scope)) return policy === 'readonly-owned-source-graph-v61';
    if (same(scope, integratedOsc633Scope) || same(scope, integratedLegacyScope)) {
        const laterFamily = Object.keys(context).some(key => key.startsWith('osc5') || key.startsWith('cellSize') || key.startsWith('kittyAnimation'));
        return (policy === 'readonly-owned-source-graph-v62' && !laterFamily) || isOsc5ForkScope(context);
    }
    if (same(scope, osc633ScopeV3) || same(scope, legacyScopeV3)) return policy === 'readonly-owned-source-graph-v63';
    if (same(scope, osc633ScopeV4) || same(scope, legacyScopeV4)) return isCellSizeV64Scope(context);
    if (same(scope, osc5Osc633ScopeV4) || same(scope, osc5LegacyScopeV4)) return isOsc5V64Scope(context);
    return ['readonly-owned-source-graph-v65', 'readonly-owned-source-graph-v66', 'readonly-owned-source-graph-v67', 'readonly-owned-source-graph-v68', 'readonly-owned-source-graph-v69', 'readonly-owned-source-graph-v70', 'readonly-owned-source-graph-v71', 'readonly-owned-source-graph-v72', 'readonly-owned-source-graph-v73', 'readonly-owned-source-graph-v74', 'readonly-owned-source-graph-v79'].includes(policy);
};
const omittedArtifacts = Object.freeze([
    'osn-observations.json', 'osn-run.json', 'osn-snapshots.jsonl', 'osn-unavailable.json', 'osn-focus.json', 'osn-failed-observation.json',
    'ntf-observations.json', 'ntf-run.json', 'ntf-snapshots.jsonl', 'ntf-unavailable.json', 'ntf-focus.json', 'ntf-failed-observation.json',
]);

/** A scope is admitted only when the parent proof, source policy and context
 * all agree on the fixed selection. Partial omitted-suite evidence is fatal. */
export function validateCollectionScope(output, evidence, context, proof) {
    if (context.collectionScope === undefined && proof.collectionScope === undefined) return false;
    if (!same(context.collectionScope, proof.collectionScope)) throw new Error('Invalid or incomplete scoped collection declaration.');
    if (isHistoricOsc633Scope(context.collectionScope) && (evidence.pxmEvidence !== undefined
        || ['pxmAdapterId', 'pxmSuiteId', 'pxmBundleSha256', 'pxmContractsVersion'].some(key => context[key] !== undefined)
        || ['collector-pxm.mjs', 'pxm-run.json', 'pxm-observations.json', 'pxm-unavailable.json'].some(file => lstatSync(join(output, file), { throwIfNoEntry: false }) !== undefined))) {
        throw new Error('Invalid or incomplete scoped collection declaration.');
    }
    if (isOsc5ForkScope(context) && (evidence.animationEvidence !== undefined
        || Object.keys(context).some(key => key.startsWith('kittyAnimation') || key === 'animationSuiteId' || key === 'ownedAnimationPolicy')
        || ['animation-snapshots.jsonl', 'animation-actions.jsonl', 'animation-observations.json', 'animation-unavailable.json'].some(file => lstatSync(join(output, file), { throwIfNoEntry: false }) !== undefined))) {
        throw new Error('Invalid or incomplete scoped collection declaration.');
    }
    if (isLegacyScope(context.collectionScope) && isLegacyScope(proof.collectionScope)) {
        const markers = ['osc633AdapterId', 'osc633SuiteId', 'osc633BundleSha256', 'osc633ContractsVersion'];
        const files = ['collector-vs633.mjs', 'vs633-snapshots.jsonl', 'vs633-run.json', 'vs633-observations.json', 'vs633-unavailable.json', 'vs633-failed-observation.json'];
        if (!scopePolicyOk(context) || evidence.osc633Evidence !== undefined
            || markers.some(key => context[key] !== undefined) || files.some(file => lstatSync(join(output, file), { throwIfNoEntry: false }) !== undefined)) {
            throw new Error('Invalid or incomplete scoped collection declaration.');
        }
        return true;
    }
    if (!isOsc633Scope(context.collectionScope) || !isOsc633Scope(proof.collectionScope)
        || !scopePolicyOk(context)
        || context.osc633SuiteId !== 'ntilde-owned-osc633-graded-v1' || evidence.osc633Evidence === undefined
        || evidence.osc9Evidence !== undefined || evidence.notifyEvidence !== undefined
        || omittedArtifacts.some(file => lstatSync(join(output, file), { throwIfNoEntry: false }) !== undefined)) {
        throw new Error('Invalid or incomplete scoped collection declaration.');
    }
    return true;
}

// Terminate a stalled extractor without leaving the desktop lease held indefinitely.
export function runPredecessorExtractor(spawn, python, script, archive, root) {
    const result = spawn(python, ['-c', script, archive, root],
        { encoding: 'utf8', windowsHide: true, timeout: 30000, killSignal: 'SIGKILL' });
    if (result.error || result.status !== 0) throw new Error('Scoped predecessor archive extraction failed.');
}

/** Reissue the pinned predecessor's full receipt with the trusted current
 * validators. The predecessor remains a different run, never a same-run claim. */
export function verifyScopePredecessor(output, finalizeReceipt, scope = osc633Scope) {
    if (!isOsc633Scope(scope)) throw new Error('Unknown scoped predecessor.');
    const archive = join(output, scope.predecessor.file);
    if (!lstatSync(archive).isFile() || createHash('sha256').update(readFileSync(archive)).digest('hex') !== scope.predecessor.sha256) {
        throw new Error('Scoped predecessor archive changed.');
    }
    const root = mkdtempSync(join(tmpdir(), 'ntilde-scope-predecessor-'));
    try {
        const python = process.env.PYTHON ?? (process.platform === 'win32' ? 'python' : 'python3');
        const script = String.raw`import zipfile,pathlib,sys,stat
with zipfile.ZipFile(sys.argv[1]) as z:
 names=z.namelist()
 assert len(names)==len(set(names)) and len(names)<2000
 assert sum(i.file_size for i in z.infolist())<150*1024*1024
 for i in z.infolist():
  p=pathlib.PurePosixPath(i.filename)
  assert not p.is_absolute() and '..' not in p.parts and '\\' not in i.filename and not stat.S_ISLNK(i.external_attr>>16)
  dest=pathlib.Path(sys.argv[2]).joinpath(*p.parts)
  dest.parent.mkdir(parents=True,exist_ok=True)
  dest.write_bytes(z.read(i))`;
        runPredecessorExtractor(spawnSync, python, script, archive, root);
        const path = join(root, 'cell-receipt.json'), original = JSON.parse(readFileSync(path));
        const context = JSON.parse(readFileSync(join(root, 'context.json')));
        if (original.schemaVersion !== (isHistoricOsc633Scope(scope) ? 66 : 67) || original.runId !== scope.predecessor.runId
            || context.runId !== original.runId || context.sourceSnapshotPolicy !== scope.predecessor.sourcePolicy
            || original.osc9Evidence?.suiteId !== 'ntilde-owned-osc9-graded-v1' || original.notifyEvidence?.suiteId !== 'ntilde-owned-notify-graded-v1'
            || original.osc9Evidence.contractsVersion !== 1 || original.notifyEvidence.contractsVersion !== 1) {
            throw new Error('Scoped predecessor identity changed.');
        }
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(path);
        const rederived = finalizeReceipt(root, join(root, 'removed-fixture'), evidence);
        if (!isDeepStrictEqual({ ...rederived, completedAt }, original)) throw new Error('Scoped predecessor receipt does not rederive.');
    } finally { rmSync(root, { recursive: true, force: true }); }
}
