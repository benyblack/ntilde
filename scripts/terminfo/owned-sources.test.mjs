import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { capturedV82SourceFiles, capturedV81SourceFiles, capturedV80SourceFiles, capturedV79SourceFiles, capturedV78SourceFiles, capturedV77SourceFiles, capturedV76SourceFiles, capturedV75SourceFiles, capturedV74SourceFiles, capturedV73SourceFiles, capturedV72SourceFiles, capturedOsc5ForkV63Files, capturedOsc5ForkV64Files, capturedV71SourceFiles, capturedCellSizeForkV70Files, capturedV70SourceFiles, capturedOsc633ForkV61Files, capturedV69SourceFiles, capturedV68SourceFiles, capturedV67SourceFiles, capturedV66SourceFiles, capturedV65SourceFiles, capturedV64SourceFiles, capturedV63SourceFiles, capturedV62SourceFiles, capturedV61SourceFiles, capturedV60SourceFiles, capturedV59SourceFiles, capturedV57SourceFiles, capturedV58SourceFiles, capturedV55SourceFiles, capturedV54SourceFiles, capturedV53SourceFiles, capturedV56SourceFiles, capturedV52SourceFiles, capturedV51SourceFiles, capturedV50SourceFiles, capturedV49SourceFiles, capturedV48SourceFiles, capturedV47SourceFiles, capturedV46SourceFiles, capturedV45SourceFiles, capturedV44SourceFiles, capturedV43SourceFiles, capturedV42SourceFiles, capturedV41SourceFiles, capturedV40SourceFiles, capturedV39SourceFiles, capturedV38SourceFiles, capturedV37SourceFiles, capturedV36SourceFiles, capturedV35SourceFiles, capturedV34SourceFiles, capturedV33SourceFiles, capturedV32SourceFiles, capturedV31SourceFiles, capturedV30SourceFiles, capturedV29SourceFiles, capturedV28SourceFiles, capturedV27SourceFiles, capturedV26SourceFiles, capturedV25SourceFiles, capturedV24SourceFiles, capturedV23SourceFiles, capturedV22SourceFiles, capturedV21SourceFiles, capturedV20SourceFiles, capturedV19SourceFiles, capturedV18SourceFiles, capturedV17SourceFiles, capturedV16SourceFiles, capturedV15SourceFiles, capturedV14SourceFiles, capturedV13SourceFiles, capturedV12SourceFiles, capturedV11SourceFiles, privateClipboardReferencePaths, oscTitleReferencePaths, sgrRgbReferencePaths, ownedSourceFiles, ownedSourcePath, assertOwnedSourcesUnchanged, assertRetainedSourceClosure } from './owned-sources.mjs';
function fixture(action) {
    const root = mkdtempSync(join(tmpdir(), 'nt-source-unit-')), context = {};
    try {
        for (const [key, file] of Object.entries(ownedSourceFiles)) {
            const bytes = readFileSync(ownedSourcePath(fileURLToPath(new URL('../../', import.meta.url)), file));
            const path = ownedSourcePath(root, file); mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, bytes);
            context[key] = createHash('sha256').update(bytes).digest('hex');
        }
        action({ root, context });
    } finally { rmSync(root, { recursive: true, force: true }); }
}
test('all 494 exact live source/input files still match their initial snapshot', () => fixture(f => {
    assert.equal(Object.keys(ownedSourceFiles).length, 494); assert.doesNotThrow(() => assertOwnedSourcesUnchanged(f.root, f.context))
}));
for (const file of ['metadata-state.mjs', 'worker.mjs', 'ownership.mjs', 'terminfo-owned-runner.mjs', 'clipboard-pane-session.cs', 'core-icon-window.cs']) test('rejects post-snapshot live source change: ' + file, () => fixture(f => {
    writeFileSync(ownedSourcePath(f.root, file), '// Changed only in disposable fixture.');
    assert.throws(() => assertOwnedSourcesUnchanged(f.root, f.context), /Owned source changed before completion/);
}));

test('cell-size metric producer and independent observer retain their existing fixed aliases', () => {
    const root = fileURLToPath(new URL('../../', import.meta.url));
    assert.equal(capturedV70SourceFiles.privateClipboardInput8SourceSha256, 'clipboard-pane-session.cs');
    assert.equal(capturedV70SourceFiles.coreIconWindowSourceSha256, 'core-icon-window.cs');
    assert.equal(ownedSourcePath(root, 'clipboard-pane-session.cs'), join(root, 'src/Ntilde.App/Controls/TerminalPane.Session.cs'));
    assert.equal(ownedSourcePath(root, 'core-icon-window.cs'), join(root, 'src/Ntilde.App/MainWindow.AgentMetadata.cs'));
});

test('future measured inputs cannot change the historical published v8 source scope', async () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-legacy-source-unit-'));
    try {
        writeFileSync(join(root, 'owned-sources.mjs'), readFileSync(new URL('./owned-sources.mjs', import.meta.url)));
        writeFileSync(join(root, 'source-imports.mjs'), readFileSync(new URL('./source-imports.mjs', import.meta.url)));
        const inventory = { ...ownedSourceFiles, futureSourceSha256: 'future.mjs' };
        writeFileSync(join(root, 'owned-source-inventory.json'), JSON.stringify(inventory));
        const module = await import(pathToFileURL(join(root, 'owned-sources.mjs')).href);
        assert.equal(module.ownedSourceFiles.futureSourceSha256, 'future.mjs');
        assert.equal(module.legacyV8SourceFiles.futureSourceSha256, undefined);
        const published = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-03-metadata.json', import.meta.url)));
        const historical = Object.keys(published.archive.fileSha256).filter(name => name.startsWith('sources/')).map(name => name.slice(8));
        assert.equal(historical.length, 51);
        assert.deepEqual(Object.values(module.legacyV8SourceFiles).sort(), historical.sort());
        assert.ok(Object.keys(module.legacyV8SourceFiles).every(key => typeof published.context[key] === 'string'));
    } finally { rmSync(root, { recursive: true, force: true }); }
});

for (const statement of ["import/* a */'./dependency.mjs';", "import value from/* a */'./dependency.mjs';", "await import/* a */(/* b */'./dependency.mjs');", "import// a\n'./dependency.mjs';"])
    test('retained closure accepts only inventoried comment-separated dependency: ' + statement, () => {
        const root = mkdtempSync(join(tmpdir(), 'nt-comment-closure-'));
        try {
            writeFileSync(join(root, 'entry.mjs'), statement); writeFileSync(join(root, 'dependency.mjs'), 'export default 1;');
            const inventory = { entrySourceSha256: 'entry.mjs', dependencySourceSha256: 'dependency.mjs' };
            assert.doesNotThrow(() => assertRetainedSourceClosure(root, inventory));
            writeFileSync(join(root, 'entry.mjs'), statement.replace('./dependency.mjs', './unlisted.mjs'));
            assert.throws(() => assertRetainedSourceClosure(root, inventory), /outside its versioned scope/);
        } finally { rmSync(root, { recursive: true, force: true }); }
    });

test('retained closure refuses a genuine dependency hidden behind a comment-context lookalike', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-comment-context-'));
    try {
        writeFileSync(join(root, 'entry.mjs'), '// import /*\nimport "file:///outside.mjs";\n// */');
        assert.throws(() => assertRetainedSourceClosure(root, { entrySourceSha256: 'entry.mjs' }), /outside its versioned scope/);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

for (const [label, prefix] of [['string', 'const lookalike = "import /*";\n'], ['regexp', 'const lookalike = /[import /*]/;\n']])
    test('retained ' + label + ' candidate cannot suppress the later real dependency', () => {
        const root = mkdtempSync(join(tmpdir(), 'nt-literal-context-'));
        try {
            writeFileSync(join(root, 'entry.mjs'), prefix + 'import "file:///outside.mjs";\n// */');
            assert.throws(() => assertRetainedSourceClosure(root, { entrySourceSha256: 'entry.mjs' }), /outside its versioned scope/);
        } finally { rmSync(root, { recursive: true, force: true }); }
    });
test('repeated spurious comment candidates fail closed within the cumulative traversal budget', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-import-budget-'));
    try {
        writeFileSync(join(root, 'entry.mjs'), '/* ' + 'import /* '.repeat(1024) + '*/');
        assert.throws(() => assertRetainedSourceClosure(root, { entrySourceSha256: 'entry.mjs' }), /traversal budget exceeded/);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('private v61 fixed360 source scope adds the seven selection-colors scripts to v60 fixed353 (notifications)', () => {
    assert.equal(Object.keys(capturedV61SourceFiles).length, 360);
    assert.deepEqual(Object.keys(capturedV61SourceFiles).slice(0, 353), Object.keys(capturedV60SourceFiles));
    assert.deepEqual(Object.values(capturedV61SourceFiles).slice(353), ['selection-colors-contracts.mjs', 'selection-colors-model.mjs', 'selection-colors-collector.mjs', 'selection-colors-capture.mjs', 'selection-colors-grading.mjs', 'selection-colors-grading-collector.mjs', 'selection-colors-receipt.mjs']);
});

test('the two retained v61 scopes stay distinct and the integrated graph preserves the pixel predecessor', () => {
    assert.equal(Object.keys(capturedOsc633ForkV61Files).length, 407);
    assert.equal(Object.keys(capturedV68SourceFiles).length, 407);
    assert.notDeepEqual(capturedOsc633ForkV61Files, capturedV68SourceFiles);
    assert.deepEqual(Object.keys(capturedV69SourceFiles).slice(0, 407), Object.keys(capturedV68SourceFiles));
    assert.deepEqual(Object.values(capturedV69SourceFiles).slice(407), ['osc633-contracts.mjs', 'osc633-collector.mjs', 'osc633-grading.mjs', 'osc633-grading-collector.mjs', 'osc633-receipt.mjs', 'collection-scope.mjs']);
});

test('the OSC cell-size graph adds its seven measured scripts and five additional C# references without changing the integrated predecessor', () => {
    assert.deepEqual(Object.keys(capturedCellSizeForkV70Files).slice(0, 413), Object.keys(capturedV69SourceFiles));
    assert.equal(Object.keys(capturedV69SourceFiles).length, 413);
    assert.equal(Object.keys(capturedCellSizeForkV70Files).length, 425);
    assert.deepEqual(Object.values(capturedCellSizeForkV70Files).slice(413, 420), [
        'cell-size-contracts.mjs', 'cell-size-frame.mjs', 'cell-size-grading-collector.mjs', 'cell-size-collector.mjs',
        'cell-size-capture.mjs', 'cell-size-grading.mjs', 'cell-size-receipt.mjs',
    ]);
    assert.ok(Object.values(capturedCellSizeForkV70Files).slice(420).every(file => file.startsWith('cell-size-') && file.endsWith('.cs')));
    const paths = Object.values(ownedSourceFiles).map(file => ownedSourcePath('/synthetic-root', file));
    assert.equal(new Set(paths).size, paths.length, 'one captured alias per actual source path');
});

test('private v70 fixed420 source scope adds the seven Kitty animation scripts to the frozen v69 fixed413 scope', () => {
    assert.equal(Object.keys(capturedV70SourceFiles).length, 420);
    assert.deepEqual(Object.keys(capturedV70SourceFiles).slice(0, 413), Object.keys(capturedV69SourceFiles));
    assert.deepEqual(Object.values(capturedV70SourceFiles).slice(413), ['kitty-animation-inputs.mjs', 'kitty-animation-cases.mjs', 'kitty-animation-oracle.mjs',
        'kitty-animation.mjs', 'kitty-animation-capture.mjs', 'kitty-animation-collector.mjs', 'kitty-animation-receipt.mjs']);
});

test('private v68 fixed407 source scope adds the six SGR-pixel scripts to v67 fixed401 (a frozen earlier layer)', () => {
    assert.equal(Object.keys(capturedV69SourceFiles).length, 413);
    assert.deepEqual(Object.keys(capturedV69SourceFiles).slice(0, 407), Object.keys(capturedV68SourceFiles));
    assert.equal(Object.keys(capturedV68SourceFiles).length, 407);
    assert.deepEqual(Object.keys(capturedV68SourceFiles).slice(0, 401), Object.keys(capturedV67SourceFiles));
    assert.deepEqual(Object.values(capturedV68SourceFiles).slice(401), ['pxm-cases.mjs', 'pxm-contracts.mjs', 'pxm-collector.mjs', 'pxm-grading.mjs', 'pxm-grading-collector.mjs', 'pxm-receipt.mjs']);
});

test('private v67 fixed401 source scope adds the seven kitty color-control scripts to v66 fixed394 (xterm saved modes)', () => {
    assert.equal(Object.keys(capturedV67SourceFiles).length, 401);
    assert.deepEqual(Object.keys(capturedV67SourceFiles).slice(0, 394), Object.keys(capturedV66SourceFiles));
    assert.deepEqual(Object.values(capturedV67SourceFiles).slice(394), ['kcl-report.mjs', 'kcl-contracts.mjs', 'kcl-capture.mjs', 'kcl-collector.mjs', 'kcl-grading.mjs', 'kcl-grading-collector.mjs', 'kcl-receipt.mjs']);
});

test('private v66 fixed394 source scope adds the seven xterm saved-mode scripts to v65 fixed387 (extended mouse)', () => {
    assert.equal(Object.keys(capturedV66SourceFiles).length, 394);
    assert.deepEqual(Object.keys(capturedV66SourceFiles).slice(0, 387), Object.keys(capturedV65SourceFiles));
    assert.deepEqual(Object.values(capturedV66SourceFiles).slice(387), ['xsv-report.mjs', 'xsv-contracts.mjs', 'xsv-capture.mjs', 'xsv-collector.mjs', 'xsv-grading.mjs', 'xsv-grading-collector.mjs', 'xsv-receipt.mjs']);
});

test('private v65 fixed387 source scope adds the six extended-mouse scripts to v64 fixed381 (xterm SGR stack)', () => {
    assert.equal(Object.keys(capturedV65SourceFiles).length, 387);
    assert.deepEqual(Object.keys(capturedV65SourceFiles).slice(0, 381), Object.keys(capturedV64SourceFiles));
    assert.deepEqual(Object.values(capturedV65SourceFiles).slice(381), ['mex-cases.mjs', 'mex-contracts.mjs', 'mex-collector.mjs', 'mex-grading.mjs', 'mex-grading-collector.mjs', 'mex-receipt.mjs']);
});

test('private v64 fixed381 source scope adds the seven xterm SGR-stack scripts to v63 fixed374 (color stack)', () => {
    assert.equal(Object.keys(capturedV64SourceFiles).length, 381);
    assert.deepEqual(Object.keys(capturedV64SourceFiles).slice(0, 374), Object.keys(capturedV63SourceFiles));
    assert.deepEqual(Object.values(capturedV64SourceFiles).slice(374), ['xss-report.mjs', 'xss-contracts.mjs', 'xss-capture.mjs', 'xss-collector.mjs', 'xss-grading.mjs', 'xss-grading-collector.mjs', 'xss-receipt.mjs']);
});

test('private v63 fixed374 source scope adds the seven xterm color-stack scripts to v62 fixed367 (device queries)', () => {
    assert.equal(Object.keys(capturedV63SourceFiles).length, 374);
    assert.deepEqual(Object.keys(capturedV63SourceFiles).slice(0, 367), Object.keys(capturedV62SourceFiles));
    assert.deepEqual(Object.values(capturedV63SourceFiles).slice(367), ['xcs-report.mjs', 'xcs-contracts.mjs', 'xcs-capture.mjs', 'xcs-collector.mjs', 'xcs-grading.mjs', 'xcs-grading-collector.mjs', 'xcs-receipt.mjs']);
});

test('private v62 fixed367 source scope adds the seven device-query scripts to v61 fixed360 (selection colors)', () => {
    assert.equal(Object.keys(capturedV62SourceFiles).length, 367);
    assert.deepEqual(Object.keys(capturedV62SourceFiles).slice(0, 360), Object.keys(capturedV61SourceFiles));
    assert.deepEqual(Object.values(capturedV62SourceFiles).slice(360), ['devq-report.mjs', 'devq-contracts.mjs', 'devq-capture.mjs', 'devq-collector.mjs', 'devq-grading.mjs', 'devq-grading-collector.mjs', 'devq-receipt.mjs']);
});

test('private v60 fixed353 source scope adds the five notification scripts to v59 fixed348 (ASCII reflow)', () => {
    assert.equal(Object.keys(capturedV60SourceFiles).length, 353);
    assert.deepEqual(Object.keys(capturedV60SourceFiles).slice(0, 348), Object.keys(capturedV59SourceFiles));
    assert.deepEqual(Object.values(capturedV60SourceFiles).slice(348), ['notify-contracts.mjs', 'notify-collector.mjs', 'notify-grading.mjs', 'notify-grading-collector.mjs', 'notify-receipt.mjs']);
});

test('private v59 fixed348 source scope adds the six ASCII-reflow scripts to v58 fixed342 (OSC 9)', () => {
    assert.equal(Object.keys(capturedV59SourceFiles).length, 348);
    assert.deepEqual(Object.keys(capturedV59SourceFiles).slice(0, 342), Object.keys(capturedV58SourceFiles));
    assert.deepEqual(Object.values(capturedV59SourceFiles).slice(342), ['ascii-reflow-contracts.mjs', 'ascii-reflow-observer.mjs', 'ascii-reflow-collector.mjs', 'ascii-reflow-grading.mjs', 'ascii-reflow-grading-collector.mjs', 'ascii-reflow-receipt.mjs']);
});

test('private v58 fixed342 source scope adds the six OSC 9 scripts to v57 fixed336 (modifyOtherKeys)', () => {
    assert.equal(Object.keys(capturedV58SourceFiles).length, 342);
    assert.deepEqual(Object.keys(capturedV58SourceFiles).slice(0, 336), Object.keys(capturedV57SourceFiles));
    assert.deepEqual(Object.values(capturedV58SourceFiles).slice(336), ['osc9-contracts.mjs', 'osc9-collector.mjs', 'osc9-grading.mjs', 'osc9-grading-collector.mjs', 'osc9-receipt.mjs', 'osc9-focus.mjs']);
});

test('private v57 fixed336 source scope adds the six modifyOtherKeys scripts to v56 fixed330 (the eight XTGETTCAP scripts) to v55 fixed322 (insert/replace) to v54 fixed311 (OSC title; the tab-labels input makes seven) to v53 fixed305 (title-stack)', () => {
    assert.equal(Object.keys(capturedV57SourceFiles).length, 336);
    assert.deepEqual(Object.keys(capturedV57SourceFiles).slice(0, 330), Object.keys(capturedV56SourceFiles));
    assert.deepEqual(Object.values(capturedV57SourceFiles).slice(330), ['modify-other-keys-cases.mjs', 'modify-other-keys-contracts.mjs', 'modify-other-keys-collector.mjs', 'modify-other-keys-grading.mjs', 'modify-other-keys-grading-collector.mjs', 'modify-other-keys-receipt.mjs']);
    assert.deepEqual(Object.keys(capturedV56SourceFiles).slice(0, 322), Object.keys(capturedV55SourceFiles));
    assert.deepEqual(Object.values(capturedV56SourceFiles).slice(322), ['xtgettcap-report.mjs', 'xtgettcap-contracts.mjs', 'xtgettcap-capture.mjs', 'xtgettcap-collector.mjs', 'xtgettcap-grading.mjs', 'xtgettcap-grading-collector.mjs', 'xtgettcap-receipt.mjs', 'xtgettcap-receipt-core.mjs']);
    assert.deepEqual(Object.keys(capturedV55SourceFiles).slice(0, 311), Object.keys(capturedV54SourceFiles));
    assert.deepEqual(Object.keys(capturedV54SourceFiles).slice(0, 305), Object.keys(capturedV53SourceFiles));
    assert.deepEqual(Object.values(capturedV52SourceFiles).slice(295), ['osc78-contracts.mjs', 'osc78-collector.mjs', 'osc78-grading.mjs', 'osc78-grading-collector.mjs', 'osc78-receipt.mjs']);
    assert.deepEqual(Object.keys(capturedV51SourceFiles).slice(0, 292), Object.keys(capturedV50SourceFiles));
    assert.deepEqual(Object.values(capturedV51SourceFiles).slice(292), ['mode-report.mjs', 'core-reverse-wide.mjs', 'core-reverse-wide-receipt.mjs']);
    assert.deepEqual(Object.keys(capturedV50SourceFiles).slice(0, 290), Object.keys(capturedV49SourceFiles));
    assert.deepEqual(Object.values(capturedV50SourceFiles).slice(290), ['core-sgr-raster.mjs', 'core-sgr-raster-receipt.mjs']);
    assert.deepEqual(Object.keys(capturedV49SourceFiles).slice(0, 285), Object.keys(capturedV48SourceFiles));
    assert.deepEqual(Object.values(capturedV49SourceFiles).slice(285), ['core-wrap-contract.mjs', 'core-wrap-collector.mjs', 'core-wrap-observer.mjs', 'core-wrap-geometry.mjs', 'core-wrap-receipt.mjs']);
    assert.deepEqual(Object.keys(capturedV48SourceFiles).slice(0, 283), Object.keys(capturedV47SourceFiles));
    assert.deepEqual(Object.values(capturedV48SourceFiles).slice(283), ['core-tab-stops.mjs', 'core-tab-stops-receipt.mjs']);
    assert.deepEqual(Object.keys(capturedV47SourceFiles).slice(0, 281), Object.keys(capturedV46SourceFiles));
    assert.deepEqual(Object.values(capturedV47SourceFiles).slice(281), ['core-erase.mjs', 'core-erase-receipt.mjs']);
    assert.equal(Object.keys(capturedV45SourceFiles).length, 276);
    assert.deepEqual(Object.keys(capturedV46SourceFiles).slice(0, 276), Object.keys(capturedV45SourceFiles));
    assert.deepEqual(Object.values(capturedV46SourceFiles).slice(276), ['osc133-contracts.mjs', 'osc133-collector.mjs', 'osc133-grading.mjs',
        'osc133-grading-collector.mjs', 'osc133-receipt.mjs']);
    assert.deepEqual(Object.keys(capturedV45SourceFiles).slice(0, 274), Object.keys(capturedV44SourceFiles));
    assert.deepEqual(Object.values(capturedV45SourceFiles).slice(274), ['core-text-cursor.mjs', 'core-text-cursor-receipt.mjs']);
    assert.equal(Object.keys(capturedV43SourceFiles).length, 267);
    assert.deepEqual(Object.keys(capturedV44SourceFiles).slice(0, 267), Object.keys(capturedV43SourceFiles));
    assert.deepEqual(Object.values(capturedV44SourceFiles).slice(267), ['color-reset-contracts.mjs', 'color-reset-model.mjs', 'color-reset-collector.mjs',
        'color-reset-capture.mjs', 'color-reset-grading.mjs', 'color-reset-grading-collector.mjs', 'color-reset-receipt.mjs']);
    assert.equal(Object.keys(capturedV42SourceFiles).length, 260);
    assert.deepEqual(Object.keys(capturedV43SourceFiles).slice(0, 260), Object.keys(capturedV42SourceFiles));
    assert.deepEqual(Object.values(capturedV43SourceFiles).slice(260), ['input-plan.mjs', 'routed-input-cases.mjs', 'routed-input-contracts.mjs', 'routed-input-collector.mjs',
        'routed-input-grading.mjs', 'routed-input-grading-collector.mjs', 'routed-input-receipt.mjs']);
    assert.deepEqual(Object.keys(capturedV42SourceFiles).slice(0, 253), Object.keys(capturedV41SourceFiles));
    assert.equal(Object.keys(capturedV40SourceFiles).length, 248);
    assert.deepEqual(Object.keys(capturedV41SourceFiles).slice(0, 248), Object.keys(capturedV40SourceFiles));
    assert.deepEqual(Object.values(capturedV41SourceFiles).slice(248), ['lr-margins-contracts.mjs', 'lr-margins-collector.mjs', 'lr-margins-grading.mjs',
        'lr-margins-grading-collector.mjs', 'lr-margins-receipt.mjs']);
    assert.equal(Object.keys(capturedV39SourceFiles).length, 241);
    assert.deepEqual(Object.keys(capturedV40SourceFiles).slice(0, 241), Object.keys(capturedV39SourceFiles));
    assert.deepEqual(Object.values(capturedV40SourceFiles).slice(241), ['checksum-report.mjs', 'rect-checksum-contracts.mjs', 'rect-checksum-collector.mjs',
        'rect-checksum-capture.mjs', 'rect-checksum-grading.mjs', 'rect-checksum-grading-collector.mjs', 'rect-checksum-receipt.mjs']);
    assert.equal(Object.keys(capturedV38SourceFiles).length, 236);
    assert.deepEqual(Object.keys(capturedV39SourceFiles).slice(0, 236), Object.keys(capturedV38SourceFiles));
    assert.equal(Object.keys(capturedV37SourceFiles).length, 231);
    assert.deepEqual(Object.keys(capturedV38SourceFiles).slice(0, 231), Object.keys(capturedV37SourceFiles));
    assert.equal(Object.keys(capturedV36SourceFiles).length, 226);
    assert.deepEqual(Object.keys(capturedV37SourceFiles).slice(0, 226), Object.keys(capturedV36SourceFiles));
    assert.equal(Object.keys(capturedV35SourceFiles).length, 221);
    assert.deepEqual(Object.keys(capturedV36SourceFiles).slice(0, 221), Object.keys(capturedV35SourceFiles));
    assert.equal(Object.keys(capturedV34SourceFiles).length, 214);
    assert.deepEqual(Object.keys(capturedV35SourceFiles).slice(0, 214), Object.keys(capturedV34SourceFiles));
    assert.equal(Object.keys(capturedV33SourceFiles).length, 209);
    assert.deepEqual(Object.keys(capturedV34SourceFiles).slice(0, 209), Object.keys(capturedV33SourceFiles));
    assert.equal(Object.keys(capturedV32SourceFiles).length, 204);
    assert.deepEqual(Object.keys(capturedV33SourceFiles).slice(0, 204), Object.keys(capturedV32SourceFiles));
    assert.equal(Object.keys(capturedV31SourceFiles).length, 199);
    assert.deepEqual(Object.keys(capturedV32SourceFiles).slice(0, 199), Object.keys(capturedV31SourceFiles));
    assert.equal(Object.keys(capturedV30SourceFiles).length, 193);
    assert.deepEqual(Object.keys(capturedV31SourceFiles).slice(0, 193), Object.keys(capturedV30SourceFiles));
    assert.equal(Object.keys(capturedV29SourceFiles).length, 188);
    assert.deepEqual(Object.keys(capturedV30SourceFiles).slice(0, 188), Object.keys(capturedV29SourceFiles));
    assert.equal(Object.keys(capturedV28SourceFiles).length, 183);
    assert.deepEqual(Object.keys(capturedV29SourceFiles).slice(0, 183), Object.keys(capturedV28SourceFiles));
    assert.equal(Object.keys(capturedV26SourceFiles).length, 173);
    assert.deepEqual(Object.keys(capturedV27SourceFiles).slice(0, 173), Object.keys(capturedV26SourceFiles));
    assert.equal(Object.keys(capturedV25SourceFiles).length, 168);
    assert.deepEqual(Object.keys(capturedV26SourceFiles).slice(0, 168), Object.keys(capturedV25SourceFiles));
    assert.equal(Object.keys(capturedV24SourceFiles).length, 163);
    assert.deepEqual(Object.keys(capturedV25SourceFiles).slice(0, 163), Object.keys(capturedV24SourceFiles));
    assert.equal(Object.keys(capturedV23SourceFiles).length, 158);
    assert.deepEqual(Object.keys(capturedV24SourceFiles).slice(0, 158), Object.keys(capturedV23SourceFiles));
    assert.equal(Object.keys(capturedV22SourceFiles).length, 154);
    assert.deepEqual(Object.keys(capturedV23SourceFiles).slice(0, 154), Object.keys(capturedV22SourceFiles));
    assert.equal(Object.keys(capturedV21SourceFiles).length, 149);
    assert.deepEqual(Object.keys(capturedV22SourceFiles).slice(0, 149), Object.keys(capturedV21SourceFiles));
    assert.equal(Object.keys(capturedV20SourceFiles).length, 144);
    assert.deepEqual(Object.keys(capturedV21SourceFiles).slice(0, 144), Object.keys(capturedV20SourceFiles));
    assert.equal(Object.keys(capturedV19SourceFiles).length, 140);
    assert.deepEqual(Object.keys(capturedV20SourceFiles).slice(0, 140), Object.keys(capturedV19SourceFiles));
    assert.equal(Object.keys(capturedV18SourceFiles).length, 133);
    assert.deepEqual(Object.keys(capturedV19SourceFiles).slice(0, 133), Object.keys(capturedV18SourceFiles));
    assert.equal(Object.keys(capturedV17SourceFiles).length, 128);
    assert.deepEqual(Object.keys(capturedV18SourceFiles).slice(0, 128), Object.keys(capturedV17SourceFiles));
    assert.equal(Object.keys(capturedV16SourceFiles).length, 123);
    assert.deepEqual(Object.keys(capturedV17SourceFiles).slice(0, 123), Object.keys(capturedV16SourceFiles));
    assert.equal(Object.keys(capturedV15SourceFiles).length, 114);
    assert.equal(Object.keys(capturedV14SourceFiles).length, 113);
    assert.equal(Object.keys(capturedV13SourceFiles).length, 108);
    assert.equal(Object.keys(capturedV11SourceFiles).length, 74);
    assert.equal(Object.keys(privateClipboardReferencePaths).length, 21);
    assert.equal(Object.keys(sgrRgbReferencePaths).length, 3);
    // No captured file may be pinned twice: one alias, one byte set.
    const targets = [...Object.values(privateClipboardReferencePaths), ...Object.values(sgrRgbReferencePaths)];
    assert.equal(new Set(targets).size, targets.length);
    const bootstrap = readFileSync(new URL('../terminfo-owned.mjs', import.meta.url), 'utf8');
    assert.deepEqual(oscTitleReferencePaths, { 'osc-title-tab-labels.cs': 'src/Ntilde.App/MainWindow.TabLabels.cs' });
    assert.match(bootstrap, /"osc-title-tab-labels\.cs": "src\/Ntilde\.App\/MainWindow\.TabLabels\.cs"/);
    const start = bootstrap.indexOf('Object.freeze(', bootstrap.indexOf('const privateClipboardReferencePaths')) + 'Object.freeze('.length;
    const literal = bootstrap.slice(start, bootstrap.indexOf(');', start));
    assert.deepEqual(JSON.parse(literal), privateClipboardReferencePaths);
    const second = bootstrap.indexOf('Object.freeze(', bootstrap.indexOf('const sgrRgbReferencePaths')) + 'Object.freeze('.length;
    assert.deepEqual(JSON.parse(bootstrap.slice(second, bootstrap.indexOf(');', second))), sgrRgbReferencePaths);
    assert.ok([...Object.values(privateClipboardReferencePaths), ...Object.values(sgrRgbReferencePaths)]
        .every(path => path.startsWith('src/') && path.endsWith('.cs')));
});


test('frozen v74 fixed450 adds the six OSC 720 scripts to the frozen v73 graph', () => {
    assert.equal(Object.keys(capturedV74SourceFiles).length, 450);
    assert.deepEqual(Object.values(capturedV74SourceFiles).filter(file => file.startsWith('osc720-')).sort(),
        ['osc720-action.mjs', 'osc720-collector.mjs', 'osc720-contracts.mjs', 'osc720-grading-collector.mjs', 'osc720-grading.mjs', 'osc720-receipt.mjs']);
    assert.deepEqual(Object.fromEntries(Object.entries(capturedV74SourceFiles).filter(([key]) => !key.startsWith('osc720'))), capturedV73SourceFiles);
});

test('frozen v73 fixed444 adds the five OSC 3008 scripts to the frozen v72 graph', () => {
    assert.equal(Object.keys(capturedV73SourceFiles).length, 444);
    assert.deepEqual(Object.values(capturedV73SourceFiles).filter(file => file.startsWith('osc3008-')).sort(),
        ['osc3008-collector.mjs', 'osc3008-contracts.mjs', 'osc3008-grading-collector.mjs', 'osc3008-grading.mjs', 'osc3008-receipt.mjs']);
    assert.deepEqual(Object.fromEntries(Object.entries(capturedV73SourceFiles).filter(([key]) => !key.startsWith('osc3008'))), capturedV72SourceFiles);
});

test('frozen v72 fixed439 integrates both v64 forks without changing either frozen graph', () => {
    assert.equal(Object.keys(capturedV72SourceFiles).length, 439);
    assert.equal(Object.keys(capturedV71SourceFiles).length, 432);
    assert.equal(Object.keys(capturedOsc5ForkV64Files).length, 427);
    assert.equal(Object.keys(capturedOsc5ForkV63Files).length, 420);
    assert.deepEqual(capturedV72SourceFiles, { ...capturedV71SourceFiles, ...capturedOsc5ForkV64Files });
});

test('frozen v75 fixed455 preserves frozen v74 and adds its five modules', () => {
    assert.equal(Object.keys(capturedV75SourceFiles).length, 455);
    assert.equal(Object.keys(capturedV76SourceFiles).length, 461);
    assert.deepEqual(Object.fromEntries(Object.entries(capturedV75SourceFiles).filter(([key]) => !key.startsWith('osc22'))), capturedV74SourceFiles);
});

test('frozen v79 fixed483 preserves frozen v78 and adds its seven DECCOLM modules', () => {
    assert.equal(Object.keys(capturedV79SourceFiles).length, 483);
    assert.deepEqual(Object.fromEntries(Object.entries(capturedV79SourceFiles).filter(([key]) => !key.startsWith('dcl'))), capturedV78SourceFiles);
});

test('frozen v78 fixed476 preserves frozen v77 and adds its seven pointer-color modules', () => {
    assert.equal(Object.keys(capturedV78SourceFiles).length, 476);
    assert.deepEqual(Object.fromEntries(Object.entries(capturedV78SourceFiles).filter(([key]) => !key.startsWith('pcl'))), capturedV77SourceFiles);
});

test('frozen v77 fixed469 preserves frozen v76 and adds its eight modules', () => {
    assert.equal(Object.keys(capturedV77SourceFiles).length, 469);
    assert.deepEqual(Object.fromEntries(Object.entries(capturedV77SourceFiles).filter(([key]) => !key.startsWith('caps') && key !== 'probeSuiteSharedSourceSha256')), capturedV76SourceFiles);
});

test('frozen v76 fixed461 extends frozen455 only by six font modules', () => {
    assert.equal(Object.keys(capturedV76SourceFiles).length, 461);
    assert.deepEqual(Object.fromEntries(Object.entries(capturedV76SourceFiles).filter(([key]) => !key.startsWith('fontSize'))), capturedV75SourceFiles);
});


test('live v82 preserves the Modern modes graph and adds its seven DECSCLM modules', () => {
    assert.deepEqual(ownedSourceFiles, capturedV82SourceFiles);
    assert.equal(Object.keys(capturedV82SourceFiles).length, 494);
    assert.deepEqual(Object.fromEntries(Object.entries(capturedV82SourceFiles).filter(([key]) => !key.startsWith('dsl'))), capturedV81SourceFiles);
    assert.equal(Object.keys(capturedV81SourceFiles).length, 487);
});

test('frozen v81 preserves the DECCOLM graph and adds both Modern cursor modules', () => {
    assert.deepEqual(Object.fromEntries(Object.entries(capturedV81SourceFiles).filter(([key]) => !key.startsWith('modernModes'))), capturedV80SourceFiles);
    assert.equal(Object.keys(capturedV80SourceFiles).length, 485);
    assert.deepEqual(Object.fromEntries(Object.entries(capturedV80SourceFiles).filter(([key]) => !key.startsWith('modernCursor'))), capturedV79SourceFiles);
});
