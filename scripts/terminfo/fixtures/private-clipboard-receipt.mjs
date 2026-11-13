// Synthetic suffix over untouched real v11 prefix DATA. No archive module executes.
import { cpSync, mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { historicalArchive } from './captured-sources.mjs';
import { privateClipboardFixture } from './private-clipboard-owned.mjs';
import { original as originalCollector } from './private-clipboard-upstream.mjs';
import { capturedV41SourceFiles, capturedV44SourceFiles, ownedSourcePath } from '../owned-sources.mjs';
import { defaultInputModes, inputPlanForPolicy, inputPlanV3 } from '../input-plan.mjs';
import { inputCounts } from '../input-receipt.mjs';
import { boundInputScreen, buildInputReport, inputModeReplyHex } from '../input-state.mjs';
import { syntheticInputJournal } from './input-journal.mjs';
import { buildQueryReport, queryPlan } from '../queries.mjs';
import { rederivePrivateAdapter } from '../private-clipboard-receipt.mjs';
import { ownedPrivateClipboardPolicy, ownedPrivateClipboardSuiteId } from '../private-clipboard-worker.mjs';
import { PRIVATE_CLIPBOARD_PROBES } from '../private-clipboard-controller.mjs';
import { privateClipboardOutputPolicy } from '../terminal-output.mjs';
import { emptyPrivateClipboardReply, privateClipboardEvidenceJson } from '../private-clipboard.mjs';
import { completeOwnedReceipt } from '../receipt.mjs';
// By default this fixture is the v34 / receipt-40 prefix. Core gaps have separate retained-evidence tests. The `routed` variant is the
// current policy: the benchmark input plan replaces the archived 39-event one and the layered routed-input suffix is added.
const legacyPolicy = 'readonly-owned-source-graph-v34';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** The JSON writer of one fixture directory. */
const writerFor = output => (file, value) => writeFileSync(join(output, file), JSON.stringify(value, null, 2) + '\n');

/** Synthetic old-scope source projection. Normalize checkout line endings before
 * dropping only the newer suffixes' imports (Core gaps, runtime-color reset, OSC 133); the historical production maps stay fixed. */
export function legacyPrefixSource(bytes) {
    return Buffer.from(bytes.toString().replaceAll('\r\n', '\n')
        .replace(/^import .*from '\.\/(?:terminfo\/)?(?:core-wrap-[a-z-]+|core-sgr-raster(?:-receipt)?|core-reverse-wide(?:-receipt)?)\.mjs';\n/gm, '')
        .replace(/^import .*from '\.\/(?:terminfo\/)?(?:core-erase|core-tab-stops)(?:-receipt)?\.mjs';\n/gm, '')
        .replace(/^import .*from '\.\/(?:terminfo\/)?(?:core-gaps(?:-receipt)?|core-text-cursor(?:-receipt)?|input-plan|routed-input-[a-z-]+|color-reset-[a-z-]+)\.mjs';\n/gm, '')
        .replace(/^import .*from '\.\/(?:terminfo\/)?(?:osc133|osc78|title-stack|osc-title|insert-replace|xtgettcap|modify-other-keys|osc9)-[a-z-]+\.mjs';\n/gm, ''));
}

/** Remove the newer suffixes (text/cursor, OSC 133, title/icon stack, modifyOtherKeys) from the synthetic v37 scope on LF and CRLF checkouts. */
export function routedPrefixSource(bytes) {
    return Buffer.from(bytes.toString().replaceAll('\r\n', '\n')
        .replace(/^import .*from '\.\/(?:terminfo\/)?(?:core-wrap-[a-z-]+|core-sgr-raster(?:-receipt)?|core-reverse-wide(?:-receipt)?)\.mjs';\n/gm, '')
        .replace(/^import .*from '\.\/(?:terminfo\/)?(?:core-erase|core-tab-stops)(?:-receipt)?\.mjs';\n/gm, '')
        .replace(/^import .*from '\.\/(?:terminfo\/)?(?:core-text-cursor(?:-receipt)?|osc133-[a-z-]+|osc78-[a-z-]+|title-stack-[a-z-]+|osc-title-[a-z-]+)\.mjs';\n/gm, '')
        .replace(/^import .*from '\.\/(?:terminfo\/)?(?:insert-replace|xtgettcap|modify-other-keys|osc9)-[a-z-]+\.mjs';\n/gm, ''));
}

/** The opt-in title-stack suffix: synthetic frames from the suite's own oracle (never collected ones), written into `output` and declared in `context` and `evidence`. */
async function addTitleStackEvidence({ output, write, context, evidence, lrMarginsBytes }) {
    const { syntheticTitleStackEvidence } = await import('./title-stack-evidence.mjs');
    const { buildTitleStackGradingReport, titleStackCounts } = await import('../title-stack-grading.mjs');
    const { adaptTitleStackCollector, titleStackAdapterId } = await import('../title-stack-grading-collector.mjs');
    const { titleStackSuiteId, titleStackContractsVersion } = await import('../title-stack-contracts.mjs');
    const titleStackBytes = Buffer.from(adaptTitleStackCollector(lrMarginsBytes.toString()));
    const titleStack = syntheticTitleStackEvidence({ runId: context.runId, bundleSha256: hash(titleStackBytes) });
    titleStack.adapter = { ...titleStack.adapter, upstreamBundleSha256: context.upstreamBundleSha256 };
    const titleStackReport = buildTitleStackGradingReport(titleStack.frames, titleStack.run, context.runId, titleStack.adapter);
    write('tst-observations.json', titleStackReport);
    write('tst-run.json', titleStack.run);
    writeFileSync(join(output, 'tst-snapshots.jsonl'), titleStack.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
    writeFileSync(join(output, 'collector-tst.mjs'), titleStackBytes);
    Object.assign(context, { titleStackAdapterId, titleStackSuiteId, titleStackBundleSha256: hash(titleStackBytes), titleStackContractsVersion });
    evidence.titleStackEvidence = { runId: context.runId, suiteId: titleStackSuiteId,
        file: 'tst-observations.json', sha256: hash(readFileSync(join(output, 'tst-observations.json'))),
        runFile: 'tst-run.json', runSha256: hash(readFileSync(join(output, 'tst-run.json'))),
        adapterBundleSha256: hash(titleStackBytes), contractsVersion: titleStackContractsVersion,
        counts: titleStackCounts(titleStackReport), provisionalUntil: 'cell-receipt.json' };
}

/** The opt-in OSC 9 suffix: synthetic frames from the suite's own oracle (never collected ones), layered over the left/right-margin bytes the title-stack suffix also uses. */
async function addOsc9Evidence({ output, write, context, evidence, lrMarginsBytes }) {
    const { syntheticOsc9Evidence } = await import('./osc9-evidence.mjs');
    const { buildOsc9Report } = await import('../osc9-grading.mjs');
    const { osc9Counts } = await import('../osc9-receipt.mjs');
    const { adaptOsc9Collector, osc9AdapterId } = await import('../osc9-grading-collector.mjs');
    const { osc9SuiteId, osc9ContractsVersion } = await import('../osc9-contracts.mjs');
    const osc9Bytes = Buffer.from(adaptOsc9Collector(lrMarginsBytes.toString()));
    const osc9 = syntheticOsc9Evidence({ runId: context.runId, bundleSha256: hash(osc9Bytes) });
    osc9.adapter = { ...osc9.adapter, upstreamBundleSha256: context.upstreamBundleSha256 };
    const osc9Report = buildOsc9Report(osc9.frames, osc9.run, context.runId, osc9.adapter);
    write('osn-observations.json', osc9Report);
    write('osn-run.json', osc9.run);
    write('osn-focus.json', osc9.focus);
    writeFileSync(join(output, 'osn-snapshots.jsonl'), osc9.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
    writeFileSync(join(output, 'collector-osn.mjs'), osc9Bytes);
    Object.assign(context, { osc9AdapterId, osc9SuiteId, osc9BundleSha256: hash(osc9Bytes), osc9ContractsVersion });
    evidence.osc9Evidence = { runId: context.runId, suiteId: osc9SuiteId, file: 'osn-observations.json', sha256: hash(readFileSync(join(output, 'osn-observations.json'))),
        runFile: 'osn-run.json', runSha256: hash(readFileSync(join(output, 'osn-run.json'))), focusFile: 'osn-focus.json', focusSha256: hash(readFileSync(join(output, 'osn-focus.json'))),
        adapterBundleSha256: hash(osc9Bytes), contractsVersion: osc9ContractsVersion, counts: osc9Counts(osc9Report), provisionalUntil: 'cell-receipt.json' };
}

/** The opt-in XTGETTCAP suffix: synthetic journaled replies from the suite's own second oracle (never collected ones), written into
 *  `output` and declared in `context` and `evidence`. Layered over the title-stack suffix, which must be added first. */
async function addXtgettcapEvidence({ output, write, context, evidence, titleStackBytes }) {
    const { syntheticXtgettcapEvidence } = await import('./xtgettcap-evidence.mjs');
    const { buildXtgettcapGradingReport } = await import('../xtgettcap-grading.mjs');
    const { xtgettcapCounts } = await import('../xtgettcap-receipt.mjs');
    const { adaptXtgettcapCollector, xtgettcapAdapterId } = await import('../xtgettcap-grading-collector.mjs');
    const { xtgettcapSuiteId, xtgettcapContractsVersion } = await import('../xtgettcap-contracts.mjs');
    const xtgettcapBytes = Buffer.from(adaptXtgettcapCollector(titleStackBytes.toString()));
    const xtgettcap = syntheticXtgettcapEvidence(context.runId, hash(xtgettcapBytes));
    xtgettcap.adapter = { ...xtgettcap.adapter, upstreamBundleSha256: context.upstreamBundleSha256 };
    const xtgettcapReport = buildXtgettcapGradingReport(xtgettcap.frames, xtgettcap.run, context.runId, xtgettcap.adapter);
    write('xgtc-observations.json', xtgettcapReport);
    write('xgtc-run.json', xtgettcap.run);
    writeFileSync(join(output, 'xgtc-snapshots.jsonl'), xtgettcap.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
    writeFileSync(join(output, 'collector-xgtc.mjs'), xtgettcapBytes);
    Object.assign(context, { xtgettcapAdapterId, xtgettcapSuiteId, xtgettcapBundleSha256: hash(xtgettcapBytes), xtgettcapContractsVersion });
    evidence.xtgettcapEvidence = { runId: context.runId, suiteId: xtgettcapSuiteId,
        file: 'xgtc-observations.json', sha256: hash(readFileSync(join(output, 'xgtc-observations.json'))),
        runFile: 'xgtc-run.json', runSha256: hash(readFileSync(join(output, 'xgtc-run.json'))),
        adapterBundleSha256: hash(xtgettcapBytes), contractsVersion: xtgettcapContractsVersion,
        counts: xtgettcapCounts(xtgettcapReport), provisionalUntil: 'cell-receipt.json' };
}

/** Clone immutable v11 data into `output`, add every synthetic suffix and return the mutable state the tests receive. */
async function buildFixture(output, { routed = false, modifyOtherKeys = false } = {}) {
    const write = writerFor(output);
    const sourceSnapshotPolicy = routed || modifyOtherKeys ? 'readonly-owned-source-graph-v37' : legacyPolicy;
    const scope = routed || modifyOtherKeys ? capturedV44SourceFiles : capturedV41SourceFiles;
    /** The modifyOtherKeys variant executes the v3 benchmark plan; every other variant keeps its policy-mapped plan. */
    const executedInputPlan = modifyOtherKeys ? inputPlanV3 : inputPlanForPolicy(sourceSnapshotPolicy);
    {
        const archive = historicalArchive('2026-11-05-query-observations');
        for (const [file, bytes] of archive) { const path = join(output, file); mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, bytes); }
        const old = JSON.parse(archive.get('cell-receipt.json'));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = structuredClone(old);
        rmSync(join(output, 'cell-receipt.json'));
        const context = JSON.parse(archive.get('context.json')), proof = JSON.parse(archive.get('ownership-proof.json'));
        const settings = JSON.parse(archive.get('initial-settings.json')), query = JSON.parse(archive.get('query-observations.json'));
        const raw = JSON.parse(archive.get('raw.json')), process = proof.proofs[0];
        // Policy v33 runs turn the off-by-default DECRQCRA report on, so the archived witness request is answered: FFB2, the
        // checksum of the N the query suite binds at its home cell. Patch that one capture and re-derive the query report.
        settings.AllowRectangleChecksumReport = true;
        const journalOf = file => archive.get(file).toString().trim().split('\n').map(line => JSON.parse(line));
        const queryFrames = journalOf('query-snapshots.jsonl');
        const witness = queryFrames[queryPlan.find(entry => entry.id === 'q07.editing.decrqcra').index].capture;
        const witnessReply = Buffer.from('\x1bP1!~FFB2\x1b\\', 'ascii');
        Object.assign(witness, { hex: witnessReply.toString('hex'), byteLength: witnessReply.length });
        writeFileSync(join(output, 'query-snapshots.jsonl'), queryFrames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        const priorOf = file => JSON.parse(archive.get(file)).snapshots.filter(frame => frame.capture).map(frame => frame.capture.nonces.join(','));
        // The archive froze the v1 input plan. The routed variant executes the current plan, so the input suite is rebuilt from the archived
        // real screens and process facts with the plan's own events (synthetic child bytes, never collected ones), and the image suite's
        // report over the new challenges.
        let inputPrior = null;
        if (routed || modifyOtherKeys) {
            const inputPlan = executedInputPlan;
            const frames0 = journalOf('input-snapshots.jsonl'), actions0 = journalOf('input-actions.jsonl'), template = actions0[0].observed;
            const synthetic = syntheticInputJournal(context.runId, { paneId: frames0[0].paneId, initial: frames0[0].screen, plan: inputPlan,
                ...(modifyOtherKeys ? {
                    controlHex: item => (item.controlPrefixHex ?? '') + inputModeReplyHex(item.modes),
                    finalHex: () => inputModeReplyHex({ ...defaultInputModes, modifyOtherKeysLevel: 0 }),
                } : {}) });
            for (const action of synthetic.actions) {
                action.observed.before = structuredClone(template.before); action.observed.after = structuredClone(template.after);
                action.observed.observedAt = template.observedAt;
            }
            const inputReport = buildInputReport(synthetic.frames, synthetic.actions, synthetic.seal, context.runId, inputPlan);
            write('input-observations.json', inputReport);
            writeFileSync(join(output, 'input-snapshots.jsonl'), synthetic.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'input-actions.jsonl'), synthetic.actions.map(action => JSON.stringify(action)).join('\n') + '\n');
            Object.assign(process.input, { policy: inputPlan.policy, actions: synthetic.actions, seal: synthetic.seal, paneId: frames0[0].paneId });
            Object.assign(context, { inputSuiteId: inputPlan.suiteId, ownedInputPolicy: inputPlan.policy });
            evidence.inputEvidence = { runId: context.runId, suiteId: inputPlan.suiteId, file: 'input-observations.json',
                sha256: hash(readFileSync(join(output, 'input-observations.json'))), counts: inputCounts(inputReport), provisionalUntil: 'cell-receipt.json' };
            inputPrior = inputReport.snapshots.filter(frame => frame.capture).map(frame => frame.capture.nonces.join(','));
            const { buildStaticImageReport } = await import('../static-images.mjs');
            const imageReport = buildStaticImageReport(journalOf('image-snapshots.jsonl'), journalOf('image-actions.jsonl'), process.staticImages.seal, context.runId, output, inputPrior);
            write('image-observations.json', imageReport);
            evidence.staticImageEvidence = { ...evidence.staticImageEvidence, sha256: hash(readFileSync(join(output, 'image-observations.json'))) };
        }
        const queryReport = buildQueryReport({ binding: process.queries.binding, priorChallenges: [...(inputPrior ?? priorOf('input-observations.json')), ...priorOf('image-observations.json')],
            initial: JSON.parse(archive.get('query-initial.json')), actions: journalOf('query-actions.jsonl'), frames: queryFrames, seal: process.queries.seal, checksumReport: true });
        write('query-observations.json', queryReport);
        evidence.queryEvidence = { ...evidence.queryEvidence, sha256: hash(readFileSync(join(output, 'query-observations.json'))) };
        for (const [key, file] of Object.entries(scope)) {
            let data = readFileSync(ownedSourcePath(fileURLToPath(new URL('../../../', import.meta.url)), file));
            if (!routed && !modifyOtherKeys && file.endsWith('.mjs')) data = legacyPrefixSource(data);
            if ((routed || modifyOtherKeys) && file.endsWith('.mjs')) data = routedPrefixSource(data);
            if (file === 'owned-source-inventory.json') data = Buffer.from(JSON.stringify(scope, null, 2) + '\n');
            writeFileSync(join(output, 'sources', file), data); context[key] = hash(data);
        }
        const manifest = { schemaVersion: 1, policy: sourceSnapshotPolicy,
            hashes: Object.fromEntries(Object.keys(scope).map(key => [key, context[key]])) };
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
        const adapted = rederivePrivateAdapter(originalCollector, context.sourceSnapshotRoot);
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
            // Policy v27 always carries the layered attribute-rectangle suffix as well.
            const { syntheticAttrRectEvidence } = await import('./attr-rect-evidence.mjs');
            const { buildAttrRectGradingReport } = await import('../attr-rect-grading.mjs');
            const { adaptAttrRectCollector, attrRectAdapterId } = await import('../attr-rect-grading-collector.mjs');
            const { attrRectSuiteId, attrRectContractsVersion } = await import('../attr-rect-contracts.mjs');
            const { attrRectCounts } = await import('../attr-rect-receipt.mjs');
            const attrRectBytes = Buffer.from(adaptAttrRectCollector(eraseAttrsRegionBytes.toString()));
            const attrRect = syntheticAttrRectEvidence(context.runId, hash(attrRectBytes));
            attrRect.adapter = { id: attrRectAdapterId, derivedFromAdapterId: eraseAttrsRegionAdapterId,
                bundleSha256: hash(attrRectBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const attrRectReport = buildAttrRectGradingReport(attrRect.frames, attrRect.run, context.runId, attrRect.adapter);
            write('atrc-observations.json', attrRectReport);
            write('atrc-run.json', attrRect.run);
            writeFileSync(join(output, 'atrc-snapshots.jsonl'), attrRect.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-atrc.mjs'), attrRectBytes);
            Object.assign(context, { attrRectAdapterId, attrRectSuiteId, attrRectBundleSha256: hash(attrRectBytes), attrRectContractsVersion });
            evidence.attrRectEvidence = { runId: context.runId, suiteId: attrRectSuiteId,
                file: 'atrc-observations.json', sha256: hash(readFileSync(join(output, 'atrc-observations.json'))),
                runFile: 'atrc-run.json', runSha256: hash(readFileSync(join(output, 'atrc-run.json'))),
                adapterBundleSha256: hash(attrRectBytes), contractsVersion: attrRectContractsVersion,
                ...attrRectCounts(attrRectReport), provisionalUntil: 'cell-receipt.json' };
            // Policy v28 always carries the layered cursor suffix as well, with its retained controlled PNGs.
            const { syntheticCursorEvidence, writeCursorPngs } = await import('./cursor-qual-evidence.mjs');
            const { buildCursorQualGradingReport } = await import('../cursor-qual-grading.mjs');
            const { adaptCursorQualCollector, cursorQualAdapterId } = await import('../cursor-qual-grading-collector.mjs');
            const { cursorQualSuiteId, cursorQualContractsVersion } = await import('../cursor-qual-contracts.mjs');
            const cursorQualBytes = Buffer.from(adaptCursorQualCollector(attrRectBytes.toString()));
            const cursorQual = syntheticCursorEvidence(context.runId, hash(cursorQualBytes));
            cursorQual.adapter = { id: cursorQualAdapterId, derivedFromAdapterId: attrRectAdapterId,
                bundleSha256: hash(cursorQualBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            writeCursorPngs(cursorQual, output);
            const cursorQualReport = buildCursorQualGradingReport(cursorQual.frames, cursorQual.run, context.runId, cursorQual.adapter, { output });
            write('curq-observations.json', cursorQualReport);
            write('curq-run.json', cursorQual.run);
            writeFileSync(join(output, 'curq-snapshots.jsonl'), cursorQual.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-curq.mjs'), cursorQualBytes);
            Object.assign(context, { cursorQualAdapterId, cursorQualSuiteId, cursorQualBundleSha256: hash(cursorQualBytes), cursorQualContractsVersion });
            const cursorQualCounts = {};
            for (const item of cursorQualReport.cases) cursorQualCounts[item.conclusion.outcome] = (cursorQualCounts[item.conclusion.outcome] ?? 0) + 1;
            evidence.cursorQualEvidence = { runId: context.runId, suiteId: cursorQualSuiteId,
                file: 'curq-observations.json', sha256: hash(readFileSync(join(output, 'curq-observations.json'))),
                runFile: 'curq-run.json', runSha256: hash(readFileSync(join(output, 'curq-run.json'))),
                adapterBundleSha256: hash(cursorQualBytes), contractsVersion: cursorQualContractsVersion,
                counts: cursorQualCounts, provisionalUntil: 'cell-receipt.json' };
            // Policy v30 always carries the layered erase-character/saved-lines suffix as well.
            const { syntheticEraseCharsHistoryEvidence } = await import('./erase-chars-history-evidence.mjs');
            const { buildEraseCharsHistoryGradingReport } = await import('../erase-chars-history-grading.mjs');
            const { adaptEraseCharsHistoryCollector, eraseCharsHistoryAdapterId } = await import('../erase-chars-history-grading-collector.mjs');
            const { eraseCharsHistorySuiteId, eraseCharsHistoryContractsVersion } = await import('../erase-chars-history-contracts.mjs');
            const { eraseCharsHistoryCounts } = await import('../erase-chars-history-receipt.mjs');
            const eraseCharsHistoryBytes = Buffer.from(adaptEraseCharsHistoryCollector(cursorQualBytes.toString()));
            const eraseCharsHistory = syntheticEraseCharsHistoryEvidence(context.runId, hash(eraseCharsHistoryBytes));
            eraseCharsHistory.adapter = { id: eraseCharsHistoryAdapterId, derivedFromAdapterId: cursorQualAdapterId,
                bundleSha256: hash(eraseCharsHistoryBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const eraseCharsHistoryReport = buildEraseCharsHistoryGradingReport(eraseCharsHistory.frames, eraseCharsHistory.run, context.runId, eraseCharsHistory.adapter);
            write('echh-observations.json', eraseCharsHistoryReport);
            write('echh-run.json', eraseCharsHistory.run);
            writeFileSync(join(output, 'echh-snapshots.jsonl'), eraseCharsHistory.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-echh.mjs'), eraseCharsHistoryBytes);
            Object.assign(context, { eraseCharsHistoryAdapterId, eraseCharsHistorySuiteId, eraseCharsHistoryBundleSha256: hash(eraseCharsHistoryBytes), eraseCharsHistoryContractsVersion });
            evidence.eraseCharsHistoryEvidence = { runId: context.runId, suiteId: eraseCharsHistorySuiteId,
                file: 'echh-observations.json', sha256: hash(readFileSync(join(output, 'echh-observations.json'))),
                runFile: 'echh-run.json', runSha256: hash(readFileSync(join(output, 'echh-run.json'))),
                adapterBundleSha256: hash(eraseCharsHistoryBytes), contractsVersion: eraseCharsHistoryContractsVersion,
                counts: eraseCharsHistoryCounts(eraseCharsHistoryReport), provisionalUntil: 'cell-receipt.json' };
            // Policy v30 also always carries the layered selective-erasure suffix.
            const { syntheticSelectiveEraseEvidence } = await import('./selective-erase-evidence.mjs');
            const { buildSelectiveEraseGradingReport } = await import('../selective-erase-grading.mjs');
            const { adaptSelectiveEraseCollector, selectiveEraseAdapterId } = await import('../selective-erase-grading-collector.mjs');
            const { selectiveEraseSuiteId, selectiveEraseContractsVersion } = await import('../selective-erase-contracts.mjs');
            const { selectiveEraseCounts } = await import('../selective-erase-receipt.mjs');
            const selectiveEraseBytes = Buffer.from(adaptSelectiveEraseCollector(eraseCharsHistoryBytes.toString()));
            const selectiveErase = syntheticSelectiveEraseEvidence(context.runId, hash(selectiveEraseBytes));
            selectiveErase.adapter = { id: selectiveEraseAdapterId, derivedFromAdapterId: eraseCharsHistoryAdapterId,
                bundleSha256: hash(selectiveEraseBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const selectiveEraseReport = buildSelectiveEraseGradingReport(selectiveErase.frames, selectiveErase.run, context.runId, selectiveErase.adapter);
            write('sels-observations.json', selectiveEraseReport);
            write('sels-run.json', selectiveErase.run);
            writeFileSync(join(output, 'sels-snapshots.jsonl'), selectiveErase.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-sels.mjs'), selectiveEraseBytes);
            Object.assign(context, { selectiveEraseAdapterId, selectiveEraseSuiteId, selectiveEraseBundleSha256: hash(selectiveEraseBytes), selectiveEraseContractsVersion });
            evidence.selectiveEraseEvidence = { runId: context.runId, suiteId: selectiveEraseSuiteId,
                file: 'sels-observations.json', sha256: hash(readFileSync(join(output, 'sels-observations.json'))),
                runFile: 'sels-run.json', runSha256: hash(readFileSync(join(output, 'sels-run.json'))),
                adapterBundleSha256: hash(selectiveEraseBytes), contractsVersion: selectiveEraseContractsVersion,
                counts: selectiveEraseCounts(selectiveEraseReport), provisionalUntil: 'cell-receipt.json' };
            // Policy v31 always carries the layered rectangle-editing suffix as well.
            const { syntheticRectEditEvidence } = await import('./rect-edit-evidence.mjs');
            const { buildRectEditGradingReport } = await import('../rect-edit-grading.mjs');
            const { adaptRectEditCollector, rectEditAdapterId } = await import('../rect-edit-grading-collector.mjs');
            const { rectEditSuiteId, rectEditContractsVersion } = await import('../rect-edit-contracts.mjs');
            const { rectEditCounts } = await import('../rect-edit-receipt.mjs');
            const rectEditBytes = Buffer.from(adaptRectEditCollector(selectiveEraseBytes.toString()));
            const rectEdit = syntheticRectEditEvidence(context.runId, hash(rectEditBytes));
            rectEdit.adapter = { id: rectEditAdapterId, derivedFromAdapterId: selectiveEraseAdapterId,
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
            // Policy v32 always carries the layered column-editing suffix as well.
            const { syntheticColEditEvidence } = await import('./col-edit-evidence.mjs');
            const { buildColEditGradingReport } = await import('../col-edit-grading.mjs');
            const { adaptColEditCollector, colEditAdapterId } = await import('../col-edit-grading-collector.mjs');
            const { colEditSuiteId, colEditContractsVersion } = await import('../col-edit-contracts.mjs');
            const { colEditCounts } = await import('../col-edit-receipt.mjs');
            const colEditBytes = Buffer.from(adaptColEditCollector(rectEditBytes.toString()));
            const colEdit = syntheticColEditEvidence(context.runId, hash(colEditBytes));
            colEdit.adapter = { id: colEditAdapterId, derivedFromAdapterId: rectEditAdapterId,
                bundleSha256: hash(colEditBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const colEditReport = buildColEditGradingReport(colEdit.frames, colEdit.run, context.runId, colEdit.adapter);
            write('cole-observations.json', colEditReport);
            write('cole-run.json', colEdit.run);
            writeFileSync(join(output, 'cole-snapshots.jsonl'), colEdit.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-cole.mjs'), colEditBytes);
            Object.assign(context, { colEditAdapterId, colEditSuiteId, colEditBundleSha256: hash(colEditBytes), colEditContractsVersion });
            evidence.colEditEvidence = { runId: context.runId, suiteId: colEditSuiteId,
                file: 'cole-observations.json', sha256: hash(readFileSync(join(output, 'cole-observations.json'))),
                runFile: 'cole-run.json', runSha256: hash(readFileSync(join(output, 'cole-run.json'))),
                adapterBundleSha256: hash(colEditBytes), contractsVersion: colEditContractsVersion,
                counts: colEditCounts(colEditReport), provisionalUntil: 'cell-receipt.json' };
            // Policy v33 always carries the layered rectangle-checksum suffix as well.
            const { syntheticRectChecksumEvidence } = await import('./rect-checksum-evidence.mjs');
            const { buildRectChecksumGradingReport } = await import('../rect-checksum-grading.mjs');
            const { adaptRectChecksumCollector, rectChecksumAdapterId } = await import('../rect-checksum-grading-collector.mjs');
            const { rectChecksumSuiteId, rectChecksumContractsVersion } = await import('../rect-checksum-contracts.mjs');
            const { rectChecksumCounts } = await import('../rect-checksum-receipt.mjs');
            const rectChecksumBytes = Buffer.from(adaptRectChecksumCollector(colEditBytes.toString()));
            const rectChecksum = syntheticRectChecksumEvidence(context.runId, hash(rectChecksumBytes));
            rectChecksum.adapter = { id: rectChecksumAdapterId, derivedFromAdapterId: colEditAdapterId,
                bundleSha256: hash(rectChecksumBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const rectChecksumReport = buildRectChecksumGradingReport(rectChecksum.frames, rectChecksum.run, context.runId, rectChecksum.adapter);
            write('csum-observations.json', rectChecksumReport);
            write('csum-run.json', rectChecksum.run);
            writeFileSync(join(output, 'csum-snapshots.jsonl'), rectChecksum.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-csum.mjs'), rectChecksumBytes);
            Object.assign(context, { rectChecksumAdapterId, rectChecksumSuiteId, rectChecksumBundleSha256: hash(rectChecksumBytes), rectChecksumContractsVersion });
            evidence.rectChecksumEvidence = { runId: context.runId, suiteId: rectChecksumSuiteId,
                file: 'csum-observations.json', sha256: hash(readFileSync(join(output, 'csum-observations.json'))),
                runFile: 'csum-run.json', runSha256: hash(readFileSync(join(output, 'csum-run.json'))),
                adapterBundleSha256: hash(rectChecksumBytes), contractsVersion: rectChecksumContractsVersion,
                counts: rectChecksumCounts(rectChecksumReport), provisionalUntil: 'cell-receipt.json' };
            // Policy v34 always carries the layered left/right-margin suffix as well.
            const { syntheticLrMarginsEvidence } = await import('./lr-margins-evidence.mjs');
            const { buildLrMarginsGradingReport } = await import('../lr-margins-grading.mjs');
            const { adaptLrMarginsCollector, lrMarginsAdapterId } = await import('../lr-margins-grading-collector.mjs');
            const { lrMarginsSuiteId, lrMarginsContractsVersion } = await import('../lr-margins-contracts.mjs');
            const { lrMarginsCounts } = await import('../lr-margins-receipt.mjs');
            const lrMarginsBytes = Buffer.from(adaptLrMarginsCollector(rectChecksumBytes.toString()));
            const lrMargins = syntheticLrMarginsEvidence(context.runId, hash(lrMarginsBytes));
            lrMargins.adapter = { id: lrMarginsAdapterId, derivedFromAdapterId: rectChecksumAdapterId,
                bundleSha256: hash(lrMarginsBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            const lrMarginsReport = buildLrMarginsGradingReport(lrMargins.frames, lrMargins.run, context.runId, lrMargins.adapter);
            write('lrmm-observations.json', lrMarginsReport);
            write('lrmm-run.json', lrMargins.run);
            writeFileSync(join(output, 'lrmm-snapshots.jsonl'), lrMargins.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-lrmm.mjs'), lrMarginsBytes);
            Object.assign(context, { lrMarginsAdapterId, lrMarginsSuiteId, lrMarginsBundleSha256: hash(lrMarginsBytes), lrMarginsContractsVersion });
            evidence.lrMarginsEvidence = { runId: context.runId, suiteId: lrMarginsSuiteId,
                file: 'lrmm-observations.json', sha256: hash(readFileSync(join(output, 'lrmm-observations.json'))),
                runFile: 'lrmm-run.json', runSha256: hash(readFileSync(join(output, 'lrmm-run.json'))),
                adapterBundleSha256: hash(lrMarginsBytes), contractsVersion: lrMarginsContractsVersion,
                counts: lrMarginsCounts(lrMarginsReport), provisionalUntil: 'cell-receipt.json' };
            // The current policy always carries the layered routed-input suffix over the owned input suite as well.
            if (routed || modifyOtherKeys) {
                const { fakeGradingModule } = await import('./routed-input-evidence.mjs');
                const { buildRoutedInputGradingReport, routedInputCounts } = await import('../routed-input-grading.mjs');
                const { adaptRoutedInputCollector, routedInputAdapterId } = await import('../routed-input-grading-collector.mjs');
                const { routedInputGradedSuiteId, routedInputContractsVersion } = await import('../routed-input-contracts.mjs');
                const { collectRoutedInputPinned } = await import('../routed-input-collector.mjs');
                const routedInputBytes = Buffer.from(adaptRoutedInputCollector(lrMarginsBytes.toString()));
                const lines = file => readFileSync(join(output, file), 'utf8').trim().split('\n').map(line => JSON.parse(line));
                const inputFrames = lines('input-snapshots.jsonl'), inputActions = lines('input-actions.jsonl'), controls = {};
                for (const frame of inputFrames) if (frame.kind === 'input-control') controls[frame.caseId] = frame.capture.hex;
                const routedInputRun = collectRoutedInputPinned({ geometry: { rows: inputFrames[0].screen.rows, cols: inputFrames[0].screen.cols }, controls,
                    restored: inputFrames.at(-1).capture.hex, gradingModule: fakeGradingModule });
                const routedInputAdapter = { id: routedInputAdapterId, derivedFromAdapterId: lrMarginsAdapterId,
                    bundleSha256: hash(routedInputBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
                const routedInputReport = buildRoutedInputGradingReport({ frames: inputFrames, actions: inputActions, seal: process.input.seal }, routedInputRun, context.runId,
                    routedInputAdapter, { inputPlan: executedInputPlan, inputReportSha256: evidence.inputEvidence.sha256 });
                write('rin-observations.json', routedInputReport);
                write('rin-run.json', routedInputRun);
                writeFileSync(join(output, 'collector-rin.mjs'), routedInputBytes);
                Object.assign(context, { routedInputAdapterId, routedInputSuiteId: routedInputGradedSuiteId, routedInputBundleSha256: hash(routedInputBytes), routedInputContractsVersion });
                evidence.routedInputEvidence = { runId: context.runId, suiteId: routedInputGradedSuiteId,
                    file: 'rin-observations.json', sha256: hash(readFileSync(join(output, 'rin-observations.json'))),
                    runFile: 'rin-run.json', runSha256: hash(readFileSync(join(output, 'rin-run.json'))),
                    adapterBundleSha256: hash(routedInputBytes), contractsVersion: routedInputContractsVersion,
                    counts: routedInputCounts(routedInputReport), provisionalUntil: 'cell-receipt.json' };
            }
            // The modifyOtherKeys suffix rides the same v3 journal: the pinned probe replays over the child's XTGETTCAP declaration capture.
            if (modifyOtherKeys) {
                const { fakeModifyOtherKeysGradingModule } = await import('./modify-other-keys-evidence.mjs');
                const { buildModifyOtherKeysGradingReport, modifyOtherKeysCounts } = await import('../modify-other-keys-grading.mjs');
                const { adaptModifyOtherKeysCollector, modifyOtherKeysAdapterId } = await import('../modify-other-keys-grading-collector.mjs');
                const { modifyOtherKeysGradedSuiteId, modifyOtherKeysContractsVersion } = await import('../modify-other-keys-contracts.mjs');
                const { collectModifyOtherKeysPinned } = await import('../modify-other-keys-collector.mjs');
                const modifyOtherKeysBytes = Buffer.from(adaptModifyOtherKeysCollector(lrMarginsBytes.toString()));
                const lines = file => readFileSync(join(output, file), 'utf8').trim().split('\n').map(line => JSON.parse(line));
                const inputFrames = lines('input-snapshots.jsonl'), inputActions = lines('input-actions.jsonl'), mokControls = {};
                for (const frame of inputFrames) if (frame.kind === 'input-control') mokControls[frame.caseId] = frame.capture.hex;
                const modifyOtherKeysRun = collectModifyOtherKeysPinned({ geometry: { rows: inputFrames[0].screen.rows, cols: inputFrames[0].screen.cols },
                    controls: mokControls, gradingModule: fakeModifyOtherKeysGradingModule });
                const modifyOtherKeysAdapter = { id: modifyOtherKeysAdapterId, derivedFromAdapterId: lrMarginsAdapterId,
                    bundleSha256: hash(modifyOtherKeysBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
                const modifyOtherKeysReport = buildModifyOtherKeysGradingReport({ frames: inputFrames, actions: inputActions, seal: process.input.seal },
                    modifyOtherKeysRun, context.runId, modifyOtherKeysAdapter,
                    { inputPlan: executedInputPlan, inputReportSha256: evidence.inputEvidence.sha256 });
                write('mok-observations.json', modifyOtherKeysReport);
                write('mok-run.json', modifyOtherKeysRun);
                writeFileSync(join(output, 'collector-mok.mjs'), modifyOtherKeysBytes);
                Object.assign(context, { modifyOtherKeysAdapterId, modifyOtherKeysSuiteId: modifyOtherKeysGradedSuiteId,
                    modifyOtherKeysBundleSha256: hash(modifyOtherKeysBytes), modifyOtherKeysContractsVersion });
                evidence.modifyOtherKeysEvidence = { runId: context.runId, suiteId: modifyOtherKeysGradedSuiteId,
                    file: 'mok-observations.json', sha256: hash(readFileSync(join(output, 'mok-observations.json'))),
                    runFile: 'mok-run.json', runSha256: hash(readFileSync(join(output, 'mok-run.json'))),
                    adapterBundleSha256: hash(modifyOtherKeysBytes), contractsVersion: modifyOtherKeysContractsVersion,
                    counts: modifyOtherKeysCounts(modifyOtherKeysReport), provisionalUntil: 'cell-receipt.json' };
            }
            // Policy v35 always carries the layered runtime-color reset suffix as well.
            const { syntheticColorResetEvidence, writeColorResetPngs } = await import('./color-reset-evidence.mjs');
            const { buildColorResetGradingReport } = await import('../color-reset-grading.mjs');
            const { adaptColorResetCollector, colorResetAdapterId } = await import('../color-reset-grading-collector.mjs');
            const { colorResetSuiteId, colorResetContractsVersion } = await import('../color-reset-contracts.mjs');
            const { colorResetCounts } = await import('../color-reset-receipt.mjs');
            const colorResetBytes = Buffer.from(adaptColorResetCollector(lrMarginsBytes.toString()));
            const colorReset = syntheticColorResetEvidence(context.runId, hash(colorResetBytes));
            colorReset.adapter = { id: colorResetAdapterId, derivedFromAdapterId: lrMarginsAdapterId,
                bundleSha256: hash(colorResetBytes), upstreamBundleSha256: context.upstreamBundleSha256 };
            writeColorResetPngs(colorReset, output);
            const colorResetReport = buildColorResetGradingReport(colorReset.frames, colorReset.run, context.runId, colorReset.adapter, { output });
            write('crst-observations.json', colorResetReport);
            write('crst-run.json', colorReset.run);
            writeFileSync(join(output, 'crst-snapshots.jsonl'), colorReset.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-crst.mjs'), colorResetBytes);
            Object.assign(context, { colorResetAdapterId, colorResetSuiteId, colorResetBundleSha256: hash(colorResetBytes), colorResetContractsVersion });
            evidence.colorResetEvidence = { runId: context.runId, suiteId: colorResetSuiteId,
                file: 'crst-observations.json', sha256: hash(readFileSync(join(output, 'crst-observations.json'))),
                runFile: 'crst-run.json', runSha256: hash(readFileSync(join(output, 'crst-run.json'))),
                adapterBundleSha256: hash(colorResetBytes), contractsVersion: colorResetContractsVersion,
                counts: colorResetCounts(colorResetReport), provisionalUntil: 'cell-receipt.json' };
            // The OSC 133 suffix is admitted by its retained artifacts (synthetic frames from the suite's own oracle, never collected ones), whatever the frozen policy.
            const { syntheticOsc133Evidence } = await import('./osc133-evidence.mjs');
            const { buildOsc133GradingReport, osc133Counts } = await import('../osc133-grading.mjs');
            const { adaptOsc133Collector, osc133AdapterId } = await import('../osc133-grading-collector.mjs');
            const { osc133SuiteId, osc133ContractsVersion } = await import('../osc133-contracts.mjs');
            const osc133Bytes = Buffer.from(adaptOsc133Collector(lrMarginsBytes.toString()));
            const osc133 = syntheticOsc133Evidence({ runId: context.runId, bundleSha256: hash(osc133Bytes) });
            osc133.adapter = { ...osc133.adapter, upstreamBundleSha256: context.upstreamBundleSha256 };
            const osc133Report = buildOsc133GradingReport(osc133.frames, osc133.run, context.runId, osc133.adapter);
            write('shi-observations.json', osc133Report);
            write('shi-run.json', osc133.run);
            writeFileSync(join(output, 'shi-snapshots.jsonl'), osc133.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-shi.mjs'), osc133Bytes);
            Object.assign(context, { osc133AdapterId, osc133SuiteId, osc133BundleSha256: hash(osc133Bytes), osc133ContractsVersion });
            evidence.osc133Evidence = { runId: context.runId, suiteId: osc133SuiteId,
                file: 'shi-observations.json', sha256: hash(readFileSync(join(output, 'shi-observations.json'))),
                runFile: 'shi-run.json', runSha256: hash(readFileSync(join(output, 'shi-run.json'))),
                adapterBundleSha256: hash(osc133Bytes), contractsVersion: osc133ContractsVersion,
                counts: osc133Counts(osc133Report), provisionalUntil: 'cell-receipt.json' };
            // The OSC 7/8 suffix is admitted by its retained artifacts (synthetic frames from the suite's own oracle, never collected ones), whatever the frozen policy.
            const { syntheticOsc78Evidence } = await import('./osc78-evidence.mjs');
            const { buildOsc78GradingReport, osc78Counts } = await import('../osc78-grading.mjs');
            const { adaptOsc78Collector, osc78AdapterId } = await import('../osc78-grading-collector.mjs');
            const { osc78SuiteId, osc78ContractsVersion } = await import('../osc78-contracts.mjs');
            const osc78Bytes = Buffer.from(adaptOsc78Collector(lrMarginsBytes.toString()));
            const osc78 = syntheticOsc78Evidence({ runId: context.runId, bundleSha256: hash(osc78Bytes) });
            osc78.adapter = { ...osc78.adapter, upstreamBundleSha256: context.upstreamBundleSha256 };
            const osc78Report = buildOsc78GradingReport(osc78.frames, osc78.run, context.runId, osc78.adapter);
            write('lnk-observations.json', osc78Report);
            write('lnk-run.json', osc78.run);
            writeFileSync(join(output, 'lnk-snapshots.jsonl'), osc78.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            writeFileSync(join(output, 'collector-lnk.mjs'), osc78Bytes);
            Object.assign(context, { osc78AdapterId, osc78SuiteId, osc78BundleSha256: hash(osc78Bytes), osc78ContractsVersion });
            evidence.osc78Evidence = { runId: context.runId, suiteId: osc78SuiteId,
                file: 'lnk-observations.json', sha256: hash(readFileSync(join(output, 'lnk-observations.json'))),
                runFile: 'lnk-run.json', runSha256: hash(readFileSync(join(output, 'lnk-run.json'))),
                adapterBundleSha256: hash(osc78Bytes), contractsVersion: osc78ContractsVersion,
                counts: osc78Counts(osc78Report), provisionalUntil: 'cell-receipt.json' };
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
        return { context, proof, report, evidence, raw, manifest, old };
    }
}

/** The fixture is deterministic and expensive (every synthetic suite is built and graded), so one process builds it once into a template
 * directory and every test receives its own copy of the files and a deep clone of the state; the template is removed when the process exits. */
const templates = new Map();
function fixtureTemplate(opts) {
    const key = JSON.stringify(opts);
    if (!templates.has(key)) templates.set(key, (async () => {
        const output = mkdtempSync(join(tmpdir(), 'nt-private-receipt-template-'));
        process.once('exit', () => rmSync(output, { recursive: true, force: true }));
        try {
            const state = await buildFixture(output, opts);
            if (JSON.stringify(state).includes(output)) throw new Error('The fixture state names its template directory; it cannot be copied.');
            return { output, state };
        } catch (error) {
            // A failed build is reported to every test of this process; its directory is not kept until exit.
            rmSync(output, { recursive: true, force: true });
            throw error;
        }
    })());
    return templates.get(key);
}

/** A private copy of the v11 data with every synthetic suffix, which is always removed after `action`. */
export async function privateClipboardReceiptFixture(action, { routed = false, titleStack = false, xtgettcap = false, modifyOtherKeys = false, osc9 = false } = {}) {
    const { output: source, state } = await fixtureTemplate({ routed, titleStack, xtgettcap, modifyOtherKeys });
    const output = mkdtempSync(join(tmpdir(), 'nt-private-receipt-'));
    try {
        cpSync(source, output, { recursive: true, verbatimSymlinks: true });
        const { context, proof, report, evidence, raw, manifest, old } = structuredClone(state);
        // The title-stack suffix is opt-in, so every other layer's receipt tests keep their own newest schema; it is added to this test's private copy.
        if (titleStack || xtgettcap) {
            await addTitleStackEvidence({ output, write: writerFor(output), context, evidence, lrMarginsBytes: readFileSync(join(output, 'collector-lrmm.mjs')) });
        }
        // The XTGETTCAP suffix is layered over the title-stack one and equally opt-in.
        if (xtgettcap) {
            await addXtgettcapEvidence({ output, write: writerFor(output), context, evidence, titleStackBytes: readFileSync(join(output, 'collector-tst.mjs')) });
        }
        // The OSC 9 suffix derives from the left/right-margin bytes (a sibling of the title-stack one) and is equally opt-in.
        if (osc9) {
            await addOsc9Evidence({ output, write: writerFor(output), context, evidence, lrMarginsBytes: readFileSync(join(output, 'collector-lrmm.mjs')) });
        }
        if (titleStack || xtgettcap || osc9) writerFor(output)('context.json', context);
        await action({ output, write: writerFor(output), context, proof, report, evidence, raw, manifest, old,
            finish: () => completeOwnedReceipt(output, join(output, 'already-removed-fixture'), evidence) });
    } finally { rmSync(output, { recursive: true, force: true }); }
}