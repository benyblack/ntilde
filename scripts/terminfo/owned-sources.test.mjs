import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { capturedV12SourceFiles, capturedV11SourceFiles, privateClipboardReferencePaths, ownedSourceFiles, ownedSourcePath, assertOwnedSourcesUnchanged, assertRetainedSourceClosure } from './owned-sources.mjs';
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
test('all 103 exact live source/input files still match their initial snapshot', () => fixture(f => {
    assert.equal(Object.keys(ownedSourceFiles).length, 103); assert.doesNotThrow(() => assertOwnedSourcesUnchanged(f.root, f.context));
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

test('private v12 fixed103 source scope includes all selected nonexecuted references while v11 remains fixed74', () => {
    assert.deepEqual(ownedSourceFiles, capturedV12SourceFiles);
    assert.equal(Object.keys(capturedV11SourceFiles).length, 74);
    assert.equal(Object.keys(privateClipboardReferencePaths).length, 21);
    const bootstrap = readFileSync(new URL('../terminfo-owned.mjs', import.meta.url), 'utf8');
    const start = bootstrap.indexOf('Object.freeze(') + 'Object.freeze('.length;
    const literal = bootstrap.slice(start, bootstrap.indexOf(');', start));
    assert.deepEqual(JSON.parse(literal), privateClipboardReferencePaths);
    assert.ok(Object.values(privateClipboardReferencePaths).every(path => path.startsWith('src/') && path.endsWith('.cs')));
});
