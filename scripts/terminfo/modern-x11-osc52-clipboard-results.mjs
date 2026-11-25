import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { digest, pinnedJson, requireModern } from './modern-inputs.mjs';
import { modernRoot } from './modern-results.mjs';
import { verifyAttempts, verifyX11Run } from './modern-x11-mouse-results.mjs';
import { id, replayClipboard } from './modern-x11-osc52-clipboard.mjs';
import { clipboardPeerSteps } from './modern-x11-osc52-write.mjs';
import { buildOsc52WritePublication, publication as writePublication } from './modern-x11-osc52-write-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-25-modern-osc52-clipboard-results';
const manifestSha256 = '5c77bd1edf43e972c1ef72096279f20ad33cee711f17391adc3b52b531d24783';
const criterionSha256 = 'f670914558fac01e4ef7a815bc2b499e0357e448f8cd9adbb06a208b305cce4c';
export const osc52ClipboardPaths = base => ({ json: join(base, publication, 'report.json'), markdown: join(base, publication, 'report.md') });

export const verifyOsc52ClipboardRun = directory => verifyX11Run(directory, {
    policy: 'owned-modern-x11-osc52-clipboard-v1', profilePrefix: 'X11 OSC 52 ', markerPrefix: 'x11-osc52-',
    clipboardWrite: true, replay: replayClipboard, peerSteps: clipboardPeerSteps,
    extraSources: ['modern-x11-keyboard.mjs', 'modern-x11-osc52-clipboard.mjs', 'modern-x11-osc52-write.mjs', 'peer-linux.py', 'frames.py'],
});

function readMeasurement(base) {
    const directory = join(base, publication);
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    verifyAttempts(directory, manifest);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-osc52-clipboard-replay-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const { run, results: [result], measurement } = verifyOsc52ClipboardRun(scratch);
        requireModern(manifest.schemaVersion === 1 && manifest.runId === run.runId
            && [manifest.appRevision, manifest.scriptsRevision].every(revision => /^[a-f\d]{40}$/.test(revision))
            && ['linux-x64', 'linux-arm64'].includes(manifest.runtime), 'wrong OSC 52 round-trip run manifest');
        const [trial] = run.cases;
        return { result, osc52ClipboardMeasurement: { publication, manifestSha256, ...manifest, ...measurement,
            appVersion: run.appVersion, appExecutableSha256: run.appFiles.Ntilde, assemblySha256: run.appFiles['Ntilde.dll'],
            nodeVersion: run.nodeVersion, paneId: run.identity.pane.paneId, display: run.display.name,
            root: run.identity.root, windowId: run.identity.window.id, xtest: run.identity.xtestVersion, serverPid: run.identity.serverPid,
            appPid: run.identity.appPid, workerPid: run.lineage.child.pid, tty: run.lineage.child.pty.descriptors[0].path,
            baselineOwnerPid: trial.seed.after.ownerPid, restorationOwnerPid: trial.restoreSeed.after.ownerPid,
            peerPids: run.cleanup.peers.map(peer => peer.pid), allowOsc52ClipboardWrite: run.settings.AllowOsc52ClipboardWrite } };
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

export function buildOsc52ClipboardPublication(base = modernRoot) {
    const previous = buildOsc52WritePublication(base);
    const { result, osc52ClipboardMeasurement } = readMeasurement(base);
    const index = previous.rows.findIndex(row => row.id === id);
    const target = previous.rows[index];
    requireModern(previous.rows.length === 69 && target?.conclusion === 'inconclusive'
        && digest(target.requirement) === criterionSha256, 'changed frozen OSC 52 round-trip criterion');
    const row = { ...target, conclusion: result.conclusion, blocker: result.conclusion === 'inconclusive' ? result.reason : null,
        observed: result, criterionPath: 'frozen App round trip with XTEST, independently verified X11 CLIPBOARD ownership and raw direct PTY replies',
        evidence: { file: 'observation.json', runId: osc52ClipboardMeasurement.runId, sha256: osc52ClipboardMeasurement.sha256 } };
    const rows = previous.rows.with(index, row);
    const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(conclusion =>
        [conclusion, rows.filter(item => item.conclusion === conclusion).length]));
    return { ...previous, schemaVersion: 14, rows, totals, ownedCriterionPassPercentage: totals.pass / rows.length * 100,
        predecessor: { publication: writePublication, totals: previous.totals },
        evidencePolicy: 'Replay the OSC 52 write predecessor and independently grade the frozen OSC 52 App round trip from the raw query and GTK receipts.',
        osc52ClipboardMeasurement };
}

const bytes = value => value.match(/../g)?.join(' ') ?? '';
const tableText = value => JSON.stringify(value).replaceAll('|', String.raw`\u007c`).replaceAll('`', String.raw`\u0060`);

export function readableOsc52Clipboard(report) {
    const { totals, predecessor, osc52ClipboardMeasurement: m } = report;
    const { observed: o } = report.rows.find(row => row.id === id);
    const read = o.read;
    return [
        '# Ntilde Modern OSC 52 clipboard assessment', '',
        `**${o.conclusion.toUpperCase()} · ${totals.pass} pass, ${totals.fail} fail, ${totals.inconclusive} inconclusive across 69 frozen requirements.**`, '',
        '| Observation | Retained value |', '| --- | --- |',
        `| Independent baseline read | \`${o.baseline}\` (GTK owner PID ${m.baselineOwnerPid}) |`,
        `| XTEST a in the armed direct PTY | \`${bytes(o.keyHex)}\` |`,
        `| OSC 52 c write | \`${bytes(o.osc52Hex)}\` |`,
        ...o.reads.map(({ text, ownerPid }) => `| Independent post-write CLIPBOARD read | \`${tableText(text)}\` (owner PID ${ownerPid}) |`),
        ...(read.attempts ? [
            `| Second nonce verified by a fresh GTK reader | \`${read.nonce}\` (GTK owner PID ${read.ownerPid}) |`,
            `| OSC 52 c query | \`${bytes(read.queryHex)}\` |`,
            ...read.attempts.map((attempt, index) => `| Raw PTY query capture ${index + 1}, including CPR | \`${bytes(attempt.rawHex)}\` |`),
            `| Settled OSC 52 c reply | ${read.attempts.at(-1).replyHex ? '\`' + bytes(read.attempts.at(-1).replyHex) + '\`' : read.outcome} |`,
        ] : []),
        `| Independent restoration read | \`${o.restored}\` (GTK owner PID ${m.restorationOwnerPid}) |`, '',
        `**Write.** ${o.write.reason}`, '',
        `**Combined criterion.** ${o.reason}`, '',
        'The disposable profile used `AllowOsc52ClipboardWrite=true`. Ntilde’s OSC 52 read-denial policy remains in effect.', '',
        `The [OSC 52 write predecessor](../2026-11-25-modern-osc52-write-results/report.md) recorded ${predecessor.totals.pass} pass, ${predecessor.totals.fail} fail and ${predecessor.totals.inconclusive} inconclusive. [Frozen requirements](../2026-11-15-modern-results/requirements-v1.json) · [All 69 rows](report.json).`, '',
        '## Evidence and reproduction', '',
        `[Owned run](owned-run.zip) \`${m.runId}\` on ${m.platform}. XTEST ${m.xtest.join('.')} delivered input to App window ${m.windowId} on display \`${m.display}\` with root ${m.root}. App PID ${m.appPid} owned pane \`${m.paneId}\` and child PID ${m.workerPid} on \`${m.tty}\`. XRes verified the App and all GTK peers on the owned Xvfb server. The child restored termios and exited. The GTK peers, App and Xvfb exited, and the display socket and scratch tree disappeared before the lease released at ${m.leaseReleasedAt}.`, '',
        ...m.attempts.flatMap(attempt => [`[Earlier run](${attempt.archive}) \`${attempt.runId}\`. ${attempt.reason}`, '']),
        `App ${m.appVersion} was built from source \`${m.appRevision}\` with .NET SDK ${m.dotnetSdk}. The archive hashes the App publish tree. Collector \`${m.scriptsRevision}\` ran under Node ${m.nodeVersion} and retained its source bytes, PTY requests and complete replies, X11 ownership checks, settings and cleanup. [Manifest](manifest.json).`, '',
        'The frozen App path retries a canonical reply carrying the earlier OSC 52 write nonce at 25 ms intervals within the default one-second write deadline. The collector uses XTEST `a` with verified child input as the activation event. The query captures a cursor-position control in the same PTY window, and a fresh GTK reader confirms the second nonce before the first query. [Frozen upstream probe](https://github.com/beorn/terminfo.dev/blob/3eea0f130d87d64eab33ab342c96e668daab0292/packages/probe-defs/src/extensions.ts#L4597).', '',
        'Rederive the publication offline with Node 24 and Python 3.', '',
        '\`\`\`sh', 'node scripts/terminfo-modern-results.mjs --check --osc52-clipboard', '\`\`\`', '',
        'A fresh Linux run requires Node 24, Python 3 with PyGObject and GTK4, Xvfb, libX11, libXtst and libXRes. See the [App build prerequisites](../../../../CONTRIBUTING.md#1-prerequisites) for .NET and Rust.', '',
        '\`\`\`sh', `# From an App checkout at ${m.appRevision}`,
        `scripts/build.sh publish src/Ntilde.App -c Release -r ${m.runtime} --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar`,
        'chmod -R go-w /absolute/private/sidecar', `# From a collector checkout at ${m.scriptsRevision}`,
        `node scripts/terminfo-modern-x11-owned.mjs "$(realpath /absolute/private/sidecar/Ntilde)" ${m.appVersion} --osc52-clipboard`, '\`\`\`', '',
    ].join('\n');
}
