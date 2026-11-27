import { ok as requireEvidence } from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { leaseReleasedAt } from './modern-child-environment-results.mjs';
import { pinnedJson } from './modern-inputs.mjs';

const controls = ['clean', 'inherited'];

export function describeEnvironmentRun(scratch, verifyChildEnvironmentRun) {
    const { child, context, ownership, launch, pane, receipt } = verifyChildEnvironmentRun(scratch);
    const proof = ownership.proofs[0];
    const host = launch.host;
    requireEvidence(host?.product === 'macOS' && /^\d+\.\d+(\.\d+)?$/.test(host.version)
        && /^[A-Z\d]+$/.test(host.build) && ['arm64', 'x64'].includes(host.arch), 'missing macOS launch metadata');
    return { child, measurement: {
        runId: context.runId, control: launch.control, host, startedAt: context.startedAt, appVersion: context.appVersion,
        appVersionSource: context.appVersionSource, executableSha256: context.executableSha256, appBinarySha256: launch.appBinaries,
        nodeVersion: context.nodeVersion, nodeSha256: launch.nodeSha256, sourcePolicy: context.sourceSnapshotPolicy,
        collectorSnapshotSha256: context.sourceSnapshotManifestSha256, receiptSchema: receipt.schemaVersion,
        childOutputSha256: receipt.sha256['child-environment.json'], appData: launch.appData, pane, app: proof.app, child: proof.worker,
        terminalRestoredAt: proof.privateClipboard.restoredAt, cleanedUpAt: ownership.cleanedUpAt, fixtureRemoved: receipt.fixtureRemoved,
        sourceSnapshotRemovedAt: ownership.sourceSnapshot.removedAt, leaseReleasedAt: leaseReleasedAt(scratch, context, ownership),
    } };
}

export function withEnvironmentArchive(directory, entry, callback) {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-child-environment-'));
    try {
        requireEvidence(controls.includes(entry.control) && entry.archive === entry.control + '-run.zip', 'unexpected archive name');
        extractOwnedArchive(directory, { archives: { 'owned-run.zip': entry.sha256 } }, scratch, join(directory, entry.archive));
        return callback(scratch);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

function readRun(directory, entry, verify) {
    const { child, measurement } = withEnvironmentArchive(directory, entry, scratch => describeEnvironmentRun(scratch, verify));
    requireEvidence(measurement.runId === entry.runId && measurement.control === entry.control, 'run identity differs from manifest');
    return { child, measurement: { ...measurement, archive: entry.archive, archiveSha256: entry.sha256 } };
}

export function readEnvironmentPair(directory, manifestSha256, verify) {
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    requireEvidence(manifest.schemaVersion === 1 && [manifest.appRevision, manifest.scriptsRevision].every(value => /^[a-f\d]{40}$/.test(value))
        && same(manifest.runs.map(run => run.control), controls), 'unexpected pair in the measurement manifest');
    const [clean, inherited] = manifest.runs.map(entry => readRun(directory, entry, verify));
    const a = clean.measurement, b = inherited.measurement;
    const common = ['host', 'appVersion', 'executableSha256', 'appBinarySha256', 'nodeVersion', 'nodeSha256',
        'sourcePolicy', 'collectorSnapshotSha256', 'receiptSchema'];
    requireEvidence(a.runId !== b.runId && a.appData !== b.appData && a.pane.paneId !== b.pane.paneId
        && a.app.executable === b.app.executable && a.child.executable === b.child.executable
        && Date.parse(a.leaseReleasedAt) <= Date.parse(b.startedAt)
        && common.every(field => same(a[field], b[field])), 'expected two isolated, serialized runs of the same App and collector');
    return { manifest, clean, inherited };
}
