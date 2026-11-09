// Reproducible inventory of the terminfo.dev@4.0.0 benchmark IDs that carry retained owned effect evidence.
//
// Everything here is derived from the merged contract modules and the retained publications under docs/qa/terminfo; no
// total is hand-written. A claim only counts as qualified when its suite's trusted rederivation (the suite's publication
// test, which rebuilds the retained report from the retained journals and compares it byte for byte) passed. The inventory
// is read-only over historical evidence: it never rewrites a grade, publication, journal, receipt or source policy.
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync, statSync } from 'node:fs';
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
const readJson = path => JSON.parse(readFileSync(path));
const posix = path => path.split(sep).join('/');

/** Stable digest over every retained file of one publication: `<relative path> <sha256>` lines, sorted. */
function evidenceDigest(root, suite) {
    const files = [];
    const walk = dir => {
        for (const entry of readdirSync(dir, { withFileTypes: true }).sort((a, b) => (a.name < b.name ? -1 : 1))) {
            const path = join(dir, entry.name);
            if (entry.isDirectory()) walk(path);
            else files.push(path);
        }
    };
    if (suite.layout === 'single') files.push(join(root, suite.file));
    else walk(join(root, suite.dir));
    const lines = files.map(path => `${posix(relative(root, path))} ${sha256(readFileSync(path))}`);
    return { files: files.length, bytes: files.reduce((total, path) => total + statSync(path).size, 0), sha256: sha256(lines.join('\n')) };
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
    const run = spawnSync(process.execPath, ['--test', test], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], maxBuffer: 1 << 26 });
    return { status: run.status === 0 ? 'passed' : 'failed', command: `node --test scripts/terminfo/${suite.test}`, testSha256: testDigest(suite) };
}

/** The digest of the publication test that performs a suite's rederivation, so a result is bound to the test that produced it. */
export const testDigest = suite => sha256(readFileSync(join(scriptsDir, suite.test)));

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
    const qualified = rows.filter(row => row.claims.some(claim => claim.status === 'qualified'));
    const effectSupported = qualified.filter(row => row.effect.verdict === 'supported');
    const pinnedSupported = qualified.filter(row => row.pinned.best === 'supported');
    const pinnedInconclusive = qualified.filter(row => row.pinned.best !== 'supported');
    const inconclusiveBaseline = Object.values(baseline.outcomes).filter(outcome => outcome === 'inconclusive').length;
    const total = Object.keys(baseline.outcomes).length;
    return {
        uniqueBenchmarkIds: qualified.length,
        suiteClaims: rows.reduce((count, row) => count + row.claims.filter(claim => claim.status === 'qualified').length, 0),
        idsWithMultipleClaims: qualified.filter(row => row.claims.length > 1).map(row => row.id),
        effectSupportedIds: effectSupported.length,
        pinnedAndEffectSupportedIds: effectSupported.filter(row => row.pinned.best === 'supported').length,
        effectSupportedPinnedInconclusiveIds: pinnedInconclusive.filter(row => row.effect.verdict === 'supported').map(row => row.id),
        pinnedSupportedIds: pinnedSupported.length,
        baseline: {
            description: 'stock terminfo.dev@4.0.0 collector run, graded cases only',
            allCases: total,
            stockSupported: Object.values(baseline.outcomes).filter(outcome => outcome === 'supported').length,
            stockUnsupported: Object.values(baseline.outcomes).filter(outcome => outcome === 'unsupported').length,
            stockInconclusive: inconclusiveBaseline,
        },
        remainingAgainstStockInconclusive: {
            effectUnqualified: inconclusiveBaseline - effectSupported.length,
            notPinnedSupported: inconclusiveBaseline - pinnedSupported.length,
        },
    };
}

/** Build the inventory. `options.rederive === false` skips the (slow) trusted rederivation and marks every claim unrederived. */
export async function buildInventory({ root = defaultQaRoot, ...options } = {}) {
    const baseline = readJson(join(root, stockBaselineFile));
    const suites = [], claimsById = new Map(), loaded = [];
    for (const suite of benchmarkSuites) {
        const entry = await suiteRecord(root, suite, options);
        suites.push(entry.record);
        loaded.push(entry);
        for (const item of entry.report.cases) {
            if (!claimsById.has(item.caseId)) claimsById.set(item.caseId, []);
            claimsById.get(item.caseId).push(claimOf(suite, entry.report, item, entry.record.rederivation.status));
        }
    }
    const benchmark = [...claimsById].sort(([a], [b]) => (a < b ? -1 : 1)).map(([id, claims]) => ({ id, kind: 'benchmark', stockOutcome: baseline.outcomes[id] ?? null, ...rollup(claims), claims }));
    const findings = [];
    for (const suite of findingSuites) {
        const publication = suite.suite === 'truecolor-literal-finding' ? null : loadPublication(root, { layout: 'directory', dir: suite.dir });
        findings.push(await findingRecord(root, suite, publication, options));
    }
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

/** Structural guards: duplicate counting, lost cases, unsupported promotion and pinned/effect collapse all throw. */
export function assertInventory(inventory, { baseline, contracts, ...options }) {
    const problems = [];
    const suiteNames = new Set(inventory.suites.map(suite => suite.suite));
    if (suiteNames.size !== inventory.suites.length) problems.push('A suite appears twice.');
    const ids = inventory.benchmark.map(row => row.id);
    if (new Set(ids).size !== ids.length) problems.push('A benchmark id is listed twice; multiple suites must be separate claims of one row.');
    const seenClaims = new Set();
    for (const row of inventory.benchmark) {
        if (row.claims.length === 0) problems.push(`${row.id}: no claim.`);
        if (!(row.id in baseline.outcomes)) problems.push(`${row.id}: not a case of the pinned stock baseline.`);
        if (/^(?:finding|ntilde)\./.test(row.id)) problems.push(`${row.id}: an Ntilde-specific id cannot be a benchmark id.`);
        if (row.id === 'cursor.reverse-wrap') problems.push('cursor.reverse-wrap is an ungraded diagnostic, not a qualified or unsupported case.');
        if (row.stockOutcome !== baseline.outcomes[row.id]) problems.push(`${row.id}: stock outcome differs from the baseline.`);
        if (row.stockOutcome !== 'inconclusive') problems.push(`${row.id}: qualification evidence can only cover stock-inconclusive cases (stock: ${row.stockOutcome}).`);
        for (const claim of row.claims) {
            const key = `${row.id}\u0000${claim.suite}`;
            if (seenClaims.has(key)) problems.push(`${row.id}: duplicate claim for suite ${claim.suite}.`);
            seenClaims.add(key);
            if (!suiteNames.has(claim.suite)) problems.push(`${row.id}: claim names unknown suite ${claim.suite}.`);
            if (!verdicts.includes(claim.pinned.verdict) || !verdicts.includes(claim.effect.verdict)) problems.push(`${row.id}: unknown verdict.`);
            if (claim.measuredLayers.includes('pixels')) problems.push(`${row.id}: no benchmark claim here measures pixels.`);
            if (claim.pinned.verdict === claim.effect.verdict && claim.divergence) problems.push(`${row.id}: a divergence is recorded for agreeing verdicts.`);
            if (claim.pinned.verdict !== claim.effect.verdict && !claim.divergence) problems.push(`${row.id} (${claim.suite}): pinned and effect verdicts differ but no divergence is recorded; they must not be collapsed.`);
            if (claim.divergence && (claim.divergence.pinned !== claim.pinned.verdict || claim.divergence.effect !== claim.effect.verdict)) problems.push(`${row.id}: the divergence does not match the claim's verdicts.`);
            const suite = inventory.suites.find(item => item.suite === claim.suite);
            if (suite && claim.status === 'qualified' && suite.rederivation.status !== 'passed') problems.push(`${row.id}: counted as qualified without a passed rederivation of suite ${claim.suite}.`);
            if (suite && claim.status !== 'qualified' && suite.rederivation.status === 'passed') problems.push(`${row.id}: rederived suite ${claim.suite} left a claim unqualified.`);
        }
        const derived = rollup(row.claims);
        if (JSON.stringify(derived.pinned) !== JSON.stringify(row.pinned) || JSON.stringify(derived.effect) !== JSON.stringify(row.effect)) problems.push(`${row.id}: the pinned/effect roll-up differs from its claims.`);
    }
    for (const suite of inventory.suites) {
        const contract = contracts?.get(suite.suite);
        const claimed = inventory.benchmark.flatMap(row => row.claims.filter(claim => claim.suite === suite.suite).map(() => row.id));
        const retained = options.reportCaseIds?.get(suite.suite);
        if (retained && JSON.stringify([...retained].sort()) !== JSON.stringify([...suite.caseIds].sort())) problems.push(`${suite.suite}: the retained report's case ids differ from the live contract's (lost or added case).`);
        if (contract && JSON.stringify(contract.caseIds) !== JSON.stringify(suite.caseIds)) problems.push(`${suite.suite}: contract ids differ from the recorded ones.`);
        if (JSON.stringify([...claimed].sort()) !== JSON.stringify([...suite.caseIds].sort())) problems.push(`${suite.suite}: its claims are not exactly its contract's case ids (lost, added or duplicated case).`);
        if (claimed.length !== suite.caseCount) problems.push(`${suite.suite}: ${claimed.length} claims for ${suite.caseCount} retained cases.`);
        if (suite.evidence.files === 0) problems.push(`${suite.suite}: no retained evidence files.`);
    }
    const totals = totalsOf(inventory.benchmark, baseline);
    if (JSON.stringify(totals) !== JSON.stringify(inventory.totals)) problems.push('The recorded totals differ from the recomputed ones.');
    if (totals.suiteClaims !== inventory.suites.reduce((count, suite) => count + suite.caseCount, 0) && inventory.suites.every(suite => suite.rederivation.status === 'passed')) problems.push('Suite claims differ from the sum of suite case counts.');
    for (const diagnostic of inventory.ungradedDiagnostics) {
        if (diagnostic.graded !== false) problems.push(`${diagnostic.id}: a diagnostic must stay ungraded.`);
        if (diagnostic.suiteComplete !== false) problems.push(`${diagnostic.id}: the diagnostic's suite must be recorded as incomplete (suiteComplete=false).`);
    }
    if (problems.length) throw new Error(`Qualification inventory is inconsistent:\n- ${problems.join('\n- ')}`);
}

const short = (hash, length = 8) => (hash ? hash.slice(0, length) : 'n/a');

/** The readable summary. Generated, never edited by hand. */
export function renderSummary(inventory) {
    const { totals } = inventory;
    const lines = [];
    const push = (...text) => lines.push(...text);
    push('# Terminfo qualification inventory (2026-11-09)', '',
        'Generated by `node scripts/terminfo-inventory.mjs --write --rederive` from the merged contract modules and the retained publications; do not edit by hand.',
        'Machine-readable form: [`2026-11-09-qualification-inventory.json`](2026-11-09-qualification-inventory.json). It consolidates the evidence already published; it adds no measurement and rewrites no grade.', '',
        `Pinned collector: \`${inventory.pinned.package}\`, bundle SHA-256 \`${short(inventory.pinned.bundleSha256)}…\`, probeHash \`${inventory.pinned.probeHash}\`.`, '',
        '## Totals', '', '| Measure | Value |', '|---|---:|',
        `| Unique benchmark ids with qualified effect evidence | ${totals.uniqueBenchmarkIds} |`,
        `| Suite claims (an id covered by two suites has two claims) | ${totals.suiteClaims} |`,
        `| Ids with more than one claim | ${totals.idsWithMultipleClaims.length} (${totals.idsWithMultipleClaims.map(id => `\`${id}\``).join(', ')}) |`,
        `| Effect contract supported | ${totals.effectSupportedIds} |`,
        `| Supported by both the pinned probe and the effect contract | ${totals.pinnedAndEffectSupportedIds} |`,
        `| Effect supported, pinned verdict inconclusive | ${totals.effectSupportedPinnedInconclusiveIds.length} |`, '',
        'The pinned and effect verdicts are different measurements and stay separate fields. Where the effect contract decides what the pinned probe cannot, the divergence is recorded, never absorbed into one grade. An id whose suites disagree is rolled up as pinned-supported when any claim\'s pinned probe decided supported (`pinned.best`) and keeps every per-claim verdict.', '',
        `Effect supported but pinned inconclusive: ${totals.effectSupportedPinnedInconclusiveIds.map(id => `\`${id}\``).join(', ')}.`, '',
        '## Remaining cases against an explicit baseline', '',
        `Baseline: stock run \`${inventory.stockBaseline.stockCollectorRunId}\` (${inventory.stockBaseline.measuredAt}, ${inventory.stockBaseline.freshness.split(';')[0]}), ${totals.baseline.allCases} graded cases: ${totals.baseline.stockSupported} supported, ${totals.baseline.stockUnsupported} unsupported, ${totals.baseline.stockInconclusive} inconclusive. Every qualified id is a stock-inconclusive case, so the remaining counts are taken against the ${totals.baseline.stockInconclusive} inconclusive ones.`, '',
        '| Remaining | Count |', '|---|---:|',
        `| Effect-unqualified (${totals.baseline.stockInconclusive} − ${totals.effectSupportedIds}) | ${totals.remainingAgainstStockInconclusive.effectUnqualified} |`,
        `| Not pinned-supported (${totals.baseline.stockInconclusive} − ${totals.pinnedSupportedIds}) | ${totals.remainingAgainstStockInconclusive.notPinnedSupported} |`, '',
        `The baseline's single stock-unsupported case (${inventory.stockBaseline.stockUnsupported.map(id => `\`${id}\``).join(', ')}) is graded and separate from both remaining counts. The ${totals.baseline.stockUnsupported} unsupported plus the ${totals.remainingAgainstStockInconclusive.notPinnedSupported} not-pinned-supported inconclusive cases are the ${totals.baseline.stockUnsupported + totals.remainingAgainstStockInconclusive.notPinnedSupported} cases outside the ${totals.baseline.stockSupported} stock-supported plus ${totals.pinnedSupportedIds} pinned-supported ones.`, '',
        '### Ungraded diagnostic', '');
    for (const diagnostic of inventory.ungradedDiagnostics) {
        push(`- \`${diagnostic.id}\`: ${diagnostic.kind} (${diagnostic.name}). Ungraded, \`suiteComplete=${diagnostic.suiteComplete}\`. It is not a graded unsupported case and is counted in none of the totals above.`);
    }
    push('', '## Suites and retained evidence', '',
        '| Suite | Cases | Contracts | Rederivation | Receipt | App revision | Policy | Evidence files | Evidence SHA-256 |', '|---|---:|---:|---|---:|---|---|---:|---|');
    for (const suite of inventory.suites) {
        push(`| \`${suite.suite}\` | ${suite.caseCount} | v${suite.contractsVersion} | ${suite.rederivation.status} | ${suite.receipt.schemaVersion ?? 'n/a'} | \`${short(suite.run.appRevision, 8)}\` | ${suite.run.sourceSnapshotPolicy?.replace('readonly-owned-source-graph-', '') ?? 'n/a'} | ${suite.evidence.files} | \`${short(suite.evidence.sha256)}\` |`);
    }
    push('', 'Each suite is rederived by its publication test (`node --test scripts/terminfo/<suite>-publication.test.mjs`), which rebuilds the retained report from the retained journals through the live grading module and compares it byte for byte. Declarations and unit tests alone are not counted as qualification.', '',
        '## Stock collector counts', '',
        'Stock grades are never rewritten by owned evidence. Each publication records its own stock counts; `basis` says whether they were measured in that run.', '',
        '| Suite | Basis | Stock run | Counts (inconclusive/supported/unsupported) | Collector source |', '|---|---|---|---|---|');
    for (const suite of inventory.suites) {
        const stock = suite.stock;
        const counts = stock?.counts ? `${stock.counts.inconclusive}/${stock.counts.supported}/${stock.counts.unsupported}` : 'carried (see note)';
        push(`| \`${suite.suite}\` | ${stock?.basis ?? 'none'} | ${stock?.runId ? `\`${short(stock.runId)}\`` : stock?.rawSha256 ? `raw \`${short(stock.rawSha256)}\`` : 'n/a'} | ${counts} | ${stock?.sourceRevision ? `\`${short(stock.sourceRevision)}\`` : 'n/a'} |`);
    }
    push('', `The baseline run itself is retained as a case-id to outcome map in [\`${stockBaselineFile}\`](${stockBaselineFile}); its raw output is git-ignored and not in the repository (\`${inventory.stockBaseline.rawSha256.slice(0, 12)}…\`). Profile: macOS, Ntilde ${inventory.stockBaseline.target.version} (identity \`${inventory.stockBaseline.identity}\`, operator supplied), no outer terminal or mux, default settings.`,
        'The stock collector reports `suiteComplete=false` in every run.', '',
        '## Benchmark ids', '',
        '| Id | Stock | Pinned | Effect | Layers | Suites | Divergence |', '|---|---|---|---|---|---|---|');
    for (const row of inventory.benchmark) {
        const layers = [...new Set(row.claims.flatMap(claim => claim.measuredLayers))].join(' + ');
        const divergence = row.claims.filter(claim => claim.divergence).map(claim => `${claim.suite}: ${claim.divergence.kind}${claim.divergence.recordedInReport ? '' : ' (derived)'}`).join('; ') || '-';
        const pinned = row.pinned.byClaim.length > 1 ? row.pinned.byClaim.map(entry => `${entry.suite}=${entry.verdict}`).join(', ') : row.pinned.best;
        push(`| \`${row.id}\` | ${row.stockOutcome} | ${pinned} | ${row.effect.verdict} | ${layers} | ${row.claims.map(claim => claim.suite).join(', ')} | ${divergence} |`);
    }
    push('', '## Ntilde-specific findings (not benchmark ids)', '');
    for (const finding of inventory.findings) {
        push(`- \`${finding.suite}\` (${finding.caseIds.length} cases, contracts v${finding.contractsVersion}, rederivation ${finding.rederivation.status}): ${finding.note} Layer: ${finding.measuredLayer}.`);
    }
    push('', 'Controls and aggregate summaries inside each publication are retained in their journals and are not benchmark ids; they are not counted here.', '',
        '## Guards', '',
        '`assertInventory` (run on every generation and by `scripts/terminfo/qualification-inventory.test.mjs`) refuses: a benchmark id listed twice or claimed twice by one suite; a retained report whose case ids differ from the live contract (lost or added case); a claim whose id is not a stock-inconclusive case of the pinned baseline; an Ntilde finding or the `cursor.reverse-wrap` diagnostic among benchmark ids; a pinned/effect difference without a recorded divergence; a qualified claim whose suite was not rederived; and recorded totals that differ from the recomputed ones.', '');
    return lines.join('\n');
}

export const inventoryPaths = (root = defaultQaRoot) => ({ json: join(root, inventoryFiles.json), markdown: join(root, inventoryFiles.markdown) });
export const serialize = inventory => `${JSON.stringify(inventory, null, 2)}\n`;
