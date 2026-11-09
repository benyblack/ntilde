import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { checkEraseDisplayEvidence, includesEraseDisplayEvidence } from './erase-display-receipt.mjs';
import { checkEraseLineEvidence } from './erase-line-receipt.mjs';
import { checkSgrResetEvidence } from './sgr-reset-receipt.mjs';
import { checkSgrClosureEvidence } from './sgr-closure-receipt.mjs';
import { checkSgrUnderlineColorEvidence } from './sgr-underline-color-receipt.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { adaptEraseLineCollector } from './erase-line-grading-collector.mjs';
import { adaptEraseDisplayCollector, eraseDisplayAdapterId } from './erase-display-grading-collector.mjs';
import { eraseDisplaySuiteId, eraseDisplayContractsVersion } from './erase-display-contracts.mjs';
import { buildEraseDisplayGradingReport } from './erase-display-grading.mjs';
import { syntheticEraseDisplayEvidence } from './fixtures/erase-display-evidence.mjs';
import { fixture as chainFixture, hash, runId, syntheticOwnedAdapter, syntheticUnderlineColorExtension, syntheticUnderlineExtension,
    syntheticPaletteExtension, syntheticClosureExtension, syntheticResetExtension } from './fixtures/receipt-chain.mjs';

const syntheticEraseLineExtension = adaptEraseLineCollector(syntheticClosureExtension);
// The erase-in-display suite is built once: it is deterministic, and each fixture gets its own copy of the report to mutate.
const displayBytes = Buffer.from(adaptEraseDisplayCollector(syntheticEraseLineExtension));
const displayEvidence = syntheticEraseDisplayEvidence(runId, hash(displayBytes));
const displayReportText = JSON.stringify(buildEraseDisplayGradingReport(displayEvidence.frames, displayEvidence.run, runId, displayEvidence.adapter));

/** The shared predecessor chain (through erase-in-line, policy v19) plus this suite's retained files, declarations and binding at policy v20. */
function fixture(action) {
    chainFixture(f => {
        const displayReport = JSON.parse(displayReportText);
        f.write('erdi-observations.json', displayReport); f.write('erdi-run.json', displayEvidence.run);
        writeFileSync(join(f.output, 'erdi-snapshots.jsonl'), displayEvidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(f.output, 'collector-erdi.mjs'), displayBytes);
        Object.assign(f.context, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v20', eraseDisplayAdapterId, eraseDisplaySuiteId,
            eraseDisplayBundleSha256: hash(displayBytes), eraseDisplayContractsVersion });
        f.write('context.json', f.context);
        const displayBinding = { runId, suiteId: eraseDisplaySuiteId, file: 'erdi-observations.json',
            sha256: hash(readFileSync(join(f.output, 'erdi-observations.json'))),
            runFile: 'erdi-run.json', runSha256: hash(readFileSync(join(f.output, 'erdi-run.json'))),
            adapterBundleSha256: hash(displayBytes), contractsVersion: eraseDisplayContractsVersion, counts: { supported: 3 }, provisionalUntil: 'cell-receipt.json' };
        action({ ...f, displayEvidence, displayReport, displayBinding });
    });
}

const predecessors = f => ({ eraseDisplayEvidence: f.displayBinding, eraseLineEvidence: f.binding, sgrClosureEvidence: f.closureBinding, sgrResetEvidence: f.resetBinding, sgrUnderlineColorEvidence: f.underlineColorBinding, sgrUnderlineEvidence: f.underlineBinding, sgrTruecolorEvidence: f.truecolorBinding, sgrPaletteEvidence: f.paletteBinding, sgrAttrsEvidence: f.attrsBinding, sgrRgbEvidence: f.rgbBinding, sgrColorsEvidence: f.colorsBinding });

const reasons = /Erase-in-display|erase-in-line|SGR closure|erase-in-display|SGR selective-reset|SGR-underline|Invalid|Missing|bytes|report|journal|counts|identity|derivation|contracts|differs|predecessor|ENOENT/i;

test('the erase-in-display receipt binding re-derives the whole report from retained bytes', () => fixture(f => {
    assert.equal(includesEraseDisplayEvidence(f.output, {}, f.context), true);
    assert.doesNotThrow(() => checkEraseDisplayEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrClosureEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrResetEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrUnderlineColorEvidence(f.output, predecessors(f), runId, f.context));
}));

test('the shared finalizer publishes receipt schema 26 beside every predecessor suite', () => fixture(f => {
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
    assert.equal(receipt.schemaVersion, 26);
    assert.equal(receipt.eraseDisplayEvidence, f.displayBinding);
    assert.equal(receipt.eraseLineEvidence, f.binding);
    assert.equal(receipt.sgrClosureEvidence, f.closureBinding);
    assert.equal(receipt.sgrResetEvidence, f.resetBinding);
    assert.equal(receipt.sgrUnderlineColorEvidence, f.underlineColorBinding);
    assert.equal(receipt.sgrUnderlineEvidence, f.underlineBinding);
}));

for (const [name, mutate] of [
    ['changed observation bytes', f => f.write('erdi-observations.json', { changed: true })],
    ['changed run log bytes', f => f.write('erdi-run.json', { changed: true })],
    ['changed adapter bytes', f => writeFileSync(join(f.output, 'collector-erdi.mjs'), Buffer.from('tampered'))],
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
    ['changed journal', f => writeFileSync(join(f.output, 'erdi-snapshots.jsonl'), '\n')],
    ['changed conclusion counts', f => { f.displayBinding.counts = { supported: 1 }; }],
    ['wrong run identity', f => { f.displayBinding.runId = 'f'.repeat(32); }],
    ['wrong suite identity', f => { f.displayBinding.suiteId = 'ntilde-owned-other-v1'; }],
    ['wrong contracts version', f => { f.displayBinding.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.eraseDisplayContractsVersion = 2; f.write('context.json', f.context); }],
    ['missing adapter declaration', f => { delete f.context.eraseDisplayAdapterId; f.write('context.json', f.context); }],
    ['missing suite declaration', f => { delete f.context.eraseDisplaySuiteId; f.write('context.json', f.context); }],
    ['missing erase-in-line predecessor evidence', f => { delete f.binding; }],
    ['missing erase-in-line predecessor declaration', f => { delete f.context.eraseLineAdapterId; f.write('context.json', f.context); }],
    ['declared erase-in-line layer digest drift', f => {
        const renamed = Buffer.from(syntheticEraseLineExtension.replace('ntilde-owned-erase-line-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.eraseLineBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
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
        f.displayReport.adapter.bundleSha256 = 'a'.repeat(64);
        f.write('erdi-observations.json', f.displayReport);
        f.displayBinding.sha256 = hash(readFileSync(join(f.output, 'erdi-observations.json')));
    }],
    ['report identity drift', f => {
        f.displayReport.cases[0].conclusion.outcome = 'inconclusive';
        f.write('erdi-observations.json', f.displayReport);
        f.displayBinding.sha256 = hash(readFileSync(join(f.output, 'erdi-observations.json')));
    }],
    ['erase-in-display artifact removal downgrade', f => {
        for (const file of ['erdi-observations.json', 'erdi-run.json', 'erdi-snapshots.jsonl', 'collector-erdi.mjs'])
            rmSync(join(f.output, file));
    }],
]) test('Erase-in-display receipt refuses ' + name, () => fixture(f => {
    mutate(f);
    assert.throws(() => checkEraseDisplayEvidence(f.output, predecessors(f), runId, f.context), reasons);
    assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

test('policy v19 requires the erase-in-display binding even without artifacts', () => fixture(f => {
    for (const file of ['erdi-observations.json', 'erdi-run.json', 'erdi-snapshots.jsonl', 'collector-erdi.mjs'])
        rmSync(join(f.output, file));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v20' };
    assert.equal(includesEraseDisplayEvidence(f.output, {}, context), true);
    assert.throws(() => checkEraseDisplayEvidence(f.output, predecessors(f), runId, context), /Missing or changed erase-in-display adapter declaration/);
}));

test('a retained contracts-version declaration alone admits the suffix so incomplete evidence is rejected', () => fixture(f => {
    for (const file of ['erdi-observations.json', 'erdi-run.json', 'erdi-snapshots.jsonl', 'collector-erdi.mjs']) rmSync(join(f.output, file));
    const context = { eraseDisplayContractsVersion: 1 };
    assert.equal(includesEraseDisplayEvidence(f.output, {}, context), true);
    assert.throws(() => checkEraseDisplayEvidence(f.output, {}, runId, context), /Missing or changed erase-in-display adapter declaration/);
}));

test('a symlinked retained artifact is refused even when it points at valid evidence', () => fixture(f => {
    for (const file of ['erdi-observations.json', 'erdi-run.json', 'erdi-snapshots.jsonl', 'collector-erdi.mjs']) {
        const target = join(f.scratch, 'elsewhere-' + file);
        mkdirSync(f.scratch, { recursive: true });
        writeFileSync(target, readFileSync(join(f.output, file)));
        rmSync(join(f.output, file));
        symlinkSync(target, join(f.output, file));
        assert.throws(() => checkEraseDisplayEvidence(f.output, predecessors(f), runId, f.context), /not a regular file/, file);
        rmSync(join(f.output, file));
        writeFileSync(join(f.output, file), readFileSync(target));
    }
    assert.doesNotThrow(() => checkEraseDisplayEvidence(f.output, predecessors(f), runId, f.context));
}));

test('historical receipts stay valid without any erase-in-display artifacts (policies v10-v19)', () => {
    const publication = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-attributes-qualification.json', import.meta.url)));
    assert.equal(publication.receipt.schemaVersion, 16);
    assert.equal(publication.receipt.eraseDisplayEvidence, undefined);
    for (const version of [10, 11, 13, 14, 15, 16, 17, 18, 19]) {
        assert.equal(includesEraseDisplayEvidence('/nonexistent', {}, { sourceSnapshotPolicy: `readonly-owned-source-graph-v${version}` }), false, `v${version}`);
    }
    assert.equal(includesEraseDisplayEvidence('/nonexistent', {}, {}), false);
});
