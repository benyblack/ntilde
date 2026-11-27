import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { gunzipSync } from 'node:zlib';
import { featureId, fontSha256, resolvedFontSha256 } from './font-ligatures-contracts.mjs';
import { deriveRun, receiptFile, reportFile } from './font-ligatures-owned.mjs';
import { gradePixels } from './font-ligatures-pixels.mjs';
import { assertRetainedSourceClosure, capturedFontLigaturesSourceFiles, capturedFontLigaturesSourcePolicy } from './owned-sources.mjs';
import { digest, focusedMeasurement, pinnedBundle, readJson, readManifest, withOwnedArchive, writeUnicodeReport } from './unicode-publication-inputs.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const publication = join(qa, '2026-11-27-rich-font-ligatures');
const font = new URL('../../tests/Ntilde.App.Tests/Fixtures/Fonts/FiraCode-Regular.ttf', import.meta.url);
const bundle = join(qa, '2026-11-16-static-images/terminfo-dev-4.0.0.bundle.mjs.gz');
const manifestHash = '1775bb919e85c504935dea03fa2f05a903e736c0699bf71f4bd8ca2894aa60a1';
const oracleHash = '9b5d947e439f81a9f46d93edffee5b1eac7b92a0dde7e0582be0c8706e4e85a0';
const catalogueHash = '1eda08a977546832b740b9b7606412e69a062f0e3915011994e18ae01ddfd5a8';
const kinds = ['baseline-on', 'off', 'on', 'off-restored'];

function pinnedStatus() {
    const bytes = gunzipSync(readFileSync(bundle), { maxOutputLength: 1_000_000 });
    assert.equal(digest(bytes), pinnedBundle);
    assert.equal(bytes.includes(Buffer.from(featureId)), false, 'pinned collector now has a registration');
    const { rows, catalogue } = readJson(join(qa, '2026-11-26-rich-tui'), 'requirements-v1.json');
    assert.equal(rows.filter(row => row.id === featureId).length, 1);
    assert.equal(catalogue.path, '2026-11-15-modern-results/upstream-snapshot.zip');
    const file = join(qa, catalogue.path);
    assert.equal(digest(readFileSync(file)), catalogueHash);
    const python = process.env.PYTHON ?? (process.platform === 'win32' ? 'python' : 'python3');
    const catalogueBytes = execFileSync(python, ['-c', 'import sys, zipfile; sys.stdout.buffer.write(zipfile.ZipFile(sys.argv[1]).read("features.json"))', file],
        { maxBuffer: 1_000_000, windowsHide: true });
    const caseInfo = JSON.parse(catalogueBytes)[featureId];
    assert.equal(caseInfo.probe, 'Not measured in this suite: visually compare a controlled sample with a ligature-enabled font and known shaping settings.');
    const frozen = readJson(join(qa, '2026-11-26-rich-tui'), 'evidence-map.json').rows.find(row => row.id === featureId);
    assert.equal(frozen.stock, null);
    assert.equal(frozen.commonCore, null);
    return { probe: 'unprobed', pinnedBundleSha256: pinnedBundle, stock: frozen.stock, commonHarness: frozen.commonCore };
}

function fontTables(bytes) {
    assert.equal(bytes.readUInt32BE(0), 0x00010000);
    const count = bytes.readUInt16BE(4), tables = new Map();
    assert.ok(count > 0 && count <= 64);
    for (let i = 0; i < count; i++) {
        const entry = 12 + i * 16, tag = bytes.toString('ascii', entry, entry + 4);
        const offset = bytes.readUInt32BE(entry + 8), length = bytes.readUInt32BE(entry + 12);
        assert.ok(!tables.has(tag) && offset >= 12 + count * 16 && offset + length <= bytes.length);
        tables.set(tag, digest(bytes.subarray(offset, offset + length)));
    }
    return tables;
}

function fontIdentity(directory, oracle) {
    const release = readFileSync(font), resolved = readFileSync(join(directory, 'resolved-FiraCode-Regular.ttf'));
    assert.equal(digest(release), fontSha256);
    assert.equal(digest(resolved), resolvedFontSha256);
    const tables = fontTables(resolved);
    assert.deepEqual(tables, fontTables(release));
    return { family: oracle.family, subfamily: oracle.subfamily, version: oracle.version, postscript: oracle.postscript,
        releaseSha256: fontSha256, resolvedSha256: resolvedFontSha256, matchingTables: tables.size, sizeDip: oracle.sizeDip };
}

function verifySources(retained, directory, context) {
    const manifest = readJson(directory, 'source-snapshot.json');
    const files = capturedFontLigaturesSourceFiles;
    assert.equal(context.sourceSnapshotPolicy, capturedFontLigaturesSourcePolicy);
    assert.equal(manifest.schemaVersion, 1);
    assert.equal(manifest.policy, context.sourceSnapshotPolicy);
    assert.equal(Object.keys(manifest.hashes).length, Object.keys(files).length);
    assert.equal(readdirSync(retained).length, Object.keys(files).length);
    assert.deepEqual(readJson(retained, 'owned-source-inventory.json'), files);
    for (const [field, file] of Object.entries(files)) {
        assert.equal(manifest.hashes[field], context[field], file + ' manifest binding');
        assert.equal(digest(readFileSync(join(retained, file))), context[field], file + ' retained bytes');
    }
    assertRetainedSourceClosure(retained, files);
}

export function withLigatureArchive(callback) {
    const manifest = readManifest(publication, manifestHash, assert.ok);
    assert.deepEqual(Object.keys(manifest.runs), kinds);
    return withOwnedArchive(publication, manifest, callback);
}

function loadRun(directory, manifest, name) {
    const root = join(directory, name), run = deriveRun(root);
    assert.deepEqual(run.report, readJson(root, reportFile));
    assert.deepEqual(run.receipt, readJson(root, receiptFile));
    verifySources(join(name === 'baseline-on' ? root : directory, 'sources'), root, run.context);
    const provisional = { ...manifest, measurement: manifest.runs[name] };
    const measurement = focusedMeasurement(root, provisional, run.context, run.receipt, run.frames[0].screen,
        { mode: 'render', scale: 1, presentationTimeMs: 0 });
    return { ...run, measurement };
}

function verifyPairing(runs) {
    const [baseline, off, on, restore] = runs;
    assert.deepEqual(runs.map(run => [run.report.configured, run.report.enabled]), [[true, false], [false, false], [true, true], [false, false]]);
    assert.equal(new Set(runs.map(run => run.context.runId)).size, 4);
    assert.ok(runs.every((run, i) => i === 0 || Date.parse(runs[i - 1].measurement.releasedAt) <= Date.parse(run.context.startedAt)));
    assert.ok(runs.every(run => run.measurement.scriptsRevision === baseline.measurement.scriptsRevision));
    for (const [field, file] of Object.entries(capturedFontLigaturesSourceFiles)) {
        if (file.endsWith('.mjs')) assert.equal(baseline.context[field], off.context[field], file);
    }
    assert.notEqual(baseline.measurement.appRevision, off.measurement.appRevision);
    for (const run of [on, restore]) {
        assert.equal(run.measurement.appRevision, off.measurement.appRevision);
        assert.equal(run.context.sourceSnapshotManifestSha256, off.context.sourceSnapshotManifestSha256);
        assert.equal(run.context.executableSha256, off.context.executableSha256);
        assert.equal(run.context.assemblySha256, off.context.assemblySha256);
    }
    assert.ok(runs.every(run => run.frames.slice(2, -1).every((frame, i) => frame.sha256 === off.frames[i + 2].sha256)));
}

export function buildPublication() {
    const pinned = pinnedStatus();
    const oracleBytes = readFileSync(join(publication, 'oracle.json'));
    assert.equal(digest(oracleBytes), oracleHash);
    const oracle = JSON.parse(oracleBytes);
    return withLigatureArchive((directory, manifest) => {
        const runs = kinds.map(kind => loadRun(directory, manifest, kind));
        verifyPairing(runs);
        return {
            schemaVersion: 1, featureId, pinned, oracleSha256: oracleHash, font: fontIdentity(directory, oracle),
            owned: gradePixels(runs, oracle),
            runs: Object.fromEntries(runs.map((run, index) => [kinds[index], {
                ...run.report, measurement: run.measurement,
                captures: run.report.captures.map(({ file, sha256, syntheticSelection }) => ({ file, sha256, selection: syntheticSelection ?? null })),
            }])),
        };
    });
}

const code = value => '\x60' + value + '\x60';

export function renderReport(report) {
    const { owned, runs, font: face } = report;
    const { measurement, cursors } = runs.on;
    const { rows, cols } = measurement.geometry;
    return [
        '# Rich TUI font ligatures', '',
        'The owned ' + code(report.featureId) + ' fixture is **supported** after fixing the App settings path and renderer. The digest-pinned terminfo.dev 4.0.0 bundle has no registration for this case. The frozen Rich TUI row has no stock or common-harness grade. [Frozen assessment](../2026-11-26-rich-tui/report.md)', '',
        '## Glyphs and cells', '',
        'The pinned face is Fira Code 6.2 Regular (' + code(face.version) + ', PostScript ' + code(face.postscript) + ') at ' + face.sizeDip + ' DIP. The release SHA-256 is ' + code(face.releaseSha256) + '. The App resolved a macOS font stream with SHA-256 ' + code(face.resolvedSha256) + '. All ' + face.matchingTables + ' OpenType tables agree byte for byte.', '',
        'Independent HarfBuzz shaping identified the glyph IDs and advances with contextual ligatures disabled and enabled. The checked-in oracle samples those glyph outlines independently of App rendering. Each App crop agreed with its expected outline more closely than with the other outline, within the fixed coverage tolerance. [Glyph reference](oracle.json)', '',
        '| Fixture | Changed pixels, off to on | Changed pixels, on to partially selected | Selection (zero-based row, column) |', '| --- | ---: | ---: | --- |',
        ...owned.observations.map(row => '| ' + code(row.text) + ' | ' + row.changedPixels + ' | ' + row.selectedChangedPixels + ' | (' +
            row.selection.startRow + ', ' + row.selection.startCol + ') |'), '',
        'Every pixel outside the two operator cells stayed fixed for each comparison. The selected crops exactly matched the ordinary selected controls. Clearing the synthetic selection restored the unselected pixels, and the final off run reproduced the first off run byte for byte at the pixel level.', '',
        'The App reads retained ' + code('L=>R') + ' and ' + code('L!=R') + ' throughout. The child CPRs were ' +
            cursors.map(cursor => code('CSI ' + cursor.raw.slice(2))).join(', ') + ', agreeing with physical App reads at both logical columns of each operator.', '',
        'On the baseline App, the private settings requested ligatures on and the pane reported them off. Those pixels matched the fixed App with ligatures off. The fix carries the global or profile setting into the pane, uses the font’s ASCII substitutions on logical columns, and splits the shaping run at the selection boundaries. Tests cover both halves of each operator, selection removal and effective settings.', '',
        '## Reproduce', '',
        'Four disposable local tabs used ' + measurement.platform + ' with a ' + rows + ' × ' + cols + ' grid. The ordinary captures use scale 1 and a fixed presentation time with the cursor hidden in the fixture. The selection is a render-only App control. The receipt binds the exact PTY bytes, screen reads, original PNGs, profile, executable/assembly digests, source snapshot, restoration and lock release. [Retained archive](owned-run.zip) · [Machine-readable result](report.json)', '',
        'Build the baseline ' + code(runs['baseline-on'].measurement.appRevision) + ' and fixed App ' + code(measurement.appRevision) + ' into separate private directories using ' +
            code('scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar') + '.', '',
        'Install the pinned Regular face from [the Fira Code release](https://github.com/tonsky/FiraCode/releases/tag/6.2). Use Node 24+ in collector revision ' + code(measurement.scriptsRevision) + '. Run ' +
            code('NTILDE_TERMINFO_LIGATURES=on node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version VERSION+APP_SHA --scope font-ligatures') +
            ' once against the baseline, then with ' + code('off') + ', ' + code('on') + ', ' + code('off') + ' against the fixed App. Each invocation acquires and releases the shared desktop lease.', '',
        'Run ' + code('node scripts/terminfo/font-ligatures-publication.mjs --check') + ' to verify all retained runs and rederive the verdict. To regenerate the shaping oracle use ' +
            code('python scripts/terminfo-font-ligature-oracle.py docs/qa/terminfo/2026-11-27-rich-font-ligatures/oracle.json --check') +
            ' with fontTools 4.61.1 and uharfbuzz 0.53.3. The case covers these two ASCII operators and this resolved face in the measured macOS renderer.', '',
    ].join('\n');
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    const report = buildPublication();
    writeUnicodeReport(publication, report, renderReport(report), process.argv.includes('--check'), assert.ok);
}
