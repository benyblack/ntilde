// Reproducible inventory of the terminfo.dev@4.0.0 benchmark IDs that carry retained owned effect evidence.
//
// Everything here is derived from the merged contract modules and the retained publications under docs/qa/terminfo; no
// total is hand-written. A claim only counts as qualified when its suite's trusted rederivation (the suite's publication
// test, which rebuilds the retained report from the retained journals and compares it byte for byte) passed. The inventory
// is read-only over historical evidence: it never rewrites a grade, publication, journal, receipt or source policy.
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

export const inventorySchemaVersion = 1;
export const inventoryDate = '2026-11-09';
export const inventoryFiles = Object.freeze({ json: '2026-11-09-qualification-inventory.json', markdown: '2026-11-09-qualification-inventory.md' });
export const stockBaselineFile = '2026-11-09-stock-baseline.json';

const scriptsDir = fileURLToPath(new URL('.', import.meta.url));
export const defaultQaRoot = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));

export const verdicts = Object.freeze(['supported', 'unsupported', 'inconclusive']);
/** Every layer an owned suite can measure; a claim naming anything else is refused. */
export const measuredLayerNames = Object.freeze(['stored-state', 'actual-scrollback', 'app-resolved-color', 'parent-derived-resolution', 'cursor-position-report', 'controlled-pixels']);
/** Suites whose ids are terminfo.dev benchmark ids; the two findings are Ntilde-specific and never counted as benchmark ids. */
const sgrAttrsFile = '2026-11-08-sgr-attributes-qualification.json';

const single = (suite, file, field) => ({ layout: 'single', file, field, doc: file.replace(/\.json$/, '.md'), suite });
const directory = (suite, dir, doc) => ({ layout: 'directory', dir, doc, suite });
/** The merged benchmark suites, in merge order. `exports` names the live contract module's suite id, version and case ids. */
export const benchmarkSuites = Object.freeze([
    { ...single('ascii-sgr', '2026-11-05-ascii-sgr-qualification.json', 'gradingReport'), module: 'ascii-sgr-contracts.mjs', exports: ['asciiSgrSuiteId', 'asciiSgrContractsVersion', 'asciiSgrCaseIds'], test: 'ascii-sgr-publication.test.mjs' },
    { ...single('sgr-colors', '2026-11-06-sgr-colors-qualification.json', 'sgrColorsReport'), module: 'sgr-colors-contracts.mjs', exports: ['sgrColorsSuiteId', 'sgrColorsContractsVersion', 'sgrColorsCaseIds'], test: 'sgr-colors-publication.test.mjs' },
    { ...single('sgr-rgb', '2026-11-07-sgr-rgb-qualification.json', 'sgrRgbReport'), module: 'sgr-rgb-contracts.mjs', exports: ['sgrRgbSuiteId', 'sgrRgbContractsVersion', 'sgrRgbCaseIds'], test: 'sgr-rgb-publication.test.mjs' },
    { ...single('sgr-attrs', sgrAttrsFile, 'sgrAttrsReport'), module: 'sgr-attrs-contracts.mjs', exports: ['sgrAttrsSuiteId', 'sgrAttrsContractsVersion', 'sgrAttrsCaseIds'], test: 'sgr-attrs-publication.test.mjs' },
    { ...directory('sgr-palette', '2026-11-08-sgr-palette', '2026-11-08-sgr-palette-qualification.md'), module: 'sgr-palette-contracts.mjs', exports: ['sgrPaletteSuiteId', 'sgrPaletteContractsVersion', 'sgrPaletteCaseIds'], test: 'sgr-palette-publication.test.mjs' },
    { ...directory('sgr-truecolor', '2026-11-08-sgr-truecolor', '2026-11-08-sgr-truecolor-qualification.md'), module: 'sgr-truecolor-contracts.mjs', exports: ['sgrTruecolorSuiteId', 'sgrTruecolorContractsVersion', 'sgrTruecolorCaseIds'], test: 'sgr-truecolor-publication.test.mjs' },
    { ...directory('sgr-underline', '2026-11-08-sgr-underline', '2026-11-08-sgr-underline-qualification.md'), module: 'sgr-underline-contracts.mjs', exports: ['sgrUnderlineSuiteId', 'sgrUnderlineContractsVersion', 'sgrUnderlineCaseIds'], test: 'sgr-underline-publication.test.mjs' },
    { ...directory('sgr-underline-color', '2026-11-08-sgr-underline-color', '2026-11-08-sgr-underline-color-qualification.md'), module: 'sgr-underline-color-contracts.mjs', exports: ['sgrUnderlineColorSuiteId', 'sgrUnderlineColorContractsVersion', 'sgrUnderlineColorCaseIds'], test: 'sgr-underline-color-publication.test.mjs' },
    { ...directory('sgr-reset', '2026-11-08-sgr-selective-reset', '2026-11-08-sgr-selective-reset-qualification.md'), module: 'sgr-reset-contracts.mjs', exports: ['sgrResetSuiteId', 'sgrResetContractsVersion', 'sgrResetCaseIds'], test: 'sgr-reset-publication.test.mjs' },
    { ...directory('sgr-closure', '2026-11-08-sgr-closure', '2026-11-08-sgr-closure-qualification.md'), module: 'sgr-closure-contracts.mjs', exports: ['sgrClosureSuiteId', 'sgrClosureContractsVersion', 'sgrClosureCaseIds'], test: 'sgr-closure-publication.test.mjs' },
    { ...directory('erase-line', '2026-11-08-erase-line', '2026-11-08-erase-line-qualification.md'), module: 'erase-line-contracts.mjs', exports: ['eraseLineSuiteId', 'eraseLineContractsVersion', 'eraseLineCaseIds'], test: 'erase-line-publication.test.mjs' },
    { ...directory('erase-display', '2026-11-09-erase-display', '2026-11-09-erase-display-qualification.md'), module: 'erase-display-contracts.mjs', exports: ['eraseDisplaySuiteId', 'eraseDisplayContractsVersion', 'eraseDisplayCaseIds'], test: 'erase-display-publication.test.mjs' },
    { ...directory('char-edit', '2026-11-09-char-edit', '2026-11-09-char-edit-qualification.md'), module: 'char-edit-contracts.mjs', exports: ['charEditSuiteId', 'charEditContractsVersion', 'charEditCaseIds'], test: 'char-edit-publication.test.mjs' },
    { ...directory('line-edit', '2026-11-09-line-editing', '2026-11-09-line-editing-qualification.md'), module: 'line-edit-contracts.mjs', exports: ['lineEditSuiteId', 'lineEditContractsVersion', 'lineEditCaseIds'], test: 'line-edit-publication.test.mjs' },
    { ...directory('charsets', '2026-11-09-charsets', '2026-11-09-charsets-qualification.md'), module: 'charsets-contracts.mjs', exports: ['charsetsSuiteId', 'charsetsContractsVersion', 'charsetsCaseIds'], test: 'charsets-publication.test.mjs' },
    { ...directory('scrollback', '2026-11-09-scrollback', '2026-11-09-scrollback-qualification.md'), module: 'scrollback-contracts.mjs', exports: ['scrollbackSuiteId', 'scrollbackContractsVersion', 'scrollbackCaseIds'], test: 'scrollback-publication.test.mjs' },
    { ...directory('saved-reset', '2026-11-09-saved-reset', '2026-11-09-saved-reset-qualification.md'), module: 'saved-reset-contracts.mjs', exports: ['savedResetSuiteId', 'savedResetContractsVersion', 'savedResetCaseIds'], test: 'saved-reset-publication.test.mjs' },
    { ...directory('erase-attrs-region', '2026-11-09-erase-attrs-region', '2026-11-09-erase-attrs-region-qualification.md'), module: 'erase-attrs-region-contracts.mjs', exports: ['eraseAttrsRegionSuiteId', 'eraseAttrsRegionContractsVersion', 'eraseAttrsRegionCaseIds'], test: 'erase-attrs-region-publication.test.mjs' },
    { ...directory('attr-rect', '2026-11-09-attr-rect', '2026-11-09-attr-rect-qualification.md'), module: 'attr-rect-contracts.mjs', exports: ['attrRectSuiteId', 'attrRectContractsVersion', 'attrRectCaseIds'], test: 'attr-rect-publication.test.mjs' },
    { ...directory('cursor', '2026-11-09-cursor', '2026-11-09-cursor-qualification.md'), module: 'cursor-qual-contracts.mjs', exports: ['cursorQualSuiteId', 'cursorQualContractsVersion', 'cursorQualCaseIds'], test: 'cursor-qual-publication.test.mjs' },
    { ...directory('erase-chars-history', '2026-11-09-erase-chars-history', '2026-11-09-erase-chars-history-qualification.md'), module: 'erase-chars-history-contracts.mjs', exports: ['eraseCharsHistorySuiteId', 'eraseCharsHistoryContractsVersion', 'eraseCharsHistoryCaseIds'], test: 'erase-chars-history-publication.test.mjs' },
    { ...directory('selective-erase', '2026-11-09-selective-erase', '2026-11-09-selective-erase-qualification.md'), module: 'selective-erase-contracts.mjs', exports: ['selectiveEraseSuiteId', 'selectiveEraseContractsVersion', 'selectiveEraseCaseIds'], test: 'selective-erase-publication.test.mjs' },
    { ...directory('rect-edit', '2026-11-09-rectangle-editing', '2026-11-09-rectangle-editing-qualification.md'), module: 'rect-edit-contracts.mjs', exports: ['rectEditSuiteId', 'rectEditContractsVersion', 'rectEditCaseIds'], test: 'rect-edit-publication.test.mjs' },
]);

/** Ntilde-specific findings: separate evidence, separate claims, never part of the benchmark id totals. */
export const findingSuites = Object.freeze([
    { suite: 'truecolor-literal-finding', dir: '2026-11-08-truecolor-literal-finding', doc: '2026-11-08-truecolor-literal-finding.md', module: 'truecolor-literal-finding-contracts.mjs',
        exports: ['truecolorFindingSuiteId', 'truecolorFindingContractsVersion', 'truecolorFindingCaseIds'], test: 'truecolor-literal-finding-publication.test.mjs',
        measuredLayer: 'stored-state and parent-derived resolution (stored literal against the App-resolved value, before and after the VT fix)' },
    { suite: 'sgr-underline-color-resolved', dir: '2026-11-08-underline-color-resolved', doc: '2026-11-08-underline-color-resolved-qualification.md', module: 'sgr-underline-color-resolved-contracts.mjs',
        exports: ['underlineResolvedSuiteId', 'underlineResolvedContractsVersion', 'underlineResolvedCaseIds'], test: 'sgr-underline-color-resolved-publication.test.mjs',
        measuredLayer: 'app-observed resolution (the App-reported underline color, kept separate from the parent-derived expectation)' },
]);

const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
/** Retained evidence is LF in git; a Windows checkout may add CR. Hash and size the LF form so the inventory is checkout-independent. */
const lf = bytes => Buffer.from(bytes.toString('latin1').replaceAll('\r\n', '\n'), 'latin1');
export const readText = path => readFileSync(path, 'utf8').replaceAll('\r\n', '\n');
const readJson = path => JSON.parse(readFileSync(path));
const posix = path => path.split(sep).join('/');

/** Stable digest over every retained file of one publication: `<relative path> <sha256>` lines, sorted. */
function evidenceDigest(root, suite) {
    const files = [];
    const walk = dir => {
        for (const entry of readdirSync(dir, { withFileTypes: true }).sort((a, b) => byText(a.name, b.name))) {
            const path = join(dir, entry.name);
            if (entry.isDirectory()) walk(path);
            else files.push(path);
        }
    };
    if (suite.layout === 'single') files.push(join(root, suite.file));
    else walk(join(root, suite.dir));
    const lines = files.map(path => `${posix(relative(root, path))} ${sha256(lf(readFileSync(path)))}`);
    return { files: files.length, bytes: files.reduce((total, path) => total + lf(readFileSync(path)).length, 0), sha256: sha256(lines.join('\n')) };
}

/** Load one publication into `{ summary, report }` (cases fully materialized) whatever its retained layout. */
export function loadPublication(root, suite) {
    if (suite.layout === 'single') {
        const publication = readJson(join(root, suite.file));
        return { summary: publication, report: publication[suite.field] };
    }
    const dir = join(root, suite.dir);
    const summary = readJson(join(dir, 'summary.json'));
    const names = readdirSync(dir);
    let header;
    if (names.includes('report.json')) header = readJson(join(dir, 'report.json'));
    else if (names.includes('report-common.json')) header = readJson(join(dir, 'report-common.json'));
    else header = readJson(join(dir, 'journal-common.json')).reportHeader;
    // The report header lists cases as full objects, as ids, or not at all (the case files then name them).
    let cases = header.cases;
    if (!Array.isArray(cases) || cases.some(item => typeof item === 'string')) {
        const ids = (Array.isArray(cases) ? cases : header.caseIds)
            ?? names.filter(name => /^(?:case|report)-.+\.json$/.test(name) && name !== 'report-common.json').map(name => name.replace(/^(?:case|report)-/, '').replace(/\.json$/, ''));
        cases = ids.map(id => readJson(join(dir, names.includes(`report-${id}.json`) ? `report-${id}.json` : `case-${id}.json`)));
    }
    return { summary, report: { ...header, cases } };
}

/** The pinned verdict of one case. A pinned probe that neither passed nor carries an observation is not interpretable. */
export function pinnedVerdictOf(item) {
    const observed = item.pinned?.observation?.outcome;
    if (observed !== undefined) {
        if (!verdicts.includes(observed)) throw new Error(`${item.caseId}: unknown pinned outcome ${observed}.`);
        return observed;
    }
    if (item.pinned?.pass === true) return 'supported';
    throw new Error(`${item.caseId}: the retained pinned result has neither an observation nor a pass.`);
}

/** The independent effect-contract verdict. */
export function effectVerdictOf(item) {
    const outcome = item.contractResult?.outcome;
    if (!verdicts.includes(outcome)) throw new Error(`${item.caseId}: no effect-contract verdict is retained.`);
    return outcome;
}

/** The coarse measured layers, derived from the retained evidence tag and the report's own scope sentence. */
export function measuredLayersOf(evidence, scope) {
    const text = String(evidence ?? '');
    const layers = [];
    if (/stored|cells/.test(text)) layers.push('stored-state');
    if (/scrollback/.test(text)) layers.push('actual-scrollback');
    // The evidence tag decides whether a resolution layer was measured; the scope sentence only says where it came from (and often
    // says what was NOT measured, e.g. "never theme-resolved RGB").
    if (/resolved/.test(text)) {
        layers.push(/derived parent-side|parent-derived/i.test(scope ?? '') ? 'parent-derived-resolution' : 'app-resolved-color');
    }
    // Retained CPR exchanges and controlled synthetic-phase captures are their own layers, never folded into stored state.
    if (/cpr/.test(text)) layers.push('cursor-position-report');
    if (/controlled-pixels/.test(text)) layers.push('controlled-pixels');
    if (layers.length === 0) throw new Error(`Cannot classify the measured layer of evidence "${text}".`);
    return layers;
}

const divergenceOf = (item, pinned, effect) => {
    if (item.divergence) {
        return { recordedInReport: true, kind: item.divergence.kind, pinned: item.divergence.pinnedOutcome ?? pinned, effect: item.divergence.contractOutcome ?? effect,
            reason: item.pinned?.note ?? item.conclusion?.note ?? null };
    }
    if (pinned === effect) return null;
    return { recordedInReport: false, kind: 'derived-by-inventory:pinned-differs-from-effect', pinned, effect,
        reason: item.pinned?.observation?.note ?? item.pinned?.note ?? 'the pinned probe did not decide from the evidence this suite measured; a later suite re-measured the id at another layer' };
};

/** The receipt that binds the run, and the entry inside it that binds this suite's own observations. */
export function receiptBinding(receipt, suiteId) {
    if (!receipt) return null;
    const own = Object.values(receipt).find(value => value && typeof value === 'object' && value.suiteId === suiteId);
    const cell = receipt.suiteId === suiteId ? null : pick(receipt, ['file', 'sha256']);
    return { schemaVersion: receipt.schemaVersion, runId: receipt.runId ?? own?.runId ?? null, cellReceipt: cell,
        suiteEvidence: own ? pick(own, ['suiteId', 'file', 'sha256', 'runFile', 'runSha256', 'counts', 'contractsVersion']) : null };
}

const pick = (source, keys) => Object.fromEntries(keys.filter(key => source?.[key] !== undefined).map(key => [key, source[key]]));

/** The App/adapter/source identity of one measured run, with `null` where the retained summary does not record it. */
function runIdentity(summary, report) {
    const context = summary.context ?? {};
    const grading = report.adapter ?? summary.adapter ?? null;
    return {
        ownedRunId: summary.ownedRunId ?? summary.runId ?? null,
        measuredAt: summary.measuredAt ?? null,
        appRevision: summary.appRevision ?? summary.adapterRevision ?? (/\+([0-9a-f]{7,40})$/.exec(context.appVersion ?? '')?.[1] ?? null),
        appVersion: context.appVersion ?? null,
        appVersionSource: context.appVersionSource ?? null,
        executableSha256: context.executableSha256 ?? null,
        scriptsRevision: summary.scriptsRevision ?? null,
        collectorAdapterId: context.adapter ?? null,
        collectorAdapterBundleSha256: context.adapterBundleSha256 ?? null,
        gradingAdapterId: grading?.id ?? context.gradingAdapterId ?? null,
        gradingAdapterBundleSha256: grading?.bundleSha256 ?? null,
        sourceSnapshotPolicy: context.sourceSnapshotPolicy ?? null,
        sourceSnapshotManifestSha256: context.sourceSnapshotManifestSha256 ?? null,
    };
}

/** What the retained publication says about its stock-collector counts and whether they were measured in that run. */
export function stockProvenance(summary) {
    const stock = summary.stockCollector ?? null;
    if (!stock) return null;
    const runId = summary.stockCollectorRunId ?? null;
    let basis;
    if (stock.rerun === false) basis = 'carried-forward';
    else if (runId) basis = 'collected-run-id-recorded';
    else if (stock.rawSha256) basis = 'collected-raw-digest-recorded';
    else basis = 'unspecified';
    return { basis, runId, counts: stock.counts ?? null, suiteComplete: stock.suiteComplete ?? null, probeHash: stock.probeHash ?? null,
        rawSha256: stock.rawSha256 ?? null, sourceRevision: stock.sourceRevision ?? null, note: stock.note ?? null };
}

async function loadContract(suite) {
    const module = await import(new URL(suite.module, new URL('.', import.meta.url)));
    const [suiteId, version, caseIds] = suite.exports.map(name => {
        if (!(name in module)) throw new Error(`${suite.module} no longer exports ${name}.`);
        return module[name];
    });
    return { suiteId, contractsVersion: version, caseIds: [...caseIds] };
}

/** Run one suite's trusted rederivation: its publication test rebuilds the retained report from the retained journals. */
export function rederive(suite) {
    const test = join(scriptsDir, suite.test);
    // NODE_OPTIONS could filter or redirect the run; the reporter summary must also show tests that ran, passed and none that failed.
    const env = { ...process.env };
    delete env.NODE_OPTIONS;
    const run = spawnSync(process.execPath, ['--test', '--test-reporter=tap', test], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], maxBuffer: 1 << 26, env });
    const count = name => Number(new RegExp(String.raw`^# ${name} (\d+)$`, 'm').exec(run.stdout ?? '')?.[1] ?? Number.NaN);
    const ran = run.status === 0 && count('tests') > 0 && count('pass') === count('tests') && count('fail') === 0 && count('skipped') === 0;
    const result = { status: ran ? 'passed' : 'failed', command: `node --test scripts/terminfo/${suite.test}`, testSha256: testDigest(suite) };
    // Diagnostics for the CLI only: non-enumerable, so they are never serialized into the inventory.
    Object.defineProperty(result, 'output', { value: `${run.stdout ?? ''}${run.stderr ?? ''}${run.error ? String(run.error) : ''}` });
    return result;
}

/** The digest of the publication test that performs a suite's rederivation, so a result is bound to the test that produced it. */
export const testDigest = suite => sha256(lf(readFileSync(join(scriptsDir, suite.test))));

const rederivationOf = (suite, options) => {
    if (options.rederive === false) return { status: 'not-run', command: `node --test scripts/terminfo/${suite.test}`, testSha256: testDigest(suite) };
    return (options.rederivation ?? rederive)(suite);
};

async function suiteRecord(root, suite, options) {
    const { summary, report } = loadPublication(root, suite);
    const contract = await loadContract(suite);
    const ids = report.cases.map(item => item.caseId);
    return {
        reportCaseIds: ids,
        record: {
            suite: suite.suite,
            suiteId: report.suiteId ?? contract.suiteId,
            contractsVersion: report.contractsVersion ?? contract.contractsVersion,
            liveContractsVersion: contract.contractsVersion,
            publication: suite.layout === 'single' ? suite.file : `${suite.dir}/`,
            doc: suite.doc,
            caseCount: ids.length,
            caseIds: contract.caseIds,
            evidence: evidenceDigest(root, suite),
            receipt: receiptBinding(summary.receipt, report.suiteId ?? contract.suiteId),
            run: runIdentity(summary, report),
            stock: stockProvenance(summary),
            rederivation: rederivationOf(suite, options),
        },
        report,
        contract,
    };
}

const claimOf = (suite, report, item, rederivationStatus) => {
    const pinned = pinnedVerdictOf(item), effect = effectVerdictOf(item);
    return {
        id: item.caseId,
        suite: suite.suite,
        status: rederivationStatus === 'passed' ? 'qualified' : 'declared-not-rederived',
        pinned: { verdict: pinned, note: item.pinned?.note ?? item.pinned?.observation?.note ?? null },
        effect: { verdict: effect },
        reportedConclusion: { outcome: item.conclusion?.outcome ?? null, grading: item.conclusion?.grading ?? null },
        divergence: divergenceOf(item, pinned, effect),
        measuredLayers: measuredLayersOf(item.conclusion?.evidence, report.scope),
        evidenceTag: item.conclusion?.evidence ?? null,
    };
};

const rollup = claims => {
    const effectVerdicts = [...new Set(claims.map(claim => claim.effect.verdict))];
    return {
        pinned: { best: claims.some(claim => claim.pinned.verdict === 'supported') ? 'supported' : claims[0].pinned.verdict, byClaim: claims.map(claim => ({ suite: claim.suite, verdict: claim.pinned.verdict })) },
        effect: { verdict: effectVerdicts.length === 1 ? effectVerdicts[0] : 'mixed', byClaim: claims.map(claim => ({ suite: claim.suite, verdict: claim.effect.verdict })) },
    };
};

/** Aggregate counts, computed only from qualified claims; every consumer reads these instead of recounting. */
export function totalsOf(rows, baseline) {
    // Only qualified claims count, and a row's aggregate verdicts are re-derived from them alone: a failed suite must not lend its verdicts.
    const counted = rows.map(row => ({ row, claims: row.claims.filter(claim => claim.status === 'qualified') })).filter(entry => entry.claims.length > 0)
        .map(({ row, claims }) => ({ id: row.id, claims, ...rollup(claims) }));
    const qualified = counted;
    const effectSupported = qualified.filter(row => row.effect.verdict === 'supported');
    const pinnedSupported = qualified.filter(row => row.pinned.best === 'supported');
    const pinnedInconclusive = qualified.filter(row => row.pinned.best !== 'supported');
    const inconclusiveBaseline = Object.values(baseline.outcomes).filter(outcome => outcome === 'inconclusive').length;
    // The remaining counts are against the stock-inconclusive baseline: an id the stock collector never graded is neither in it nor remaining from it.
    const ofStockInconclusive = list => list.filter(row => baseline.outcomes[row.id] === 'inconclusive').length;
    const total = Object.keys(baseline.outcomes).length;
    return {
        uniqueBenchmarkIds: qualified.length,
        suiteClaims: qualified.reduce((count, row) => count + row.claims.length, 0),
        idsWithMultipleClaims: qualified.filter(row => row.claims.length > 1).map(row => row.id),
        effectSupportedIds: effectSupported.length,
        pinnedAndEffectSupportedIds: effectSupported.filter(row => row.pinned.best === 'supported').length,
        effectSupportedPinnedInconclusiveIds: pinnedInconclusive.filter(row => row.effect.verdict === 'supported').map(row => row.id),
        effectSupportedOfStockInconclusive: ofStockInconclusive(effectSupported),
        pinnedSupportedOfStockInconclusive: ofStockInconclusive(pinnedSupported),
        ungradedStockIdsWithEvidence: qualified.filter(row => !(row.id in baseline.outcomes)).map(row => row.id),
        pinnedSupportedIds: pinnedSupported.length,
        baseline: {
            description: 'stock terminfo.dev@4.0.0 collector run, graded cases only',
            allCases: total,
            stockSupported: Object.values(baseline.outcomes).filter(outcome => outcome === 'supported').length,
            stockUnsupported: Object.values(baseline.outcomes).filter(outcome => outcome === 'unsupported').length,
            stockInconclusive: inconclusiveBaseline,
        },
        remainingAgainstStockInconclusive: {
            effectUnqualified: inconclusiveBaseline - ofStockInconclusive(effectSupported),
            notPinnedSupported: inconclusiveBaseline - ofStockInconclusive(pinnedSupported),
        },
    };
}

/** Build the inventory. `options.rederive === false` skips the (slow) trusted rederivation and marks every claim unrederived. */
export async function buildInventory({ root = defaultQaRoot, ...options } = {}) {
    const baseline = readJson(join(root, stockBaselineFile));
    const suites = [], claimsById = new Map();
    const loaded = await Promise.all(benchmarkSuites.map(suite => suiteRecord(root, suite, options)));
    for (const [index, entry] of loaded.entries()) {
        const suite = benchmarkSuites[index];
        suites.push(entry.record);
        for (const item of entry.report.cases) {
            if (!claimsById.has(item.caseId)) claimsById.set(item.caseId, []);
            claimsById.get(item.caseId).push(claimOf(suite, entry.report, item, entry.record.rederivation.status));
        }
    }
    const benchmark = [...claimsById].sort(([a], [b]) => byText(a, b)).map(([id, claims]) => ({ id, kind: 'benchmark', stockOutcome: baseline.outcomes[id] ?? null,
        ...(Object.hasOwn(baseline.ungradedDiagnostics ?? {}, id) ? { stockStatus: 'ungraded-collector-error' } : {}), ...rollup(claims), claims }));
    const findings = await Promise.all(findingSuites.map(suite => {
        const publication = suite.suite === 'truecolor-literal-finding' ? null : loadPublication(root, { layout: 'directory', dir: suite.dir });
        return findingRecord(root, suite, publication, options);
    }));
    const diagnostics = Object.entries(baseline.ungradedDiagnostics ?? {}).map(([id, diagnostic]) => ({
        id, graded: false, suiteComplete: baseline.source.suiteComplete, kind: diagnostic.kind, name: diagnostic.name, message: diagnostic.message,
        note: 'An ungraded collector diagnostic, retained as such. It is not an unsupported case: the stock unsupported count is separate.' }));
    const inventory = {
        schemaVersion: inventorySchemaVersion,
        generatedFor: inventoryDate,
        reproduce: ['node scripts/terminfo-inventory.mjs --write --rederive', 'node scripts/terminfo-inventory.mjs --check'],
        pinned: { package: baseline.source.pinnedPackage, bundleSha256: baseline.source.pinnedBundleSha256, probeHash: baseline.source.probeHash },
        stockBaseline: { file: stockBaselineFile, ...pick(baseline.source, ['stockCollectorRunId', 'measuredAt', 'identity', 'suiteComplete', 'rawSha256', 'freshness', 'localRawArtifact']), target: baseline.source.target, counts: baseline.counts, stockUnsupported: baseline.stockUnsupported },
        totals: totalsOf(benchmark, baseline),
        suites,
        benchmark,
        findings,
        ungradedDiagnostics: diagnostics,
    };
    assertInventory(inventory, { baseline, contracts: new Map(loaded.map(entry => [entry.record.suite, entry.contract])), reportCaseIds: new Map(loaded.map(entry => [entry.record.suite, entry.reportCaseIds])) });
    return inventory;
}

async function findingRecord(root, suite, publication, options) {
    const contract = await loadContract(suite);
    const dir = join(root, suite.dir);
    const summaryFile = readJson(join(dir, 'summary.json'));
    const rederivation = rederivationOf(suite, options);
    const base = { suite: suite.suite, kind: 'ntilde-finding', suiteId: contract.suiteId, contractsVersion: contract.contractsVersion, publication: `${suite.dir}/`, doc: suite.doc,
        caseIds: contract.caseIds, measuredLayer: suite.measuredLayer, evidence: evidenceDigest(root, { layout: 'directory', dir: suite.dir }), rederivation };
    if (suite.suite === 'truecolor-literal-finding') {
        return { ...base, run: { before: pick(summaryFile.before, ['runId', 'appRevision', 'scriptsRevision', 'sourceSnapshotPolicy', 'receiptSchemaVersion', 'appVersion', 'executableSha256']),
            after: pick(summaryFile.after, ['runId', 'appRevision', 'scriptsRevision', 'sourceSnapshotPolicy', 'receiptSchemaVersion', 'appVersion', 'executableSha256']) },
            outcomes: { before: summaryFile.before.findingCounts, after: summaryFile.after.findingCounts },
            note: 'A criterion Ntilde failed before the VT fix (5 values snapped x fg/bg = 10 unsupported) and passes after it (10 supported). Not a terminfo.dev case.' };
    }
    const counts = {};
    for (const item of publication.report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { ...base, run: runIdentity(summaryFile, publication.report), outcomes: counts,
        note: 'App-observed underline-color resolution, reported separately from the parent-derived expectation. Not a terminfo.dev case.' };
}

const byText = (a, b) => Number(a > b) - Number(a < b);
const sortedJson = list => JSON.stringify([...list].sort(byText));

/** Guards over one claim of one row. */
function claimProblems(inventory, row, claim, suiteNames, seenClaims) {
    const problems = [];
    const key = `${row.id}\u0000${claim.suite}`;
    if (seenClaims.has(key)) problems.push(`${row.id}: duplicate claim for suite ${claim.suite}.`);
    seenClaims.add(key);
    if (!suiteNames.has(claim.suite)) problems.push(`${row.id}: claim names unknown suite ${claim.suite}.`);
    if (!verdicts.includes(claim.pinned.verdict) || !verdicts.includes(claim.effect.verdict)) problems.push(`${row.id}: unknown verdict.`);
    for (const layer of claim.measuredLayers) if (!measuredLayerNames.includes(layer)) problems.push(`${row.id}: unknown measured layer ${layer}.`);
    const agree = claim.pinned.verdict === claim.effect.verdict;
    if (agree && claim.divergence) problems.push(`${row.id}: a divergence is recorded for agreeing verdicts.`);
    if (!agree && !claim.divergence) problems.push(`${row.id} (${claim.suite}): pinned and effect verdicts differ but no divergence is recorded; they must not be collapsed.`);
    if (claim.divergence && (claim.divergence.pinned !== claim.pinned.verdict || claim.divergence.effect !== claim.effect.verdict)) problems.push(`${row.id}: the divergence does not match the claim's verdicts.`);
    const suite = inventory.suites.find(item => item.suite === claim.suite);
    if (suite && claim.status === 'qualified' && suite.rederivation.status !== 'passed') problems.push(`${row.id}: counted as qualified without a passed rederivation of suite ${claim.suite}.`);
    if (suite && claim.status !== 'qualified' && suite.rederivation.status === 'passed') problems.push(`${row.id}: rederived suite ${claim.suite} left a claim unqualified.`);
    return problems;
}

/** Guards over one benchmark row and its claims. */
function rowProblems(inventory, row, baseline, suiteNames, seenClaims) {
    const problems = [];
    if (row.claims.length === 0) problems.push(`${row.id}: no claim.`);
    // An id the stock collector could not grade (an ungraded collector diagnostic) may carry repaired-probe and effect evidence, but never a stock outcome.
    const ungraded = Object.hasOwn(baseline.ungradedDiagnostics ?? {}, row.id);
    if (!(row.id in baseline.outcomes) && !ungraded) problems.push(`${row.id}: not a case of the pinned stock baseline.`);
    if (ungraded && (row.stockOutcome !== null || row.stockStatus !== 'ungraded-collector-error')) problems.push(`${row.id}: an ungraded stock diagnostic has no stock outcome and must say so.`);
    if (!ungraded && row.stockStatus !== undefined) problems.push(`${row.id}: only an ungraded stock diagnostic carries a stock status.`);
    if (/^(?:finding|ntilde)\./.test(row.id)) problems.push(`${row.id}: an Ntilde-specific id cannot be a benchmark id.`);
    if (row.stockOutcome !== (baseline.outcomes[row.id] ?? null)) problems.push(`${row.id}: stock outcome differs from the baseline.`);
    if (row.stockOutcome !== 'inconclusive' && !ungraded) problems.push(`${row.id}: qualification evidence can only cover stock-inconclusive cases (stock: ${row.stockOutcome}).`);
    for (const claim of row.claims) problems.push(...claimProblems(inventory, row, claim, suiteNames, seenClaims));
    const derived = rollup(row.claims);
    if (JSON.stringify(derived.pinned) !== JSON.stringify(row.pinned) || JSON.stringify(derived.effect) !== JSON.stringify(row.effect)) problems.push(`${row.id}: the pinned/effect roll-up differs from its claims.`);
    return problems;
}

/** Guards over one suite: its claims must be exactly its contract's case ids. */
function suiteProblems(inventory, suite, contracts, options) {
    const problems = [];
    const contract = contracts?.get(suite.suite);
    const claimed = inventory.benchmark.flatMap(row => row.claims.filter(claim => claim.suite === suite.suite).map(() => row.id));
    const retained = options.reportCaseIds?.get(suite.suite);
    if (retained && sortedJson(retained) !== sortedJson(suite.caseIds)) problems.push(`${suite.suite}: the retained report's case ids differ from the live contract's (lost or added case).`);
    if (contract && JSON.stringify(contract.caseIds) !== JSON.stringify(suite.caseIds)) problems.push(`${suite.suite}: contract ids differ from the recorded ones.`);
    if (sortedJson(claimed) !== sortedJson(suite.caseIds)) problems.push(`${suite.suite}: its claims are not exactly its contract's case ids (lost, added or duplicated case).`);
    if (claimed.length !== suite.caseCount) problems.push(`${suite.suite}: ${claimed.length} claims for ${suite.caseCount} retained cases.`);
    if (suite.evidence.files === 0) problems.push(`${suite.suite}: no retained evidence files.`);
    return problems;
}

/** Structural guards: duplicate counting, lost cases, unsupported promotion and pinned/effect collapse all throw. */
export function assertInventory(inventory, { baseline, contracts, ...options }) {
    const problems = [];
    const suiteNames = new Set(inventory.suites.map(suite => suite.suite));
    if (suiteNames.size !== inventory.suites.length) problems.push('A suite appears twice.');
    const ids = inventory.benchmark.map(row => row.id);
    if (new Set(ids).size !== ids.length) problems.push('A benchmark id is listed twice; multiple suites must be separate claims of one row.');
    const seenClaims = new Set();
    for (const row of inventory.benchmark) problems.push(...rowProblems(inventory, row, baseline, suiteNames, seenClaims));
    for (const suite of inventory.suites) problems.push(...suiteProblems(inventory, suite, contracts, options));
    const totals = totalsOf(inventory.benchmark, baseline);
    if (JSON.stringify(totals) !== JSON.stringify(inventory.totals)) problems.push('The recorded totals differ from the recomputed ones.');
    const allPassed = inventory.suites.every(suite => suite.rederivation.status === 'passed');
    if (allPassed && totals.suiteClaims !== inventory.suites.reduce((count, suite) => count + suite.caseCount, 0)) problems.push('Suite claims differ from the sum of suite case counts.');
    for (const diagnostic of inventory.ungradedDiagnostics) {
        if (diagnostic.graded !== false) problems.push(`${diagnostic.id}: a diagnostic must stay ungraded.`);
        if (diagnostic.suiteComplete !== false) problems.push(`${diagnostic.id}: the diagnostic's suite must be recorded as incomplete (suiteComplete=false).`);
    }
    if (problems.length) throw new Error(`Qualification inventory is inconsistent:\n- ${problems.join('\n- ')}`);
}

const short = (hash, length = 8) => (hash ? hash.slice(0, length) : 'n/a');
const code = text => `\`${text}\``;
const codeList = ids => ids.map(code).join(', ');

function diagnosticLine(diagnostic) {
    const complete = code(`suiteComplete=${diagnostic.suiteComplete}`);
    return `- ${code(diagnostic.id)}: ${diagnostic.kind} (${diagnostic.name}). Ungraded, ${complete}. It is not a graded stock case: it has no stock outcome and is in no stock count or remaining count. Where a repaired-probe suite qualifies it, that is a separate claim of that suite.`;
}

function totalsSection(inventory) {
    const { totals, stockBaseline } = inventory;
    const remaining = totals.remainingAgainstStockInconclusive;
    const allBaseline = totals.baseline;
    const multi = `${totals.idsWithMultipleClaims.length} (${codeList(totals.idsWithMultipleClaims)})`;
    const freshness = stockBaseline.freshness.split(';')[0];
    const baselineLine = `Baseline: stock run ${code(stockBaseline.stockCollectorRunId)} (${stockBaseline.measuredAt}, ${freshness}), ${allBaseline.allCases} graded cases: ${allBaseline.stockSupported} supported, ${allBaseline.stockUnsupported} unsupported, ${allBaseline.stockInconclusive} inconclusive. Every qualified id that has a stock outcome is a stock-inconclusive case (an ungraded diagnostic has none), so the remaining counts are taken against the ${allBaseline.stockInconclusive} inconclusive ones.`;
    const outside = allBaseline.stockUnsupported + remaining.notPinnedSupported;
    const unsupportedLine = `The baseline's single stock-unsupported case (${codeList(stockBaseline.stockUnsupported)}) is graded and separate from both remaining counts. The ${allBaseline.stockUnsupported} unsupported plus the ${remaining.notPinnedSupported} not-pinned-supported inconclusive cases are the ${outside} cases outside the ${allBaseline.stockSupported} stock-supported plus ${totals.pinnedSupportedOfStockInconclusive} pinned-supported ones.`;
    return ['## Totals', '', '| Measure | Value |', '|---|---:|',
        `| Unique benchmark ids with qualified effect evidence | ${totals.uniqueBenchmarkIds} |`,
        `| Suite claims (an id covered by two suites has two claims) | ${totals.suiteClaims} |`,
        `| Ids with more than one claim | ${multi} |`,
        `| Effect contract supported | ${totals.effectSupportedIds} |`,
        `| Supported by both the pinned probe and the effect contract | ${totals.pinnedAndEffectSupportedIds} |`,
        `| Effect supported, pinned verdict inconclusive | ${totals.effectSupportedPinnedInconclusiveIds.length} |`, '',
        'The pinned and effect verdicts are different measurements and stay separate fields. Where the effect contract decides what the pinned probe cannot, the divergence is recorded, never absorbed into one grade. An id whose suites disagree is rolled up as pinned-supported when any claim\'s pinned probe decided supported (`pinned.best`) and keeps every per-claim verdict.', '',
        `Effect supported but pinned inconclusive: ${codeList(totals.effectSupportedPinnedInconclusiveIds)}.`, '',
        '## Remaining cases against an explicit baseline', '', baselineLine, '',
        '| Remaining | Count |', '|---|---:|',
        `| Effect-unqualified (${allBaseline.stockInconclusive} − ${totals.effectSupportedOfStockInconclusive}) | ${remaining.effectUnqualified} |`,
        `| Not pinned-supported (${allBaseline.stockInconclusive} − ${totals.pinnedSupportedOfStockInconclusive}) | ${remaining.notPinnedSupported} |`, '',
        ...(totals.ungradedStockIdsWithEvidence.length ? [`Ungraded stock diagnostics with qualified repaired-probe and effect evidence: ${codeList(totals.ungradedStockIdsWithEvidence)}. They have no stock outcome, so they are in neither the stock-inconclusive baseline nor the remaining counts above.`, ''] : []),
        unsupportedLine, '', '### Ungraded diagnostic', '',
        ...inventory.ungradedDiagnostics.map(diagnosticLine)];
}

function suitesSection(inventory) {
    const lines = ['', '## Suites and retained evidence', '',
        '| Suite | Cases | Contracts | Rederivation | Receipt | App revision | Policy | Evidence files | Evidence SHA-256 |', '|---|---:|---:|---|---:|---|---|---:|---|'];
    for (const suite of inventory.suites) {
        const policy = suite.run.sourceSnapshotPolicy?.replace('readonly-owned-source-graph-', '') ?? 'n/a';
        lines.push(`| ${code(suite.suite)} | ${suite.caseCount} | v${suite.contractsVersion} | ${suite.rederivation.status} | ${suite.receipt.schemaVersion ?? 'n/a'} | ${code(short(suite.run.appRevision, 8))} | ${policy} | ${suite.evidence.files} | ${code(short(suite.evidence.sha256))} |`);
    }
    lines.push('', 'Each suite is rederived by its publication test (`node --test scripts/terminfo/<suite>-publication.test.mjs`), which rebuilds the retained report from the retained journals through the live grading module and compares it byte for byte. Declarations and unit tests alone are not counted as qualification.', '');
    return lines;
}

function stockRunCell(stock) {
    if (stock?.runId) return code(short(stock.runId));
    if (stock?.rawSha256) return `raw ${code(short(stock.rawSha256))}`;
    return 'n/a';
}

function stockSection(inventory) {
    const lines = ['## Stock collector counts', '',
        'Stock grades are never rewritten by owned evidence. Each publication records its own stock counts; `basis` says whether they were measured in that run.', '',
        '| Suite | Basis | Stock run | Counts (inconclusive/supported/unsupported) | Collector source |', '|---|---|---|---|---|'];
    for (const suite of inventory.suites) {
        const stock = suite.stock;
        const counts = stock?.counts ? `${stock.counts.inconclusive}/${stock.counts.supported}/${stock.counts.unsupported}` : 'carried (see note)';
        const source = stock?.sourceRevision ? code(short(stock.sourceRevision)) : 'n/a';
        lines.push(`| ${code(suite.suite)} | ${stock?.basis ?? 'none'} | ${stockRunCell(stock)} | ${counts} | ${source} |`);
    }
    const { stockBaseline } = inventory;
    const rawDigest = `${stockBaseline.rawSha256.slice(0, 12)}…`;
    lines.push('', `The baseline run itself is retained as a case-id to outcome map in [${code(stockBaselineFile)}](${stockBaselineFile}); its raw output is git-ignored and not in the repository (${code(rawDigest)}). Profile: macOS, Ntilde ${stockBaseline.target.version} (identity ${code(stockBaseline.identity)}, operator supplied), no outer terminal or mux, default settings.`,
        'The stock collector reports `suiteComplete=false` in every run.', '');
    return lines;
}

function benchmarkSection(inventory) {
    const lines = ['## Benchmark ids', '', '| Id | Stock | Pinned | Effect | Layers | Suites | Divergence |', '|---|---|---|---|---|---|---|'];
    for (const row of inventory.benchmark) {
        const layers = [...new Set(row.claims.flatMap(claim => claim.measuredLayers))].join(' + ');
        const divergence = row.claims.filter(claim => claim.divergence).map(claim => {
            const derived = claim.divergence.recordedInReport ? '' : ' (derived)';
            return `${claim.suite}: ${claim.divergence.kind}${derived}`;
        }).join('; ') || '-';
        const pinned = row.pinned.byClaim.length > 1 ? row.pinned.byClaim.map(entry => `${entry.suite}=${entry.verdict}`).join(', ') : row.pinned.best;
        lines.push(`| ${code(row.id)} | ${row.stockOutcome ?? 'ungraded'} | ${pinned} | ${row.effect.verdict} | ${layers} | ${row.claims.map(claim => claim.suite).join(', ')} | ${divergence} |`);
    }
    return lines;
}

function findingsSection(inventory) {
    const lines = ['', '## Ntilde-specific findings (not benchmark ids)', ''];
    for (const finding of inventory.findings) {
        lines.push(`- ${code(finding.suite)} (${finding.caseIds.length} cases, contracts v${finding.contractsVersion}, rederivation ${finding.rederivation.status}): ${finding.note} Layer: ${finding.measuredLayer}.`);
    }
    lines.push('', 'Controls and aggregate summaries inside each publication are retained in their journals and are not benchmark ids; they are not counted here.', '',
        '## Guards', '',
        '`assertInventory` (run on every generation and by `scripts/terminfo/qualification-inventory.test.mjs`) refuses: a benchmark id listed twice or claimed twice by one suite; a retained report whose case ids differ from the live contract (lost or added case); a claim whose id is not a stock-inconclusive case of the pinned baseline; an Ntilde finding or the `cursor.reverse-wrap` diagnostic among benchmark ids; a pinned/effect difference without a recorded divergence; a qualified claim whose suite was not rederived; and recorded totals that differ from the recomputed ones.', '');
    return lines;
}

/** The readable summary. Generated, never edited by hand. */
export function renderSummary(inventory) {
    const pinnedHash = `${short(inventory.pinned.bundleSha256)}…`;
    const head = ['# Terminfo qualification inventory (2026-11-09)', '',
        'Generated by `node scripts/terminfo-inventory.mjs --write --rederive` from the merged contract modules and the retained publications; do not edit by hand.',
        'Machine-readable form: [`2026-11-09-qualification-inventory.json`](2026-11-09-qualification-inventory.json). It consolidates the evidence already published; it adds no measurement and rewrites no grade.', '',
        `Pinned collector: ${code(inventory.pinned.package)}, bundle SHA-256 ${code(pinnedHash)}, probeHash ${code(inventory.pinned.probeHash)}.`, ''];
    return [...head, ...totalsSection(inventory), ...suitesSection(inventory), ...stockSection(inventory), ...benchmarkSection(inventory), ...findingsSection(inventory)].join('\n');
}

export const inventoryPaths = (root = defaultQaRoot) => ({ json: join(root, inventoryFiles.json), markdown: join(root, inventoryFiles.markdown) });
export const serialize = inventory => `${JSON.stringify(inventory, null, 2)}\n`;
