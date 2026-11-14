import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { capturedV59SourceFiles, capturedV57SourceFiles, capturedV58SourceFiles, capturedV55SourceFiles, capturedV54SourceFiles, capturedV53SourceFiles, capturedV56SourceFiles, capturedV52SourceFiles, capturedV51SourceFiles, capturedV50SourceFiles, capturedV49SourceFiles, capturedV48SourceFiles, capturedV47SourceFiles, capturedV46SourceFiles, capturedV45SourceFiles, capturedV44SourceFiles, capturedV43SourceFiles, capturedV42SourceFiles, capturedV41SourceFiles, capturedV40SourceFiles, capturedV39SourceFiles, capturedV38SourceFiles, capturedV37SourceFiles, capturedV36SourceFiles, capturedV35SourceFiles, capturedV34SourceFiles, capturedV33SourceFiles, capturedV32SourceFiles, capturedV31SourceFiles, capturedV30SourceFiles, capturedV29SourceFiles, capturedV28SourceFiles, capturedV27SourceFiles, capturedV26SourceFiles, capturedV25SourceFiles, capturedV24SourceFiles, capturedV23SourceFiles, capturedV22SourceFiles, capturedV21SourceFiles, capturedV20SourceFiles, capturedV19SourceFiles, capturedV18SourceFiles, capturedV17SourceFiles, capturedV16SourceFiles, capturedV15SourceFiles, capturedV14SourceFiles, capturedV13SourceFiles, capturedV12SourceFiles, capturedV11SourceFiles, privateClipboardReferencePaths, oscTitleReferencePaths, sgrRgbReferencePaths, ownedSourceFiles, ownedSourcePath, assertOwnedSourcesUnchanged, assertRetainedSourceClosure } from './owned-sources.mjs';
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
test('all 348 exact live source/input files still match their initial snapshot', () => fixture(f => {
    assert.equal(Object.keys(ownedSourceFiles).length, 348); assert.doesNotThrow(() => assertOwnedSourcesUnchanged(f.root, f.context));
}));
for (const file of ['metadata-state.mjs', 'worker.mjs', 'ownership.mjs', 'terminfo-owned-runner.mjs']) test('rejects post-snapshot live source change: ' + file, () => fixture(f => {
    writeFileSync(ownedSourcePath(f.root, file), '// Changed only in disposable fixture.');
    assert.throws(() => assertOwnedSourcesUnchanged(f.root, f.context), /Owned source changed before completion/);
}));

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

test('private v59 fixed348 source scope adds the six ASCII-reflow scripts to v58 fixed342 (OSC 9)', () => {
    assert.deepEqual(ownedSourceFiles, capturedV59SourceFiles);
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

