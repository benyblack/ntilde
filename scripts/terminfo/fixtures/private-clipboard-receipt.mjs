// Synthetic suffix over untouched real v11 prefix DATA. No archive module executes.
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { historicalArchive } from './captured-sources.mjs';
import { privateClipboardFixture } from './private-clipboard-owned.mjs';
import { original as originalCollector } from './private-clipboard-upstream.mjs';
import { capturedV34SourceFiles, ownedSourcePath, sourceSnapshotPolicy } from '../owned-sources.mjs';
import { adaptCollector } from '../collector.mjs';
import { ownedPrivateClipboardPolicy, ownedPrivateClipboardSuiteId } from '../private-clipboard-worker.mjs';
import { PRIVATE_CLIPBOARD_PROBES } from '../private-clipboard-controller.mjs';
import { privateClipboardOutputPolicy } from '../terminal-output.mjs';
import { emptyPrivateClipboardReply, privateClipboardEvidenceJson } from '../private-clipboard.mjs';
import { completeOwnedReceipt } from '../receipt.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Clone immutable v11 data into a private temporary directory, add a synthetic 17th suffix and always remove the copy. */
export async function privateClipboardReceiptFixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-private-receipt-'));
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value, null, 2) + '\n');
    try {
        const archive = historicalArchive('2026-11-05-query-observations');
        for (const [file, bytes] of archive) { const path = join(output, file); mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, bytes); }
        const old = JSON.parse(archive.get('cell-receipt.json'));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = structuredClone(old);
        rmSync(join(output, 'cell-receipt.json'));
        const context = JSON.parse(archive.get('context.json')), proof = JSON.parse(archive.get('ownership-proof.json'));
        const settings = JSON.parse(archive.get('initial-settings.json')), query = JSON.parse(archive.get('query-observations.json'));
        const raw = JSON.parse(archive.get('raw.json')), process = proof.proofs[0];
        for (const [key, file] of Object.entries(capturedV34SourceFiles)) {
            const data = readFileSync(ownedSourcePath(fileURLToPath(new URL('../../../', import.meta.url)), file));
            writeFileSync(join(output, 'sources', file), data); context[key] = hash(data);
        }
        const manifest = { schemaVersion: 1, policy: sourceSnapshotPolicy,
            hashes: Object.fromEntries(Object.keys(capturedV34SourceFiles).map(key => [key, context[key]])) };
        write('source-snapshot.json', manifest);
        Object.assign(context, { sourceSnapshotPolicy, sourceSnapshotManifestSha256: hash(readFileSync(join(output, 'source-snapshot.json'))),
            privateClipboardSuiteId: ownedPrivateClipboardSuiteId, ownedPrivateClipboardPolicy,
            terminalOutput: privateClipboardOutputPolicy, clipboard: 'armed synthetic App-private storage only; no native interchange or OSC52 read permission',
            privateClipboardReferenceScope: 'selected App/VT observational reference inputs; not complete App closure or binary-source attestation', retainedOriginalCollector: 'collector-original.mjs' });
        const binding = { ...query.binding, sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256,
            executableSha256: context.executableSha256, assemblySha256: context.assemblySha256,
            appBirth: 'Thu Nov  5 08:10:00 2026', workerBirth: 'Thu Nov  5 08:10:01 2026', outputDevice: { dev: '1', rdev: '2', ino: '3' } };
        const api = await privateClipboardFixture({ binding, processFacts: process, initialScreen: query.initial.before.screen,
            atStart: Date.parse(query.seal.ownership.at) + 1 });
        const report = await api.run(), file = 'private-clipboard-observations.json';
        writeFileSync(join(output, file), privateClipboardEvidenceJson(report));
        writeFileSync(join(output, 'private-clipboard-journal.json'), privateClipboardEvidenceJson(api.observer.journal));
        const privateBinding = { runId: context.runId, suiteId: ownedPrivateClipboardSuiteId, file,
            sha256: hash(readFileSync(join(output, file))), counts: { completedProbes: 3, acceptedWrites: 2 }, complete: true };
        evidence.privateClipboardEvidence = privateBinding;
        const at = Date.parse(report.frames.at(-1).after.afterOwnership.at);
        process.privateClipboard = { policy: ownedPrivateClipboardPolicy, appBirth: binding.appBirth, workerBirth: binding.workerBirth,
            outputDevice: binding.outputDevice, binding, originalTerminalState: 'gfmt1:lflag=5cb:oflag=3',
            restoredTerminalState: 'gfmt1:lflag=5cb:oflag=3', restoredAt: new Date(at + 1).toISOString() };
        proof.cleanedUpAt = new Date(at + 2).toISOString(); proof.sourceSnapshot.policy = sourceSnapshotPolicy;
        proof.sourceSnapshot.removedAt = new Date(at + 3).toISOString();
        settings.AllowOsc52ClipboardWrite = true; write('initial-settings.json', settings);
        context.initialSettingsSha256 = hash(readFileSync(join(output, 'initial-settings.json')));
        writeFileSync(join(output, 'collector-original.mjs'), originalCollector);
        const adapted = adaptCollector(originalCollector, join(context.sourceSnapshotRoot, 'scripts', 'terminfo', 'ownership.mjs'),
            { privateClipboardModulePath: join(context.sourceSnapshotRoot, 'scripts', 'terminfo', 'private-clipboard-worker.mjs') });
        writeFileSync(join(output, 'collector-adapter.mjs'), adapted); context.adapterBundleSha256 = hash(adapted);
        // Policy v6 always carries the grading suffix; add the synthetic
        // grading evidence beside the private-clipboard one.
        {
            const { syntheticGradingEvidence } = await import('./ascii-sgr-evidence.mjs');
            const { buildAsciiSgrGradingReport } = await import('../ascii-sgr-grading.mjs');
            const { adaptGradingCollector, gradingAdapterId } = await import('../grading-collector.mjs');
            const { asciiSgrSuiteId, asciiSgrContractsVersion } = await import('../ascii-sgr-contracts.mjs');
            const gradingBytes = Buffer.from(adaptGradingCollector(adapted));
            const grading = syntheticGradingEvidence(context.runId, hash(gradingBytes));
            grading.adapter = { id: gradingAdapterId, derivedFromAdapterId: 'ntilde-owned-macos-v1',
                bundleSha256: hash(gradingBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const gradingReport = buildAsciiSgrGradingReport(grading.frames, grading.run, context.runId, grading.adapter);
            write('grading-observations.json', gradingReport);
            write('grading-run.json', grading.run);
            writeFileSync(join(output, 'grading-snapshots.jsonl'), grading.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-grading.mjs'), gradingBytes);
            Object.assign(context, { gradingAdapterId, asciiSgrSuiteId, gradingBundleSha256: hash(gradingBytes),
                gradingContractsVersion: asciiSgrContractsVersion });
            evidence.gradingEvidence = { runId: context.runId, suiteId: asciiSgrSuiteId,
                file: 'grading-observations.json', sha256: hash(readFileSync(join(output, 'grading-observations.json'))),
                runFile: 'grading-run.json', runSha256: hash(readFileSync(join(output, 'grading-run.json'))),
                adapterBundleSha256: hash(gradingBytes), contractsVersion: asciiSgrContractsVersion,
                counts: { supported: 4 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v7 always carries the layered SGR-colors suffix as well.
            const { syntheticSgrColorsEvidence } = await import('./sgr-colors-evidence.mjs');
            const { buildSgrColorsGradingReport } = await import('../sgr-colors-grading.mjs');
            const { adaptSgrColorsCollector, sgrColorsAdapterId } = await import('../sgr-colors-grading-collector.mjs');
            const { sgrColorsSuiteId, sgrColorsContractsVersion } = await import('../sgr-colors-contracts.mjs');
            const sgrColorsBytes = Buffer.from(adaptSgrColorsCollector(gradingBytes.toString()));
            const sgrColors = syntheticSgrColorsEvidence(context.runId, hash(sgrColorsBytes));
            sgrColors.adapter = { id: sgrColorsAdapterId, derivedFromAdapterId: 'ntilde-owned-ascii-sgr-grading-v1',
                bundleSha256: hash(sgrColorsBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const sgrColorsReport = buildSgrColorsGradingReport(sgrColors.frames, sgrColors.run, context.runId, sgrColors.adapter);
            write('sgcolors-observations.json', sgrColorsReport);
            write('sgcolors-run.json', sgrColors.run);
            writeFileSync(join(output, 'sgcolors-snapshots.jsonl'), sgrColors.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-sgcolors.mjs'), sgrColorsBytes);
            Object.assign(context, { sgrColorsAdapterId, sgrColorsSuiteId, sgrColorsBundleSha256: hash(sgrColorsBytes),
                sgrColorsContractsVersion });
            evidence.sgrColorsEvidence = { runId: context.runId, suiteId: sgrColorsSuiteId,
                file: 'sgcolors-observations.json', sha256: hash(readFileSync(join(output, 'sgcolors-observations.json'))),
                runFile: 'sgcolors-run.json', runSha256: hash(readFileSync(join(output, 'sgcolors-run.json'))),
                adapterBundleSha256: hash(sgrColorsBytes), contractsVersion: sgrColorsContractsVersion,
                counts: { supported: 5 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v9 always carries the layered SGR-RGB suffix as well.
            const { syntheticSgrRgbEvidence } = await import('./sgr-rgb-evidence.mjs');
            const { buildSgrRgbGradingReport } = await import('../sgr-rgb-grading.mjs');
            const { adaptSgrRgbCollector, sgrRgbAdapterId } = await import('../sgr-rgb-grading-collector.mjs');
            const { sgrRgbSuiteId, sgrRgbContractsVersion } = await import('../sgr-rgb-contracts.mjs');
            const sgrRgbBytes = Buffer.from(adaptSgrRgbCollector(sgrColorsBytes.toString()));
            const sgrRgb = syntheticSgrRgbEvidence(context.runId, hash(sgrRgbBytes));
            sgrRgb.adapter = { id: sgrRgbAdapterId, derivedFromAdapterId: sgrColorsAdapterId,
                bundleSha256: hash(sgrRgbBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const sgrRgbReport = buildSgrRgbGradingReport(sgrRgb.frames, sgrRgb.run, context.runId, sgrRgb.adapter);
            write('sgrgb-observations.json', sgrRgbReport);
            write('sgrgb-run.json', sgrRgb.run);
            writeFileSync(join(output, 'sgrgb-snapshots.jsonl'), sgrRgb.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-sgrgb.mjs'), sgrRgbBytes);
            Object.assign(context, { sgrRgbAdapterId, sgrRgbSuiteId, sgrRgbBundleSha256: hash(sgrRgbBytes),
                sgrRgbContractsVersion });
            evidence.sgrRgbEvidence = { runId: context.runId, suiteId: sgrRgbSuiteId,
                file: 'sgrgb-observations.json', sha256: hash(readFileSync(join(output, 'sgrgb-observations.json'))),
                runFile: 'sgrgb-run.json', runSha256: hash(readFileSync(join(output, 'sgrgb-run.json'))),
                adapterBundleSha256: hash(sgrRgbBytes), contractsVersion: sgrRgbContractsVersion,
                counts: { supported: 2 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v10 always carries the layered SGR-attributes suffix as well.
            const { syntheticSgrAttrsEvidence } = await import('./sgr-attrs-evidence.mjs');
            const { buildSgrAttrsGradingReport } = await import('../sgr-attrs-grading.mjs');
            const { adaptSgrAttrsCollector, sgrAttrsAdapterId } = await import('../sgr-attrs-grading-collector.mjs');
            const { sgrAttrsSuiteId, sgrAttrsContractsVersion } = await import('../sgr-attrs-contracts.mjs');
            const sgrAttrsBytes = Buffer.from(adaptSgrAttrsCollector(sgrRgbBytes.toString()));
            const sgrAttrs = syntheticSgrAttrsEvidence(context.runId, hash(sgrAttrsBytes));
            sgrAttrs.adapter = { id: sgrAttrsAdapterId, derivedFromAdapterId: sgrRgbAdapterId,
                bundleSha256: hash(sgrAttrsBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const sgrAttrsReport = buildSgrAttrsGradingReport(sgrAttrs.frames, sgrAttrs.run, context.runId, sgrAttrs.adapter);
            write('sgattr-observations.json', sgrAttrsReport);
            write('sgattr-run.json', sgrAttrs.run);
            writeFileSync(join(output, 'sgattr-snapshots.jsonl'), sgrAttrs.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-sgattr.mjs'), sgrAttrsBytes);
            Object.assign(context, { sgrAttrsAdapterId, sgrAttrsSuiteId, sgrAttrsBundleSha256: hash(sgrAttrsBytes),
                sgrAttrsContractsVersion });
            evidence.sgrAttrsEvidence = { runId: context.runId, suiteId: sgrAttrsSuiteId,
                file: 'sgattr-observations.json', sha256: hash(readFileSync(join(output, 'sgattr-observations.json'))),
                runFile: 'sgattr-run.json', runSha256: hash(readFileSync(join(output, 'sgattr-run.json'))),
                adapterBundleSha256: hash(sgrAttrsBytes), contractsVersion: sgrAttrsContractsVersion,
                counts: { supported: 5 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v11 always carries the layered SGR-palette suffix as well.
            const { syntheticSgrPaletteEvidence } = await import('./sgr-palette-evidence.mjs');
            const { buildSgrPaletteGradingReport } = await import('../sgr-palette-grading.mjs');
            const { adaptSgrPaletteCollector, sgrPaletteAdapterId } = await import('../sgr-palette-grading-collector.mjs');
            const { sgrPaletteSuiteId, sgrPaletteContractsVersion } = await import('../sgr-palette-contracts.mjs');
            const sgrPaletteBytes = Buffer.from(adaptSgrPaletteCollector(sgrAttrsBytes.toString()));
            const sgrPalette = syntheticSgrPaletteEvidence(context.runId, hash(sgrPaletteBytes));
            sgrPalette.adapter = { id: sgrPaletteAdapterId, derivedFromAdapterId: sgrAttrsAdapterId,
                bundleSha256: hash(sgrPaletteBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const sgrPaletteReport = buildSgrPaletteGradingReport(sgrPalette.frames, sgrPalette.run, context.runId, sgrPalette.adapter);
            write('sgpal-observations.json', sgrPaletteReport);
            write('sgpal-run.json', sgrPalette.run);
            writeFileSync(join(output, 'sgpal-snapshots.jsonl'), sgrPalette.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-sgpal.mjs'), sgrPaletteBytes);
            Object.assign(context, { sgrPaletteAdapterId, sgrPaletteSuiteId, sgrPaletteBundleSha256: hash(sgrPaletteBytes),
                sgrPaletteContractsVersion });
            evidence.sgrPaletteEvidence = { runId: context.runId, suiteId: sgrPaletteSuiteId,
                file: 'sgpal-observations.json', sha256: hash(readFileSync(join(output, 'sgpal-observations.json'))),
                runFile: 'sgpal-run.json', runSha256: hash(readFileSync(join(output, 'sgpal-run.json'))),
                adapterBundleSha256: hash(sgrPaletteBytes), contractsVersion: sgrPaletteContractsVersion,
                counts: { supported: 6 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v12 always carries the layered SGR-truecolor suffix as well.
            const { syntheticSgrTruecolorEvidence } = await import('./sgr-truecolor-evidence.mjs');
            const { buildSgrTruecolorGradingReport } = await import('../sgr-truecolor-grading.mjs');
            const { adaptSgrTruecolorCollector, sgrTruecolorAdapterId } = await import('../sgr-truecolor-grading-collector.mjs');
            const { sgrTruecolorSuiteId, sgrTruecolorContractsVersion } = await import('../sgr-truecolor-contracts.mjs');
            const sgrTruecolorBytes = Buffer.from(adaptSgrTruecolorCollector(sgrPaletteBytes.toString()));
            const sgrTruecolor = syntheticSgrTruecolorEvidence(context.runId, hash(sgrTruecolorBytes));
            sgrTruecolor.adapter = { id: sgrTruecolorAdapterId, derivedFromAdapterId: sgrPaletteAdapterId,
                bundleSha256: hash(sgrTruecolorBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const sgrTruecolorReport = buildSgrTruecolorGradingReport(sgrTruecolor.frames, sgrTruecolor.run, context.runId, sgrTruecolor.adapter);
            write('sgtc-observations.json', sgrTruecolorReport);
            write('sgtc-run.json', sgrTruecolor.run);
            writeFileSync(join(output, 'sgtc-snapshots.jsonl'), sgrTruecolor.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-sgtc.mjs'), sgrTruecolorBytes);
            Object.assign(context, { sgrTruecolorAdapterId, sgrTruecolorSuiteId, sgrTruecolorBundleSha256: hash(sgrTruecolorBytes),
                sgrTruecolorContractsVersion });
            evidence.sgrTruecolorEvidence = { runId: context.runId, suiteId: sgrTruecolorSuiteId,
                file: 'sgtc-observations.json', sha256: hash(readFileSync(join(output, 'sgtc-observations.json'))),
                runFile: 'sgtc-run.json', runSha256: hash(readFileSync(join(output, 'sgtc-run.json'))),
                adapterBundleSha256: hash(sgrTruecolorBytes), contractsVersion: sgrTruecolorContractsVersion,
                counts: { supported: 2 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v13 always carries the truecolor-literal finding suffix as well.
            const { syntheticTruecolorFindingEvidence } = await import('./truecolor-literal-finding-evidence.mjs');
            const { buildTruecolorFindingReport } = await import('../truecolor-literal-finding-grading.mjs');
            const { truecolorFindingSuiteId, truecolorFindingContractsVersion } = await import('../truecolor-literal-finding-contracts.mjs');
            const finding = syntheticTruecolorFindingEvidence(context.runId);
            const findingReport = buildTruecolorFindingReport(finding.frames, finding.run, context.runId);
            write('sgtf-observations.json', findingReport);
            write('sgtf-run.json', finding.run);
            writeFileSync(join(output, 'sgtf-snapshots.jsonl'), finding.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            Object.assign(context, { truecolorFindingSuiteId, truecolorFindingContractsVersion });
            evidence.truecolorFindingEvidence = { runId: context.runId, suiteId: truecolorFindingSuiteId,
                file: 'sgtf-observations.json', sha256: hash(readFileSync(join(output, 'sgtf-observations.json'))),
                runFile: 'sgtf-run.json', runSha256: hash(readFileSync(join(output, 'sgtf-run.json'))),
                contractsVersion: truecolorFindingContractsVersion, counts: { supported: 10 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v14 always carries the layered SGR-underline-style suffix as well.
            const { syntheticSgrUnderlineEvidence } = await import('./sgr-underline-evidence.mjs');
            const { buildSgrUnderlineGradingReport } = await import('../sgr-underline-grading.mjs');
            const { adaptSgrUnderlineCollector, sgrUnderlineAdapterId } = await import('../sgr-underline-grading-collector.mjs');
            const { sgrUnderlineSuiteId, sgrUnderlineContractsVersion } = await import('../sgr-underline-contracts.mjs');
            const sgrUnderlineBytes = Buffer.from(adaptSgrUnderlineCollector(sgrTruecolorBytes.toString()));
            const sgrUnderline = syntheticSgrUnderlineEvidence(context.runId, hash(sgrUnderlineBytes));
            sgrUnderline.adapter = { id: sgrUnderlineAdapterId, derivedFromAdapterId: sgrTruecolorAdapterId,
                bundleSha256: hash(sgrUnderlineBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const sgrUnderlineReport = buildSgrUnderlineGradingReport(sgrUnderline.frames, sgrUnderline.run, context.runId, sgrUnderline.adapter);
            write('sgus-observations.json', sgrUnderlineReport);
            write('sgus-run.json', sgrUnderline.run);
            writeFileSync(join(output, 'sgus-snapshots.jsonl'), sgrUnderline.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-sgus.mjs'), sgrUnderlineBytes);
            Object.assign(context, { sgrUnderlineAdapterId, sgrUnderlineSuiteId, sgrUnderlineBundleSha256: hash(sgrUnderlineBytes),
                sgrUnderlineContractsVersion });
            evidence.sgrUnderlineEvidence = { runId: context.runId, suiteId: sgrUnderlineSuiteId,
                file: 'sgus-observations.json', sha256: hash(readFileSync(join(output, 'sgus-observations.json'))),
                runFile: 'sgus-run.json', runSha256: hash(readFileSync(join(output, 'sgus-run.json'))),
                adapterBundleSha256: hash(sgrUnderlineBytes), contractsVersion: sgrUnderlineContractsVersion,
                counts: { supported: 4 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v15 always carries the layered SGR-underline-color suffix as well.
            const { syntheticSgrUnderlineColorEvidence } = await import('./sgr-underline-color-evidence.mjs');
            const { buildSgrUnderlineColorGradingReport } = await import('../sgr-underline-color-grading.mjs');
            const { adaptSgrUnderlineColorCollector, sgrUnderlineColorAdapterId } = await import('../sgr-underline-color-grading-collector.mjs');
            const { sgrUnderlineColorSuiteId, sgrUnderlineColorContractsVersion } = await import('../sgr-underline-color-contracts.mjs');
            const sgrUnderlineColorBytes = Buffer.from(adaptSgrUnderlineColorCollector(sgrUnderlineBytes.toString()));
            const sgrUnderlineColor = syntheticSgrUnderlineColorEvidence(context.runId, hash(sgrUnderlineColorBytes));
            sgrUnderlineColor.adapter = { id: sgrUnderlineColorAdapterId, derivedFromAdapterId: sgrUnderlineAdapterId,
                bundleSha256: hash(sgrUnderlineColorBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const sgrUnderlineColorReport = buildSgrUnderlineColorGradingReport(sgrUnderlineColor.frames, sgrUnderlineColor.run, context.runId, sgrUnderlineColor.adapter);
            write('sguc-observations.json', sgrUnderlineColorReport);
            write('sguc-run.json', sgrUnderlineColor.run);
            writeFileSync(join(output, 'sguc-snapshots.jsonl'), sgrUnderlineColor.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-sguc.mjs'), sgrUnderlineColorBytes);
            Object.assign(context, { sgrUnderlineColorAdapterId, sgrUnderlineColorSuiteId, sgrUnderlineColorBundleSha256: hash(sgrUnderlineColorBytes),
                sgrUnderlineColorContractsVersion });
            evidence.sgrUnderlineColorEvidence = { runId: context.runId, suiteId: sgrUnderlineColorSuiteId,
                file: 'sguc-observations.json', sha256: hash(readFileSync(join(output, 'sguc-observations.json'))),
                runFile: 'sguc-run.json', runSha256: hash(readFileSync(join(output, 'sguc-run.json'))),
                adapterBundleSha256: hash(sgrUnderlineColorBytes), contractsVersion: sgrUnderlineColorContractsVersion,
                counts: { supported: 3 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v16 always carries the App-observed underline-resolution suffix as well.
            const { syntheticUnderlineResolvedEvidence } = await import('./underline-color-resolved-evidence.mjs');
            const { buildUnderlineResolvedReport } = await import('../sgr-underline-color-resolved-grading.mjs');
            const { underlineResolvedSuiteId, underlineResolvedContractsVersion } = await import('../sgr-underline-color-resolved-contracts.mjs');
            const resolvedUnderline = syntheticUnderlineResolvedEvidence(context.runId);
            const resolvedUnderlineReport = buildUnderlineResolvedReport(resolvedUnderline.frames, resolvedUnderline.run, context.runId);
            write('sgur-observations.json', resolvedUnderlineReport);
            write('sgur-run.json', resolvedUnderline.run);
            writeFileSync(join(output, 'sgur-snapshots.jsonl'), resolvedUnderline.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            Object.assign(context, { underlineResolvedSuiteId, underlineResolvedContractsVersion });
            evidence.underlineResolvedEvidence = { runId: context.runId, suiteId: underlineResolvedSuiteId,
                file: 'sgur-observations.json', sha256: hash(readFileSync(join(output, 'sgur-observations.json'))),
                runFile: 'sgur-run.json', runSha256: hash(readFileSync(join(output, 'sgur-run.json'))),
                contractsVersion: underlineResolvedContractsVersion, counts: { supported: 3 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v17 always carries the layered SGR selective-reset suffix as well.
            const { syntheticSgrResetEvidence } = await import('./sgr-reset-evidence.mjs');
            const { buildSgrResetGradingReport } = await import('../sgr-reset-grading.mjs');
            const { adaptSgrResetCollector, sgrResetAdapterId } = await import('../sgr-reset-grading-collector.mjs');
            const { sgrResetSuiteId, sgrResetContractsVersion } = await import('../sgr-reset-contracts.mjs');
            const sgrResetBytes = Buffer.from(adaptSgrResetCollector(sgrUnderlineColorBytes.toString()));
            const sgrReset = syntheticSgrResetEvidence(context.runId, hash(sgrResetBytes));
            sgrReset.adapter = { id: sgrResetAdapterId, derivedFromAdapterId: sgrUnderlineColorAdapterId,
                bundleSha256: hash(sgrResetBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const sgrResetReport = buildSgrResetGradingReport(sgrReset.frames, sgrReset.run, context.runId, sgrReset.adapter);
            write('sgsr-observations.json', sgrResetReport);
            write('sgsr-run.json', sgrReset.run);
            writeFileSync(join(output, 'sgsr-snapshots.jsonl'), sgrReset.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-sgsr.mjs'), sgrResetBytes);
            Object.assign(context, { sgrResetAdapterId, sgrResetSuiteId, sgrResetBundleSha256: hash(sgrResetBytes), sgrResetContractsVersion });
            evidence.sgrResetEvidence = { runId: context.runId, suiteId: sgrResetSuiteId,
                file: 'sgsr-observations.json', sha256: hash(readFileSync(join(output, 'sgsr-observations.json'))),
                runFile: 'sgsr-run.json', runSha256: hash(readFileSync(join(output, 'sgsr-run.json'))),
                adapterBundleSha256: hash(sgrResetBytes), contractsVersion: sgrResetContractsVersion,
                counts: { supported: 2 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v18 and later always carry the layered SGR closure suffix as well.
            const { syntheticSgrClosureEvidence } = await import('./sgr-closure-evidence.mjs');
            const { buildSgrClosureGradingReport } = await import('../sgr-closure-grading.mjs');
            const { adaptSgrClosureCollector, sgrClosureAdapterId } = await import('../sgr-closure-grading-collector.mjs');
            const { sgrClosureSuiteId, sgrClosureContractsVersion } = await import('../sgr-closure-contracts.mjs');
            const sgrClosureBytes = Buffer.from(adaptSgrClosureCollector(sgrResetBytes.toString()));
            const sgrClosure = syntheticSgrClosureEvidence(context.runId, hash(sgrClosureBytes));
            sgrClosure.adapter = { id: sgrClosureAdapterId, derivedFromAdapterId: sgrResetAdapterId,
                bundleSha256: hash(sgrClosureBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const sgrClosureReport = buildSgrClosureGradingReport(sgrClosure.frames, sgrClosure.run, context.runId, sgrClosure.adapter);
            write('sgcl-observations.json', sgrClosureReport);
            write('sgcl-run.json', sgrClosure.run);
            writeFileSync(join(output, 'sgcl-snapshots.jsonl'), sgrClosure.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-sgcl.mjs'), sgrClosureBytes);
            Object.assign(context, { sgrClosureAdapterId, sgrClosureSuiteId, sgrClosureBundleSha256: hash(sgrClosureBytes), sgrClosureContractsVersion });
            evidence.sgrClosureEvidence = { runId: context.runId, suiteId: sgrClosureSuiteId,
                file: 'sgcl-observations.json', sha256: hash(readFileSync(join(output, 'sgcl-observations.json'))),
                runFile: 'sgcl-run.json', runSha256: hash(readFileSync(join(output, 'sgcl-run.json'))),
                adapterBundleSha256: hash(sgrClosureBytes), contractsVersion: sgrClosureContractsVersion,
                counts: { supported: 3 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v19 and later always carry the layered erase-in-line suffix as well.
            const { syntheticEraseLineEvidence } = await import('./erase-line-evidence.mjs');
            const { buildEraseLineGradingReport } = await import('../erase-line-grading.mjs');
            const { adaptEraseLineCollector, eraseLineAdapterId } = await import('../erase-line-grading-collector.mjs');
            const { eraseLineSuiteId, eraseLineContractsVersion } = await import('../erase-line-contracts.mjs');
            const eraseLineBytes = Buffer.from(adaptEraseLineCollector(sgrClosureBytes.toString()));
            const eraseLine = syntheticEraseLineEvidence(context.runId, hash(eraseLineBytes));
            eraseLine.adapter = { id: eraseLineAdapterId, derivedFromAdapterId: sgrClosureAdapterId,
                bundleSha256: hash(eraseLineBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const eraseLineReport = buildEraseLineGradingReport(eraseLine.frames, eraseLine.run, context.runId, eraseLine.adapter);
            write('erln-observations.json', eraseLineReport);
            write('erln-run.json', eraseLine.run);
            writeFileSync(join(output, 'erln-snapshots.jsonl'), eraseLine.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-erln.mjs'), eraseLineBytes);
            Object.assign(context, { eraseLineAdapterId, eraseLineSuiteId, eraseLineBundleSha256: hash(eraseLineBytes), eraseLineContractsVersion });
            evidence.eraseLineEvidence = { runId: context.runId, suiteId: eraseLineSuiteId,
                file: 'erln-observations.json', sha256: hash(readFileSync(join(output, 'erln-observations.json'))),
                runFile: 'erln-run.json', runSha256: hash(readFileSync(join(output, 'erln-run.json'))),
                adapterBundleSha256: hash(eraseLineBytes), contractsVersion: eraseLineContractsVersion,
                counts: { supported: 3 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v20 always carries the layered character-editing suffix as well.
            const { syntheticCharEditEvidence } = await import('./char-edit-evidence.mjs');
            const { buildCharEditGradingReport } = await import('../char-edit-grading.mjs');
            const { adaptCharEditCollector, charEditAdapterId } = await import('../char-edit-grading-collector.mjs');
            const { charEditSuiteId, charEditContractsVersion } = await import('../char-edit-contracts.mjs');
            const charEditBytes = Buffer.from(adaptCharEditCollector(eraseLineBytes.toString()));
            const charEdit = syntheticCharEditEvidence(context.runId, hash(charEditBytes));
            charEdit.adapter = { id: charEditAdapterId, derivedFromAdapterId: eraseLineAdapterId,
                bundleSha256: hash(charEditBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const charEditReport = buildCharEditGradingReport(charEdit.frames, charEdit.run, context.runId, charEdit.adapter);
            write('ched-observations.json', charEditReport);
            write('ched-run.json', charEdit.run);
            writeFileSync(join(output, 'ched-snapshots.jsonl'), charEdit.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-ched.mjs'), charEditBytes);
            Object.assign(context, { charEditAdapterId, charEditSuiteId, charEditBundleSha256: hash(charEditBytes), charEditContractsVersion });
            evidence.charEditEvidence = { runId: context.runId, suiteId: charEditSuiteId,
                file: 'ched-observations.json', sha256: hash(readFileSync(join(output, 'ched-observations.json'))),
                runFile: 'ched-run.json', runSha256: hash(readFileSync(join(output, 'ched-run.json'))),
                adapterBundleSha256: hash(charEditBytes), contractsVersion: charEditContractsVersion,
                counts: { supported: 3 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v21 always carries the layered erase-in-display suffix as well.
            const { syntheticEraseDisplayEvidence } = await import('./erase-display-evidence.mjs');
            const { buildEraseDisplayGradingReport } = await import('../erase-display-grading.mjs');
            const { adaptEraseDisplayCollector, eraseDisplayAdapterId } = await import('../erase-display-grading-collector.mjs');
            const { eraseDisplaySuiteId, eraseDisplayContractsVersion } = await import('../erase-display-contracts.mjs');
            const eraseDisplayBytes = Buffer.from(adaptEraseDisplayCollector(charEditBytes.toString()));
            const eraseDisplay = syntheticEraseDisplayEvidence(context.runId, hash(eraseDisplayBytes));
            eraseDisplay.adapter = { id: eraseDisplayAdapterId, derivedFromAdapterId: charEditAdapterId,
                bundleSha256: hash(eraseDisplayBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const eraseDisplayReport = buildEraseDisplayGradingReport(eraseDisplay.frames, eraseDisplay.run, context.runId, eraseDisplay.adapter);
            write('erdi-observations.json', eraseDisplayReport);
            write('erdi-run.json', eraseDisplay.run);
            writeFileSync(join(output, 'erdi-snapshots.jsonl'), eraseDisplay.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-erdi.mjs'), eraseDisplayBytes);
            Object.assign(context, { eraseDisplayAdapterId, eraseDisplaySuiteId, eraseDisplayBundleSha256: hash(eraseDisplayBytes), eraseDisplayContractsVersion });
            evidence.eraseDisplayEvidence = { runId: context.runId, suiteId: eraseDisplaySuiteId,
                file: 'erdi-observations.json', sha256: hash(readFileSync(join(output, 'erdi-observations.json'))),
                runFile: 'erdi-run.json', runSha256: hash(readFileSync(join(output, 'erdi-run.json'))),
                adapterBundleSha256: hash(eraseDisplayBytes), contractsVersion: eraseDisplayContractsVersion,
                counts: { supported: 3 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v22 always carries the layered line-editing suffix as well.
            const { syntheticLineEditEvidence } = await import('./line-edit-evidence.mjs');
            const { buildLineEditGradingReport } = await import('../line-edit-grading.mjs');
            const { adaptLineEditCollector, lineEditAdapterId } = await import('../line-edit-grading-collector.mjs');
            const { lineEditSuiteId, lineEditContractsVersion } = await import('../line-edit-contracts.mjs');
            const lineEditBytes = Buffer.from(adaptLineEditCollector(eraseDisplayBytes.toString()));
            const lineEdit = syntheticLineEditEvidence(context.runId, hash(lineEditBytes));
            lineEdit.adapter = { id: lineEditAdapterId, derivedFromAdapterId: eraseDisplayAdapterId,
                bundleSha256: hash(lineEditBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const lineEditReport = buildLineEditGradingReport(lineEdit.frames, lineEdit.run, context.runId, lineEdit.adapter);
            write('ledt-observations.json', lineEditReport);
            write('ledt-run.json', lineEdit.run);
            writeFileSync(join(output, 'ledt-snapshots.jsonl'), lineEdit.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-ledt.mjs'), lineEditBytes);
            Object.assign(context, { lineEditAdapterId, lineEditSuiteId, lineEditBundleSha256: hash(lineEditBytes), lineEditContractsVersion });
            evidence.lineEditEvidence = { runId: context.runId, suiteId: lineEditSuiteId,
                file: 'ledt-observations.json', sha256: hash(readFileSync(join(output, 'ledt-observations.json'))),
                runFile: 'ledt-run.json', runSha256: hash(readFileSync(join(output, 'ledt-run.json'))),
                adapterBundleSha256: hash(lineEditBytes), contractsVersion: lineEditContractsVersion,
                counts: { supported: 2 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v23 always carries the layered charsets suffix as well.
            const { syntheticCharsetsEvidence } = await import('./charsets-evidence.mjs');
            const { buildCharsetsGradingReport } = await import('../charsets-grading.mjs');
            const { adaptCharsetsCollector, charsetsAdapterId } = await import('../charsets-grading-collector.mjs');
            const { charsetsSuiteId, charsetsContractsVersion } = await import('../charsets-contracts.mjs');
            const charsetsBytes = Buffer.from(adaptCharsetsCollector(lineEditBytes.toString()));
            const charsets = syntheticCharsetsEvidence(context.runId, hash(charsetsBytes));
            charsets.adapter = { id: charsetsAdapterId, derivedFromAdapterId: lineEditAdapterId,
                bundleSha256: hash(charsetsBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const charsetsReport = buildCharsetsGradingReport(charsets.frames, charsets.run, context.runId, charsets.adapter);
            write('chrs-observations.json', charsetsReport);
            write('chrs-run.json', charsets.run);
            writeFileSync(join(output, 'chrs-snapshots.jsonl'), charsets.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-chrs.mjs'), charsetsBytes);
            Object.assign(context, { charsetsAdapterId, charsetsSuiteId, charsetsBundleSha256: hash(charsetsBytes), charsetsContractsVersion });
            evidence.charsetsEvidence = { runId: context.runId, suiteId: charsetsSuiteId,
                file: 'chrs-observations.json', sha256: hash(readFileSync(join(output, 'chrs-observations.json'))),
                runFile: 'chrs-run.json', runSha256: hash(readFileSync(join(output, 'chrs-run.json'))),
                adapterBundleSha256: hash(charsetsBytes), contractsVersion: charsetsContractsVersion,
                counts: { supported: 4 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v24 always carries the layered scrolling/history suffix as well.
            const { syntheticScrollbackEvidence } = await import('./scrollback-evidence.mjs');
            const { buildScrollbackGradingReport } = await import('../scrollback-grading.mjs');
            const { adaptScrollbackCollector, scrollbackAdapterId } = await import('../scrollback-grading-collector.mjs');
            const { scrollbackSuiteId, scrollbackContractsVersion } = await import('../scrollback-contracts.mjs');
            const scrollbackBytes = Buffer.from(adaptScrollbackCollector(charsetsBytes.toString()));
            const scrollback = syntheticScrollbackEvidence(context.runId, hash(scrollbackBytes));
            scrollback.adapter = { id: scrollbackAdapterId, derivedFromAdapterId: charsetsAdapterId,
                bundleSha256: hash(scrollbackBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const scrollbackReport = buildScrollbackGradingReport(scrollback.frames, scrollback.run, context.runId, scrollback.adapter);
            write('scrl-observations.json', scrollbackReport);
            write('scrl-run.json', scrollback.run);
            writeFileSync(join(output, 'scrl-snapshots.jsonl'), scrollback.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-scrl.mjs'), scrollbackBytes);
            Object.assign(context, { scrollbackAdapterId, scrollbackSuiteId, scrollbackBundleSha256: hash(scrollbackBytes), scrollbackContractsVersion });
            evidence.scrollbackEvidence = { runId: context.runId, suiteId: scrollbackSuiteId,
                file: 'scrl-observations.json', sha256: hash(readFileSync(join(output, 'scrl-observations.json'))),
                runFile: 'scrl-run.json', runSha256: hash(readFileSync(join(output, 'scrl-run.json'))),
                adapterBundleSha256: hash(scrollbackBytes), contractsVersion: scrollbackContractsVersion,
                counts: { supported: 9 }, provisionalUntil: 'cell-receipt.json' };
            // Policy v25 always carries the layered saved-state/reset suffix as well.
            const { syntheticSavedResetEvidence } = await import('./saved-reset-evidence.mjs');
            const { buildSavedResetGradingReport } = await import('../saved-reset-grading.mjs');
            const { adaptSavedResetCollector, savedResetAdapterId } = await import('../saved-reset-grading-collector.mjs');
            const { savedResetSuiteId, savedResetContractsVersion } = await import('../saved-reset-contracts.mjs');
            const { savedResetCounts } = await import('../saved-reset-receipt.mjs');
            const savedResetBytes = Buffer.from(adaptSavedResetCollector(scrollbackBytes.toString()));
            const savedReset = syntheticSavedResetEvidence(context.runId, hash(savedResetBytes));
            savedReset.adapter = { id: savedResetAdapterId, derivedFromAdapterId: scrollbackAdapterId,
                bundleSha256: hash(savedResetBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const savedResetReport = buildSavedResetGradingReport(savedReset.frames, savedReset.run, context.runId, savedReset.adapter);
            write('svrs-observations.json', savedResetReport);
            write('svrs-run.json', savedReset.run);
            writeFileSync(join(output, 'svrs-snapshots.jsonl'), savedReset.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-svrs.mjs'), savedResetBytes);
            Object.assign(context, { savedResetAdapterId, savedResetSuiteId, savedResetBundleSha256: hash(savedResetBytes), savedResetContractsVersion });
            evidence.savedResetEvidence = { runId: context.runId, suiteId: savedResetSuiteId,
                file: 'svrs-observations.json', sha256: hash(readFileSync(join(output, 'svrs-observations.json'))),
                runFile: 'svrs-run.json', runSha256: hash(readFileSync(join(output, 'svrs-run.json'))),
                adapterBundleSha256: hash(savedResetBytes), contractsVersion: savedResetContractsVersion,
                ...savedResetCounts(savedResetReport), provisionalUntil: 'cell-receipt.json' };
            // Policy v26 always carries the layered erase attributes / erase margins suffix as well.
            const { syntheticEraseAttrsRegionEvidence } = await import('./erase-attrs-region-evidence.mjs');
            const { buildEraseAttrsRegionGradingReport } = await import('../erase-attrs-region-grading.mjs');
            const { adaptEraseAttrsRegionCollector, eraseAttrsRegionAdapterId } = await import('../erase-attrs-region-grading-collector.mjs');
            const { eraseAttrsRegionSuiteId, eraseAttrsRegionContractsVersion } = await import('../erase-attrs-region-contracts.mjs');
            const { eraseAttrsRegionCounts } = await import('../erase-attrs-region-receipt.mjs');
            const eraseAttrsRegionBytes = Buffer.from(adaptEraseAttrsRegionCollector(savedResetBytes.toString()));
            const eraseAttrsRegion = syntheticEraseAttrsRegionEvidence(context.runId, hash(eraseAttrsRegionBytes));
            eraseAttrsRegion.adapter = { id: eraseAttrsRegionAdapterId, derivedFromAdapterId: savedResetAdapterId,
                bundleSha256: hash(eraseAttrsRegionBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const eraseAttrsRegionReport = buildEraseAttrsRegionGradingReport(eraseAttrsRegion.frames, eraseAttrsRegion.run, context.runId, eraseAttrsRegion.adapter);
            write('erar-observations.json', eraseAttrsRegionReport);
            write('erar-run.json', eraseAttrsRegion.run);
            writeFileSync(join(output, 'erar-snapshots.jsonl'), eraseAttrsRegion.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-erar.mjs'), eraseAttrsRegionBytes);
            Object.assign(context, { eraseAttrsRegionAdapterId, eraseAttrsRegionSuiteId, eraseAttrsRegionBundleSha256: hash(eraseAttrsRegionBytes), eraseAttrsRegionContractsVersion });
            evidence.eraseAttrsRegionEvidence = { runId: context.runId, suiteId: eraseAttrsRegionSuiteId,
                file: 'erar-observations.json', sha256: hash(readFileSync(join(output, 'erar-observations.json'))),
                runFile: 'erar-run.json', runSha256: hash(readFileSync(join(output, 'erar-run.json'))),
                adapterBundleSha256: hash(eraseAttrsRegionBytes), contractsVersion: eraseAttrsRegionContractsVersion,
                ...eraseAttrsRegionCounts(eraseAttrsRegionReport), provisionalUntil: 'cell-receipt.json' };
            // Policy v27 always carries the layered rectangle-editing suffix as well.
            const { syntheticRectEditEvidence } = await import('./rect-edit-evidence.mjs');
            const { buildRectEditGradingReport } = await import('../rect-edit-grading.mjs');
            const { adaptRectEditCollector, rectEditAdapterId } = await import('../rect-edit-grading-collector.mjs');
            const { rectEditSuiteId, rectEditContractsVersion } = await import('../rect-edit-contracts.mjs');
            const { rectEditCounts } = await import('../rect-edit-receipt.mjs');
            const rectEditBytes = Buffer.from(adaptRectEditCollector(eraseAttrsRegionBytes.toString()));
            const rectEdit = syntheticRectEditEvidence(context.runId, hash(rectEditBytes));
            rectEdit.adapter = { id: rectEditAdapterId, derivedFromAdapterId: eraseAttrsRegionAdapterId,
                bundleSha256: hash(rectEditBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const rectEditReport = buildRectEditGradingReport(rectEdit.frames, rectEdit.run, context.runId, rectEdit.adapter);
            write('rect-observations.json', rectEditReport);
            write('rect-run.json', rectEdit.run);
            writeFileSync(join(output, 'rect-snapshots.jsonl'), rectEdit.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-rect.mjs'), rectEditBytes);
            Object.assign(context, { rectEditAdapterId, rectEditSuiteId, rectEditBundleSha256: hash(rectEditBytes), rectEditContractsVersion });
            evidence.rectEditEvidence = { runId: context.runId, suiteId: rectEditSuiteId,
                file: 'rect-observations.json', sha256: hash(readFileSync(join(output, 'rect-observations.json'))),
                runFile: 'rect-run.json', runSha256: hash(readFileSync(join(output, 'rect-run.json'))),
                adapterBundleSha256: hash(rectEditBytes), contractsVersion: rectEditContractsVersion,
                counts: rectEditCounts(rectEditReport), provisionalUntil: 'cell-receipt.json' };
        }
        raw.collectorAdapter.bundleSha256 = context.adapterBundleSha256;
        raw.rawReplies['collector.ntildePrivateClipboard'] = api.worker.summary;
        raw.observations = raw.observations.filter(value => !PRIVATE_CLIPBOARD_PROBES.includes(value.featureId));
        raw.assertions = raw.assertions.filter(value => !PRIVATE_CLIPBOARD_PROBES.includes(value.featureId));
        const screen = report.frames[0].before.screen, row = Math.max(1, screen.cursorRow - (screen.bufferState.originMode ? screen.bufferState.scrollTop : 0) + 1), col = screen.cursorCol + 1;
        const queryTrace = (sequence, reply, captures) => ({ sequence, match: [reply, ...captures], reason: 'reply', raw: reply, rawBase64: Buffer.from(reply).toString('base64') });
        for (const [index, probeId] of PRIVATE_CLIPBOARD_PROBES.entries()) {
            const result = report.callbackResults[index].result, writes = [], queries = [], events = [];
            if (index !== 2) {
                const nonce = report.controller.events.find(value => value.probeId === probeId && value.request.operation === 'armWrite').request.value;
                const sequence = '\x1b]52;c;' + Buffer.from(nonce).toString('base64') + '\x07'; writes.push(sequence); events.push({ kind: 'write', sequence });
                const query = queryTrace('\x1b[6n', '\x1b[' + row + ';' + col + 'R', [String(row), String(col)]); queries.push(query); events.push({ kind: 'query', ...query });
            }
            if (index !== 1) { const query = queryTrace('\x1b]52;c;?\x07', emptyPrivateClipboardReply, ['']); queries.push(query); events.push({ kind: 'query', ...query }); raw.rawReplies[probeId + '.callbackResponse'] = emptyPrivateClipboardReply; }
            raw.rawReplies[probeId] = JSON.stringify({ writes, queries, events, clipboard: [{ profile: 'ntilde-private-app-v1', probeId, restored: true }] });
            raw.observations.push({ featureId: probeId, ...result.observation, rawReplyRef: probeId });
            raw.assertions.push(...(result.assertions ?? []).map(value => ({ featureId: probeId, ...value, rawReplyRef: probeId })));
            delete raw.ungradedDiagnostics[probeId];
        }
        write('raw.json', raw); write('context.json', context); write('ownership-proof.json', proof);
        await action({ output, write, context, proof, report, evidence, raw, manifest, old,
            finish: () => completeOwnedReceipt(output, join(output, 'already-removed-fixture'), evidence) });
    } finally { rmSync(output, { recursive: true, force: true }); }
}
