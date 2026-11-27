import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { gunzipSync } from 'node:zlib';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { digest, parseJson, pinnedJson, requireModern, snapshotFiles } from './modern-inputs.mjs';
import { verifyX11Run } from './modern-x11-mouse-results.mjs';
import { caseId, replayClicks } from './rich-x11-kitty-clicks.mjs';

const root = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
const directory = join(root, '2026-11-27-rich-kitty-clicks');
const manifestSha256 = '36d81e00dc818c430018bf69f1dc2211e5e73c64ef5e922b7687761ff33f134b';
const bundleSha256 = 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6';
const scope = 'XTEST pointer events through the production App view to one direct-local child PTY.';

function pinnedStatus() {
    const bundle = gunzipSync(readFileSync(join(root, '2026-11-16-shell-integration-aggregates', 'terminfo-dev-4.0.0.bundle.mjs.gz')));
    const baseline = parseJson(join(root, '2026-11-09-stock-baseline.json'));
    const rich = parseJson(join(root, '2026-11-26-rich-tui', 'requirements-v1.json'));
    const catalogue = join(root, rich.catalogue.path);
    requireModern(digest(bundle) === bundleSha256 && baseline.source.pinnedBundleSha256 === bundleSha256
        && digest(readFileSync(catalogue)) === rich.catalogue.sha256
        && rich.rows.filter(row => row.id === caseId).length === 1, 'unexpected pinned Kitty click sources');
    const criterion = JSON.parse(snapshotFiles(catalogue)['features.json'])[caseId]?.probe;
    requireModern(criterion === 'Not measured: enable shell-integration click events, perform a click, and record the resulting SGR mouse event.'
        && bundle.includes('extensions.kitty-keyboard.report-text')
        && !bundle.includes('kitty-click-events') && !bundle.includes('click_events')
        && !Object.hasOwn(baseline.outcomes, caseId) && !Object.hasOwn(baseline.ungradedDiagnostics ?? {}, caseId),
    'Kitty click registration or stock grade differs from the frozen assessment');
    return { package: 'terminfo.dev@4.0.0', bundleSha256, verdict: 'unprobed', stock: null, criterion };
}

export function verifyRichKittyClickRun(scratch) {
    return verifyX11Run(scratch, { policy: 'owned-rich-x11-kitty-clicks-v1',
        profilePrefix: 'X11 Kitty Prompt ', markerPrefix: 'x11-prompt-',
        extraSources: ['modern-x11-keyboard.mjs', 'rich-x11-kitty-clicks.mjs'], replay: replayClicks });
}

export function withMeasurement(callback) {
    const manifest = pinnedJson(join(directory, 'manifest.json'), manifestSha256);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-rich-kitty-clicks-'));
    try {
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        return callback(scratch, manifest);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
}

function hostCleanup(scratch, measurement, manifest) {
    const audit = parseJson(join(scratch, 'host-desktop-lease.json'));
    const [acquired, launch, cleanup, released] = audit;
    requireModern(audit.map(event => event.event).join('/') === 'acquired/launch-attempt/cleanup-verified/released'
        && audit.every(event => event.lease === manifest.hostLease && event.pid === acquired.pid)
        && acquired.entrypoint === 'kitty-click-docker-collection' && cleanup.by === 'marker-scan'
        && released.launch === 'attempted' && Date.parse(acquired.at) <= Date.parse(launch.at)
        && Date.parse(launch.at) <= Date.parse(measurement.startedAt)
        && Date.parse(measurement.leaseReleasedAt) <= Date.parse(cleanup.at)
        && Date.parse(cleanup.at) <= Date.parse(released.at), 'missing bracketing host desktop lease');
    return released.at;
}

export function buildPublication() {
    return withMeasurement((scratch, manifest) => {
        const { run, results, measurement } = verifyRichKittyClickRun(scratch);
        requireModern(manifest.schemaVersion === 1 && run.runId === manifest.runId
            && [manifest.appRevision, manifest.scriptsRevision].every(value => /^[a-f\d]{40}$/.test(value))
            && /^[a-f\d]{32}$/.test(manifest.hostLease) && /^[a-f\d]{64}$/.test(manifest.appSourceArchiveSha256)
            && run.appVersion.endsWith('+' + manifest.appRevision)
            && results.id === caseId, 'wrong Kitty prompt click archive or App identity');
        return { schemaVersion: 1, scope, ...results, pinned: pinnedStatus(), measurement: {
            manifestSha256, ...manifest, ...measurement, hostLeaseReleasedAt: hostCleanup(scratch, measurement, manifest),
            nodeVersion: run.nodeVersion, appVersion: run.appVersion, appExecutableSha256: run.appFiles.Ntilde,
            appAssemblySha256: run.appFiles['Ntilde.dll'], xtest: run.identity.xtestVersion,
            appPid: run.identity.appPid, childPid: run.lineage.child.pid, paneId: run.identity.pane.paneId,
            tty: run.lineage.child.pty.descriptors[0].path, rows: run.identity.pane.rows, columns: run.identity.pane.cols,
            marker: run.identity.marker,
        } };
    });
}

const code = value => '\x60' + value + '\x60';
const frame = value => value ? code(Buffer.from(value, 'hex').toString('ascii').replace('\x1b[', 'CSI ')) : 'silent';
function renderReport(report) {
    const { effect, anchors, pinned, samples, measurement: m } = report;
    return [
        '# Kitty shell prompt click events', '',
        'The owned App effect for ' + code(caseId) + ' is **' + effect + '**. The pinned ' + code(pinned.package)
            + ' bundle has no runnable probe for this case. Its stock grade is absent.', '',
        'Kitty’s shell integration specification chooses the primary anchor before the secondary prompt and the secondary anchor from its marked row onward. In Kitty’s mouse encoder, the zero-based relative row is adjusted before the one-based SGR encoding. A primary-button click produces a single left press with a final uppercase M. [Shell integration specification](https://sw.kovidgoyal.net/kitty/shell-integration/#notes-for-shell-developers) · [Kitty encoder](https://github.com/kovidgoyal/kitty/blob/a82dac6a03aae6c48fc84d7c759caf12c0c3dc25/kitty/mouse.c#L82-L112) · [Kitty prompt click dispatch](https://github.com/kovidgoyal/kitty/blob/a82dac6a03aae6c48fc84d7c759caf12c0c3dc25/kitty/mouse.c#L1176-L1192)', '',
        'The linked Kitty dispatch finds an anchor by scanning back from the cursor and rejects clicks above that anchor. This fixture follows the specification’s documented primary-anchor fallback for clicks before the secondary marker. [Kitty anchor lookup](https://github.com/kovidgoyal/kitty/blob/a82dac6a03aae6c48fc84d7c759caf12c0c3dc25/kitty/screen.c#L5398-L5415)', '',
        'The fixture wrote OSC 133 A (primary), A;k=s (secondary), and C (submission) from a raw child connected to the App through a direct PTY. It cleared the secondary row after its marker and before drawing the prompt text. The private read-only pane connection and cursor-position replies verified marker text and cursor coordinates. XTEST delivered real X11 pointer moves, button presses, and button releases on the isolated display. Each target pixel was first calibrated with paired ordinary mode 1000/1006 press and release reports. After disabling application mouse tracking and verifying mode readback, a correlated keyboard delimiter followed every pointer action and closed its child input capture.', '',
        'The one-based viewport anchors were primary row ' + anchors.primary + ' and secondary row ' + anchors.secondary
            + '. The live editor cursor was on row ' + anchors.cursor + '. The click coordinates below use one-based viewport cells and X11 screen pixels.', '',
        '| Fixture state | Position | Cell (column, row) | Screen pixel (x, y) | Child frame |',
        '| --- | --- | --- | --- | --- |',
        ...samples.map(sample => '| ' + sample.phase + ' | ' + sample.position + ' | ' + sample.column + ', ' + sample.row
            + ' | ' + sample.point.join(', ') + ' | ' + frame(sample.childHex.join('')) + ' |'),
        '', 'After moving the editor cursor up to the primary input row, the secondary and continuation clicks repeated the same relative frames. After erasing those lines, the same clicks were silent. The target click frames arrived only after pointer release. The capture retains the three action windows, exact child chunks and delimiters, test marker output bytes, CPR replies, pane snapshots, click coordinates, and process identities. A plain primary A marker cleared the old opt-in. RIS repainted the same fixture text and cursor without prompt markers. The child restored and read back its termios attributes, exited, and the collector reaped the App and Xvfb and verified the socket and fixture were gone. Both the Linux collection lease and its enclosing host desktop lease were released.', '',
        'Run ' + code(m.runId) + ' used Ntilde ' + code(m.appVersion) + ' in ' + m.platform + ' with XTEST ' + m.xtest.join('.')
            + '. The App was published from the source archive of ' + code(m.appRevision) + '. The measured collector revision was '
            + code(m.scriptsRevision) + '. The fixture exercised one direct-local App pane with a fake shell prompt. The live geometry stayed fixed. VT tests cover older prompt anchors in scrollback and width reflow.', '',
        'Re-derive with ' + code('node scripts/terminfo/rich-x11-kitty-clicks-results.mjs --check')
            + '. [Raw owned run](owned-run.zip) · [Measurement manifest](manifest.json) · [Exact child bytes and controls](report.json)', '',
        'To collect on Linux arm64, use Node 24, Python 3, Xvfb, libX11, libXtst, and libXRes. Build the App at the recorded App revision and collect at the recorded collector revision.', '',
        '~~~sh',
        'scripts/build.sh publish src/Ntilde.App -c Release -r linux-arm64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -p:SourceRevisionId=' + m.appRevision + ' -o /absolute/private/sidecar',
        'chmod -R go-w /absolute/private/sidecar',
        'node scripts/terminfo-modern-x11-owned.mjs /absolute/private/sidecar/Ntilde ' + m.appVersion + ' --rich-kitty-clicks',
        'node scripts/terminfo/rich-x11-kitty-clicks-results.mjs --verify-run /absolute/path/to/modern-x11-run',
        '~~~', '',
    ].join('\n');
}

function publish(check) {
    const report = buildPublication();
    const outputs = { 'report.json': JSON.stringify(report, null, 2) + '\n', 'report.md': renderReport(report) };
    for (const [name, content] of Object.entries(outputs)) {
        const path = join(directory, name);
        if (check) requireModern(readFileSync(path, 'utf8').replaceAll('\r\n', '\n') === content, 'published ' + name + ' differs');
        else writeFileSync(path, content);
    }
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    const [flag, path, ...rest] = process.argv.slice(2);
    if (flag === '--verify-run' && path && rest.length === 0) {
        console.log(JSON.stringify(verifyRichKittyClickRun(resolve(path)).results, null, 2));
    } else {
        requireModern(['--check', '--write'].includes(flag) && !path, 'expected --check, --write or --verify-run PATH');
        publish(flag === '--check');
    }
}
