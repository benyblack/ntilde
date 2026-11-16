// A narrowly selected owned collection. It is not a full-run receipt: the
// omitted, time-consuming notification suites remain in their own historical
// receipts and are independently rederived by the publication.
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

export const osc633Scope = Object.freeze({
    id: 'owned-osc633-without-notification-suites-v2', omittedSuites: Object.freeze(['osc9', 'notify']), deadlineMs: 300000,
    predecessor: Object.freeze({ file: 'scope-predecessor.zip', sha256: 'a3ef48571fc95de3e7c54f8c3d4d924d85b4ead8c7f8f6b959b11414a9d8e820',
        runId: 'b45702ad11ea1f0ea46eeb4908206155', sourcePolicy: 'readonly-owned-source-graph-v61' }),
});
export const historicLegacyScope = Object.freeze({ id: 'owned-pre-osc633-full-selection-v1', omittedSuites: Object.freeze(['osc633']), deadlineMs: 300000 });
export const legacyScope = Object.freeze({ id: 'owned-pre-osc633-full-selection-v2', omittedSuites: Object.freeze(['osc633']), deadlineMs: 300000 });

const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
export const isOsc633Scope = value => same(value, osc633Scope) || same(value, historicOsc633Scope);
export const isLegacyScope = value => same(value, legacyScope) || same(value, historicLegacyScope);
export const isHistoricOsc633Scope = value => same(value, historicOsc633Scope) || same(value, historicLegacyScope);
const policyForScope = scope => isHistoricOsc633Scope(scope) ? 'readonly-owned-source-graph-v61' : 'readonly-owned-source-graph-v62';
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
    if (isLegacyScope(context.collectionScope) && isLegacyScope(proof.collectionScope)) {
        const markers = ['osc633AdapterId', 'osc633SuiteId', 'osc633BundleSha256', 'osc633ContractsVersion'];
        const files = ['collector-vs633.mjs', 'vs633-snapshots.jsonl', 'vs633-run.json', 'vs633-observations.json', 'vs633-unavailable.json', 'vs633-failed-observation.json'];
        if (context.sourceSnapshotPolicy !== policyForScope(context.collectionScope) || evidence.osc633Evidence !== undefined
            || markers.some(key => context[key] !== undefined) || files.some(file => lstatSync(join(output, file), { throwIfNoEntry: false }) !== undefined)) {
            throw new Error('Invalid or incomplete scoped collection declaration.');
        }
        return true;
    }
    if (!isOsc633Scope(context.collectionScope) || !isOsc633Scope(proof.collectionScope)
        || context.sourceSnapshotPolicy !== policyForScope(context.collectionScope)
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
