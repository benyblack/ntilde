import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { id, peerSteps, replayClipboard } from './modern-x11-osc52-read.mjs';
import { verifyAttempts, verifyX11Run } from './modern-x11-mouse-results.mjs';
import { pinnedJson, requireModern } from './modern-inputs.mjs';
import { replayPinned } from './osc52-read-pinned.mjs';
import { pinnedBundle, withOwnedArchive } from './unicode-publication-inputs.mjs';

export const suiteId = 'ntilde-owned-x11-osc52-read-v1';
export const contractsVersion = 1;
export const caseIds = Object.freeze([id]);
const denialContract = 'ntilde-owned-x11-osc52-read-denial-v1';
const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
const directory = join(qa, '2026-11-27-osc52-read');
const manifestSha256 = '26e5fb89452980cc05ec4d3536649e3088d3f056751a74403ed5ac6fc881d8b8';
const stockSha256 = 'd4418484f935ac1a534eaf0b0368d2e5bb174974ae92f2c9c9a4ff2f8905a82b';

export function verifyRun(scratch) {
    return verifyX11Run(scratch, {
        policy: 'owned-modern-x11-osc52-read-v1', profilePrefix: 'X11 OSC 52 Read ', markerPrefix: 'x11-osc52-read-',
        nativeClipboard: true, replay: replayClipboard, peerSteps,
        extraSources: ['modern-x11-keyboard.mjs', 'modern-x11-osc52-read.mjs', 'modern-x11-osc52-write.mjs', 'peer-linux.py', 'frames.py'],
    });
}

export function withMeasurement(callback) {
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    verifyAttempts(directory, manifest);
    return withOwnedArchive(directory, manifest, scratch => callback(scratch, manifest));
}

function effectOf(kind) {
    if (kind === 'empty-denial') return 'unsupported';
    if (kind === 'seeded-nonce') return 'supported';
    return 'inconclusive';
}

function denialOf(kind) {
    if (kind === 'empty-denial') return 'supported';
    if (kind === 'seeded-nonce') return 'unsupported';
    return 'inconclusive';
}

export async function buildPublication() {
    const { run, results: [result], measurement, manifest } = withMeasurement((scratch, manifest) => ({ ...verifyRun(scratch), manifest }));
    requireModern(manifest.schemaVersion === 1 && manifest.runId === run.runId && manifest.runtime === 'linux-arm64'
        && [manifest.appRevision, manifest.scriptsRevision].every(revision => /^[a-f\d]{40}$/.test(revision)), 'invalid OSC 52 App or collector identity');
    const stock = pinnedJson(join(qa, '2026-11-09-stock-baseline.json'), stockSha256);
    const { kind } = result.exchange;
    requireModern(!['delivery-failure', 'late-reply'].includes(kind), 'no timely delivered OSC 52 read exchange');
    const pinned = await replayPinned(run.cases[0].uuid, result.exchange);
    const contractResult = { outcome: effectOf(kind), kind };
    const denial = { contract: denialContract, outcome: denialOf(kind), kind };
    const summary = { ...manifest, ownedRunId: run.runId, measuredAt: measurement.startedAt,
        context: { appVersion: run.appVersion, appVersionSource: 'operator-recorded source archive and publish command',
            executableSha256: run.appFiles.Ntilde, assemblySha256: run.appFiles['Ntilde.dll'], adapter: suiteId,
            sourceSnapshotPolicy: measurement.source.policy, sourceSnapshotManifestSha256: measurement.source.sha256 },
        stockCollector: { rerun: false, probeHash: stock.source.probeHash, note: 'Historical stock grade carried forward from the frozen baseline.' },
        stockCollectorRunId: stock.source.stockCollectorRunId };
    const report = { suiteId, contractsVersion, pinnedBundleSha256: pinnedBundle,
        scope: 'One disposable direct-local Linux X11 pane, default CLIPBOARD and one read-only OSC 52 c query.',
        measurement: { ...measurement, nodeVersion: run.nodeVersion, display: run.display.name, window: run.identity.window.id,
            serverPid: run.identity.serverPid, appPid: run.identity.appPid, workerPid: run.lineage.child.pid, pane: run.identity.pane,
            tty: run.lineage.child.pty.descriptors[0].path, peerPids: run.cleanup.peers.map(peer => peer.pid),
            clipboardWriteEnabled: run.settings.AllowOsc52ClipboardWrite },
        cases: [{ caseId: id, pinned, contractResult, denial,
            conclusion: { outcome: contractResult.outcome, grading: suiteId, evidence: 'cpr-fenced-pty-and-independent-x11-clipboard',
                note: 'The complete, empty OSC 52 c reply establishes read denial while the seeded clipboard value survives the query.' },
            stock: { outcome: stock.outcomes[id], runId: stock.source.stockCollectorRunId, target: stock.source.target }, ...result }] };
    return { summary, report };
}

const code = value => '\x60' + value + '\x60';
const bytes = value => code((value.match(/../g) ?? []).join(' '));

function render({ summary, report }) {
    const [row] = report.cases;
    const m = report.measurement;
    return [
        '# Rich TUI OSC 52 read qualification', '',
        '| Measurement | Verdict |', '| --- | --- |',
        '| Pinned terminfo.dev 4.0.0 read-only terminal callback | **' + row.pinned.observation.outcome + '** |',
        '| Owned OSC 52 clipboard read capability | **' + row.contractResult.outcome + '** |',
        '| Owned OSC 52 read denial (' + code(row.denial.contract) + ') | **' + row.denial.outcome + '** |',
        '| Historical stock run ' + code(row.stock.runId) + ' | **' + row.stock.outcome + '** |', '',
        'The unchanged pinned live callback seeds through its fixture and requires the OSC 52 c response to return that exact nonce. The replay supplies the observed seed and fenced reply. The complete empty frame grades ' + code(row.pinned.observation.reason) + '. The pinned parser-context registration shares a round-trip path that issues an OSC write. This run measures the live read-only callback.', '',
        'A fresh GTK peer read ' + code(row.before) + ' from the independently seeded default X11 CLIPBOARD. The child sent the pinned query ' + bytes(row.exchange.queryHex) + ' followed by a cursor-position request. The full PTY capture was ' + bytes(row.exchange.rawHex) + '. Its OSC reply was ' + bytes(row.exchange.replyHex) + ', followed by the expected cursor-position fence. A different GTK peer read the same nonce from the same owner after the full one-second capture and before restoration.', '',
        'The complete empty response and the surviving nonce satisfy the denial contract. Silence with a valid CPR, malformed frames, stale data, late bytes and missing or ambiguous delivery fences have distinct classifications. The owned read-capability result enters the qualification inventory as measured unsupported. The denial verdict records the policy separately. The original stock collector refused the query without a disposable clipboard. [Historical stock baseline](../2026-11-09-stock-baseline.json) · [Stock triage](../2026-11-02-inconclusive-triage.md).', '',
        'The disposable profile had OSC 52 writes disabled. The prior Modern round trip captured the same denial but restored its baseline immediately after the query. This run verifies the seed before and after the query. OSC 5522 permissions remain governed by the existing policy. [Modern round trip](../2026-11-25-modern-osc52-clipboard-results/report.md) · [Clipboard permission policy](../../clipboard-permissions.md).', '',
        '## Retained evidence and reproduction', '',
        'App ' + code(summary.context.appVersion) + ' came from ' + code(summary.appRevision) + ' with .NET SDK ' + code(summary.dotnetSdk) + '. Collector ' + code(summary.scriptsRevision) + ' ran with Node ' + code(m.nodeVersion) + ' on ' + summary.platform + '. App PID ' + m.appPid + ' owned pane ' + code(m.pane.paneId) + ' and PTY child PID ' + m.workerPid + ' on ' + code(m.tty) + '. XRes verified the App and each GTK peer against the owned Xvfb server.', '',
        'The restoring peer reset the fixture to ' + code(row.baseline) + ', and a fresh reader verified ' + code(row.restored) + '. The archive retains the complete PTY stream, X11 ownership checks, App hashes, measured sources, termios restoration, and all process exits. The X11 socket and fixture tree were removed before the shared desktop lease released at ' + m.leaseReleasedAt + '. [Owned run](owned-run.zip) · [Manifest](manifest.json) · [Derived report](report.json).', '',
        ...summary.attempts.flatMap(attempt => ['[Earlier attempt ' + code(attempt.runId) + '](' + attempt.archive + '). ' + attempt.reason, '']),
        'Rederive offline with Node 24 and Python 3.', '',
        '~~~sh', 'node scripts/terminfo/osc52-read-results.mjs --check', '~~~', '',
        'For a fresh Linux run, install Node 24, Python 3 with PyGObject and GTK4, Xvfb, libX11, libXtst and libXRes. Use the shared desktop lock across any container boundary.', '',
        '~~~sh',
        '# Build from App revision ' + summary.appRevision + '.',
        'scripts/build.sh publish src/Ntilde.App -c Release -r ' + summary.runtime + ' --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        'chmod -R go-w /absolute/private/sidecar',
        '# Launch from collector revision ' + summary.scriptsRevision + '.',
        'node scripts/terminfo-modern-x11-owned.mjs /absolute/private/sidecar/Ntilde ' + summary.context.appVersion + ' --osc52-read',
        '~~~', '',
    ].join('\n');
}

export async function publish(check) {
    const derived = await buildPublication();
    const outputs = { 'summary.json': JSON.stringify(derived.summary, null, 2) + '\n',
        'report.json': JSON.stringify(derived.report, null, 2) + '\n', 'report.md': render(derived) };
    for (const [name, content] of Object.entries(outputs)) {
        const path = join(directory, name);
        if (check) requireModern(readFileSync(path, 'utf8').replaceAll('\r\n', '\n') === content, 'published ' + name + ' differs');
        else writeFileSync(path, content);
    }
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    const args = process.argv.slice(2);
    requireModern(args.length === 1 && ['--check', '--write'].includes(args[0]), 'expected --check or --write');
    await publish(args[0] === '--check');
}
