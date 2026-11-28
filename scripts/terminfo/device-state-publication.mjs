import assert from 'node:assert/strict';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { capturedDeviceStateSourceFiles, capturedDeviceStateSourcePolicy } from './owned-sources.mjs';
import { focusedMeasurement, pinnedBundle, readJson, readManifest, withOwnedArchive, writeUnicodeReport } from './unicode-publication-inputs.mjs';
import { deviceQueryReproduction, readPinnedDeviceProbes, replayDeviceProbe, verifySources } from './device-identity-publication.mjs';
import { caseIds, requests } from './device-state-contracts.mjs';
import { deriveRun } from './device-state-owned.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const publication = join(qa, '2026-11-28-rich-device-state');
const manifestHash = '55f892c7fb89ca496713ef1354a1cef8504f30b349dbe5710e82891b08cd6148';

export function readPinnedProbes() {
    return readPinnedDeviceProbes(caseIds, [
        [caseIds[0], 'device.xtgettcap'],
        ['    {\n      ...probe("device.dsr-996-color-scheme"', 'device.xtwinops-14'],
    ]);
}

export function withDeviceStateArchive(callback) {
    return withOwnedArchive(publication, readManifest(publication, manifestHash, assert.ok), callback);
}

export async function buildPublication() {
    const { owned, measurement, superseded } = withDeviceStateArchive((directory, manifest) => {
        const { context, frames, report, receipt } = deriveRun(directory);
        assert.deepEqual(report, readJson(directory, 'device-state-report.json'));
        assert.deepEqual(receipt, readJson(directory, 'device-state-receipt.json'));
        verifySources(directory, context, capturedDeviceStateSourceFiles, capturedDeviceStateSourcePolicy);
        return { owned: report, measurement: focusedMeasurement(directory, manifest, context, receipt, frames[0].screen), superseded: manifest.superseded };
    });
    const map = readJson(join(qa, '2026-11-26-rich-tui'), 'evidence-map.json');
    const stock = Object.fromEntries(caseIds.map(id => [id, map.rows.find(row => row.id === id)?.stock]));
    assert.deepEqual(Object.values(stock), ['supported', 'supported']);
    const probes = Array.from(readPinnedProbes());
    const pinned = await Promise.all(owned.exchanges.filter(row => row.id).map(async row => {
        const index = caseIds.indexOf(row.id);
        return { stage: row.stage, ...await replayDeviceProbe(probes[index], row, requests[index]) };
    }));
    return { schemaVersion: 1, suite: owned.suite, pinnedBundleSha256: pinnedBundle, stock, pinned, owned, measurement, superseded };
}

const code = value => '\x60' + value + '\x60';
const wire = value => value ? code(value.replaceAll('\x1b[', 'CSI ').replaceAll('\x1bP', 'DCS ').replaceAll('\x1b\\', ' ST')) : 'Silence';
const rgb = argb => '#' + (argb & 0xffffff).toString(16).padStart(6, '0');

function tableRow(row, pinned) {
    const criterion = pinned.find(item => item.stage === row.stage);
    const grade = criterion ? criterion.terminal.observation.outcome + ' / ' + criterion.termlessReplay.observation.outcome : 'Control';
    const state = row.background === undefined ? row.stage : row.stage + ' ' + code(rgb(row.background));
    return '| ' + [row.id ? code(row.id) : 'DECRQSS rejection', state,
        wire(row.request.replace(/\x1b\[c$/, '')), wire(row.reply), grade, row.outcome].join(' | ') + ' |';
}

export function renderReport(report) {
    const { owned, measurement: m, pinned } = report;
    return [
        '# Rich TUI DECRQSS SGR and DSR 996 color scheme', '',
        'One disposable direct-local Ntilde pane on macOS passed both exact state checks. Both historical stock grades are supported. This assessment leaves the frozen stock grades and inventory totals as published. [Frozen Rich TUI evidence map](../2026-11-26-rich-tui/evidence-map.json)', '',
        '| Case | State | Request | Complete child PTY reply | Pinned terminal / termless | Exact state |',
        '| --- | --- | --- | --- | --- | --- |',
        ...owned.exchanges.map(row => tableRow(row, pinned)), '',
        'The pinned terminfo.dev 4.0.0 DECRQSS callback accepts a complete status-1 SGR frame with digits, colons and semicolons. The DSR callback accepts either framed scheme value. Both unchanged terminal callbacks were replayed over replies with their retained DA1 sentinel. The unchanged termless callbacks received the isolated replies. Each child PTY exchange used the two-nonce DECRPM ordering fence. The state controls and exact payload checks are additional evidence.', '',
        'DECRQSS was tested with the neutral pen, then bold with curly underline, ANSI red foreground, indexed background 200 and literal underline RGB ' + code('#090807') + ', then SGR 0 again. Paired witness cells ' + code('Aa Bb Cc') + ' had the independently specified stored attributes before and after each query. The unknown-setting control also preserved the pen. The DECRQSS scope is limited to SGR and that rejection. [Earlier query observations](../2026-11-05-query-observations.md)', '',
        'With each OSC 11 override and after OSC 111, the App color binding and default-background cells showed the expected effective background. Separate OSC 11 readbacks before and after each DSR matched that binding, and DECRPM confirmed mode 2031 reset. The color writes themselves were silent. Dark and light were fixed grayscale controls. The restored-scheme expectation used the documented perceived-luma threshold on the original observed RGB. This grades Ntilde’s effective pane background policy. OS theme switching and opt-in notifications are outside this assessment.', '',
        'Paired App observations showed the query stages preserved the cells, cursor, geometry, buffer modes and effective colors. The background reset removed the override and restored the initial RGB ' + code(rgb(owned.initialBackground)) + '. Leaving the alternate screen restored the original pane snapshot. The runner verified exact kernel PTY mode restoration, fixture removal and desktop lease release. The measured App worked with its existing handlers.', '',
        '## Reproduce', '',
        'Run ' + code(m.runId) + ' used ' + m.platform + ', App revision ' + code(m.appRevision) + ' and collector revision ' + code(m.scriptsRevision) + '. The [owned archive](owned-run.zip) contains the raw exchanges, pane observations, App and collector identity records, and cleanup proof. [Machine-readable report](report.json)', '',
        ...report.superseded.map(run => run.reason + ' [Superseded run ' + run.runId + '](' + run.archive + ')'), '',
        'Build the recorded App revision with the repository .NET SDK. Run the collector from the recorded collector revision with Node 24 or later.', '',
        ...deviceQueryReproduction(m.version, 'device-state', 'Rederive the archived evidence and verify the frozen map from the publication revision.'),
    ].join('\n');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    assert.ok(process.argv.length === 3 && ['--write', '--check'].includes(process.argv[2]), 'expected --write or --check');
    const report = await buildPublication();
    writeUnicodeReport(publication, report, renderReport(report), process.argv[2] === '--check', assert.ok);
}
