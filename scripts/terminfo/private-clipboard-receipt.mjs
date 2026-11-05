import { lstatSync, readdirSync, readFileSync } from 'node:fs';
import { isDeepStrictEqual as same } from 'node:util';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { snapshotHash } from './agent-observer.mjs';
import { adaptCollector } from './collector.mjs';
import { PRIVATE_CLIPBOARD_UPSTREAM_SHA256 } from './private-clipboard-source.mjs';
import { PRIVATE_CLIPBOARD_PROBES } from './private-clipboard-controller.mjs';
import { ownedPrivateClipboardPolicy, ownedPrivateClipboardSuiteId } from './private-clipboard-worker.mjs';
import { buildPrivateClipboardReport, checkPrivateClipboard as check, emptyPrivateClipboardReply } from './private-clipboard.mjs';
import { privateClipboardOutputPolicy } from './terminal-output.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
function present(path) { try { lstatSync(path); return true; } catch (error) { if (error.code !== 'ENOENT') throw error; return false; } }
function bytes(output, file, max = 16 * 1024 * 1024) {
    const path = join(output, file), stat = lstatSync(path); check(stat.isFile() && !stat.isSymbolicLink() && stat.size <= max);
    const data = readFileSync(path); check(data.length <= max); return data;
}
const json = (output, file) => JSON.parse(bytes(output, file));
/** No declaration, partial journal, source input, import or summary can downgrade to a v11 receipt. */
export function includesPrivateClipboardEvidence(output, evidence, context, proof) {
    if (context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v5' || proof.sourceSnapshot?.policy === 'readonly-owned-source-graph-v5'
        || evidence.privateClipboardEvidence !== undefined || Object.keys(context).some(key => /privateClipboard/i.test(key))
        || proof.proofs?.some(item => item.privateClipboard !== undefined)
        || readdirSync(output).some(file => file.startsWith('private-clipboard-') || file === 'collector-original.mjs')) return true;
    if (present(join(output, 'sources')) && readdirSync(join(output, 'sources')).some(file => file.startsWith('private-clipboard') || file.startsWith('clipboard-')
        || (file.endsWith('.mjs') && /private-clipboard/.test(bytes(output, join('sources', file), 2 * 1024 * 1024).toString())))) return true;
    const raw = json(output, 'raw.json');
    return raw.rawReplies?.['collector.ntildePrivateClipboard'] !== undefined;
}
function rawQuery(sequence, raw, captures) {
    return { sequence, match: [raw, ...captures], reason: 'reply', raw, rawBase64: Buffer.from(raw).toString('base64') };
}
/** Original packets and grader projection, independently cross-checked against the admitted App journal. */
function checkRaw(raw, report, binding) {
    check(raw.target?.permissions === null && raw.rawReplies?.['collector.clipboardFixture'] === undefined);
    const expectedSummary = { policy: ownedPrivateClipboardPolicy, profile: 'ntilde-private-app-v1',
        boundary: 'App-private storage only; no native interchange or clipboard-read permission',
        complete: true, completedProbes: 3, binding };
    check(same(JSON.parse(raw.rawReplies['collector.ntildePrivateClipboard']), expectedSummary));
    const observations = raw.observations.filter(item => PRIVATE_CLIPBOARD_PROBES.includes(item.featureId));
    check(observations.length === 3 && same(observations.map(item => item.featureId), [...PRIVATE_CLIPBOARD_PROBES])
        && same(raw.observations.slice(-3).map(item => item.featureId), [...PRIVATE_CLIPBOARD_PROBES]));
    const events = report.controller.events, screen = report.frames[0].before.screen;
    const row = Math.max(1, screen.cursorRow - (screen.bufferState.originMode ? screen.bufferState.scrollTop : 0) + 1), col = screen.cursorCol + 1;
    const cpr = '\x1b[' + row + ';' + col + 'R';
    for (const [index, probeId] of PRIVATE_CLIPBOARD_PROBES.entries()) {
        const result = report.callbackResults[index].result;
        const observation = { featureId: probeId, ...result.observation, rawReplyRef: probeId };
        check(same(observations[index], observation) && raw.ungradedDiagnostics?.[probeId] === undefined
            && !raw.notTested?.some(item => item.featureId === probeId));
        const assertions = raw.assertions.filter(item => item.featureId === probeId);
        check(same(assertions, (result.assertions ?? []).map(item => ({ featureId: probeId, ...item, rawReplyRef: probeId }))));
        if (index !== 1) check(raw.rawReplies[probeId + '.callbackResponse'] === emptyPrivateClipboardReply);
        else check(raw.rawReplies[probeId + '.callbackResponse'] === undefined);
        const writes = [], queries = [], ordered = [];
        if (index !== 2) {
            const nonce = events.find(item => item.probeId === probeId && item.request.operation === 'armWrite').request.value;
            const packet = '\x1b]52;c;' + Buffer.from(nonce).toString('base64') + '\x07';
            writes.push(packet); ordered.push({ kind: 'write', sequence: packet });
            const query = rawQuery('\x1b[6n', cpr, [String(row), String(col)]); queries.push(query); ordered.push({ kind: 'query', ...query });
        }
        if (index !== 1) {
            const query = rawQuery('\x1b]52;c;?\x07', emptyPrivateClipboardReply, ['']); queries.push(query); ordered.push({ kind: 'query', ...query });
        }
        check(same(JSON.parse(raw.rawReplies[probeId]), { writes, queries, events: ordered,
            clipboard: [{ profile: 'ntilde-private-app-v1', probeId, restored: true }] }));
    }
}
export function checkPrivateClipboardEvidence(output, binding, runId, context, proof, prefixes) {
    const original = proof.proofs?.[0], marker = original?.privateClipboard;
    check(context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v5' && context.privateClipboardSuiteId === ownedPrivateClipboardSuiteId
        && context.ownedPrivateClipboardPolicy === ownedPrivateClipboardPolicy && context.terminalOutput === privateClipboardOutputPolicy
        && context.clipboard === 'armed synthetic App-private storage only; no native interchange or OSC52 read permission'
        && context.privateClipboardReferenceScope === 'selected App/VT observational reference inputs; not complete App closure or binary-source attestation'
        && context.retainedOriginalCollector === 'collector-original.mjs');
    check(binding?.runId === runId && binding.suiteId === ownedPrivateClipboardSuiteId && binding.file === 'private-clipboard-observations.json'
        && Object.keys(binding).length === 6 && binding.complete === true && same(binding.counts, { completedProbes: 3, acceptedWrites: 2 }));
    check(prefixes.queryEvidence?.file === 'query-observations.json' && prefixes.queryEvidence.status === undefined);
    const data = bytes(output, binding.file); check(hash(data) === binding.sha256);
    const report = JSON.parse(data), journal = json(output, 'private-clipboard-journal.json');
    check(Object.keys(journal).length === 6 && journal.failed === false && journal.complete === true);
    const derived = buildPrivateClipboardReport({ binding: journal.binding, controller: journal.controller, frames: journal.frames, callbackResults: journal.callbackResults });
    check(same(report, derived));
    const b = derived.binding;
    check(b.runId === runId && b.ownerPid === original.app.pid && b.workerPid === original.worker.pid && b.uid === original.worker.uid
        && b.appExecutable === original.app.executable && b.workerExecutable === original.worker.executable && b.tty === original.tty
        && b.sourceSnapshotManifestSha256 === context.sourceSnapshotManifestSha256 && b.executableSha256 === context.executableSha256 && b.assemblySha256 === context.assemblySha256);
    check(Object.keys(marker ?? {}).length === 8 && marker.policy === ownedPrivateClipboardPolicy && same(marker.binding, b)
        && marker.appBirth === b.appBirth && marker.workerBirth === b.workerBirth && same(marker.outputDevice, b.outputDevice)
        && typeof marker.originalTerminalState === 'string' && marker.originalTerminalState.length <= 4096 && /^gfmt1:(?:[a-z][a-z0-9]*=[a-f0-9]+)(?::[a-z][a-z0-9]*=[a-f0-9]+)*$/.test(marker.originalTerminalState)
        && marker.restoredTerminalState === marker.originalTerminalState
        && Date.parse(marker.restoredAt) >= Date.parse(derived.frames.at(-1).after.afterOwnership.at)
        && Date.parse(marker.restoredAt) <= Date.parse(proof.cleanedUpAt));
    const query = json(output, 'query-observations.json');
    check(query.binding.paneId === b.paneId && Date.parse(derived.frames[0].before.beforeOwnership.at) >= Date.parse(query.seal.ownership.at));
    for (const side of ['app', 'worker']) check(['pid', 'ppid', 'uid', 'tty', 'executable'].every(key => derived.frames[0].before.beforeOwnership[side][key] === original[side][key]));
    const settings = json(output, 'initial-settings.json');
    check(settings.Profiles?.length === 1 && settings.Profiles[0].Name === b.profileName && settings.AllowOsc52ClipboardWrite === true
        && settings.AgentAccessActEnabled === true && settings.AgentAccessObserveEnabled === true && settings.SessionPersistence === 'Off');
    const upstream = bytes(output, 'collector-original.mjs', 2 * 1024 * 1024).toString();
    check(hash(upstream) === PRIVATE_CLIPBOARD_UPSTREAM_SHA256 && context.upstreamBundleSha256 === PRIVATE_CLIPBOARD_UPSTREAM_SHA256);
    const adapted = adaptCollector(upstream, join(context.sourceSnapshotRoot, 'scripts', 'terminfo', 'ownership.mjs'),
        { privateClipboardModulePath: join(context.sourceSnapshotRoot, 'scripts', 'terminfo', 'private-clipboard-worker.mjs') });
    check(bytes(output, 'collector-adapter.mjs', 2 * 1024 * 1024).equals(Buffer.from(adapted)));
    checkRaw(json(output, 'raw.json'), derived, binding);
}
