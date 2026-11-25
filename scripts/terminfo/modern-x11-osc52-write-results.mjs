import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { digest, pinnedJson, requireModern } from './modern-inputs.mjs';
import { modernRoot } from './modern-results.mjs';
import { buildFocusPublication, publication as focusPublication } from './modern-x11-focus-results.mjs';
import { verifyAttempts, verifyX11Run } from './modern-x11-mouse-results.mjs';
import { id, replayClipboard } from './modern-x11-osc52-write.mjs';

export const publication = 'docs/qa/terminfo/2026-11-25-modern-osc52-write-results';
const manifestSha256 = 'c46123aaca756f7459c869d469ae0dbc4cdc97aad88d08eda2e83696c41ddc0a';
const criterionSha256 = 'f8da38dda3400b796ec83a9d44d423641086197ea7a18e8066baeb1c556525db';
export const osc52WritePaths = base => ({ json: join(base, publication, 'report.json'), markdown: join(base, publication, 'report.md') });
export const verifyOsc52WriteRun = directory => verifyX11Run(directory, {
    policy: 'owned-modern-x11-osc52-write-v1', profilePrefix: 'X11 OSC 52 ', markerPrefix: 'x11-osc52-',
    clipboardWrite: true, replay: replayClipboard,
    extraSources: ['modern-x11-keyboard.mjs', 'modern-x11-osc52-write.mjs', 'peer-linux.py', 'frames.py'],
});

function readMeasurement(base) {
    const directory = join(base, publication);
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    verifyAttempts(directory, manifest);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-osc52-replay-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const { run, results: [result], measurement } = verifyOsc52WriteRun(scratch);
        requireModern(manifest.runId === run.runId && manifest.schemaVersion === 1
            && [manifest.appRevision, manifest.scriptsRevision].every(revision => /^[a-f\d]{40}$/.test(revision))
            && ['linux-x64', 'linux-arm64'].includes(manifest.runtime), 'wrong OSC 52 run manifest');
        const [trial] = run.cases;
        return { result, osc52WriteMeasurement: { publication, manifestSha256, ...manifest, ...measurement,
            appVersion: run.appVersion, appExecutableSha256: run.appFiles.Ntilde, assemblySha256: run.appFiles['Ntilde.dll'],
            nodeVersion: run.nodeVersion, paneId: run.identity.pane.paneId, display: run.display.name,
            root: run.identity.root, windowId: run.identity.window.id, xtest: run.identity.xtestVersion,
            serverPid: run.identity.serverPid, appPid: run.identity.appPid, workerPid: run.lineage.child.pid,
            tty: run.lineage.child.pty.descriptors[0].path, peerPids: run.cleanup.peers.map(peer => peer.pid),
            baselineOwnerPid: trial.seed.after.ownerPid, restorationOwnerPid: trial.restoreSeed.after.ownerPid,
            allowOsc52ClipboardWrite: run.settings.AllowOsc52ClipboardWrite } };
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

export function buildOsc52WritePublication(base = modernRoot) {
    const previous = buildFocusPublication(base);
    const { result, osc52WriteMeasurement } = readMeasurement(base);
    const index = previous.rows.findIndex(row => row.id === id);
    const target = previous.rows[index];
    requireModern(previous.rows.length === 69 && target?.conclusion === 'inconclusive'
        && digest(target.requirement) === criterionSha256, 'changed frozen OSC 52 write criterion');
    const row = { ...target, conclusion: result.conclusion,
        blocker: result.conclusion === 'inconclusive' ? result.reason : null, observed: result,
        criterionPath: 'frozen App path with XTEST delivery and independent GTK reads of the owned X11 CLIPBOARD',
        evidence: { file: 'observation.json', runId: osc52WriteMeasurement.runId, sha256: osc52WriteMeasurement.sha256 } };
    const rows = previous.rows.with(index, row);
    const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(conclusion =>
        [conclusion, rows.filter(item => item.conclusion === conclusion).length]));
    return { ...previous, schemaVersion: 13, rows, totals,
        ownedCriterionPassPercentage: totals.pass / rows.length * 100,
        predecessor: { publication: focusPublication, totals: previous.totals },
        evidencePolicy: 'Replay the focus-tracking predecessor and grade the frozen OSC 52 App write row from the direct PTY, XTEST and independent GTK clipboard receipts.',
        osc52WriteMeasurement };
}

const bytes = value => value.match(/../g).join(' ');
const tableText = value => JSON.stringify(value).replaceAll('|', '\\u007c').replaceAll('`', '\\u0060');

export function readableOsc52Write(report) {
    const { totals, predecessor, osc52WriteMeasurement: m } = report;
    const { observed: o } = report.rows.find(row => row.id === id);
    return [
        '# Ntilde Modern OSC 52 write assessment', '',
        `**${o.conclusion.toUpperCase()} · ${totals.pass} pass, ${totals.fail} fail, ${totals.inconclusive} inconclusive across 69 frozen requirements.**`, '',
        '| Observation | Retained value |', '| --- | --- |',
        `| Independent baseline read | \`${o.baseline}\` (GTK owner PID ${m.baselineOwnerPid}) |`,
        `| XTEST a in the armed direct PTY | \`${bytes(o.keyHex)}\` |`,
        `| Exact OSC 52 c write | \`${bytes(o.osc52Hex)}\` |`,
        `| Cursor-position reply after the write | \`${bytes(o.cprHex)}\` |`,
        ...o.reads.map(({ text, ownerPid }) => `| Independent post-write CLIPBOARD read | \`${tableText(text)}\` (owner PID ${ownerPid}) |`),
        `| Independent restoration read | \`${o.restored}\` (GTK owner PID ${m.restorationOwnerPid}) |`, '',
        o.reason,
        'The disposable profile used `AllowOsc52ClipboardWrite=true` and the production native clipboard path. The App write setting defaults to enabled. This case measures App output to X11 CLIPBOARD. The OSC 52 round-trip and native paste rows retain their prior verdicts.', '',
        `The [focus-tracking predecessor](../2026-11-25-modern-focus-tracking-results/report.md) recorded ${predecessor.totals.pass} pass, ${predecessor.totals.fail} fail and ${predecessor.totals.inconclusive} inconclusive. [Frozen requirements](../2026-11-15-modern-results/requirements-v1.json) · [All 69 rows](report.json).`, '',
        '## Evidence and reproduction', '',
        `[Owned run](owned-run.zip) \`${m.runId}\` on ${m.platform}. XTEST ${m.xtest.join('.')} delivered input to X11 window ${m.windowId} on display \`${m.display}\` with root ${m.root}. App PID ${m.appPid} owned pane \`${m.paneId}\` and child PID ${m.workerPid} on \`${m.tty}\`. XRes verified the App and GTK clients on the owned Xvfb server. The child restored termios and exited. The GTK peers, App and Xvfb exited, and the display socket and scratch tree disappeared before the lease released at ${m.leaseReleasedAt}.`, '',
        ...m.attempts.flatMap(attempt => [`[Earlier run](${attempt.archive}) \`${attempt.runId}\`. ${attempt.reason}`, '']),
        `App ${m.appVersion} was built from source \`${m.appRevision}\` with .NET SDK ${m.dotnetSdk}. The archive hashes the App publish tree. Collector \`${m.scriptsRevision}\` ran under Node ${m.nodeVersion} and retained the measured sources, settings, process and selection-owner checks, PTY bytes, clipboard readings and cleanup. [Manifest](manifest.json).`, '',
        'The batch applies the frozen App verdict rules with an XTEST `a` whose raw byte reached the child, as required for this collection. The upstream probe uses a silent modifier as its activation event. Both use a same-PTY cursor-position barrier and independent clipboard readback.', '',
        'Rederive the publication offline with Node 24 and Python 3.', '',
        '```sh', 'node scripts/terminfo-modern-results.mjs --check --osc52-write', '```', '',
        'A fresh Linux run requires Node 24, Python 3 with PyGObject and GTK4, Xvfb, libX11, libXtst and libXRes. The collector starts its own Xvfb and admits the GTK text peers through XRes before allowing clipboard operations. See the [App build prerequisites](../../../../CONTRIBUTING.md#1-prerequisites) for .NET and Rust.', '',
        '```sh', `# From an App checkout at ${m.appRevision}`,
        `scripts/build.sh publish src/Ntilde.App -c Release -r ${m.runtime} --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar`,
        'chmod -R go-w /absolute/private/sidecar', `# From a collector checkout at ${m.scriptsRevision}`,
        `node scripts/terminfo-modern-x11-owned.mjs "$(realpath /absolute/private/sidecar/Ntilde)" ${m.appVersion} --osc52-write`, '```', '',
    ].join('\n');
}
