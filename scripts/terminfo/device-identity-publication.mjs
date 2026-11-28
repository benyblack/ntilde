import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { assertRetainedSourceClosure, capturedDeviceIdentitySourceFiles, capturedDeviceIdentitySourcePolicy } from './owned-sources.mjs';
import { digest, focusedMeasurement, pinnedBundle, readJson, readManifest, withOwnedArchive, writeUnicodeReport } from './unicode-publication-inputs.mjs';
import { caseIds, da1, requests } from './device-identity-contracts.mjs';
import { deriveRun } from './device-identity-owned.mjs';
import { da } from './sixel-reports-contracts.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const publication = join(qa, '2026-11-27-rich-device-identity');
const manifestHash = '5d59771c08964cebb96432dfd2ddbeb1754414ac13583419e816de69014b5fdd';
const registration = id => '    deviceQuery({\n      id: "' + id + '",';

function span(source, start, end) {
    const from = source.indexOf(start), to = source.indexOf(end, from + start.length);
    assert.ok(from >= 0 && to > from && !source.includes(start, from + start.length), 'pinned code span differs');
    return source.slice(from, to);
}

export function readPinnedDeviceProbes(ids, spans) {
    const archive = readFileSync(join(qa, '2026-11-16-static-images/terminfo-dev-4.0.0.bundle.mjs.gz'));
    const bytes = gunzipSync(archive, { maxOutputLength: 1_000_000 });
    assert.equal(digest(bytes), pinnedBundle);
    const source = bytes.toString('utf8');
    const probe = span(source, 'function probe(id, termless, term, termObservationEvidence) {', '\nfunction readHyperlinkMetadata(');
    const helper = span(source, 'function deviceReplyResult(', '\nvar iconLabelReply,');
    const registrationMarker = marker => marker.startsWith('device.') ? registration(marker) : marker;
    const registrations = spans.map(([start, end]) =>
        span(source, registrationMarker(start), registrationMarker(end))).join('\n');
    const probes = runInNewContext(probe + helper + '\n[' + registrations + ']', Object.create(null), { timeout: 1000 }); // NOSONAR: digest-pinned upstream code in isolation.
    assert.deepEqual(Array.from(probes, item => item.id), ids);
    return probes;
}

export function readPinnedProbes() {
    return readPinnedDeviceProbes(caseIds, [[caseIds[0], 'device.decrqss'], [caseIds[1], '    probe("device.term-features"']]);
}

export function replay(probe, exchange) {
    return replayDeviceProbe(probe, exchange, requests[caseIds.indexOf(probe.id)]);
}

export async function replayDeviceProbe(probe, exchange, request) {
    assert.ok(request && exchange.id === probe.id);
    assert.equal(exchange.raw, exchange.reply + da1);
    let calls = 0;
    const verifySequence = sequence => {
        calls++;
        assert.equal(sequence, request);
        assert.equal(sequence + da, exchange.request);
    };
    const terminal = await probe.term({
        queryWithSentinelOutcome(sequence, pattern) {
            verifySequence(sequence);
            const sentinel = /\x1b\[\?[0-9;]*c/.exec(exchange.raw);
            assert.ok(sentinel);
            const match = pattern.exec(exchange.raw.slice(0, sentinel.index));
            return { raw: exchange.raw, match, reason: match ? 'reply' : 'sentinel' };
        },
    });
    const termlessReplay = probe.termless({
        feedCapture(sequence) {
            verifySequence(sequence);
            return exchange.reply;
        },
    });
    assert.equal(calls, 2);
    return { id: probe.id, terminal: structuredClone(terminal), termlessReplay: structuredClone(termlessReplay) };
}

export function verifySources(directory, context, sourceFiles, policy) {
    const retained = join(directory, 'sources'), manifest = readJson(directory, 'source-snapshot.json');
    const files = Object.entries(sourceFiles);
    assert.equal(context.sourceSnapshotPolicy, policy);
    assert.equal(manifest.policy, context.sourceSnapshotPolicy);
    assert.equal(manifest.schemaVersion, 1);
    assert.equal(readdirSync(retained).length, files.length);
    assert.deepEqual(readJson(retained, 'owned-source-inventory.json'), sourceFiles);
    const hashes = Object.fromEntries(files.map(([field, file]) => [field, digest(readFileSync(join(retained, file)))]));
    assert.deepEqual(hashes, manifest.hashes);
    assert.deepEqual(hashes, Object.fromEntries(files.map(([field]) => [field, context[field]])));
    assertRetainedSourceClosure(retained, sourceFiles);
}

export function withIdentityArchive(callback) {
    return withOwnedArchive(publication, readManifest(publication, manifestHash, assert.ok), callback);
}

export async function buildPublication() {
    const { owned, measurement, superseded } = withIdentityArchive((directory, manifest) => {
        const { context, frames, report, receipt } = deriveRun(directory);
        assert.deepEqual(report, readJson(directory, 'identity-report.json'));
        assert.deepEqual(receipt, readJson(directory, 'identity-receipt.json'));
        verifySources(directory, context, capturedDeviceIdentitySourceFiles, capturedDeviceIdentitySourcePolicy);
        return { owned: report, measurement: focusedMeasurement(directory, manifest, context, receipt, frames[0].screen),
            superseded: manifest.superseded };
    });
    const map = readJson(join(qa, '2026-11-26-rich-tui'), 'evidence-map.json');
    const stock = Object.fromEntries(caseIds.map(id => [id, map.rows.find(row => row.id === id)?.stock]));
    assert.deepEqual(Object.values(stock), ['supported', 'supported']);
    const pinned = await Promise.all(Array.from(readPinnedProbes(), probe => replay(probe, owned.exchanges.find(row => row.id === probe.id))));
    return { schemaVersion: 1, suite: owned.suite, pinnedBundleSha256: pinnedBundle, stock, pinned, owned, measurement, superseded };
}

const code = value => '\x60' + value + '\x60';
const wire = value => value ? code(value.replaceAll('\x1b[', 'CSI ').replaceAll('\x1bP', 'DCS ').replaceAll('\x1b\\', ' ST')) : 'Silence';

export function renderReport(report) {
    const { owned, measurement: m, pinned } = report;
    const { metadata } = owned;
    return [
        '# Rich TUI tertiary device attributes and terminal version', '',
        'One disposable direct-local Ntilde pane on macOS answered both canonical queries and their equivalent spellings. Each historical stock grade is supported. [Frozen Rich TUI evidence map](../2026-11-26-rich-tui/evidence-map.json)', '',
        '| Case or control | Request | Complete child PTY reply | Pinned terminal / termless | Exact reply |',
        '| --- | --- | --- | --- | --- |',
        ...owned.exchanges.map(row => {
            const result = pinned.find(item => item.id === row.id);
            const outcomes = result ? result.terminal.observation.outcome + ' / ' + result.termlessReplay.observation.outcome : 'Control';
            return '| ' + (row.id ? code(row.id) : row.stage) + ' | ' + wire(result ? row.request.slice(0, -da.length) : row.request)
                + ' | ' + wire(row.reply).replaceAll('|', String.raw`\|`) + ' | ' + outcomes + ' | ' + row.outcome + ' |';
        }), '',
        'The pinned terminfo.dev 4.0.0 queries accept a complete DCS with eight hexadecimal digits for DA3 and a complete DCS with printable text for XTVERSION. Their terminal callbacks were replayed with the retained DA1 sentinel. Their termless callbacks received the isolated case replies. Each child PTY request, including the silence controls, had an independent two-nonce DECRPM ordering fence.', '',
        'The exact DA3 oracle was the documented zero site/serial identifier ' + code('00000000') + ', which is unassigned and nonunique. The XTVERSION oracle came from ' + code(metadata.attribute) + ' in the sidecar ' + code(metadata.assembly) + '. A .NET metadata reader selected the attribute directly from the assembly before App launch. Offline derivation verifies the selected file bytes and independently decodes the value, then takes the part before the first ' + code('+') + '. The complete value was ' + code(metadata.informationalVersion) + ' and the VT assembly SHA-256 was ' + code(metadata.sha256) + '.', '',
        'The App observer saw red underlined marker cells, a displaced cursor, and the prepared alternate-screen, origin and wrap state. The deliberate DECSCUSR change from steady block to steady beam changed the stored cursor style alone. The query stages retained the same cell details, cursor position, visibility, style, and buffer state. Separate fenced DECRPM reads confirmed application cursor keys, origin, wrap, alternate screen, bracketed paste and focus reporting before and after the identity queries. The initial pane snapshot and exact kernel PTY mode were restored. The runner removed the disposable fixture and verified the desktop lease release.', '',
        '## Reproduce', '',
        'Run ' + code(m.runId) + ' used ' + m.platform + ', App revision ' + code(m.appRevision) + ' and collector revision ' + code(m.scriptsRevision) + '. The [owned archive](owned-run.zip) retains the original requests, reply and fence bytes, app and worker identity, snapshots, VT assembly, and cleanup records. The [machine-readable report](report.json) records both verdicts.', '',
        ...report.superseded.map(row => 'The first run also passed. The collector was remeasured after it switched to a fixed path for the .NET host. [Superseded run ' + row.runId + '](' + row.archive + ')'), '',
        'Build the recorded App revision with the repository .NET SDK. Run the collector from the recorded collector revision with Node 24+ and the macOS .NET host at /usr/local/share/dotnet/dotnet.', '',
        '~~~sh',
        'scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        'node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ' + m.version + ' --scope device-identity',
        '~~~', '',
        'At the publication revision, rederive the archived evidence and verify the frozen map.', '',
        '~~~sh',
        'node scripts/terminfo/device-identity-publication.mjs --check',
        'python3 scripts/terminfo-rich-baseline.py --check',
        '~~~', '',
    ].join('\n');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    assert.ok(process.argv.length === 3 && ['--write', '--check'].includes(process.argv[2]), 'expected --write or --check');
    const report = await buildPublication();
    writeUnicodeReport(publication, report, renderReport(report), process.argv[2] === '--check', assert.ok);
}
