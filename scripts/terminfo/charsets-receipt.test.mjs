import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { checkCharsetsEvidence, includesCharsetsEvidence } from './charsets-receipt.mjs';
import { checkEraseDisplayEvidence } from './erase-display-receipt.mjs';
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
import { eraseDisplaySuiteId, eraseDisplayContractsVersion } from './erase-display-contracts.mjs';
import { buildEraseDisplayGradingReport } from './erase-display-grading.mjs';
import { syntheticEraseDisplayEvidence } from './fixtures/erase-display-evidence.mjs';
import { adaptCharsetsCollector, charsetsAdapterId } from './charsets-grading-collector.mjs';
import { charsetsSuiteId, charsetsContractsVersion } from './charsets-contracts.mjs';
import { buildCharsetsGradingReport } from './charsets-grading.mjs';
import { syntheticCharsetsEvidence } from './fixtures/charsets-evidence.mjs';
import { fixture as chainFixture, hash, runId, syntheticOwnedAdapter, syntheticUnderlineColorExtension, syntheticUnderlineExtension,
    syntheticPaletteExtension, syntheticClosureExtension, syntheticResetExtension } from './fixtures/receipt-chain.mjs';

const syntheticEraseLineExtension = adaptEraseLineCollector(syntheticClosureExtension);
// The erase-in-display suite is built once: it is deterministic, and each fixture gets its own copy of the report to mutate.
const syntheticCharEditExtension = adaptCharEditCollector(syntheticEraseLineExtension);
const charEditBytes = Buffer.from(syntheticCharEditExtension);
const charEditEvidence = syntheticCharEditEvidence(runId, hash(charEditBytes));
charEditEvidence.adapter = { ...charEditEvidence.adapter, id: charEditAdapterId, derivedFromAdapterId: 'ntilde-owned-erase-line-grading-v1' };
const charEditReportText = JSON.stringify(buildCharEditGradingReport(charEditEvidence.frames, charEditEvidence.run, runId, charEditEvidence.adapter));
const syntheticDisplayExtension = adaptEraseDisplayCollector(syntheticCharEditExtension);
const displayBytes = Buffer.from(syntheticDisplayExtension);
const displayEvidence = syntheticEraseDisplayEvidence(runId, hash(displayBytes));
const displayReportText = JSON.stringify(buildEraseDisplayGradingReport(displayEvidence.frames, displayEvidence.run, runId, displayEvidence.adapter));
const charsetsBytes = Buffer.from(adaptCharsetsCollector(syntheticDisplayExtension));
const charsetsEvidence = syntheticCharsetsEvidence(runId, hash(charsetsBytes));
const charsetsReportText = JSON.stringify(buildCharsetsGradingReport(charsetsEvidence.frames, charsetsEvidence.run, runId, charsetsEvidence.adapter));

/** The shared predecessor chain (through erase-in-line, policy v19) plus character editing and erase-in-display as predecessors, and this suite's retained files, declarations and binding at policy v22. */
function fixture(action) {
    chainFixture(f => {
        f.write('ched-observations.json', JSON.parse(charEditReportText)); f.write('ched-run.json', charEditEvidence.run);
        writeFileSync(join(f.output, 'ched-snapshots.jsonl'), charEditEvidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(f.output, 'collector-ched.mjs'), charEditBytes);
        const charEditBinding = { runId, suiteId: charEditSuiteId, file: 'ched-observations.json',
            sha256: hash(readFileSync(join(f.output, 'ched-observations.json'))),
            runFile: 'ched-run.json', runSha256: hash(readFileSync(join(f.output, 'ched-run.json'))),
            adapterBundleSha256: hash(charEditBytes), contractsVersion: charEditContractsVersion, counts: { supported: 3 }, provisionalUntil: 'cell-receipt.json' };
        const displayReport = JSON.parse(displayReportText);
        f.write('erdi-observations.json', displayReport); f.write('erdi-run.json', displayEvidence.run);
        writeFileSync(join(f.output, 'erdi-snapshots.jsonl'), displayEvidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(f.output, 'collector-erdi.mjs'), displayBytes);
        const charsetsReport = JSON.parse(charsetsReportText);
        f.write('chrs-observations.json', charsetsReport); f.write('chrs-run.json', charsetsEvidence.run);
        writeFileSync(join(f.output, 'chrs-snapshots.jsonl'), charsetsEvidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(f.output, 'collector-chrs.mjs'), charsetsBytes);
        Object.assign(f.context, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v22', charsetsAdapterId, charsetsSuiteId,
            charsetsBundleSha256: hash(charsetsBytes), charsetsContractsVersion, charEditAdapterId, charEditSuiteId,
            charEditBundleSha256: hash(charEditBytes), charEditContractsVersion, eraseDisplayAdapterId, eraseDisplaySuiteId,
            eraseDisplayBundleSha256: hash(displayBytes), eraseDisplayContractsVersion });
        f.write('context.json', f.context);
        const displayBinding = { runId, suiteId: eraseDisplaySuiteId, file: 'erdi-observations.json',
            sha256: hash(readFileSync(join(f.output, 'erdi-observations.json'))),
            runFile: 'erdi-run.json', runSha256: hash(readFileSync(join(f.output, 'erdi-run.json'))),
            adapterBundleSha256: hash(displayBytes), contractsVersion: eraseDisplayContractsVersion, counts: { supported: 3 }, provisionalUntil: 'cell-receipt.json' };
        const binding = { runId, suiteId: charsetsSuiteId, file: 'chrs-observations.json',
            sha256: hash(readFileSync(join(f.output, 'chrs-observations.json'))),
            runFile: 'chrs-run.json', runSha256: hash(readFileSync(join(f.output, 'chrs-run.json'))),
            adapterBundleSha256: hash(charsetsBytes), contractsVersion: charsetsContractsVersion, counts: { supported: 4 }, provisionalUntil: 'cell-receipt.json' };
        action({ ...f, eraseLineBinding: f.binding, binding, report: charsetsReport, displayEvidence, displayReport, displayBinding, charEditBinding });
    });
}

const predecessors = f => ({ charsetsEvidence: f.binding, eraseDisplayEvidence: f.displayBinding, charEditEvidence: f.charEditBinding, eraseLineEvidence: f.eraseLineBinding, sgrClosureEvidence: f.closureBinding, sgrResetEvidence: f.resetBinding, sgrUnderlineColorEvidence: f.underlineColorBinding, sgrUnderlineEvidence: f.underlineBinding, sgrTruecolorEvidence: f.truecolorBinding, sgrPaletteEvidence: f.paletteBinding, sgrAttrsEvidence: f.attrsBinding, sgrRgbEvidence: f.rgbBinding, sgrColorsEvidence: f.colorsBinding });

const reasons = /Charsets|erase-in-display|Erase-in-display|erase-in-line|SGR closure|erase-in-display|SGR selective-reset|SGR-underline|Invalid|Missing|bytes|report|journal|counts|identity|derivation|contracts|differs|predecessor|ENOENT/i;

test('the erase-in-display receipt binding re-derives the whole report from retained bytes', () => fixture(f => {
    assert.equal(includesCharsetsEvidence(f.output, {}, f.context), true);
    assert.doesNotThrow(() => checkCharsetsEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkEraseDisplayEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrClosureEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrResetEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrUnderlineColorEvidence(f.output, predecessors(f), runId, f.context));
}));

test('the shared finalizer publishes receipt schema 28 beside every predecessor suite', () => fixture(f => {
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
    assert.equal(receipt.schemaVersion, 28);
    assert.equal(receipt.charsetsEvidence, f.binding);
    assert.equal(receipt.eraseDisplayEvidence, f.displayBinding);
    assert.equal(receipt.eraseLineEvidence, f.eraseLineBinding);
    assert.equal(receipt.charEditEvidence, f.charEditBinding);
    assert.equal(receipt.sgrClosureEvidence, f.closureBinding);
    assert.equal(receipt.sgrResetEvidence, f.resetBinding);
    assert.equal(receipt.sgrUnderlineColorEvidence, f.underlineColorBinding);
    assert.equal(receipt.sgrUnderlineEvidence, f.underlineBinding);
}));

/** Every predecessor layer: its label, declaration key, receipt binding key and, where the test holds its synthetic extension, the text whose digest is declared. */
const layers = [
    ['erase-in-display', 'eraseDisplay', 'displayBinding', syntheticDisplayExtension, 'ntilde-owned-erase-display-grading-v1'],
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
const rewriteDisplayReport = (f, edit) => {
    edit(f.report);
    f.write('chrs-observations.json', f.report);
    f.binding.sha256 = hash(readFileSync(join(f.output, 'chrs-observations.json')));
};
for (const [name, mutate] of [
    ['changed observation bytes', f => f.write('chrs-observations.json', { changed: true })],
    ['changed run log bytes', f => f.write('chrs-run.json', { changed: true })],
    ['changed adapter bytes', f => writeFileSync(join(f.output, 'collector-chrs.mjs'), Buffer.from('tampered'))],
    ['foreign layered derivation', f => {
        writeFileSync(join(f.output, 'collector-adapter.mjs'), Buffer.concat([Buffer.from(syntheticOwnedAdapter), Buffer.from('// doctored\n')]));
    }],
    ['changed journal', f => writeFileSync(join(f.output, 'chrs-snapshots.jsonl'), '\n')],
    ['changed conclusion counts', f => { f.binding.counts = { supported: 1 }; }],
    ['wrong run identity', f => { f.binding.runId = 'f'.repeat(32); }],
    ['wrong suite identity', f => { f.binding.suiteId = 'ntilde-owned-other-v1'; }],
    ['wrong contracts version', f => { f.binding.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.charsetsContractsVersion = 2; f.write('context.json', f.context); }],
    ['missing adapter declaration', f => { delete f.context.charsetsAdapterId; f.write('context.json', f.context); }],
    ['missing suite declaration', f => { delete f.context.charsetsSuiteId; f.write('context.json', f.context); }],
    ...layers.flatMap(([label, key, binding, extension, id]) => [
        [`missing ${label} predecessor evidence`, f => { delete f[binding]; }],
        [`missing ${label} predecessor declaration`, f => { delete f.context[`${key}AdapterId`]; f.write('context.json', f.context); }],
        ...(extension === undefined ? [] : [[`declared ${label} layer digest drift`, f => {
            f.context[`${key}BundleSha256`] = hash(Buffer.from(extension.replace(id, 'ntilde-owned-renamed-v1')));
            f.write('context.json', f.context);
        }]]),
    ]),
    ['report adapter digest drift', f => rewriteDisplayReport(f, report => { report.adapter.bundleSha256 = 'a'.repeat(64); })],
    ['report identity drift', f => rewriteDisplayReport(f, report => { report.cases[0].conclusion.outcome = 'inconclusive'; })],
    ['erase-in-display artifact removal downgrade', f => {
        for (const file of ['chrs-observations.json', 'chrs-run.json', 'chrs-snapshots.jsonl', 'collector-chrs.mjs'])
            rmSync(join(f.output, file));
    }],
]) test('Charsets receipt refuses ' + name, () => fixture(f => {
    mutate(f);
    assert.throws(() => checkCharsetsEvidence(f.output, predecessors(f), runId, f.context), reasons);
    assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

test('policy v22 requires the charsets binding even without artifacts', () => fixture(f => {
    for (const file of ['chrs-observations.json', 'chrs-run.json', 'chrs-snapshots.jsonl', 'collector-chrs.mjs'])
        rmSync(join(f.output, file));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v22' };
    assert.equal(includesCharsetsEvidence(f.output, {}, context), true);
    assert.throws(() => checkCharsetsEvidence(f.output, predecessors(f), runId, context), /Missing or changed charsets adapter declaration/);
}));

test('a retained contracts-version declaration alone admits the suffix so incomplete evidence is rejected', () => fixture(f => {
    for (const file of ['chrs-observations.json', 'chrs-run.json', 'chrs-snapshots.jsonl', 'collector-chrs.mjs']) rmSync(join(f.output, file));
    const context = { charsetsContractsVersion: 1 };
    assert.equal(includesCharsetsEvidence(f.output, {}, context), true);
    assert.throws(() => checkCharsetsEvidence(f.output, {}, runId, context), /Missing or changed charsets adapter declaration/);
}));

test('a symlinked retained artifact is refused even when it points at valid evidence', () => fixture(f => {
    for (const file of ['chrs-observations.json', 'chrs-run.json', 'chrs-snapshots.jsonl', 'collector-chrs.mjs']) {
        const target = join(f.scratch, 'elsewhere-' + file);
        mkdirSync(f.scratch, { recursive: true });
        writeFileSync(target, readFileSync(join(f.output, file)));
        rmSync(join(f.output, file));
        symlinkSync(target, join(f.output, file));
        assert.throws(() => checkCharsetsEvidence(f.output, predecessors(f), runId, f.context), /not a regular file/, file);
        rmSync(join(f.output, file));
        writeFileSync(join(f.output, file), readFileSync(target));
    }
    assert.doesNotThrow(() => checkCharsetsEvidence(f.output, predecessors(f), runId, f.context));
}));

test('historical receipts stay valid without any charsets artifacts (policies v10-v21)', () => {
    const publication = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-attributes-qualification.json', import.meta.url)));
    assert.equal(publication.receipt.schemaVersion, 16);
    assert.equal(publication.receipt.eraseDisplayEvidence, undefined);
    for (const version of [10, 11, 13, 14, 15, 16, 17, 18, 19, 20, 21]) {
        assert.equal(includesCharsetsEvidence('/nonexistent', {}, { sourceSnapshotPolicy: `readonly-owned-source-graph-v${version}` }), false, `v${version}`);
    }
    assert.equal(includesCharsetsEvidence('/nonexistent', {}, {}), false);
});
