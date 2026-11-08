import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { capturedV8SourceFiles, legacyV8SourceFiles, capturedV8SourcePolicy, capturedV9SourceFiles, capturedV10SourceFiles, capturedV11SourceFiles, capturedV12SourceFiles, capturedV13SourceFiles, capturedV14SourceFiles, capturedV15SourceFiles, capturedV16SourceFiles, capturedV17SourceFiles, capturedV18SourceFiles, capturedV19SourceFiles, capturedV20SourceFiles, capturedV21SourceFiles, capturedV22SourceFiles, capturedV23SourceFiles, assertRetainedSourceClosure } from './owned-sources.mjs';
import { buildMetadataReport } from './metadata.mjs';
import { metadataSuiteId, ownedMetadataPolicy } from './metadata-cases.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const check = value => { if (!value) throw new Error('Invalid or incomplete v8 owned metadata-state receipt.'); };
export const metadataSources = {
    metadataCasesSourceSha256: 'metadata-cases.mjs', metadataCaptureSourceSha256: 'metadata-capture.mjs',
    metadataSuiteSourceSha256: 'metadata.mjs', metadataStateSourceSha256: 'metadata-state.mjs',
    metadataExpectationsSourceSha256: 'metadata-expectations.mjs', metadataReceiptSourceSha256: 'metadata-receipt.mjs',
    sourceRegistrySourceSha256: 'owned-sources.mjs',
};
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}
function bytes(output, file, maximum = 16 * 1024 * 1024) {
    const path = join(output, file), stat = lstatSync(path);
    check(stat.isFile() && !stat.isSymbolicLink() && stat.size <= maximum);
    const value = readFileSync(path); check(value.length <= maximum); return value;
}
/** Any retained suite/source/journal or parent declaration makes the complete v8 chain mandatory. */
export function includesMetadataEvidence(output, evidence, context, proof) {
    return context.metadataSuiteId !== undefined || context.ownedMetadataPolicy !== undefined || evidence.metadataEvidence !== undefined
        || Object.keys(metadataSources).some(key => context[key] !== undefined)
        || proof.proofs?.some(value => value.metadata !== undefined) === true
        || context.sourceSnapshotPolicy !== undefined || context.sourceInventorySha256 !== undefined
        || proof.sourceSnapshot !== undefined || present(join(output, 'source-snapshot.json'))
        || present(join(output, 'sources', 'owned-source-inventory.json'))
        || present(join(output, 'sources', 'terminfo-owned-runner.mjs'))
        || present(join(output, 'metadata-failed-observation.json'))
        || present(join(output, 'metadata-snapshots.jsonl'))
        || present(join(output, 'metadata-observations.json')) || present(join(output, 'metadata-unavailable.json'))
        || Object.values(metadataSources).some(file => present(join(output, 'sources', file)));
}
function snapshotMode(output, context) {
    return context.sourceSnapshotPolicy !== undefined || context.sourceInventorySha256 !== undefined
        || present(join(output, 'source-snapshot.json')) || present(join(output, 'sources', 'owned-source-inventory.json'))
        || present(join(output, 'sources', 'terminfo-owned-runner.mjs'));
}
function checkSnapshot(output, context, proof) {
    const policy = context.sourceSnapshotPolicy, inventory = retainedInventory(output, context);
    check([capturedV8SourcePolicy, 'readonly-owned-source-graph-v2', 'readonly-owned-source-graph-v3', 'readonly-owned-source-graph-v4', 'readonly-owned-source-graph-v5', 'readonly-owned-source-graph-v6', 'readonly-owned-source-graph-v7', 'readonly-owned-source-graph-v8', 'readonly-owned-source-graph-v9', 'readonly-owned-source-graph-v10', 'readonly-owned-source-graph-v11', 'readonly-owned-source-graph-v12', 'readonly-owned-source-graph-v13', 'readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15', 'readonly-owned-source-graph-v16'].includes(policy) && /^[a-f\d]{64}$/.test(context.sourceSnapshotManifestSha256 ?? ''));
    const data = bytes(output, 'source-snapshot.json', 65536), manifest = JSON.parse(data);
    check(hash(data) === context.sourceSnapshotManifestSha256 && manifest.schemaVersion === 1 && manifest.policy === policy
        && Object.keys(manifest.hashes ?? {}).length === Object.keys(inventory).length
        && Object.keys(inventory).every(key => manifest.hashes[key] === context[key]));
    check(proof.sourceSnapshot?.policy === policy && typeof context.sourceSnapshotRoot === 'string'
        && proof.sourceSnapshot.root === context.sourceSnapshotRoot && !present(context.sourceSnapshotRoot)
        && Number.isFinite(Date.parse(proof.sourceSnapshot.removedAt))
        && Date.parse(proof.sourceSnapshot.removedAt) >= Date.parse(proof.cleanedUpAt));
}
/** Each source-snapshot policy maps to exactly its own captured file set. */
export function retainedInventory(output, context) {
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v11') return capturedV18SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v12') return capturedV19SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v16') return capturedV23SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v15') return capturedV22SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v14') return capturedV21SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v13') return capturedV20SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v10') return capturedV17SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v9') return capturedV16SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v8') return capturedV15SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v7') return capturedV14SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v6') return capturedV13SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v5') return capturedV12SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v4') return capturedV11SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v3') return capturedV10SourceFiles;
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v2') return capturedV9SourceFiles;
    return snapshotMode(output, context) ? capturedV8SourceFiles : legacyV8SourceFiles;
}
function checkSources(output, context, marker) {
    check(context.metadataSuiteId === metadataSuiteId && context.ownedMetadataPolicy === ownedMetadataPolicy && marker?.policy === ownedMetadataPolicy
        && Object.keys(marker).every(key => ['policy', 'paneId', 'geometry'].includes(key)));
    check(context.retainedSources === 'sources' && context.retainedAdapterBundle === 'collector-adapter.mjs'
        && /^[a-f\d]{64}$/.test(context.adapterBundleSha256 ?? '')
        && hash(bytes(output, 'collector-adapter.mjs')) === context.adapterBundleSha256
        && hash(bytes(output, 'package-lock.json')) === context.collectorLockSha256);
    const dir = lstatSync(join(output, 'sources')); check(dir.isDirectory() && !dir.isSymbolicLink());
    const inventory = retainedInventory(output, context);
    check(Object.keys(context).filter(key => key.endsWith('SourceSha256')).every(key => Object.hasOwn(inventory, key)));
    const retained = readdirSync(join(output, 'sources')), expected = Object.values(inventory);
    check(retained.length === expected.length && retained.every(file => expected.includes(file)));
    if (snapshotMode(output, context)) check(same(JSON.parse(bytes(output, join('sources', 'owned-source-inventory.json'))), inventory));
    for (const [key, file] of Object.entries(inventory)) {
        check(/^[a-f\d]{64}$/.test(context[key] ?? '') && hash(bytes(output, join('sources', file), 2 * 1024 * 1024)) === context[key]);
    }
    assertRetainedSourceClosure(join(output, 'sources'), inventory);
}
/** Re-derive every staged active-state observation and match its original common-pane/kernel proof. */
export function checkMetadataEvidence(output, binding, runId, context, proof) {
    const marker = proof.proofs?.[0]?.metadata;
    checkSources(output, context, marker);
    if (snapshotMode(output, context)) checkSnapshot(output, context, proof);
    check(!present(join(output, 'metadata-failed-observation.json')));
    check(binding?.runId === runId && binding.suiteId === metadataSuiteId
        && ['metadata-observations.json', 'metadata-unavailable.json'].includes(binding.file));
    const otherReport = binding.file === 'metadata-observations.json' ? 'metadata-unavailable.json' : 'metadata-observations.json';
    check(!present(join(output, otherReport)));
    check(Object.keys(binding).every(key => ['runId', 'suiteId', 'file', 'sha256', 'counts', 'provisionalUntil', 'status', 'reason'].includes(key)));
    const reportBytes = bytes(output, binding.file);
    check(/^[a-f\d]{64}$/.test(binding.sha256 ?? '') && hash(reportBytes) === binding.sha256);
    const report = JSON.parse(reportBytes);
    check(report.runId === runId && report.suiteId === metadataSuiteId);
    const text = bytes(output, 'metadata-snapshots.jsonl').toString('utf8').trim();
    const frames = text ? text.split('\n').map(line => JSON.parse(line)) : [];
    if (binding.file === 'metadata-unavailable.json') {
        check(report.status === 'unavailable' && ['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable'].includes(report.reason)
            && report.fixtureStarted === false && report.plannedExperiments === 13 && frames.length === 0
            && marker.paneId === undefined && marker.geometry === undefined
            && binding.status === 'unavailable' && binding.reason === report.reason && same(binding.counts, {}));
        return;
    }
    check(binding.status === undefined && binding.reason === undefined);
    const settingsBytes = bytes(output, 'initial-settings.json');
    check(hash(settingsBytes) === context.initialSettingsSha256);
    const settings = JSON.parse(settingsBytes);
    check(settings.Osc9NotificationsEnabled === true && settings.Osc9ProgressReportingEnabled === true
        && settings.AgentAccessObserveEnabled === true && settings.TabStripOrientation === 'Horizontal');
    const derived = buildMetadataReport(frames, runId);
    check(same(report, derived) && marker.paneId === frames[0].paneId
        && same(marker.geometry, { rows: frames[0].before.screen.rows, cols: frames[0].before.screen.cols }));
    const cell = JSON.parse(bytes(output, 'cell-observations.json'));
    check(cell.snapshots?.[0]?.paneId === marker.paneId && cell.snapshots[0].screen.rows === marker.geometry.rows
        && cell.snapshots[0].screen.cols === marker.geometry.cols);
    validateOutputGeometry(proof.proofs[0].terminalOutput.description, marker.geometry.rows, marker.geometry.cols);
    const counts = report.observations.reduce((values, item) => ({ ...values, [item.result]: (values[item.result] ?? 0) + 1 }), {});
    check(same(counts, binding.counts));
}
