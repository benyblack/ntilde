import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { assertRetainedSourceClosure, capturedXtwinopsPixelsSourceFiles, capturedXtwinopsPixelsSourcePolicy } from './owned-sources.mjs';
import { journal } from './static-images-receipt.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';
import { digest, focusedMeasurement, leaseReleasedAt, pinnedBundle, readJson, readManifest, withOwnedArchive, writeUnicodeReport } from './unicode-publication-inputs.mjs';
import { caseIds } from './xtwinops-pixels-contracts.mjs';
import { deriveRun } from './xtwinops-pixels-owned.mjs';
import { pinnedXtwinopsPixels, replayXtwinopsPixels, replayXtwinopsPixelsTermless } from './xtwinops-pixels-pinned.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const publication = join(qa, '2026-11-27-rich-xtwinops-pixels');
const manifestHash = '578c0865277abdb91e5e3dcaac166344afb542007b1074dba41aafd7a3fd34ee';

export function readPinnedProbes() {
    return pinnedXtwinopsPixels(readFileSync(join(qa, '2026-11-16-static-images/terminfo-dev-4.0.0.bundle.mjs.gz')));
}

function verifySources(directory, context) {
    const retained = join(directory, 'sources'), manifest = readJson(directory, 'source-snapshot.json');
    const files = Object.entries(capturedXtwinopsPixelsSourceFiles);
    assert.equal(context.sourceSnapshotPolicy, capturedXtwinopsPixelsSourcePolicy);
    assert.equal(manifest.policy, context.sourceSnapshotPolicy);
    assert.equal(manifest.schemaVersion, 1);
    assert.equal(readdirSync(retained).length, files.length);
    assert.deepEqual(readJson(retained, 'owned-source-inventory.json'), capturedXtwinopsPixelsSourceFiles);
    const hashes = Object.fromEntries(files.map(([field, file]) => [field, digest(readFileSync(join(retained, file)))]));
    assert.deepEqual(hashes, manifest.hashes);
    assert.deepEqual(hashes, Object.fromEntries(files.map(([field]) => [field, context[field]])));
    assertRetainedSourceClosure(retained, capturedXtwinopsPixelsSourceFiles);
}

export function withXtwinopsArchive(callback) {
    const manifest = readManifest(publication, manifestHash, assert.ok);
    return withOwnedArchive(publication, manifest, callback);
}

function retainedMeasurement(directory, manifest) {
    const { context, frames, report, receipt } = deriveRun(directory);
    assert.deepEqual(report, readJson(directory, 'xtwinops-report.json'));
    assert.deepEqual(receipt, readJson(directory, 'xtwinops-receipt.json'));
    verifySources(directory, context);
    return { report, measurement: focusedMeasurement(directory, manifest, context, receipt, frames[0].screen) };
}

function failedAttempt(attempt) {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-xtwinops-attempt-'));
    try {
        const { archive, sha256, runId, appRevision, scriptsRevision, reason } = attempt;
        extractOwnedArchive(publication, { archives: { 'owned-run.zip': sha256 } }, scratch, join(publication, archive));
        const context = readJson(scratch, 'context.json'), proof = readJson(scratch, 'ownership-proof.json');
        const frames = journal(scratch, 'xtwinops-frames.jsonl'), last = frames.at(-1);
        assert.equal(context.runId, runId);
        assert.ok(context.appVersion.endsWith('+' + appRevision));
        assert.equal(proof.completed, false);
        assert.equal(last.stage, 'increase');
        assert.equal(last.error, reason);
        const releasedAt = leaseReleasedAt(scratch, runId, assert.ok);
        verifySources(scratch, context);
        if (!last.unsettled) return { archive, sha256, runId, appRevision, scriptsRevision, reason, releasedAt };
        const row = last.unsettled.at(-1);
        const view = { rows: row.cell.viewRows, cols: row.cell.viewCols };
        assert.equal(row.font.fontSizeDip, 15);
        assert.deepEqual(row.screen, row.buffer);
        assert.notDeepEqual(view, row.buffer);
        validateOutputGeometry(row.terminalOutput.description, row.buffer.rows, row.buffer.cols);
        return { archive, sha256, runId, appRevision, scriptsRevision, reason, releasedAt, view, bufferAndKernel: row.buffer };
    } finally { rmSync(scratch, { recursive: true, force: true }); }
}

async function replayStage({ stage, replies }, probes) {
    const cases = await Promise.all(Array.from(probes, async (probe, index) => ({
        id: probe.id, raw: replies[index].raw, terminal: await replayXtwinopsPixels(probe, replies[index]),
        termlessReplay: replayXtwinopsPixelsTermless(probe, replies[index]),
    })));
    return { stage, cases };
}

export async function buildPublication() {
    const probes = readPinnedProbes();
    const manifest = readManifest(publication, manifestHash, assert.ok);
    const attempts = manifest.failedAttempts.map(failedAttempt);
    const { report, measurement } = withOwnedArchive(publication, manifest, retainedMeasurement);
    const map = readJson(join(qa, '2026-11-26-rich-tui'), 'evidence-map.json');
    const stock = Object.fromEntries(caseIds.map(id => [id, map.rows.find(row => row.id === id)?.stock]));
    assert.ok(Object.values(stock).every(outcome => outcome === 'supported'));
    const pinnedReplies = await Promise.all(report.samples.map(stage => replayStage(stage, probes)));
    return { schemaVersion: 1, suite: report.suite, pinnedBundleSha256: pinnedBundle, stock, attempts, pinnedReplies,
        ownedGeometry: { stages: report.samples, actions: report.actions, restored: report.restored }, measurement };
}

const code = value => '\x60' + value + '\x60';
const csi = raw => raw ? code(raw.replaceAll('\x1b[', 'CSI ')) : 'Absent';
const size = value => value ? value.width + ' × ' + value.height : 'Absent';

export function renderReport(report) {
    const { attempts, pinnedReplies, ownedGeometry, measurement: m } = report;
    const baseline = attempts.find(attempt => attempt.view);
    const initialWindow = ownedGeometry.stages[0].expected.outerWindow;
    const restoredWindow = ownedGeometry.stages.at(-1).expected.outerWindow;
    return [
        '# Rich TUI text-area and cell pixel reports', '',
        'One disposable direct-local Ntilde pane measured ' + caseIds.map(code).join(' and ') + ' on macOS. Both historical stock grades are supported. The [frozen Rich TUI evidence map](../2026-11-26-rich-tui/evidence-map.json) and its inventory admission rules are unchanged.', '',
        '## Pinned child PTY replies', '',
        'The exact writes were ' + code('CSI 14 t') + ' and ' + code('CSI 16 t') + ', each followed by the pinned DA1 sentinel. A separate pair of raw ordering replies fenced every exchange. The unchanged terminfo.dev 4.0.0 terminal callbacks require a complete reply with two positive dimensions in wire order (height, width). The JSON also records the pinned termless callbacks replayed over the isolated PTY case replies.', '',
        '| Stage | CSI 14 t frame | Pinned verdict | CSI 16 t frame | Pinned verdict |',
        '| --- | --- | --- | --- | --- |',
        ...pinnedReplies.map(({ stage, cases: [pane, cell] }) => '| ' + stage + ' | ' + csi(pane.terminal.assertions?.[0]?.observed)
            + ' | ' + pane.terminal.observation.outcome + ' | ' + csi(cell.terminal.assertions?.[0]?.observed)
            + ' | ' + cell.terminal.observation.outcome + ' |'),
        '', '## Owned exact geometry', '',
        '| Stage | App and kernel grid (rows × cols) | Font (DIP) | Text area (px) | Cell (px) | Native outer window (px) | Sixel report (px) | Exact checks (14 / 16 / Sixel) |',
        '| --- | --- | --- | --- | --- | --- | --- | --- |',
        ...ownedGeometry.stages.map(row => '| ' + row.stage + ' | ' + row.grid.rows + ' × ' + row.grid.cols + ' | '
            + row.font.fontSizeDip + ' | ' + size(row.replies[0].observed) + ' | ' + size(row.replies[1].observed) + ' | '
            + size(row.expected.outerWindow) + ' | ' + size(row.replies[2].observed) + ' | '
            + row.replies.map(reply => reply.outcome).join(' / ') + ' |'),
        '',
        'Pixel dimensions in this table are width × height. The parent read cell metrics and render scale from the App, took the independent kernel grid from ' + code('/bin/stty') + ', and recorded the native window bounds through Core Graphics. Each cell axis was converted to single-precision device pixels and clamped to one pixel before rounding. For the text area, the unrounded cell dimensions were multiplied by columns and rows in single precision before rounding. Both replies use nearest-even rounding. The Sixel geometry query supplied the 2000-pixel cap control.', '',
        'The parent verified the same pane, screen, cursor, buffer and presentation metadata, native window, and kernel grid around each query. The resize held font size and scale fixed. The font shortcut changed cell metrics in the same resized window at the same scale. Restoration recovered the original pane grid, exact cell metrics and pixel reports. The native window bounds were recorded independently at every stage. Restoring the grid produced an outer window of ' + size(restoredWindow) + ' px versus the initial ' + size(initialWindow) + ' px.', '',
        '## Font-change fix', '',
        'The original App dispatched ' + baseline.bufferAndKernel.cols + ' columns to the buffer and kernel after the font change, while the view had room for ' + baseline.view.cols + '. The font path divided the full view width by the new cell width. It now uses the same padded grid calculation as ordinary layout. The focused App regression failed with a one-column overdispatch on the original code and passed with the fix.', '',
        ...attempts.map(row => '- [Pre-fix attempt ' + row.runId + '](' + row.archive + ') recorded ' + code(row.reason) + ' and verified lease release.'),
        '', '## Reproduce', '',
        'The [owned archive](owned-run.zip) retains the raw fenced replies, paired App observations, kernel geometry, resize actions, App and collector identities, source hashes, and cleanup proof. The [machine-readable report](report.json) records the independent oracle and the pinned verdicts. Run ' + code(m.runId) + ' used ' + m.platform + ', App ' + code(m.appRevision) + ' and collector ' + code(m.scriptsRevision) + '. Cleanup restored the original kernel PTY mode, removed the disposable fixture and released the desktop lease.', '',
        'Build the recorded App revision with Node 24+ available for the collector. Run the collector from the recorded scripts revision.', '',
        '~~~sh',
        'scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar',
        'node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version ' + m.version + ' --scope xtwinops-pixels',
        '~~~', '',
        'At the publication revision, rederive the retained evidence and verify the frozen Rich TUI map.', '',
        '~~~sh',
        'node scripts/terminfo/xtwinops-pixels-publication.mjs --check',
        'python3 scripts/terminfo-rich-baseline.py --check',
        '~~~', '',
        'Scope covers bare ' + code('CSI 14 t') + ' and ' + code('CSI 16 t') + ' in the measured direct-local macOS pane. ' + code('CSI 14;2 t') + ', other XTWINOPS operations and cross-platform parity remain outside this measurement.', '',
    ].join('\n');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    assert.ok(process.argv.length === 3 && ['--write', '--check'].includes(process.argv[2]), 'expected --write or --check');
    const report = await buildPublication();
    writeUnicodeReport(publication, report, renderReport(report), process.argv[2] === '--check', assert.ok);
}
