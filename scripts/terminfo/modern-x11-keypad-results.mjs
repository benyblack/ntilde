import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { digest, pinnedJson, requireModern } from './modern-inputs.mjs';
import { modernRoot } from './modern-results.mjs';
import { buildKeyboardPublication, publication as keyboardPublication } from './modern-x11-keyboard-results.mjs';
import { id, replayKeyboard } from './modern-x11-keypad.mjs';
import { verifyAttempts, verifyX11Run } from './modern-x11-mouse-results.mjs';

export const publication = 'docs/qa/terminfo/2026-11-25-modern-application-keypad-results';
const manifestSha256 = '341b77f73ed6b4c30995684a0a9cb00360a8c05e161aeef571f4c0dcf8988e85';
const criterionSha256 = 'fb806a22a93e926e26ff1e5c9616ab9856bfd23d59329d13918c1b408575e8a1';
export const keypadPaths = base => ({ json: join(base, publication, 'report.json'), markdown: join(base, publication, 'report.md') });
export const verifyKeypadRun = directory => verifyX11Run(directory, { policy: 'owned-modern-x11-keypad-v1',
    profilePrefix: 'X11 Keyboard ', markerPrefix: 'x11-key-', extraSources: ['modern-x11-keyboard.mjs', 'modern-x11-keypad.mjs'], replay: replayKeyboard });

export function buildKeypadPublication(base = modernRoot) {
    const previous = buildKeyboardPublication(base);
    const directory = join(base, publication);
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    verifyAttempts(directory, manifest);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-keypad-replay-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const { run, results: [result], measurement } = verifyKeypadRun(scratch);
        requireModern(manifest.runId === run.runId && manifest.schemaVersion === 1
            && [manifest.appRevision, manifest.scriptsRevision].every(value => /^[a-f\d]{40}$/.test(value)), 'wrong X11 keypad manifest');
        const index = previous.rows.findIndex(row => row.id === id);
        const target = previous.rows[index];
        requireModern(previous.rows.length === 69 && target?.conclusion === 'inconclusive'
            && digest(target.requirement) === criterionSha256, 'changed frozen App keypad criterion');
        const rows = previous.rows.with(index, { ...target, conclusion: result.conclusion,
            blocker: result.conclusion === 'inconclusive' ? result.reason : null, observed: result,
            criterionPath: 'frozen App path with plain-a delivery and the same XTEST KP_Add under DECKPAM and DECKPNM',
            evidence: { file: 'observation.json', runId: run.runId, sha256: measurement.sha256 } });
        const totals = Object.fromEntries(['pass', 'fail', 'inconclusive'].map(value => [value, rows.filter(row => row.conclusion === value).length]));
        return { ...previous, schemaVersion: 11, rows, totals, ownedCriterionPassPercentage: totals.pass / rows.length * 100,
            predecessor: { publication: keyboardPublication, totals: previous.totals },
            evidencePolicy: 'Replay the modifyOtherKeys predecessor and grade one frozen App row from an owned XTEST keypad run.',
            keypadMeasurement: { publication, manifestSha256, ...manifest, ...measurement, appVersion: run.appVersion,
                appExecutableSha256: run.appFiles.Ntilde, assemblySha256: run.appFiles['Ntilde.dll'], nodeVersion: run.nodeVersion,
                paneId: run.identity.pane.paneId, display: run.display.name, xtest: run.identity.xtestVersion, windowId: run.identity.window.id,
                appPid: run.identity.appPid, childPid: run.lineage.child.pid, tty: run.lineage.child.pty.descriptors[0].path } };
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

const bytes = value => value === null ? 'no trial' : value.match(/../g)?.join(' ') ?? 'silence';
const keysym = value => '0x' + value.toString(16);

export function readableKeypad(report) {
    const { totals, keypadMeasurement: m } = report;
    const { observed } = report.rows.find(row => row.id === id);
    const { mapping } = observed;
    const mappingText = mapping
        ? `X11 resolved KP_Add (${keysym(mapping.keysym)}) to keycode ${mapping.code} with symbol slots ${mapping.levels.map(keysym).join(', ')}. XKB returned KP_Add with and without the NumLock mask ${keysym(mapping.numLockMask)}. Both phases used the same unmodified press and release. `
        : '';
    return [
        '# Ntilde Modern application keypad assessment', '',
        `**${observed.conclusion.toUpperCase()} · ${totals.pass} pass, ${totals.fail} fail, ${totals.inconclusive} inconclusive across 69 frozen requirements.**`, '',
        '| Phase | XTEST key | Raw child bytes |', '| --- | --- | --- |',
        `| Delivery control | a | \`${bytes(observed.controlHex)}\` |`,
        `| DECKPAM (\`1b 3d\`) | KP_Add | \`${bytes(observed.applicationHex)}\` |`,
        `| DECKPNM (\`1b 3e\`) | KP_Add | \`${bytes(observed.numericHex)}\` |`, '',
        observed.reason + ' ' + mappingText + 'Each phase armed a separate raw PTY window before injecting the key. The archive retains the complete stream with its phase-specific trailing keyboard delimiter.', '',
        'The child verified inactive DECCKM, alternate screen, Kitty keyboard flags and modifyOtherKeys before the control and after each mode write. DECNKM reported numeric mode at entry and after the final DECKPNM. XQueryKeymap and the X11 modifier mask verified key release, and the child restored and read back its original termios settings.', '',
        'The [frozen App requirement](../2026-11-15-modern-results/requirements-v1.json) grades the OS-delivered bytes. The [older generated keypad evidence](../2026-11-11-routed-input-qualification.md) retains its original verdicts. The [predecessor report](../2026-11-24-modern-modify-other-keys-results/report.md) recorded 63 pass, 0 fail and 6 inconclusive. [All 69 rows](report.json).', '',
        '## Evidence and reproduction', '',
        `[Owned run](owned-run.zip) \`${m.runId}\` on ${m.platform}. XTEST ${m.xtest.join('.')} targeted window ${m.windowId} on display \`${m.display}\`. App PID ${m.appPid} owned pane \`${m.paneId}\` and child PID ${m.childPid} on \`${m.tty}\`. The worker exited, the App and Xvfb were reaped, the socket and scratch tree disappeared, and the lease released at ${m.leaseReleasedAt}.`, '',
        `[Initial attempt](${m.attempts[0].archive}) \`${m.attempts[0].runId}\` stopped before KP_Add injection. The original mapping check rejected the X11 table’s Ctrl+Alt video-mode symbol. The revised check verifies the effective XKB symbol at the actual NumLock mask. The first attempt also reset the mode and released its lease.`, '',
        `App ${m.appVersion} was built from a source archive at \`${m.appRevision}\` with .NET SDK ${m.dotnetSdk}. The archive hashes the complete publish tree. Collector \`${m.scriptsRevision}\` ran under Node ${m.nodeVersion} and retained the launcher and its measured source files. [Manifest](manifest.json).`, '',
        'Rederive the publication offline using Node 24 and Python 3.', '',
        '```sh', 'node scripts/terminfo-modern-results.mjs --check --application-keypad', '```', '',
        'For a fresh measurement, install Node 24, Python 3, Xvfb, libXtst, libX11 and libXRes on Linux x64. Building the App requires the pinned .NET SDK and Rust toolchain (cargo and rustc). See the [App build prerequisites](../../../../CONTRIBUTING.md#1-prerequisites). The launcher requires the canonical path to the private executable, with every symlink component resolved.', '',
        '```sh', `# From an App checkout at ${m.appRevision}`,
        'scripts/build.sh publish src/Ntilde.App -c Release -r linux-x64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        'chmod -R go-w /absolute/private/sidecar', `# From a collector checkout at ${m.scriptsRevision}`,
        `node scripts/terminfo-modern-x11-owned.mjs "$(realpath /absolute/private/sidecar/Ntilde)" ${m.appVersion} --application-keypad`, '```', '',
    ].join('\n');
}
