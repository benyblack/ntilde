import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { pinnedJson, requireModern } from './modern-inputs.mjs';
import { verifyX11Run } from './modern-x11-mouse-results.mjs';
import { replayKeyboard } from './rich-x11-kitty-keyboard.mjs';

const directory = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-26-rich-tui/', import.meta.url));
const manifestSha256 = 'df18fe95b651d4e86bc9f7ee1976294f9702fee6b8261819b64a128d59649e2c';

export function verifyRichKittyRun(scratch) {
    return verifyX11Run(scratch, { policy: 'owned-rich-x11-kitty-keyboard-v1',
        profilePrefix: 'X11 Keyboard ', markerPrefix: 'x11-key-',
        extraSources: ['modern-x11-keyboard.mjs', 'rich-x11-kitty-keyboard.mjs'], replay: replayKeyboard });
}

export function withMeasurement(callback) {
    const manifest = pinnedJson(join(directory, 'kitty-manifest.json'), manifestSha256);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-rich-kitty-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'kitty-owned-run.zip'));
        return callback(scratch, manifest);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

export function buildPublication() {
    return withMeasurement((scratch, manifest) => {
        const { run, results, measurement } = verifyRichKittyRun(scratch);
        requireModern(run.runId === manifest.runId && manifest.schemaVersion === 1
            && [manifest.appRevision, manifest.scriptsRevision].every(value => /^[a-f\d]{40}$/.test(value))
            && [run.appFiles.Ntilde, run.appFiles['Ntilde.dll']].every(value => /^[a-f\d]{64}$/.test(value)), 'wrong Kitty archive or App identity');
        return { schemaVersion: 1, ...results, measurement: { manifestSha256, ...manifest, ...measurement,
            nodeVersion: run.nodeVersion, appVersion: run.appVersion, appExecutableSha256: run.appFiles.Ntilde,
            assemblySha256: run.appFiles['Ntilde.dll'], xtest: run.identity.xtestVersion, appPid: run.identity.appPid,
            childPid: run.lineage.child.pid, paneId: run.identity.pane.paneId, tty: run.lineage.child.pty.descriptors[0].path,
            geometry: { rows: run.identity.pane.rows, columns: run.identity.pane.cols } } };
    });
}

const code = value => '\x60' + value + '\x60';
const displayBytes = value => Buffer.from(value, 'hex').toString('ascii').replaceAll('\x1b[', 'CSI ');

function renderReport(report) {
    const { measurement: m, rows } = report;
    return [
        '# Rich TUI Kitty keyboard input', '',
        'XTEST delivered X11 key presses and releases to a disposable Ntilde App on an isolated Linux X11 display. A raw PTY child captured the input produced by the App.', '',
        '| Flag | Mask | X11 action | Child bytes | Owned verdict |', '| --- | ---: | --- | --- | --- |',
        ...rows.map(row => `| ${code(row.id.split('.').at(-1))} | ${row.mask} | ${row.chord} | ${code(displayBytes(row.childHex))} | ${row.effect} |`),
        '', `The flag-off controls delivered plain a as ${code('61')} and Ctrl+I as ${code('09')}. Holding Escape produced one press, ${rows[1].repeats} X11 autorepeats, and one release. The alternate-key chord used an XKB mapping with a/A on the same physical key. The associated-text sample committed a single ASCII scalar. An Escape hold without an observed repeat leaves the event-reporting result inconclusive.`,
        '', 'Each flag was pushed, queried, exercised, popped, and queried again. The report JSON retains the exact child bytes in hex and the mode replies. The archive also retains the raw chunks and mode-specific keyboard delimiter, native key transitions and ownership proof. The five pinned 4.0.0 probes test flag negotiation. The routed-input observations grade these separate App effects.',
        '', `Run ${code(m.runId)} used Ntilde ${code(m.appVersion)} on ${m.platform}, XTEST ${m.xtest.join('.')}, and Node ${m.nodeVersion}. The App source is ${code(m.appRevision)} and the measured collector is ${code(m.scriptsRevision)}. The Xvfb and App processes, private socket, termios state, and lease cleanup were verified. The fixture covers an ordinary local tab and the tested keys on its Linux X11 layout.`,
        '', `Rederive the report with ${code('node scripts/terminfo/rich-x11-kitty-results.mjs --check')}. [Owned archive](kitty-owned-run.zip) · [Manifest](kitty-manifest.json) · [Case list and evidence map](report.md)`,
        '', 'For a fresh Linux arm64 run, install Node 24, Python 3, Xvfb, libXtst, libX11 and libXRes. Build the App at the recorded source revision.',
        '', '~~~sh',
        'scripts/build.sh publish src/Ntilde.App -c Release -r linux-arm64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        'chmod -R go-w /absolute/private/sidecar',
        'node scripts/terminfo-modern-x11-owned.mjs /absolute/private/sidecar/Ntilde 0.14.0 --rich-kitty-keyboard',
        '~~~', '',
    ].join('\n');
}

function publish(check) {
    const report = buildPublication();
    const outputs = { 'kitty-report.json': JSON.stringify(report, null, 2) + '\n', 'kitty-report.md': renderReport(report) };
    for (const [name, content] of Object.entries(outputs)) {
        const path = join(directory, name);
        if (check) requireModern(readFileSync(path, 'utf8').replaceAll('\r\n', '\n') === content, 'published ' + name + ' differs');
        else writeFileSync(path, content);
    }
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    const args = process.argv.slice(2);
    requireModern(args.length === 1 && ['--check', '--write'].includes(args[0]), 'expected --check or --write');
    publish(args[0] === '--check');
}
