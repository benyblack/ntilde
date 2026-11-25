import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { digest, pinnedJson, requireModern } from './modern-inputs.mjs';
import { modernRoot } from './modern-results.mjs';
import { id, pastePeerSteps, replayPaste } from './modern-x11-clipboard-paste.mjs';
import { verifyAttempts, verifyX11Run } from './modern-x11-mouse-results.mjs';
import { buildOsc52ClipboardPublication, publication as predecessorPublication } from './modern-x11-osc52-clipboard-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-25-modern-clipboard-paste-results';
const criterionSha256 = '91c8673a0e859273df505c9e592fe1f7ef750ee6c82a5038eaab2fbf410fd37e';
const manifestSha256 = '0a9e9eb2697c5766a3aaaf49ccdf8a5b2a5cbed3b963ec88f33e414aafd871be';
export const pastePaths = base => ({ json: join(base, publication, 'report.json'), markdown: join(base, publication, 'report.md') });

export const verifyPasteRun = directory => verifyX11Run(directory, {
    policy: 'owned-modern-x11-clipboard-paste-v1', profilePrefix: 'X11 Clipboard Paste ', markerPrefix: 'x11-paste-',
    nativeClipboard: true, replay: replayPaste, peerSteps: pastePeerSteps,
    extraSources: ['modern-x11-keyboard.mjs', 'modern-x11-clipboard-paste.mjs', 'modern-x11-osc52-write.mjs', 'peer-linux.py', 'frames.py'],
});

function readMeasurement(base) {
    const directory = join(base, publication);
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    verifyAttempts(directory, manifest);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-clipboard-paste-replay-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const { run, results: [result], measurement } = verifyPasteRun(scratch);
        requireModern(manifest.schemaVersion === 1 && manifest.runId === run.runId
            && [manifest.appRevision, manifest.scriptsRevision].every(revision => /^[a-f\d]{40}$/.test(revision))
            && ['linux-x64', 'linux-arm64'].includes(manifest.runtime), 'wrong clipboard paste manifest');
        const [trial] = run.cases;
        return { result, clipboardPasteMeasurement: { publication, manifestSha256, ...manifest, ...measurement,
            appBuildSource: 'operator-recorded source archive and publish command',
            appVersion: run.appVersion, appExecutableSha256: run.appFiles.Ntilde, assemblySha256: run.appFiles['Ntilde.dll'],
            nodeVersion: run.nodeVersion, pane: run.identity.pane, display: run.display.name, root: run.identity.root,
            windowId: run.identity.window.id, xtest: run.identity.xtestVersion, serverPid: run.identity.serverPid,
            appPid: run.identity.appPid, workerPid: run.lineage.child.pid, tty: run.lineage.child.pty.descriptors[0].path,
            baselineOwnerPid: trial.seed.after.ownerPid, nonceOwnerPid: trial.nonceSeed.after.ownerPid,
            restorationOwnerPid: trial.restoreSeed.after.ownerPid, peerPids: run.cleanup.peers.map(peer => peer.pid) } };
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

export function buildPastePublication(base = modernRoot) {
    const previous = buildOsc52ClipboardPublication(base);
    const { result, clipboardPasteMeasurement } = readMeasurement(base);
    const index = previous.rows.findIndex(row => row.id === id);
    const target = previous.rows[index];
    requireModern(previous.rows.length === 69 && target?.conclusion === 'inconclusive'
        && digest(target.requirement) === criterionSha256, 'changed frozen clipboard paste App criterion');
    const row = { ...target, conclusion: result.conclusion, blocker: null, observed: result,
        criterionPath: 'frozen App default paste with real X11 CLIPBOARD, XTEST, and separate raw direct PTY windows',
        evidence: { file: 'observation.json', runId: clipboardPasteMeasurement.runId, sha256: clipboardPasteMeasurement.sha256 } };
    const rows = previous.rows.with(index, row);
    const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(conclusion =>
        [conclusion, rows.filter(item => item.conclusion === conclusion).length]));
    return { ...previous, schemaVersion: 15, rows, totals, ownedCriterionPassPercentage: totals.pass / rows.length * 100,
        predecessor: { publication: predecessorPublication, totals: previous.totals },
        evidencePolicy: 'Rederive the OSC 52 round-trip predecessor and grade the frozen clipboard paste App criterion from its distinct off/on/off raw reads and restoration evidence.',
        clipboardPasteMeasurement };
}

const bytes = value => value.match(/../g).join(' ');
const modeName = enabled => enabled ? 'set' : 'reset';

export function readablePaste(report) {
    const { totals, predecessor, clipboardPasteMeasurement: m } = report;
    const { observed: o } = report.rows.find(row => row.id === id);
    const appUrl = 'https://github.com/benyblack/ntilde/blob/' + m.appRevision;
    return [
        '# Ntilde Modern clipboard paste assessment', '',
        `**${o.conclusion.toUpperCase()} · ${totals.pass} pass, ${totals.fail} fail, ${totals.inconclusive} inconclusive across 69 frozen requirements.**`, '',
        o.reason, '',
        `The disposable Linux profile used the default \`${o.shortcut}\` binding. The App read X11 \`${o.selection}\` from the independent GTK owner. [Shortcut catalog](${appUrl}/src/Ntilde.App/Shell/Shortcuts/ShortcutCatalog.cs#L47) · [App paste route](${appUrl}/src/Ntilde.App/MainWindow.axaml.cs#L668) · [Frozen upstream App probe](https://github.com/beorn/terminfo.dev/blob/3eea0f130d87d64eab33ab342c96e668daab0292/packages/probe-defs/src/extensions.ts#L5368).`, '',
        '| Observation | Retained value |', '| --- | --- |',
        `| Incoming DECRPM 2004 | ${modeName(o.incomingMode)} |`,
        `| Baseline read by a fresh GTK peer | \`${o.baseline}\` (owner PID ${m.baselineOwnerPid}) |`,
        `| XTEST a in the armed raw PTY | \`${bytes(o.keyHex)}\` |`,
        `| Nonce read by a fresh GTK peer | \`${o.nonce}\` (owner PID ${m.nonceOwnerPid}) |`,
        ...o.reads.map(({ phase, rawHex, mode }) => `| ${phase} · DECRPM \`${bytes(mode.replyHex)}\` | \`${bytes(rawHex)}\` |`),
        `| Restored DECRPM 2004 | ${modeName(o.restoredMode)} |`,
        `| Restored selection read by a fresh GTK peer | \`${o.restoredSelection}\` (restoring owner PID ${m.restorationOwnerPid}) |`, '',
        'The private display started with no CLIPBOARD owner. The GTK fixture prepared and verified a synthetic baseline before the input control. A fresh GTK owner restored the exact baseline text. Each paste used a separate armed raw window, verified the same seeded nonce afterward, and retained its full PTY chunks with the XTEST delimiter that closed the read.', '',
        `The [OSC 52 clipboard predecessor](../2026-11-25-modern-osc52-clipboard-results/report.md) recorded ${predecessor.totals.pass} pass, ${predecessor.totals.fail} fail and ${predecessor.totals.inconclusive} inconclusive. [Frozen requirements](../2026-11-15-modern-results/requirements-v1.json) · [All 69 rows](report.json).`, '',
        '## Evidence and reproduction', '',
        `[Owned run](owned-run.zip) \`${m.runId}\` on ${m.platform}. XTEST ${m.xtest.join('.')} delivered input to App window ${m.windowId} on display \`${m.display}\` with root ${m.root}. App PID ${m.appPid} owned pane \`${m.pane.paneId}\` and child PID ${m.workerPid} on \`${m.tty}\`. XRes verified the App and GTK clients on owned Xvfb PID ${m.serverPid}. The child restored termios and exited. All peers, the App and Xvfb exited. The display socket and scratch tree disappeared before the lease released at ${m.leaseReleasedAt}.`, '',
        ...m.attempts.flatMap(attempt => [`[Earlier run](${attempt.archive}) \`${attempt.runId}\`. ${attempt.reason}`, '']),
        `App ${m.appVersion} was built from \`${m.appRevision}\` with .NET SDK ${m.dotnetSdk}. Collector \`${m.scriptsRevision}\` ran with Node ${m.nodeVersion}. The archive retains App publish tree hashes, collector source bytes, exact mode exchanges, raw child chunks, XTEST events, peer readbacks and the cleanup audit. [Manifest](manifest.json).`, '',
        'Rederive the publication offline with Node 24 and Python 3.', '',
        '```sh', 'node scripts/terminfo-modern-results.mjs --check --clipboard-paste', '```', '',
        'A fresh Linux run requires Node 24, Python 3 with PyGObject and GTK4, Xvfb, libX11, libXtst and libXRes. See the [App build prerequisites](../../../../CONTRIBUTING.md#1-prerequisites).', '',
        '```sh', `# From an App checkout at ${m.appRevision}`,
        `scripts/build.sh publish src/Ntilde.App -c Release -r ${m.runtime} --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar`,
        'chmod -R go-w /absolute/private/sidecar', `# From a collector checkout at ${m.scriptsRevision}`,
        `node scripts/terminfo-modern-x11-owned.mjs "$(realpath /absolute/private/sidecar/Ntilde)" ${m.appVersion} --clipboard-paste`, '```', '',
    ].join('\n');
}
