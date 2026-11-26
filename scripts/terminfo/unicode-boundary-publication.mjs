import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { isDeepStrictEqual as same } from 'node:util';
import { caseIds, requireBoundary, scope } from './unicode-boundary-contracts.mjs';
import { deriveRecords } from './unicode-boundary-owned.mjs';
import { assertRetainedSourceClosure, capturedUnicodeBoundarySourceFiles, capturedUnicodeBoundarySourcePolicy } from './owned-sources.mjs';
import { digest, leaseReleasedAt, pinnedBundle, readJson, readManifest, withOwnedArchive, writeUnicodeReport } from './unicode-publication-inputs.mjs';

export const publication = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-26-unicode-boundaries/', import.meta.url));
const manifestSha256 = '93b05c7585ff51743f477e6a50a43978925f12c8deb08af59e30e6a59af6bbaa';
const contextName = 'context.json', proofName = 'ownership-proof.json', sourceName = 'source-snapshot.json';
const settingsName = 'initial-settings.json', journalName = 'unicode-boundary-frames.jsonl';
const receiptFiles = [contextName, proofName, journalName, sourceName, settingsName];

export function withMeasurement(callback, directory = publication) {
    return withOwnedArchive(directory, readManifest(directory, manifestSha256, requireBoundary), callback);
}

function verifySources(directory, context, receipt) {
    const manifest = readJson(directory, sourceName), sources = join(directory, 'sources');
    const files = Object.entries(capturedUnicodeBoundarySourceFiles);
    requireBoundary(context.sourceSnapshotPolicy === capturedUnicodeBoundarySourcePolicy && manifest.schemaVersion === 1
        && manifest.policy === context.sourceSnapshotPolicy && receipt.files[sourceName] === context.sourceSnapshotManifestSha256
        && Object.keys(manifest.hashes).length === files.length && readdirSync(sources).length === files.length
        && same(readJson(sources, 'owned-source-inventory.json'), capturedUnicodeBoundarySourceFiles), 'incomplete measured source graph');
    for (const [field, file] of files) {
        requireBoundary(manifest.hashes[field] === context[field] && digest(readFileSync(join(sources, file))) === context[field],
            'retained source differs for ' + file);
    }
    assertRetainedSourceClosure(sources, capturedUnicodeBoundarySourceFiles);
}

function verifyOwnership(directory, context, receipt) {
    const proof = readJson(directory, proofName), worker = proof.proofs?.[0];
    const rawMode = worker?.privateClipboard;
    const names = readdirSync(directory);
    requireBoundary(!names.includes('failure.json') && !names.includes('cleanup-failure.json')
        && same(context.collectionScope, scope) && same(proof.collectionScope, scope)
        && proof.completed === true && !proof.cleanupError && !proof.persistentDaemon
        && proof.sourceSnapshot?.policy === context.sourceSnapshotPolicy
        && Date.parse(proof.cleanedUpAt) <= Date.parse(proof.sourceSnapshot?.removedAt)
        && worker?.app.pid === context.ownerPid && worker.worker.ppid === context.ownerPid
        && typeof rawMode?.originalTerminalState === 'string' && rawMode.originalTerminalState === rawMode.restoredTerminalState
        && receipt.files[settingsName] === context.initialSettingsSha256, 'direct-local ownership or cleanup did not verify');
    const settings = readJson(directory, settingsName);
    requireBoundary(settings.SessionPersistence === 'Off' && settings.AllowOsc52ClipboardWrite === false && settings.AgentAccessActEnabled === false,
        'fixture did not use the read-only ordinary-tab profile');
}

export function readRun(directory) {
    const context = readJson(directory, contextName), receipt = readJson(directory, 'unicode-boundary-receipt.json');
    requireBoundary(receipt.schemaVersion === 1 && receipt.runId === context.runId && /^[a-f\d]{32}$/.test(receipt.runId)
        && receipt.suite === scope.id && receipt.fixtureRemoved === true
        && Object.keys(receipt.files).length === receiptFiles.length, 'wrong measurement receipt or incomplete file bindings');
    for (const name of receiptFiles) {
        requireBoundary(digest(readFileSync(join(directory, name))) === receipt.files[name], 'receipt digest differs for ' + name);
    }
    verifyOwnership(directory, context, receipt);
    verifySources(directory, context, receipt);
    const frames = readFileSync(join(directory, journalName), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));
    const rows = deriveRecords(frames, context.runId);
    const report = { runId: context.runId, suite: scope.id, rows };
    requireBoundary(same(report, readJson(directory, 'unicode-boundary-report.json')) && same(rows.map(row => row.id), caseIds),
        'retained report did not rederive');
    return { context, receipt, report, frames };
}

export function buildPublication() {
    return withMeasurement((directory, manifest) => {
        const { context, receipt, frames, report } = readRun(directory), m = manifest.measurement;
        requireBoundary(m.runId === context.runId && m.sourcePolicy === context.sourceSnapshotPolicy
            && m.receiptSchema === receipt.schemaVersion && context.appVersion.endsWith('+' + m.appRevision)
            && [context.assemblySha256, context.executableSha256].every(hash => /^[a-f\d]{64}$/.test(hash)), 'wrong measured App or run');
        const { rows, cols } = frames[0].screen;
        return { schemaVersion: 1, baseline: 'unicode', pinnedBundleSha256: pinnedBundle, ...report,
            measurement: { ...m, version: context.appVersion, nodeVersion: context.nodeVersion, geometry: { rows, columns: cols },
                startedAt: context.startedAt, executableSha256: context.executableSha256, assemblySha256: context.assemblySha256,
                sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256,
                releasedAt: leaseReleasedAt(directory, context.runId, requireBoundary), archiveSha256: manifest.archives['owned-run.zip'] } };
    });
}

const code = value => '\x60' + value + '\x60';

export function renderReport(report) {
    const { measurement: m, rows } = report;
    const [ambiguous, grapheme, wrap] = rows;
    const cpr = wrap.observed.terminalReply;
    return [
        '# Ntilde Unicode boundary qualification', '',
        'Three cases measured in a disposable ordinary local tab on macOS. Together with the [five width cases](../2026-11-25-unicode-width/report.md), this covers the eight fixtures in the [Unicode baseline](https://terminfo.dev/baseline/unicode).', '',
        '| Case | Pinned PTY replay | Owned stored-cell effect |', '| --- | --- | --- |',
        ...rows.map(row => `| ${code(row.id)} | ${row.terminal} | ${row.effect} |`),
        '', `The ambiguous-width symbol ● occupied ${ambiguous.ptyWidth} column in this profile. The original probe accepts either one or two columns.`,
        '', `The family emoji 👨‍👩‍👧 occupied ${grapheme.ptyWidth} columns as one stored cluster. The pinned cursor probe measures the advance after the write. In a supplemental Backspace check, the cursor returned to zero-based column ${grapheme.observed.backspaceCursor[1]} and the cluster remained in place.`,
        '', `Both wide-wrap fixtures stored 中 and its continuation at the start of the second row. The parser fixture seeds a full ASCII row and uses CUP to reach the final column with pending wrap cleared. The PTY fixture stops the ASCII seed one column short and observes CPR at row ${cpr?.row}, column ${cpr?.col}, using one-based coordinates. Each seed row was preserved.`,
        '', 'The real-bundle test runs both unchanged 4.0.0 callbacks against their respective retained observations. The effect verdicts come from physical cells and the App cursor. The historical stock baseline already supported these IDs. The owned evidence here covers this App and these fixtures, with glyph appearance and other width policies outside scope.',
        '', '## Reproduce', '',
        `Run ${code('node scripts/terminfo/unicode-boundary-publication.mjs --check')} to rederive the report from the [owned run archive](owned-run.zip). Run ${code('NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED=1 node --test scripts/terminfo/unicode-boundary-grading-collector.test.mjs')} to replay the original callbacks.`,
        '', `Run ${code(m.runId)} started ${m.startedAt} on macOS ${m.platform}, using sidecar ${code(m.version)} and collector ${code(m.scriptsRevision)}. The grid was ${m.geometry.rows} × ${m.geometry.columns}. The observer verified cleanup and the desktop lease was released.`,
        '', `Build the recorded App revision with ${code('scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar')}.`,
        '', `From the recorded collector revision, collect with ${code('node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ' + m.version + ' --scope unicode-boundaries')}.`, '',
    ].join('\n');
}

export function publish(check) {
    const report = buildPublication();
    writeUnicodeReport(publication, report, renderReport(report), check, requireBoundary);
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    const args = process.argv.slice(2);
    requireBoundary(args.length === 1 && ['--check', '--write'].includes(args[0]), 'expected --check or --write');
    publish(args[0] === '--check');
}
