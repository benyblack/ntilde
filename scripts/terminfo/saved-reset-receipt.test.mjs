import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { checkLineEditEvidence } from './line-edit-receipt.mjs';
import { checkSavedResetEvidence, includesSavedResetEvidence, savedResetCounts } from './saved-reset-receipt.mjs';
import { adaptSavedResetCollector, savedResetAdapterId } from './saved-reset-grading-collector.mjs';
import { savedResetSuiteId, savedResetContractsVersion } from './saved-reset-contracts.mjs';
import { buildSavedResetGradingReport } from './saved-reset-grading.mjs';
import { syntheticSavedResetEvidence } from './fixtures/saved-reset-evidence.mjs';
import { checkEraseDisplayEvidence } from './erase-display-receipt.mjs';
import { checkCharEditEvidence } from './char-edit-receipt.mjs';
import { checkEraseLineEvidence } from './erase-line-receipt.mjs';
import { checkSgrResetEvidence } from './sgr-reset-receipt.mjs';
import { checkSgrClosureEvidence } from './sgr-closure-receipt.mjs';
import { checkSgrUnderlineColorEvidence } from './sgr-underline-color-receipt.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { adaptEraseLineCollector } from './erase-line-grading-collector.mjs';
import { adaptCharEditCollector, charEditAdapterId } from './char-edit-grading-collector.mjs';
import { charEditSuiteId, charEditContractsVersion } from './char-edit-contracts.mjs';
import { buildCharEditGradingReport } from './char-edit-grading.mjs';
import { syntheticCharEditEvidence } from './fixtures/char-edit-evidence.mjs';
import { adaptEraseDisplayCollector, eraseDisplayAdapterId } from './erase-display-grading-collector.mjs';
import { adaptLineEditCollector, lineEditAdapterId } from './line-edit-grading-collector.mjs';
import { eraseDisplaySuiteId, eraseDisplayContractsVersion } from './erase-display-contracts.mjs';
import { buildEraseDisplayGradingReport } from './erase-display-grading.mjs';
import { syntheticEraseDisplayEvidence } from './fixtures/erase-display-evidence.mjs';
import { lineEditSuiteId, lineEditContractsVersion } from './line-edit-contracts.mjs';
import { buildLineEditGradingReport } from './line-edit-grading.mjs';
import { syntheticLineEditEvidence } from './fixtures/line-edit-evidence.mjs';
import { fixture as chainFixture, hash, runId, syntheticOwnedAdapter, syntheticUnderlineColorExtension, syntheticUnderlineExtension,
    syntheticPaletteExtension, syntheticClosureExtension, syntheticResetExtension } from './fixtures/receipt-chain.mjs';

const syntheticEraseLineExtension = adaptEraseLineCollector(syntheticClosureExtension);
// The erase-in-display suite is built once: it is deterministic, and each fixture gets its own copy of the report to mutate.
const syntheticCharEditExtension = adaptCharEditCollector(syntheticEraseLineExtension);
const charEditBytes = Buffer.from(syntheticCharEditExtension);
const charEditEvidence = syntheticCharEditEvidence(runId, hash(charEditBytes));
charEditEvidence.adapter = { ...charEditEvidence.adapter, id: charEditAdapterId, derivedFromAdapterId: 'ntilde-owned-erase-line-grading-v1' };
const charEditReportText = JSON.stringify(buildCharEditGradingReport(charEditEvidence.frames, charEditEvidence.run, runId, charEditEvidence.adapter));
const syntheticEraseDisplayExtension = adaptEraseDisplayCollector(syntheticCharEditExtension);
const displayBytes = Buffer.from(syntheticEraseDisplayExtension);
const displayEvidence = syntheticEraseDisplayEvidence(runId, hash(displayBytes));
const displayReportText = JSON.stringify(buildEraseDisplayGradingReport(displayEvidence.frames, displayEvidence.run, runId, displayEvidence.adapter));
const syntheticLineEditExtension = adaptLineEditCollector(syntheticEraseDisplayExtension);
const lineEditBytes = Buffer.from(syntheticLineEditExtension);
const lineEditEvidence = syntheticLineEditEvidence(runId, hash(lineEditBytes));
const lineEditReportText = JSON.stringify(buildLineEditGradingReport(lineEditEvidence.frames, lineEditEvidence.run, runId, lineEditEvidence.adapter));
const savedBytes = Buffer.from(adaptSavedResetCollector(syntheticLineEditExtension));
const savedEvidence = syntheticSavedResetEvidence(runId, hash(savedBytes));
const savedReportText = JSON.stringify(buildSavedResetGradingReport(savedEvidence.frames, savedEvidence.run, runId, savedEvidence.adapter));

/** The shared predecessor chain (through erase-in-line, policy v19) plus this suite's retained files, declarations and binding at policy v23. */
function fixture(action) {
    chainFixture(f => {
        f.write('ched-observations.json', JSON.parse(charEditReportText)); f.write('ched-run.json', charEditEvidence.run);
        writeFileSync(join(f.output, 'ched-snapshots.jsonl'), charEditEvidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(f.output, 'collector-ched.mjs'), charEditBytes);
        const charEditBinding = { runId, suiteId: charEditSuiteId, file: 'ched-observations.json',
            sha256: hash(readFileSync(join(f.output, 'ched-observations.json'))),
            runFile: 'ched-run.json', runSha256: hash(readFileSync(join(f.output, 'ched-run.json'))),
            adapterBundleSha256: hash(charEditBytes), contractsVersion: charEditContractsVersion, counts: { supported: 3 }, provisionalUntil: 'cell-receipt.json' };
        f.write('erdi-observations.json', JSON.parse(displayReportText)); f.write('erdi-run.json', displayEvidence.run);
        writeFileSync(join(f.output, 'erdi-snapshots.jsonl'), displayEvidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(f.output, 'collector-erdi.mjs'), displayBytes);
        Object.assign(f.context, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v23', charEditAdapterId, charEditSuiteId,
            charEditBundleSha256: hash(charEditBytes), charEditContractsVersion, eraseDisplayAdapterId, eraseDisplaySuiteId,
            eraseDisplayBundleSha256: hash(displayBytes), eraseDisplayContractsVersion, lineEditAdapterId, lineEditSuiteId,
            lineEditBundleSha256: hash(lineEditBytes), lineEditContractsVersion, savedResetAdapterId, savedResetSuiteId,
            savedResetBundleSha256: hash(savedBytes), savedResetContractsVersion });
        f.write('context.json', f.context);
        const displayBinding = { runId, suiteId: eraseDisplaySuiteId, file: 'erdi-observations.json',
            sha256: hash(readFileSync(join(f.output, 'erdi-observations.json'))),
            runFile: 'erdi-run.json', runSha256: hash(readFileSync(join(f.output, 'erdi-run.json'))),
            adapterBundleSha256: hash(displayBytes), contractsVersion: eraseDisplayContractsVersion, counts: { supported: 3 }, provisionalUntil: 'cell-receipt.json' };
        const lineEditReport = JSON.parse(lineEditReportText);
        f.write('ledt-observations.json', lineEditReport); f.write('ledt-run.json', lineEditEvidence.run);
        writeFileSync(join(f.output, 'ledt-snapshots.jsonl'), lineEditEvidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(f.output, 'collector-ledt.mjs'), lineEditBytes);
        const lineEditBinding = { runId, suiteId: lineEditSuiteId, file: 'ledt-observations.json',
            sha256: hash(readFileSync(join(f.output, 'ledt-observations.json'))),
            runFile: 'ledt-run.json', runSha256: hash(readFileSync(join(f.output, 'ledt-run.json'))),
            adapterBundleSha256: hash(lineEditBytes), contractsVersion: lineEditContractsVersion, counts: { supported: 2 }, provisionalUntil: 'cell-receipt.json' };
        const savedReport = JSON.parse(savedReportText);
        f.write('svrs-observations.json', savedReport); f.write('svrs-run.json', savedEvidence.run);
        writeFileSync(join(f.output, 'svrs-snapshots.jsonl'), savedEvidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(f.output, 'collector-svrs.mjs'), savedBytes);
        const savedBinding = { ...{ runId, suiteId: savedResetSuiteId, file: 'svrs-observations.json',
            sha256: hash(readFileSync(join(f.output, 'svrs-observations.json'))),
            runFile: 'svrs-run.json', runSha256: hash(readFileSync(join(f.output, 'svrs-run.json'))),
            adapterBundleSha256: hash(savedBytes), contractsVersion: savedResetContractsVersion, provisionalUntil: 'cell-receipt.json' }, ...savedResetCounts(savedReport) };
        action({ ...f, savedResetEvidence: savedEvidence, savedResetReport: savedReport, savedResetBinding: savedBinding, lineEditEvidence, lineEditReport, lineEditBinding, displayBinding, charEditBinding });
    });
}

const predecessors = f => ({ savedResetEvidence: f.savedResetBinding, lineEditEvidence: f.lineEditBinding, eraseDisplayEvidence: f.displayBinding, charEditEvidence: f.charEditBinding, eraseLineEvidence: f.binding, sgrClosureEvidence: f.closureBinding, sgrResetEvidence: f.resetBinding, sgrUnderlineColorEvidence: f.underlineColorBinding, sgrUnderlineEvidence: f.underlineBinding, sgrTruecolorEvidence: f.truecolorBinding, sgrPaletteEvidence: f.paletteBinding, sgrAttrsEvidence: f.attrsBinding, sgrRgbEvidence: f.rgbBinding, sgrColorsEvidence: f.colorsBinding });

const reasons = /Saved-state|saved-state|line-editing|Line-editing|Erase-in-display|erase-in-line|SGR closure|erase-in-display|SGR selective-reset|SGR-underline|Invalid|Missing|bytes|report|journal|counts|identity|derivation|contracts|differs|predecessor|ENOENT/i;

test('the saved-state/reset receipt binding re-derives the whole report from retained bytes', () => fixture(f => {
    assert.equal(includesSavedResetEvidence(f.output, {}, f.context), true);
    assert.doesNotThrow(() => checkSavedResetEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkLineEditEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkEraseDisplayEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkCharEditEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrClosureEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrResetEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrUnderlineColorEvidence(f.output, predecessors(f), runId, f.context));
}));

test('the shared finalizer publishes receipt schema 29 beside every predecessor suite', () => fixture(f => {
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
    assert.equal(receipt.schemaVersion, 29);
    assert.equal(receipt.savedResetEvidence, f.savedResetBinding);
    assert.equal(receipt.lineEditEvidence, f.lineEditBinding);
    assert.equal(receipt.eraseDisplayEvidence, f.displayBinding);
    assert.equal(receipt.eraseLineEvidence, f.binding);
    assert.equal(receipt.charEditEvidence, f.charEditBinding);
    assert.equal(receipt.sgrClosureEvidence, f.closureBinding);
    assert.equal(receipt.sgrResetEvidence, f.resetBinding);
    assert.equal(receipt.sgrUnderlineColorEvidence, f.underlineColorBinding);
    assert.equal(receipt.sgrUnderlineEvidence, f.underlineBinding);
}));

/** Every predecessor layer: its label, declaration key, receipt binding key and, where the test holds its synthetic extension, the text whose digest is declared. */
const layers = [
    ['line-editing', 'lineEdit', 'lineEditBinding', syntheticLineEditExtension, 'ntilde-owned-line-editing-grading-v1'],
    ['erase-in-display', 'eraseDisplay', 'displayBinding', syntheticEraseDisplayExtension, 'ntilde-owned-erase-display-grading-v1'],
    ['character-editing', 'charEdit', 'charEditBinding', syntheticCharEditExtension, 'ntilde-owned-char-edit-grading-v1'],
    ['erase-in-line', 'eraseLine', 'binding', syntheticEraseLineExtension, 'ntilde-owned-erase-line-grading-v1'],
    ['SGR closure', 'sgrClosure', 'closureBinding', syntheticClosureExtension, 'ntilde-owned-sgr-closure-grading-v1'],
    ['SGR selective-reset', 'sgrReset', 'resetBinding', syntheticResetExtension, 'ntilde-owned-sgr-selective-reset-grading-v1'],
    ['SGR-underline-color', 'sgrUnderlineColor', 'underlineColorBinding', syntheticUnderlineColorExtension, 'ntilde-owned-sgr-underline-color-grading-v1'],
    ['SGR-underline', 'sgrUnderline', 'underlineBinding', syntheticUnderlineExtension, 'ntilde-owned-sgr-underline-grading-v1'],
    ['SGR-palette', 'sgrPalette', 'paletteBinding', syntheticPaletteExtension, 'ntilde-owned-sgr-palette-grading-v1'],
    ['SGR-truecolor', 'sgrTruecolor', 'truecolorBinding'], ['SGR-attributes', 'sgrAttrs', 'attrsBinding'],
    ['SGR-RGB', 'sgrRgb', 'rgbBinding'], ['SGR-colors', 'sgrColors', 'colorsBinding'],
];
const rewriteSavedResetReport = (f, edit) => {
    edit(f.savedResetReport);
    f.write('svrs-observations.json', f.savedResetReport);
    f.savedResetBinding.sha256 = hash(readFileSync(join(f.output, 'svrs-observations.json')));
};
for (const [name, mutate] of [
    ['changed observation bytes', f => f.write('svrs-observations.json', { changed: true })],
    ['changed run log bytes', f => f.write('svrs-run.json', { changed: true })],
    ['changed adapter bytes', f => writeFileSync(join(f.output, 'collector-svrs.mjs'), Buffer.from('tampered'))],
    ['foreign layered derivation', f => {
        writeFileSync(join(f.output, 'collector-adapter.mjs'), Buffer.concat([Buffer.from(syntheticOwnedAdapter), Buffer.from('// doctored\n')]));
    }],
    ['changed journal', f => writeFileSync(join(f.output, 'svrs-snapshots.jsonl'), '\n')],
    ['changed effect counts', f => { f.savedResetBinding.effectCounts = { supported: 4, unsupported: 1 }; }],
    ['missing effect counts', f => { delete f.savedResetBinding.effectCounts; }],
    ['changed conclusion counts', f => { f.savedResetBinding.counts = { supported: 1 }; }],
    ['wrong run identity', f => { f.savedResetBinding.runId = 'f'.repeat(32); }],
    ['wrong suite identity', f => { f.savedResetBinding.suiteId = 'ntilde-owned-other-v1'; }],
    ['wrong contracts version', f => { f.savedResetBinding.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.savedResetContractsVersion = 2; f.write('context.json', f.context); }],
    ['missing adapter declaration', f => { delete f.context.savedResetAdapterId; f.write('context.json', f.context); }],
    ['missing suite declaration', f => { delete f.context.savedResetSuiteId; f.write('context.json', f.context); }],
    ...layers.flatMap(([label, key, binding, extension, id]) => [
        [`missing ${label} predecessor evidence`, f => { delete f[binding]; }],
        [`missing ${label} predecessor declaration`, f => { delete f.context[`${key}AdapterId`]; f.write('context.json', f.context); }],
        ...(extension === undefined ? [] : [[`declared ${label} layer digest drift`, f => {
            f.context[`${key}BundleSha256`] = hash(Buffer.from(extension.replace(id, 'ntilde-owned-renamed-v1')));
            f.write('context.json', f.context);
        }]]),
    ]),
    ['report adapter digest drift', f => rewriteSavedResetReport(f, report => { report.adapter.bundleSha256 = 'a'.repeat(64); })],
    ['report identity drift', f => rewriteSavedResetReport(f, report => { report.cases[0].conclusion.outcome = 'unsupported'; })],
    ['saved-state/reset artifact removal downgrade', f => {
        for (const file of ['svrs-observations.json', 'svrs-run.json', 'svrs-snapshots.jsonl', 'collector-svrs.mjs'])
            rmSync(join(f.output, file));
    }],
]) test('Saved-state/reset receipt refuses ' + name, () => fixture(f => {
    mutate(f);
    assert.throws(() => checkSavedResetEvidence(f.output, predecessors(f), runId, f.context), reasons);
    assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

test('policy v23 requires the saved-state/reset binding even without artifacts', () => fixture(f => {
    for (const file of ['svrs-observations.json', 'svrs-run.json', 'svrs-snapshots.jsonl', 'collector-svrs.mjs'])
        rmSync(join(f.output, file));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v23' };
    assert.equal(includesSavedResetEvidence(f.output, {}, context), true);
    assert.throws(() => checkSavedResetEvidence(f.output, predecessors(f), runId, context), /Missing or changed saved-state\/reset adapter declaration/);
}));

test('a retained contracts-version declaration alone admits the suffix so incomplete evidence is rejected', () => fixture(f => {
    for (const file of ['svrs-observations.json', 'svrs-run.json', 'svrs-snapshots.jsonl', 'collector-svrs.mjs']) rmSync(join(f.output, file));
    const context = { savedResetContractsVersion: 1 };
    assert.equal(includesSavedResetEvidence(f.output, {}, context), true);
    assert.throws(() => checkSavedResetEvidence(f.output, {}, runId, context), /Missing or changed saved-state\/reset adapter declaration/);
}));

test('a symlinked retained artifact is refused even when it points at valid evidence', () => fixture(f => {
    for (const file of ['svrs-observations.json', 'svrs-run.json', 'svrs-snapshots.jsonl', 'collector-svrs.mjs']) {
        const target = join(f.scratch, 'elsewhere-' + file);
        mkdirSync(f.scratch, { recursive: true });
        writeFileSync(target, readFileSync(join(f.output, file)));
        rmSync(join(f.output, file));
        symlinkSync(target, join(f.output, file));
        assert.throws(() => checkSavedResetEvidence(f.output, predecessors(f), runId, f.context), /not a regular file/, file);
        rmSync(join(f.output, file));
        writeFileSync(join(f.output, file), readFileSync(target));
    }
    assert.doesNotThrow(() => checkSavedResetEvidence(f.output, predecessors(f), runId, f.context));
}));

test('historical receipts stay valid without any saved-state/reset artifacts (policies v10-v22)', () => {
    const publication = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-attributes-qualification.json', import.meta.url)));
    assert.equal(publication.receipt.schemaVersion, 16);
    assert.equal(publication.receipt.savedResetEvidence, undefined);
    for (const version of [10, 11, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22]) {
        assert.equal(includesSavedResetEvidence('/nonexistent', {}, { sourceSnapshotPolicy: `readonly-owned-source-graph-v${version}` }), false, `v${version}`);
    }
    assert.equal(includesSavedResetEvidence('/nonexistent', {}, {}), false);
});
