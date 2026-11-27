import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { id, peerSteps, replay } from './modern-x11-osc5522-clipboard.mjs';
import { verifyAttempts, verifyX11Run } from './modern-x11-mouse-results.mjs';
import { pinnedJson, requireModern } from './modern-inputs.mjs';
import { pinnedClipboardProbe, replayPinned } from './osc5522-clipboard-pinned.mjs';

export const suiteId = 'ntilde-owned-x11-osc5522-clipboard-v1';
export const contractsVersion = 1;
export const caseIds = Object.freeze([id]);
export const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const directory = join(qa, '2026-11-27-osc5522-clipboard');
const manifestSha256 = '80f6f2f11fb11065e0e5fd99dfd8a7e359df65046ff70c15b9e38e94bb71d700';
const stockSha256 = 'd4418484f935ac1a534eaf0b0368d2e5bb174974ae92f2c9c9a4ff2f8905a82b';
const historySha256 = 'd7e92fea243b1b037edb8073a9bc6300991084ee7e9d3eb27b7948c2fb805d3a';
const bundlePath = '2026-11-16-shell-integration-aggregates/terminfo-dev-4.0.0.bundle.mjs.gz';

export function verifyRun(scratch) {
    return verifyX11Run(scratch, {
        policy: 'owned-modern-x11-osc5522-clipboard-v1', profilePrefix: 'X11 OSC 5522 ', markerPrefix: 'x11-osc5522-',
        nativeClipboard: true, replay, peerSteps,
        extraSources: ['modern-x11-keyboard.mjs', 'modern-x11-osc5522-clipboard.mjs', 'modern-x11-osc52-write.mjs', 'peer-linux.py', 'frames.py'],
    });
}

export function withMeasurement(callback) {
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    verifyAttempts(directory, manifest);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-osc5522-replay-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        return callback(scratch, manifest);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

async function rederiveHistory(probe) {
    const history = pinnedJson(join(qa, '2026-11-01-clipboard-comparison.json'), historySha256);
    const change = history.changes.find(item => item.featureId === id);
    return Promise.all(['before', 'after'].map(async phase => {
        const trace = change[phase + 'Trace'];
        const [exchange] = trace.queries;
        requireModern(trace.writes.length === 0 && trace.queries.length === 1 && /^[a-f\d]{40}$/.test(history[phase].sourceCommit) && exchange.reason === 'reply'
            && Buffer.from(exchange.rawBase64, 'base64').toString('latin1') === exchange.raw, 'historical OSC 5522 exchange differs');
        const { terminal } = await replayPinned(probe, exchange.sequence, exchange.raw);
        requireModern(terminal.observation.outcome === change[phase].outcome
            && terminal.response === exchange.match[0], 'historical OSC 5522 grade differs');
        return { phase, appRevision: history[phase].sourceCommit, outcome: terminal.observation.outcome,
            writeHex: Buffer.from(exchange.sequence).toString('hex'), rawHex: Buffer.from(exchange.raw).toString('hex') };
    }));
}

export async function buildPublication() {
    const { run, results: [result], measurement, manifest } = withMeasurement((scratch, manifest) => ({ ...verifyRun(scratch), manifest }));
    requireModern(manifest.schemaVersion === 1 && manifest.runId === run.runId && manifest.runtime === 'linux-arm64'
        && [manifest.appRevision, manifest.scriptsRevision].every(revision => /^[a-f\d]{40}$/.test(revision)), 'invalid OSC 5522 App or collector identity');
    const stock = pinnedJson(join(qa, '2026-11-09-stock-baseline.json'), stockSha256);
    const probe = pinnedClipboardProbe(readFileSync(join(qa, bundlePath)));
    const fromHex = value => Buffer.from(value, 'hex').toString('latin1');
    const pinned = await replayPinned(probe, fromHex(result.pinnedWriteHex), fromHex(result.pinnedRawHex));
    const history = await rederiveHistory(probe);
    const summary = { ...manifest, ownedRunId: run.runId, measuredAt: measurement.startedAt,
        context: { appVersion: run.appVersion, appVersionSource: 'operator-recorded source archive and publish command',
            executableSha256: run.appFiles.Ntilde, assemblySha256: run.appFiles['Ntilde.dll'], adapter: suiteId,
            sourceSnapshotPolicy: measurement.source.policy, sourceSnapshotManifestSha256: measurement.source.sha256 },
        stockCollector: { rerun: false, probeHash: stock.source.probeHash, note: 'Historical stock grade carried forward from the frozen baseline.' },
        stockCollectorRunId: stock.source.stockCollectorRunId };
    const report = { suiteId, contractsVersion, scope: 'One disposable direct-local Linux X11 pane, default CLIPBOARD and normal Ctrl+V.',
        measurement: { ...measurement, nodeVersion: run.nodeVersion, display: run.display.name, window: run.identity.window.id,
            serverPid: run.identity.serverPid, appPid: run.identity.appPid, workerPid: run.lineage.child.pid,
            pane: run.identity.pane, tty: run.lineage.child.pty.descriptors[0].path, peerPids: run.cleanup.peers.map(peer => peer.pid),
            clipboardWriteEnabled: run.settings.AllowOsc52ClipboardWrite },
        cases: [{ caseId: id, pinned: { ...pinned.terminal, termless: pinned.termless },
            contractResult: result.effect, conclusion: { outcome: result.effect.outcome, grading: suiteId, evidence: 'generated-input-child-bytes' },
            stock: { outcome: stock.outcomes[id], runId: stock.source.stockCollectorRunId, target: stock.source.target },
            exchange: { writeHex: result.pinnedWriteHex, rawHex: result.pinnedRawHex },
            modeControls: result.controls, restorationHex: result.restoreHex, history }] };
    return { summary, report };
}

const code = value => '\x60' + value + '\x60';
const bytes = value => code(value.match(/../g).join(' '));

function render({ summary, report }) {
    const [row] = report.cases;
    const m = report.measurement;
    const [before, after] = row.history;
    return [
        '# Rich TUI OSC 5522 clipboard qualification', '',
        '| Measurement | Verdict |', '| --- | --- |',
        '| Pinned terminfo.dev 4.0.0 terminal callback | **' + row.pinned.observation.outcome + '** |',
        '| Owned OSC 5522 paste on the default X11 clipboard | **' + row.contractResult.outcome + '** |',
        '| Historical stock run ' + code(row.stock.runId) + ' | **' + row.stock.outcome + '** |', '',
        'The direct local pane received the exact ' + code('CSI ? 5522 $ p') + ' query through its child PTY. The DA1 sentinel followed it. Request ' + bytes(row.exchange.writeHex) + ' returned ' + bytes(row.exchange.rawHex) + '. The unchanged terminal callback graded the complete DECRPM as recognized with mode reset. The pinned parser-context callback also replayed that same DECRPM. The two callbacks grade mode recognition.', '',
        'The independent mode writes and queries recorded these replies.', '',
        '| Phase | Child write | Child reply |', '| --- | --- | --- |',
        ...row.modeControls.map(control => '| ' + control.key + ' | ' + bytes(control.writeHex) + ' | ' + (control.replyHex ? bytes(control.replyHex) : 'empty') + ' |'), '',
        'The prior macOS comparison replayed ' + bytes(before.rawHex) + ' from ' + code(before.appRevision) + ' as ' + before.outcome + ' and ' + bytes(after.rawHex) + ' from ' + code(after.appRevision) + ' as ' + after.outcome + '. The current direct App pane reproduced the latter reply with native clipboard wiring and OSC 52 clipboard writes disabled. The OSC 5522 implementation opts in for App-owned panes with desktop clipboard service. The later historical stock run recorded an unverified Ntilde 0.12.0 identity and null configuration, so the comparison establishes the implementation transition while the exact configuration behind the stock result remains unknown. [Prior macOS comparison](../2026-11-01-clipboard.md) · [Historical stock baseline](../2026-11-09-stock-baseline.json).', '',
        'A fresh GTK peer independently read the synthetic UTF-8 and HTML values from the default X11 CLIPBOARD. Ctrl+V delivered the raw text with mode 5522 off. With mode 5522 enabled, the entire fenced PTY window held one MIME notification for ' + row.contractResult.mimes.map(code).join(' and ') + '. The window contained zero ordinary paste bytes. The GTK fixture replaced and verified both live values before the token read, which still returned the two values captured at the gesture.', '',
        'Reusing that credential within 15 seconds opened the App-owned Clipboard access dialog. The collector closed the dialog through its normal X11 window-close path. The child received ' + code('EPERM') + ' with zero data frames. OSC 52 read returned an empty response. Existing App clipboard tests cover explicit allow-once and session-only grants, credential scope, and reset revocation. [Clipboard permission policy](../../clipboard-permissions.md) · [Kitty clipboard protocol](https://sw.kovidgoyal.net/kitty/clipboard/).', '',
        '## Retained evidence and reproduction', '',
        'App ' + code(summary.context.appVersion) + ' came from ' + code(summary.appRevision) + ' with .NET SDK ' + code(summary.dotnetSdk) + '. Collector ' + code(summary.scriptsRevision) + ' ran with Node ' + code(m.nodeVersion) + ' on ' + summary.platform + '. App PID ' + m.appPid + ' owned local pane ' + code(m.pane.paneId) + ' and child PID ' + m.workerPid + ' on ' + code(m.tty) + '. XRes verified the App and independent GTK clients on Xvfb PID ' + m.serverPid + '.', '',
        'The archive retains the raw mode and OSC exchanges, XTEST events, both independent MIME readbacks, consent window ownership and focus restoration, App binary hashes and measured collector sources. The prepared two-MIME clipboard baseline and incoming modes were read back before the worker restored termios and exited. The App, eight GTK peers and Xvfb exited. The X11 socket and fixture tree were gone before the lease released at ' + m.leaseReleasedAt + '. [Owned run](owned-run.zip) · [Manifest](manifest.json) · [Derived report](report.json).', '',
        ...summary.attempts.flatMap(attempt => ['[Earlier attempt ' + code(attempt.runId) + '](' + attempt.archive + '). ' + attempt.reason, '']),
        'Rederive offline with Node 24 and Python 3.', '',
        '~~~sh', 'node scripts/terminfo/osc5522-clipboard-results.mjs --check', '~~~', '',
        'For a fresh Linux run, install Node 24, Python 3 with PyGObject and GTK4, Xvfb, libX11, libXtst and libXRes.', '',
        '~~~sh',
        '# Build from App revision ' + summary.appRevision + '.',
        'scripts/build.sh publish src/Ntilde.App -c Release -r linux-arm64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        'chmod -R go-w /absolute/private/sidecar',
        '# Launch from collector revision ' + summary.scriptsRevision + '.',
        'node scripts/terminfo-modern-x11-owned.mjs /absolute/private/sidecar/Ntilde ' + summary.context.appVersion + ' --osc5522-clipboard',
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
