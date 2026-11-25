import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { digest, pinnedJson, requireModern } from './modern-inputs.mjs';
import { modernRoot } from './modern-results.mjs';
import { id, replayFocus } from './modern-x11-focus.mjs';
import { buildKeypadPublication, publication as keypadPublication } from './modern-x11-keypad-results.mjs';
import { verifyAttempts, verifyX11Run } from './modern-x11-mouse-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-25-modern-focus-tracking-results';
const manifestSha256 = '72aeeaa3574e17c841b911b58f97f70c8937d5a4590956cdfae3be7873bfcf07';
const criterionSha256 = '046859f6d5b20aa13527e0a5008477da632d3a0efd37a8e8989594fd53f9977f';
export const focusPaths = base => ({ json: join(base, publication, 'report.json'), markdown: join(base, publication, 'report.md') });
export const verifyFocusRun = directory => verifyX11Run(directory, { policy: 'owned-modern-x11-focus-v1',
    profilePrefix: 'X11 Keyboard ', markerPrefix: 'x11-key-', extraSources: ['modern-x11-keyboard.mjs', 'modern-x11-focus.mjs'], replay: replayFocus });

function readMeasurement(base) {
    const directory = join(base, publication);
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    verifyAttempts(directory, manifest);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-focus-replay-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const { run, results: [result], measurement } = verifyFocusRun(scratch);
        requireModern(manifest.runId === run.runId && manifest.schemaVersion === 1
            && [manifest.appRevision, manifest.scriptsRevision].every(revision => /^[a-f\d]{40}$/.test(revision)), 'wrong X11 focus manifest');
        return { result, focusMeasurement: { publication, manifestSha256, ...manifest, ...measurement,
            appVersion: run.appVersion, appExecutableSha256: run.appFiles.Ntilde, assemblySha256: run.appFiles['Ntilde.dll'],
            nodeVersion: run.nodeVersion, paneId: run.identity.pane.paneId, display: run.display.name,
            root: run.identity.root, windowId: run.identity.window.id, xtest: run.identity.xtestVersion,
            appPid: run.identity.appPid, childPid: run.lineage.child.pid, tty: run.lineage.child.pty.descriptors[0].path } };
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

export function buildFocusPublication(base = modernRoot) {
    const previous = buildKeypadPublication(base);
    const { result, focusMeasurement } = readMeasurement(base);
    const index = previous.rows.findIndex(row => row.id === id);
    const target = previous.rows[index];
    requireModern(previous.rows.length === 69 && target?.conclusion === 'inconclusive'
        && digest(target.requirement) === criterionSha256, 'changed frozen App focus criterion');
    const row = { ...target, conclusion: result.conclusion,
        blocker: result.conclusion === 'inconclusive' ? result.reason : null, observed: result,
        criterionPath: 'frozen App path with owned X11 focus readbacks and XTEST delivery under DECSET and DECRST 1004',
        evidence: { file: 'observation.json', runId: focusMeasurement.runId, sha256: focusMeasurement.sha256 } };
    const totals = { ...previous.totals };
    totals.inconclusive--;
    totals[row.conclusion]++;
    return { ...previous, schemaVersion: 12, rows: previous.rows.with(index, row), totals,
        ownedCriterionPassPercentage: totals.pass / previous.rows.length * 100,
        predecessor: { publication: keypadPublication, totals: previous.totals },
        evidencePolicy: 'Replay the application keypad predecessor and grade the frozen focus-tracking App row from the owned X11 focus and PTY observations.',
        focusMeasurement };
}

const bytes = value => value?.match(/../g)?.join(' ') || 'none';
const roundTrip = ({ out, back }) => `${back.target} → ${out.observed} → ${back.observed}`;

export function readableFocus(report) {
    const { totals, focusMeasurement: m } = report;
    const { observed: o } = report.rows.find(row => row.id === id);
    return [
        '# Ntilde Modern focus-tracking assessment', '',
        `**${o.conclusion.toUpperCase()} · ${totals.pass} pass, ${totals.fail} fail, ${totals.inconclusive} inconclusive across 69 frozen requirements.**`, '',
        '| Phase | X11 focus | Raw PTY bytes through XTEST a | DECRPM reply |',
        '| --- | --- | --- | --- |',
        `| DECSET 1004 | ${roundTrip(o.focus.on)} | \`${bytes(o.modeOnHex)}\` | \`${bytes(o.decrpm.on[0])}\` |`,
        `| DECRST 1004 | ${roundTrip(o.focus.off)} | \`${bytes(o.modeOffHex)}\` | \`${bytes(o.decrpm.off[0])}\` |`, '',
        o.reason,
        `Enable-time focus report \`${bytes(o.enableTimeHex)}\`. The raw reader started before the mode write and saved an independent checkpoint before any X11 focus change. The archive retains each read and timestamp, including the trailing XTEST delimiters, the ordered X11 focus readbacks and the restoration exchange. DECRPM replies are supporting detail.`, '',
        'The [frozen App requirement](../2026-11-15-modern-results/requirements-v1.json) grades the raw mode-on and mode-off reads. The [application keypad predecessor](../2026-11-25-modern-application-keypad-results/report.md) recorded 64 pass, 0 fail and 5 inconclusive. [All 69 rows](report.json).', '',
        '## Evidence and reproduction', '',
        `[Owned run](owned-run.zip) \`${m.runId}\` on ${m.platform}. XTEST ${m.xtest.join('.')} delivered input to App window ${m.windowId} on display \`${m.display}\` with root ${m.root}. App PID ${m.appPid} owned pane \`${m.paneId}\` and child PID ${m.childPid} on \`${m.tty}\`. The child restored termios and exited. The App and Xvfb exited, the socket and scratch tree disappeared, and the lease released at ${m.leaseReleasedAt}.`, '',
        ...m.attempts.flatMap(attempt => [`[Earlier run](${attempt.archive}) \`${attempt.runId}\`. ${attempt.reason}`, '']),
        `App ${m.appVersion} was built from source archive \`${m.appRevision}\` with .NET SDK ${m.dotnetSdk}. The archive hashes the App publish tree. Collector \`${m.scriptsRevision}\` ran under Node ${m.nodeVersion} and retained the launcher and measured scripts. [Manifest](manifest.json).`, '',
        'Rederive the publication offline with Node 24 and Python 3.', '',
        '```sh', 'node scripts/terminfo-modern-results.mjs --check --focus-tracking', '```', '',
        'A fresh Linux x64 measurement requires Node 24, Python 3, Xvfb, libX11, libXtst and libXRes. See the [App build prerequisites](../../../../CONTRIBUTING.md#1-prerequisites) for .NET and Rust.', '',
        '```sh', `# From an App checkout at ${m.appRevision}`,
        'scripts/build.sh publish src/Ntilde.App -c Release -r linux-x64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        'chmod -R go-w /absolute/private/sidecar', `# From a collector checkout at ${m.scriptsRevision}`,
        `node scripts/terminfo-modern-x11-owned.mjs "$(realpath /absolute/private/sidecar/Ntilde)" ${m.appVersion} --focus-tracking`, '```', '',
    ].join('\n');
}
