import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { checkEraseLineEvidence, includesEraseLineEvidence } from './erase-line-receipt.mjs';
import { checkSgrResetEvidence } from './sgr-reset-receipt.mjs';
import { checkSgrClosureEvidence } from './sgr-closure-receipt.mjs';
import { checkSgrUnderlineColorEvidence } from './sgr-underline-color-receipt.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { fixture, hash, runId, syntheticOwnedAdapter, syntheticUnderlineColorExtension, syntheticUnderlineExtension,
    syntheticPaletteExtension, syntheticClosureExtension, syntheticResetExtension } from './fixtures/receipt-chain.mjs';

const predecessors = f => ({ eraseLineEvidence: f.binding, sgrClosureEvidence: f.closureBinding, sgrResetEvidence: f.resetBinding, sgrUnderlineColorEvidence: f.underlineColorBinding, sgrUnderlineEvidence: f.underlineBinding, sgrTruecolorEvidence: f.truecolorBinding, sgrPaletteEvidence: f.paletteBinding, sgrAttrsEvidence: f.attrsBinding, sgrRgbEvidence: f.rgbBinding, sgrColorsEvidence: f.colorsBinding });

const reasons = /Erase-in-line|SGR closure|erase-in-line|SGR selective-reset|SGR-underline|Invalid|Missing|bytes|report|journal|counts|identity|derivation|contracts|differs|predecessor|ENOENT/i;

test('the erase-in-line receipt binding re-derives the whole report from retained bytes', () => fixture(f => {
    assert.equal(includesEraseLineEvidence(f.output, {}, f.context), true);
    assert.doesNotThrow(() => checkEraseLineEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrClosureEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrResetEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrUnderlineColorEvidence(f.output, predecessors(f), runId, f.context));
}));

test('the shared finalizer publishes receipt schema 25 beside every predecessor suite', () => fixture(f => {
    const { output, scratch, write, context } = f;
    const { sourceSnapshotPolicy, ...lightContext } = context;
    void sourceSnapshotPolicy;
    write('context.json', lightContext);
    for (const prefix of ['cell', 'sgr', 'rgb', 'rendition', 'cursor']) write(`${prefix}-observations.json`, { runId });
    const entry = file => ({ runId, suiteId: `independent-${file}-fixture`, file,
        sha256: hash(readFileSync(join(output, file))), counts: {} });
    const evidence = { ...entry('cell-observations.json'), sgrEvidence: entry('sgr-observations.json'),
        rgbEvidence: entry('rgb-observations.json'), renditionEvidence: entry('rendition-observations.json'),
        cursorEvidence: entry('cursor-observations.json'), gradingEvidence: f.gradingBinding, ...predecessors(f) };
    const receipt = completeOwnedReceipt(output, scratch, evidence);
    assert.equal(receipt.schemaVersion, 25);
    assert.equal(receipt.eraseLineEvidence, f.binding);
    assert.equal(receipt.sgrClosureEvidence, f.closureBinding);
    assert.equal(receipt.sgrResetEvidence, f.resetBinding);
    assert.equal(receipt.sgrUnderlineColorEvidence, f.underlineColorBinding);
    assert.equal(receipt.sgrUnderlineEvidence, f.underlineBinding);
}));

for (const [name, mutate] of [
    ['changed observation bytes', f => f.write('erln-observations.json', { changed: true })],
    ['changed run log bytes', f => f.write('erln-run.json', { changed: true })],
    ['changed adapter bytes', f => writeFileSync(join(f.output, 'collector-erln.mjs'), Buffer.from('tampered'))],
    ['declared SGR-underline-color layer digest drift', f => {
        const renamed = Buffer.from(syntheticUnderlineColorExtension.replace('ntilde-owned-sgr-underline-color-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrUnderlineColorBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['declared SGR-underline layer digest drift', f => {
        const renamed = Buffer.from(syntheticUnderlineExtension.replace('ntilde-owned-sgr-underline-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrUnderlineBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['declared SGR-palette layer digest drift', f => {
        const renamed = Buffer.from(syntheticPaletteExtension.replace('ntilde-owned-sgr-palette-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrPaletteBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['foreign layered derivation', f => {
        writeFileSync(join(f.output, 'collector-adapter.mjs'), Buffer.concat([Buffer.from(syntheticOwnedAdapter), Buffer.from('// doctored\n')]));
    }],
    ['changed journal', f => writeFileSync(join(f.output, 'erln-snapshots.jsonl'), '\n')],
    ['changed conclusion counts', f => { f.binding.counts = { supported: 1 }; }],
    ['wrong run identity', f => { f.binding.runId = 'f'.repeat(32); }],
    ['wrong suite identity', f => { f.binding.suiteId = 'ntilde-owned-other-v1'; }],
    ['wrong contracts version', f => { f.binding.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.eraseLineContractsVersion = 2; f.write('context.json', f.context); }],
    ['missing adapter declaration', f => { delete f.context.eraseLineAdapterId; f.write('context.json', f.context); }],
    ['missing suite declaration', f => { delete f.context.eraseLineSuiteId; f.write('context.json', f.context); }],
    ['missing SGR selective-reset predecessor declaration', f => { delete f.context.sgrResetAdapterId; f.write('context.json', f.context); }],
    ['missing SGR closure predecessor evidence', f => { delete f.closureBinding; }],
    ['missing SGR closure predecessor declaration', f => { delete f.context.sgrClosureAdapterId; f.write('context.json', f.context); }],
    ['declared SGR closure layer digest drift', f => {
        const renamed = Buffer.from(syntheticClosureExtension.replace('ntilde-owned-sgr-closure-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrClosureBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['missing SGR selective-reset predecessor evidence', f => { delete f.resetBinding; }],
    ['declared SGR selective-reset layer digest drift', f => {
        const renamed = Buffer.from(syntheticResetExtension.replace('ntilde-owned-sgr-selective-reset-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrResetBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['missing SGR-underline-color predecessor declaration', f => { delete f.context.sgrUnderlineColorAdapterId; f.write('context.json', f.context); }],
    ['missing SGR-underline-color predecessor evidence', f => { delete f.underlineColorBinding; }],
    ['missing SGR-underline predecessor evidence', f => { delete f.underlineBinding; }],
    ['missing SGR-truecolor predecessor evidence', f => { delete f.truecolorBinding; }],
    ['missing SGR-palette predecessor evidence', f => { delete f.paletteBinding; }],
    ['missing SGR-attributes predecessor evidence', f => { delete f.attrsBinding; }],
    ['missing SGR-RGB predecessor evidence', f => { delete f.rgbBinding; }],
    ['missing SGR-colors predecessor evidence', f => { delete f.colorsBinding; }],
    ['report adapter digest drift', f => {
        f.report.adapter.bundleSha256 = 'a'.repeat(64);
        f.write('erln-observations.json', f.report);
        f.binding.sha256 = hash(readFileSync(join(f.output, 'erln-observations.json')));
    }],
    ['report identity drift', f => {
        f.report.cases[0].conclusion.outcome = 'inconclusive';
        f.write('erln-observations.json', f.report);
        f.binding.sha256 = hash(readFileSync(join(f.output, 'erln-observations.json')));
    }],
    ['erase-in-line artifact removal downgrade', f => {
        for (const file of ['erln-observations.json', 'erln-run.json', 'erln-snapshots.jsonl', 'collector-erln.mjs'])
            rmSync(join(f.output, file));
    }],
]) test('Erase-in-line receipt refuses ' + name, () => fixture(f => {
    mutate(f);
    assert.throws(() => checkEraseLineEvidence(f.output, predecessors(f), runId, f.context), reasons);
    assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

test('policy v19 requires the erase-in-line binding even without artifacts', () => fixture(f => {
    for (const file of ['erln-observations.json', 'erln-run.json', 'erln-snapshots.jsonl', 'collector-erln.mjs'])
        rmSync(join(f.output, file));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v19' };
    assert.equal(includesEraseLineEvidence(f.output, {}, context), true);
    assert.throws(() => checkEraseLineEvidence(f.output, predecessors(f), runId, context), /Missing or changed erase-in-line adapter declaration/);
}));

test('a retained contracts-version declaration alone admits the suffix so incomplete evidence is rejected', () => fixture(f => {
    for (const file of ['erln-observations.json', 'erln-run.json', 'erln-snapshots.jsonl', 'collector-erln.mjs']) rmSync(join(f.output, file));
    const context = { eraseLineContractsVersion: 1 };
    assert.equal(includesEraseLineEvidence(f.output, {}, context), true);
    assert.throws(() => checkEraseLineEvidence(f.output, {}, runId, context), /Missing or changed erase-in-line adapter declaration/);
}));

test('a symlinked retained artifact is refused even when it points at valid evidence', () => fixture(f => {
    for (const file of ['erln-observations.json', 'erln-run.json', 'erln-snapshots.jsonl', 'collector-erln.mjs']) {
        const target = join(f.scratch, 'elsewhere-' + file);
        mkdirSync(f.scratch, { recursive: true });
        writeFileSync(target, readFileSync(join(f.output, file)));
        rmSync(join(f.output, file));
        symlinkSync(target, join(f.output, file));
        assert.throws(() => checkEraseLineEvidence(f.output, predecessors(f), runId, f.context), /not a regular file/, file);
        rmSync(join(f.output, file));
        writeFileSync(join(f.output, file), readFileSync(target));
    }
    assert.doesNotThrow(() => checkEraseLineEvidence(f.output, predecessors(f), runId, f.context));
}));

test('historical receipts stay valid without any erase-in-line artifacts (policies v10-v18)', () => {
    const publication = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-attributes-qualification.json', import.meta.url)));
    assert.equal(publication.receipt.schemaVersion, 16);
    assert.equal(publication.receipt.eraseLineEvidence, undefined);
    for (const version of [10, 11, 13, 14, 15, 16, 17, 18]) {
        assert.equal(includesEraseLineEvidence('/nonexistent', {}, { sourceSnapshotPolicy: `readonly-owned-source-graph-v${version}` }), false, `v${version}`);
    }
    assert.equal(includesEraseLineEvidence('/nonexistent', {}, {}), false);
});
