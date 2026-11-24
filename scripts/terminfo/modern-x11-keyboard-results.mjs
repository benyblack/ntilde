import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { digest, pinnedJson, requireModern } from './modern-inputs.mjs';
import { modernRoot } from './modern-results.mjs';
import { id, replayKeyboard } from './modern-x11-keyboard.mjs';
import { buildModernX11MousePublication, verifyX11Run, x11MousePublication } from './modern-x11-mouse-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-24-modern-modify-other-keys-results';
const manifestSha256 = '514852fbac526cdc75c9ee84f750d51956dc34c38792648054584603ea593d73';
export const keyboardPaths = base => ({ json: join(base, publication, 'report.json'), markdown: join(base, publication, 'report.md') });
export const verifyKeyboardRun = directory => verifyX11Run(directory, { policy: 'owned-modern-x11-keyboard-v1',
    profilePrefix: 'X11 Keyboard ', markerPrefix: 'x11-key-', extraSources: ['modern-x11-keyboard.mjs'], replay: replayKeyboard });

export function buildKeyboardPublication(base = modernRoot) {
    const previous = buildModernX11MousePublication(base);
    const directory = join(base, publication);
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-keyboard-replay-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const { run, results: [result], measurement } = verifyKeyboardRun(scratch);
        requireModern(manifest.runId === run.runId && manifest.schemaVersion === 1
            && [manifest.appRevision, manifest.scriptsRevision].every(value => /^[a-f\d]{40}$/.test(value)), 'wrong X11 keyboard manifest');
        const index = previous.rows.findIndex(row => row.id === id);
        const row = previous.rows[index];
        requireModern(row?.conclusion === 'inconclusive' && digest(row.requirement) === '82a5e34a64c3744af1a7206ce6d70a209e0a9fb0621d4d9a4dc494445e26a560',
            'changed frozen modifyOtherKeys criterion');
        const rows = previous.rows.with(index, { ...row, conclusion: result.conclusion,
            blocker: result.conclusion === 'inconclusive' ? result.reason : null, observed: result,
            criterionPath: 'frozen App path with an XTEST plain-a control and Ctrl+I on an owned Linux X11 pane',
            evidence: { file: 'observation.json', runId: run.runId, sha256: measurement.sha256 } });
        const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(value => [value, rows.filter(item => item.conclusion === value).length]));
        return { ...previous, schemaVersion: 10, rows, totals, ownedCriterionPassPercentage: totals.pass / rows.length * 100,
            predecessor: { publication: x11MousePublication, totals: previous.totals },
            evidencePolicy: 'Replay the X11 mouse predecessor and apply the frozen keyboard criterion to a fresh owned XTEST run.',
            keyboardMeasurement: { publication, manifestSha256, ...manifest, ...measurement, appVersion: run.appVersion,
                appExecutableSha256: run.appFiles.Ntilde, assemblySha256: run.appFiles['Ntilde.dll'],
                nodeVersion: run.nodeVersion, paneId: run.identity.pane.paneId, display: run.display.name, xtest: run.identity.xtestVersion,
                windowId: run.identity.window.id, appPid: run.identity.appPid, childPid: run.lineage.child.pid, tty: run.lineage.child.pty.descriptors[0].path } };
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

const bytes = value => value === null ? 'no trial' : value.match(/../g)?.join(' ') ?? 'silence';
export function readableKeyboard(report) {
    const { totals, keyboardMeasurement: m } = report;
    const { observed } = report.rows.find(row => row.id === id);
    return [
        '# Ntilde Modern modifyOtherKeys assessment', '',
        `**${observed.conclusion.toUpperCase()} · ${totals.pass} pass, ${totals.fail} fail, ${totals.inconclusive} inconclusive across 69 frozen requirements.**`, '',
        '| External input under level 2 | Raw child bytes |', '| --- | --- |',
        `| Plain a | \`${bytes(observed.controlHex)}\` |`, `| Ctrl+I | \`${bytes(observed.targetHex)}\` |`, '',
        observed.reason + ' Each row used a separate raw PTY window armed before the XTEST chord. The archive includes the complete stream, including each window’s distinct trailing keyboard delimiter.', '',
        'The child queried DECCKM, DECNKM, alternate screen, Kitty keyboard flags and modifyOtherKeys before enabling level 2. The collector required all five inactive. It set level 2 with `CSI > 4 ; 2 m`, read back level 2, verified plain-a delivery, and then injected Ctrl+I. After `CSI > 4 ; 0 m`, the child read back its incoming modes and restored its original termios settings. XQueryKeymap and the X11 modifier mask verified the key releases.', '',
        'The [frozen requirement](../2026-11-15-modern-results/requirements-v1.json) accepts `CSI 27;5;105~` or `CSI 105;5u`. The [predecessor report](../2026-11-24-modern-x11-mouse-results/report.md) recorded 62 pass, 0 fail and 7 inconclusive. [All 69 rows](report.json).', '',
        '## Evidence and reproduction', '',
        `[Owned run](owned-run.zip) \`${m.runId}\` on ${m.platform}. XTEST ${m.xtest.join('.')} delivered both chords to window ${m.windowId} on display \`${m.display}\`. App PID ${m.appPid} owned pane \`${m.paneId}\` and child PID ${m.childPid} on \`${m.tty}\`. The worker exited, the App and Xvfb were reaped, the socket and scratch tree disappeared, and the lease released at ${m.leaseReleasedAt}.`, '',
        `App ${m.appVersion} was built from a source archive at \`${m.appRevision}\` with .NET SDK ${m.dotnetSdk}. The archive hashes the complete publish tree. Collector \`${m.scriptsRevision}\` ran under Node ${m.nodeVersion} and retained the launcher and its measured source files. [Manifest](manifest.json).`, '',
        'Rederive the publication offline using Node 24 and Python 3.', '',
        '```sh', 'node scripts/terminfo-modern-results.mjs --check --modify-other-keys', '```', '',
        'For a fresh measurement, use Linux x64 with Xvfb, libXtst, libX11, libXRes and Python 3. Use an absolute private sidecar directory.', '',
        '```sh', `# From an App checkout at ${m.appRevision}`,
        'scripts/build.sh publish src/Ntilde.App -c Release -r linux-x64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        'chmod -R go-w /absolute/private/sidecar', `# From a collector checkout at ${m.scriptsRevision}`,
        `node scripts/terminfo-modern-x11-owned.mjs /absolute/private/sidecar/Ntilde ${m.appVersion} --modify-other-keys`, '```', '',
    ].join('\n');
}
