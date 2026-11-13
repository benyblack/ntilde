// Receipt binding for the XTGETTCAP qualification (device.xtgettcap). The graded evidence stays provisional until the shared cleanup
// receipt exists, and the check re-derives the whole report from retained bytes without the collector. A run carrying this evidence
// must also carry every earlier grading layer (through title/icon-stack): these conclusions sit beside theirs, never replace them.
import { lstatSync } from 'node:fs';
import { join } from 'node:path';
import { checkGradedReceiptSuite } from './xtgettcap-receipt-core.mjs';
import { xtgettcapContractsVersion, xtgettcapSuiteId } from './xtgettcap-contracts.mjs';
import { adaptXtgettcapCollector, xtgettcapAdapterId } from './xtgettcap-grading-collector.mjs';
import { buildXtgettcapGradingReport } from './xtgettcap-grading.mjs';

/** Every retained artifact this suffix owns. */
const files = ['xgtc-observations.json', 'xgtc-run.json', 'xgtc-snapshots.jsonl', 'collector-xgtc.mjs'];
/** The first source policy whose worker always runs this suite. */
export const xtgettcapFirstPolicy = 49;

/** Whether a retained artifact path exists; any failure other than a missing entry is fatal. */
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);

/** Any retained XTGETTCAP artifact or declaration requires this suffix, and a run from the first XTGETTCAP policy on always
 * carries them, because the current worker runs this suite unconditionally after the title/icon-stack suite. */
export function includesXtgettcapEvidence(output, evidence, context) {
    return policyNumber(context.sourceSnapshotPolicy) >= xtgettcapFirstPolicy
        || context.xtgettcapAdapterId !== undefined || context.xtgettcapSuiteId !== undefined
        || context.xtgettcapBundleSha256 !== undefined || context.xtgettcapContractsVersion !== undefined
        || evidence.xtgettcapEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** The outcome counts of the case conclusions (the pinned verdict, preserved by design). */
export function xtgettcapCounts(report) {
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return counts;
}

/** The predecessor layers, oldest first (the title/icon-stack layer last). */
const layerNames = ['sgrColors', 'sgrRgb', 'sgrAttrs', 'sgrPalette', 'sgrTruecolor', 'sgrUnderline', 'sgrUnderlineColor',
    'sgrReset', 'sgrClosure', 'eraseLine', 'charEdit', 'eraseDisplay', 'lineEdit', 'charsets', 'scrollback', 'savedReset',
    'eraseAttrsRegion', 'attrRect', 'cursorQual', 'eraseCharsHistory', 'selectiveErase', 'rectEdit', 'colEdit', 'rectChecksum',
    'lrMargins', 'titleStack'];

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkXtgettcapEvidence(output, evidence, runId, context) {
    checkGradedReceiptSuite(output, evidence, runId, context, {
        label: 'XTGETTCAP', adapterId: xtgettcapAdapterId, suiteId: xtgettcapSuiteId, contractsVersion: xtgettcapContractsVersion,
        evidenceKey: 'xtgettcapEvidence', observationsFile: 'xgtc-observations.json', runFile: 'xgtc-run.json',
        adapterFile: 'collector-xgtc.mjs', journalFile: 'xgtc-snapshots.jsonl', layerNames, finalAdapt: adaptXtgettcapCollector,
        contextAdapterKey: 'xtgettcapAdapterId', contextSuiteKey: 'xtgettcapSuiteId', contextBundleKey: 'xtgettcapBundleSha256',
        contextContractsValue: context.xtgettcapContractsVersion,
        build: (frames, run, derivedRunId, adapter, options) => buildXtgettcapGradingReport(frames, run, derivedRunId, adapter, options),
        counts: xtgettcapCounts,
    });
}

/** The case ids this suite grades, exposed for the receipt's planned-case declarations. */
export { xtgettcapCaseIds } from './xtgettcap-contracts.mjs';
