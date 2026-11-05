// Synthetic suffix over untouched real v11 prefix DATA. No archive module executes.
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { historicalArchive } from './captured-sources.mjs';
import { privateClipboardFixture } from './private-clipboard-owned.mjs';
import { original as originalCollector } from './private-clipboard-upstream.mjs';
import { capturedV13SourceFiles, ownedSourcePath, sourceSnapshotPolicy } from '../owned-sources.mjs';
import { adaptCollector } from '../collector.mjs';
import { ownedPrivateClipboardPolicy, ownedPrivateClipboardSuiteId } from '../private-clipboard-worker.mjs';
import { PRIVATE_CLIPBOARD_PROBES } from '../private-clipboard-controller.mjs';
import { privateClipboardOutputPolicy } from '../terminal-output.mjs';
import { emptyPrivateClipboardReply, privateClipboardEvidenceJson } from '../private-clipboard.mjs';
import { completeOwnedReceipt } from '../receipt.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Clone immutable v11 data into a private temporary directory, add a synthetic 17th suffix and always remove the copy. */
export async function privateClipboardReceiptFixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-private-receipt-'));
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value, null, 2) + '\n');
    try {
        const archive = historicalArchive('2026-11-05-query-observations');
        for (const [file, bytes] of archive) { const path = join(output, file); mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, bytes); }
        const old = JSON.parse(archive.get('cell-receipt.json'));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = structuredClone(old);
        rmSync(join(output, 'cell-receipt.json'));
        const context = JSON.parse(archive.get('context.json')), proof = JSON.parse(archive.get('ownership-proof.json'));
        const settings = JSON.parse(archive.get('initial-settings.json')), query = JSON.parse(archive.get('query-observations.json'));
        const raw = JSON.parse(archive.get('raw.json')), process = proof.proofs[0];
        for (const [key, file] of Object.entries(capturedV13SourceFiles)) {
            const data = readFileSync(ownedSourcePath(fileURLToPath(new URL('../../../', import.meta.url)), file));
            writeFileSync(join(output, 'sources', file), data); context[key] = hash(data);
        }
        const manifest = { schemaVersion: 1, policy: sourceSnapshotPolicy,
            hashes: Object.fromEntries(Object.keys(capturedV13SourceFiles).map(key => [key, context[key]])) };
        write('source-snapshot.json', manifest);
        Object.assign(context, { sourceSnapshotPolicy, sourceSnapshotManifestSha256: hash(readFileSync(join(output, 'source-snapshot.json'))),
            privateClipboardSuiteId: ownedPrivateClipboardSuiteId, ownedPrivateClipboardPolicy,
            terminalOutput: privateClipboardOutputPolicy, clipboard: 'armed synthetic App-private storage only; no native interchange or OSC52 read permission',
            privateClipboardReferenceScope: 'selected App/VT observational reference inputs; not complete App closure or binary-source attestation', retainedOriginalCollector: 'collector-original.mjs' });
        const binding = { ...query.binding, sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256,
            executableSha256: context.executableSha256, assemblySha256: context.assemblySha256,
            appBirth: 'Thu Nov  5 08:10:00 2026', workerBirth: 'Thu Nov  5 08:10:01 2026', outputDevice: { dev: '1', rdev: '2', ino: '3' } };
        const api = await privateClipboardFixture({ binding, processFacts: process, initialScreen: query.initial.before.screen,
            atStart: Date.parse(query.seal.ownership.at) + 1 });
        const report = await api.run(), file = 'private-clipboard-observations.json';
        writeFileSync(join(output, file), privateClipboardEvidenceJson(report));
        writeFileSync(join(output, 'private-clipboard-journal.json'), privateClipboardEvidenceJson(api.observer.journal));
        const privateBinding = { runId: context.runId, suiteId: ownedPrivateClipboardSuiteId, file,
            sha256: hash(readFileSync(join(output, file))), counts: { completedProbes: 3, acceptedWrites: 2 }, complete: true };
        evidence.privateClipboardEvidence = privateBinding;
        const at = Date.parse(report.frames.at(-1).after.afterOwnership.at);
        process.privateClipboard = { policy: ownedPrivateClipboardPolicy, appBirth: binding.appBirth, workerBirth: binding.workerBirth,
            outputDevice: binding.outputDevice, binding, originalTerminalState: 'gfmt1:lflag=5cb:oflag=3',
            restoredTerminalState: 'gfmt1:lflag=5cb:oflag=3', restoredAt: new Date(at + 1).toISOString() };
        proof.cleanedUpAt = new Date(at + 2).toISOString(); proof.sourceSnapshot.policy = sourceSnapshotPolicy;
        proof.sourceSnapshot.removedAt = new Date(at + 3).toISOString();
        settings.AllowOsc52ClipboardWrite = true; write('initial-settings.json', settings);
        context.initialSettingsSha256 = hash(readFileSync(join(output, 'initial-settings.json')));
        writeFileSync(join(output, 'collector-original.mjs'), originalCollector);
        const adapted = adaptCollector(originalCollector, join(context.sourceSnapshotRoot, 'scripts', 'terminfo', 'ownership.mjs'),
            { privateClipboardModulePath: join(context.sourceSnapshotRoot, 'scripts', 'terminfo', 'private-clipboard-worker.mjs') });
        writeFileSync(join(output, 'collector-adapter.mjs'), adapted); context.adapterBundleSha256 = hash(adapted);
        raw.collectorAdapter.bundleSha256 = context.adapterBundleSha256;
        raw.rawReplies['collector.ntildePrivateClipboard'] = api.worker.summary;
        raw.observations = raw.observations.filter(value => !PRIVATE_CLIPBOARD_PROBES.includes(value.featureId));
        raw.assertions = raw.assertions.filter(value => !PRIVATE_CLIPBOARD_PROBES.includes(value.featureId));
        const screen = report.frames[0].before.screen, row = Math.max(1, screen.cursorRow - (screen.bufferState.originMode ? screen.bufferState.scrollTop : 0) + 1), col = screen.cursorCol + 1;
        const queryTrace = (sequence, reply, captures) => ({ sequence, match: [reply, ...captures], reason: 'reply', raw: reply, rawBase64: Buffer.from(reply).toString('base64') });
        for (const [index, probeId] of PRIVATE_CLIPBOARD_PROBES.entries()) {
            const result = report.callbackResults[index].result, writes = [], queries = [], events = [];
            if (index !== 2) {
                const nonce = report.controller.events.find(value => value.probeId === probeId && value.request.operation === 'armWrite').request.value;
                const sequence = '\x1b]52;c;' + Buffer.from(nonce).toString('base64') + '\x07'; writes.push(sequence); events.push({ kind: 'write', sequence });
                const query = queryTrace('\x1b[6n', '\x1b[' + row + ';' + col + 'R', [String(row), String(col)]); queries.push(query); events.push({ kind: 'query', ...query });
            }
            if (index !== 1) { const query = queryTrace('\x1b]52;c;?\x07', emptyPrivateClipboardReply, ['']); queries.push(query); events.push({ kind: 'query', ...query }); raw.rawReplies[probeId + '.callbackResponse'] = emptyPrivateClipboardReply; }
            raw.rawReplies[probeId] = JSON.stringify({ writes, queries, events, clipboard: [{ profile: 'ntilde-private-app-v1', probeId, restored: true }] });
            raw.observations.push({ featureId: probeId, ...result.observation, rawReplyRef: probeId });
            raw.assertions.push(...(result.assertions ?? []).map(value => ({ featureId: probeId, ...value, rawReplyRef: probeId })));
            delete raw.ungradedDiagnostics[probeId];
        }
        write('raw.json', raw); write('context.json', context); write('ownership-proof.json', proof);
        await action({ output, write, context, proof, report, evidence, raw, manifest, old,
            finish: () => completeOwnedReceipt(output, join(output, 'already-removed-fixture'), evidence) });
    } finally { rmSync(output, { recursive: true, force: true }); }
}
