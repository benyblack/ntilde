import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

export const pinnedBundle = 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6';
export const digest = bytes => createHash('sha256').update(bytes).digest('hex');
export const readJson = (directory, file) => JSON.parse(readFileSync(join(directory, file), 'utf8'));
const serialize = value => JSON.stringify(value, null, 2) + '\n';

export function readManifest(directory, expectedDigest, require) {
    const bytes = readFileSync(join(directory, 'manifest.json'));
    require(digest(bytes) === expectedDigest, 'publication manifest digest differs');
    const manifest = JSON.parse(bytes);
    require(manifest.schemaVersion === 1 && manifest.pinnedBundleSha256 === pinnedBundle, 'publication manifest identity differs');
    return manifest;
}

/** Extract evidence into an isolated scratch directory. The callback must consume it synchronously. */
export function withOwnedArchive(directory, manifest, callback) {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-unicode-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        return callback(scratch, manifest);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

export function leaseReleasedAt(directory, runId, require) {
    const records = readFileSync(join(directory, 'desktop-lease.jsonl'), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));
    require(same(records.map(record => record.event), ['acquired', 'launch-attempt', 'cleanup-verified', 'released'])
        && records.every((record, index) => record.lease === runId && record.pid === records[0].pid
            && Number.isFinite(Date.parse(record.at)) && (!index || Date.parse(record.at) >= Date.parse(records[index - 1].at)))
        && records[0].entrypoint === 'terminfo-owned.mjs' && records[2].by === 'marker-scan', 'desktop lease release did not verify');
    return records.at(-1).at;
}

/** Describe a focused run only after its receipt, binary identities and lease agree. */
export function focusedMeasurement(directory, manifest, context, receipt, screen, render = {}) {
    const m = manifest.measurement;
    assert.equal(m.runId, context.runId);
    assert.equal(m.sourcePolicy, context.sourceSnapshotPolicy);
    assert.equal(m.receiptSchema, receipt.schemaVersion);
    assert.ok(context.appVersion.endsWith('+' + m.appRevision));
    assert.ok([context.executableSha256, context.assemblySha256].every(hash => /^[a-f\d]{64}$/.test(hash)));
    return {
        ...m, version: context.appVersion, nodeVersion: context.nodeVersion, startedAt: context.startedAt,
        releasedAt: leaseReleasedAt(directory, context.runId, assert.ok), geometry: { rows: screen.rows, cols: screen.cols },
        ...render, executableSha256: context.executableSha256, assemblySha256: context.assemblySha256,
        sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256, archiveSha256: manifest.archives['owned-run.zip'],
    };
}

export function writeUnicodeReport(directory, report, markdown, check, require) {
    for (const [name, text] of [['report.json', serialize(report)], ['report.md', markdown]]) {
        const path = join(directory, name);
        if (check) require(readFileSync(path, 'utf8').replaceAll('\r\n', '\n') === text, 'published ' + name + ' differs');
        else writeFileSync(path, text);
    }
}
