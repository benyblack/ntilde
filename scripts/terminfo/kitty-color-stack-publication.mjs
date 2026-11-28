import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { deviceQueryReproduction, verifySources } from './device-identity-publication.mjs';
import { caseIds } from './kitty-color-stack-contracts.mjs';
import { deriveRun } from './kitty-color-stack-owned.mjs';
import { readPinnedProbes, replay } from './kitty-color-stack-pinned.mjs';
import { capturedKittyColorStackSourceFiles, capturedKittyColorStackSourcePolicy } from './owned-sources.mjs';
import { digest, focusedMeasurement, pinnedBundle, readJson, readManifest, withOwnedArchive, writeUnicodeReport } from './unicode-publication-inputs.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const publication = join(qa, '2026-11-28-rich-kitty-color-stack');
const manifestHash = '0cc25745503b1fd252a8c96aad76a1ad853f9049b82e81ab8cc7568f157480ac';
const paletteIndices = [1, 5, 9, 200, 255];

export function withKittyColorStackArchive(callback) {
    return withOwnedArchive(publication, readManifest(publication, manifestHash, assert.ok), callback);
}

export async function buildPublication() {
    const { owned, measurement, refused, superseded } = withKittyColorStackArchive((directory, manifest) => {
        const { context, frames, report, receipt } = deriveRun(directory);
        assert.deepEqual(report, readJson(directory, 'kitty-color-stack-report.json'));
        assert.deepEqual(receipt, readJson(directory, 'kitty-color-stack-receipt.json'));
        verifySources(directory, context, capturedKittyColorStackSourceFiles, capturedKittyColorStackSourcePolicy);
        const { archive } = manifest.refused;
        assert.equal(digest(readFileSync(join(publication, archive))), manifest.archives[archive]);
        return { owned: report, measurement: focusedMeasurement(directory, manifest, context, receipt, frames[0].screen), refused: manifest.refused, superseded: manifest.superseded };
    });
    const map = readJson(join(qa, '2026-11-26-rich-tui'), 'evidence-map.json');
    const stock = Object.fromEntries(caseIds.map(id => [id, map.rows.find(row => row.id === id)?.stock]));
    assert.deepEqual(Object.values(stock), ['supported', 'supported']);
    const pinned = await Promise.all(Array.from(readPinnedProbes(), (probe, index) => replay(probe, owned.cases[index])));
    const { observations } = owned.nested;
    owned.nested.observations = observations.map(({ palette, ...row }) => ({
        ...row, palette: Object.fromEntries(paletteIndices.map(index => [index, palette[index]])),
        fullPaletteSha256: digest(JSON.stringify(palette)),
    }));
    return { schemaVersion: 1, suite: owned.suite, pinnedBundleSha256: pinnedBundle, stock, pinned, owned, measurement, refused, superseded };
}

const code = value => '\x60' + value + '\x60';
const rgb = argb => argb === null ? 'Unset' : code('#' + (argb & 0xffffff).toString(16).padStart(6, '0'));
const colorTableRow = (stage, label, rows) => {
    const row = rows.find(observation => observation.stage === stage);
    return '| ' + [label, ...Object.values(row.colors).map(rgb), row.indices.join(', ') || 'None', row.queriedPaletteEntries].join(' | ') + ' |';
};

export function renderReport(report) {
    const { owned, measurement: m, pinned, refused, superseded } = report;
    const pinnedRows = pinned.map(({ id, terminal, termlessReplay }) => {
        const { before, changed, restored } = JSON.parse(terminal.response);
        return '| ' + [code(id), terminal.observation.outcome, termlessReplay.observation.outcome, ...[before, changed, restored].map(code)].join(' | ') + ' |';
    });
    const rows = owned.nested.observations;
    return [
        '# Rich TUI Kitty OSC 30001 push and OSC 30101 pop', '',
        'Both terminfo.dev 4.0.0 registrations passed in one disposable macOS direct-local pane. Each ID got its own copy of the pinned foreground fixture. Both IDs use the same round-trip criterion. OSC 10 must report the requested mutation after the push and the original foreground after the pop. The pinned bundle registered the same terminal callback and identical termless callbacks for both IDs.', '',
        '| Case | Pinned terminal | Pinned termless | Initial foreground | After mutation | After pop |',
        '| --- | --- | --- | --- | --- | --- |',
        ...pinnedRows, '',
        'The worker journaled each OSC 10 query, OSC 30001 push, exact reply-dependent OSC 10 mutation, OSC 30101 pop, and the pinned cleanup write. Each query included the terminal callback’s DA1 sentinel. Every write and query used the two-nonce DECRPM ordering fence. Offline replay executed both unchanged callbacks against the corresponding exchanges and checked every request byte. After each pinned cleanup write, OSC 110 cleared the override created by writing the original RGB back.', '',
        '## Owned nested-stack experiment', '',
        'The independent contract passed. The fixture set A, pushed it, set B, pushed it, and set C. The first pop restored B and the second restored A. An extra pop on the drained stack was silent and preserved A. [Kitty color-stack specification](https://sw.kovidgoyal.net/kitty/color-stack/#saving-and-restoring-colors)', '',
        '| Stage | Foreground | Background | Cursor | Selection background | Selection foreground | Palette overrides | Palette entries queried |',
        '| --- | --- | --- | --- | --- | --- | --- | --- |',
        ...[['original', 'Original'], ['a', 'A'], ['b', 'B'], ['c', 'C'], ['restored-b', 'First pop → B'],
            ['restored-a', 'Second pop → A'], ['after-empty-pop', 'Empty pop'], ['restored-original', 'Cleanup']].map(([stage, label]) => colorTableRow(stage, label, rows)), '',
        'OSC 10, 11, 12, 17 and 19 replies agreed with the App theme readback. The original selection channels were unset and their fenced queries were silent. OSC 4 and App readback independently agreed at all 256 indices after each nested pop and after cleanup. The fixture distinguished both values and override membership across indices 1, 5, 9, 200 and 255. The JSON report includes their values and digests of the full palettes.', '',
        'Paired App observations preserved the attributed witness cells, cursor position, geometry, stored buffer state and pane metadata. DECRPM confirmed that application cursor keys and bracketed paste stayed enabled across the stack operations and were reset during cleanup. Cleanup restored the original colors, unset selections, empty override list, original screen, and kernel PTY mode. The runner removed the fixture and released the desktop lease.', '',
        'This grades direct-local color state. xterm CSI color stack commands, numbered slots, other Kitty color protocols and rendered pixels are outside the experiment. Both frozen stock grades are supported. This publication follows Rich TUI’s standalone assessment convention for stock-supported cases, leaving the frozen grades and inventory admission rules intact. [Frozen Rich TUI map](../2026-11-26-rich-tui/evidence-map.json)', '',
        'The measured App passed with its existing handlers. ' + refused.reason + ' [First attempt](' + refused.archive + ')', '',
        superseded.reason + ' [Earlier owned archive](' + superseded.archive + ')', '',
        '## Reproduce', '',
        'Run ' + code(m.runId) + ' used ' + m.platform + ', App revision ' + code(m.appRevision) + ' and collector revision ' + code(m.scriptsRevision) + '. The [owned archive](owned-run.zip) contains the raw fenced exchanges, paired pane observations, source and binary identity records, and cleanup receipt. [Machine-readable report](report.json)', '',
        'Build the recorded App revision with the repository .NET SDK. Run the collector from the recorded collector revision with Node 24 or later.', '',
        ...deviceQueryReproduction(m.version, 'kitty-color-stack', 'Rederive the archived evidence and verify the frozen map from the publication revision.'),
    ].join('\n');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    assert.ok(process.argv.length === 3 && ['--write', '--check'].includes(process.argv[2]), 'expected --write or --check');
    const report = await buildPublication();
    writeUnicodeReport(publication, report, renderReport(report), process.argv[2] === '--check', assert.ok);
}
