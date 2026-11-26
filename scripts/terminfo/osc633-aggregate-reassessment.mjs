import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { verifyOwnedRun } from '../terminfo-core-results.mjs';
import { osc633ImplementationPublication } from './osc633-implementation-publication.mjs';
import { buildPublication as rederiveMetadata } from './osc633-metadata-publication.mjs';
import { aggregateEvidenceSets, aggregateEvidenceTag, declaredCapabilityVariants, osc633Carrier } from './shell-integration-aggregates-contracts.mjs';
import { replayAggregatePinned, upstreamBundleSha256 } from './shell-integration-aggregates-publication.mjs';
import { digest, readJson, withOwnedArchive } from './unicode-publication-inputs.mjs';

export const suiteId = 'ntilde-owned-osc633-aggregate-projection-v2';
export const contractsVersion = 2;
export const caseIds = Object.freeze(['extensions.osc-633-vscode']);
const [caseId] = caseIds;
const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const publication = join(qa, '2026-11-26-osc633-aggregate-reassessment');
const historicalDir = '2026-11-16-shell-integration-aggregates';
const metadataDir = '2026-11-26-osc633-metadata';
const stockFile = '2026-11-09-stock-baseline.json';
const lifecycleManifestSha256 = 'fa1f63e5209c05dc534dc9f6f092c4bf16535c574df6fb6037403464626446ce';
const historicalReportSha256 = 'f16e6a3ad2a08d696cd8b2494225b83210cf995aefd5769983d663ee5f1f796c';
const original = aggregateEvidenceSets[caseId];
const scope = 'Retrospective direct-local macOS OSC 633 aggregate over independently receipted A–D lifecycle and E/P metadata observations from two App revisions. P covers Cwd.';

function checkedBytes(file, sha256) {
    const bytes = readFileSync(file);
    assert.equal(digest(bytes), sha256, file);
    return bytes;
}

function rederiveLifecycle() {
    const dir = join(qa, osc633Carrier.dir);
    const manifest = JSON.parse(checkedBytes(join(dir, 'manifest.json'), lifecycleManifestSha256));
    const measurement = {
        runId: '323882a7d7b0e3c86ce4152fa54e6aa3', receiptSchema: 69, sourcePolicy: 'readonly-owned-source-graph-v62',
    };
    return withOwnedArchive(dir, manifest, scratch => {
        const { context, receipt } = verifyOwnedRun(scratch, { measurement });
        assert.equal(context.upstreamBundleSha256, upstreamBundleSha256);
        assert.equal(context.runId, receipt.osc633Evidence.runId);
        const { report, summary } = osc633ImplementationPublication(scratch);
        summary.receipt = receipt;
        return { report, summary };
    });
}

function bindCarrier(dirName, derived) {
    const dir = join(qa, dirName);
    const { summary, report } = derived;
    const manifestBytes = readFileSync(join(dir, 'manifest.json'));
    const reportBytes = readFileSync(join(dir, 'report.json'));
    const summaryBytes = readFileSync(join(dir, 'summary.json'));
    assert.deepEqual(report, JSON.parse(reportBytes), dirName + ' report');
    assert.deepEqual(summary, JSON.parse(summaryBytes), dirName + ' summary');
    return {
        suiteId: report.suiteId, contractsVersion: report.contractsVersion, publication: dirName,
        manifestSha256: digest(manifestBytes), archiveSha256: JSON.parse(manifestBytes).archives['owned-run.zip'],
        reportSha256: digest(reportBytes), summarySha256: digest(summaryBytes),
        runId: summary.ownedRunId, appRevision: summary.appRevision, scriptsRevision: summary.scriptsRevision,
        executableSha256: summary.context.executableSha256, assemblySha256: summary.context.assemblySha256,
        sourcePolicy: summary.context.sourceSnapshotPolicy, sourceManifestSha256: summary.context.sourceSnapshotManifestSha256,
        receiptSchema: summary.receipt.schemaVersion,
    };
}

function admitCase(candidate, id, carrier, expectedContract) {
    assert.equal(candidate.caseId, id);
    assert.equal(candidate.effectContract?.id ?? candidate.contractResult.contractId, expectedContract);
    const validity = candidate.effectContract?.validity;
    assert.ok(validity ? validity.length > 0 && validity.every(check => check.held) : candidate.contractResult.controls === true);
    assert.equal(candidate.contractResult.outcome, 'supported', id);
    return { caseId: id, carrierSuiteId: carrier.suiteId, effectContractId: expectedContract, requiredOutcome: 'supported', admittedOutcome: candidate.contractResult.outcome };
}

export function admitRequired(lifecycle, metadata) {
    const records = [...lifecycle.cases, ...metadata.cases];
    assert.deepEqual(records.map(item => item.caseId), original.required.map(item => item.caseId));
    const lifecycleClaims = lifecycle.cases.map((candidate, index) => admitCase(candidate, original.required[index].caseId, lifecycle, osc633Carrier.effectContractId));
    const [e, p] = metadata.cases;
    return [...lifecycleClaims,
        admitCase(e, 'extensions.osc633-e', metadata, 'ntilde-osc633-e-cycle-metadata-v1'),
        admitCase(p, 'extensions.osc633-p', metadata, 'ntilde-osc633-p-cwd-metadata-v1')];
}

function sharedLifecycleReadback(metadata) {
    const e = metadata.cases.find(item => item.caseId === 'extensions.osc633-e');
    const observed = Object.fromEntries(e.contractResult.observations.map(item => [item.phase, item]));
    const phases = ['e-input', 'e-running', 'e-finished', 'e-no-report', 'e-no-report-finished'];
    const states = phases.map(phase => observed[phase]);
    assert.deepEqual(states.map(item => [item.status, item.accepting]), [
        ['awaitingInput', true], ['running', false], ['awaitingInput', false], ['running', false], ['awaitingInput', false],
    ]);
    assert.equal(states[1].command, e.contractResult.expectedCommand);
    assert.equal(states[3].command, null);
    return states.map(({ phase, status, accepting, command }) => ({ phase, status, accepting, command }));
}

function pinnedAggregate() {
    const replays = declaredCapabilityVariants.map(declared => ({ declared, ...replayAggregatePinned(caseId, declared) }));
    for (const { declared, termless, terminal } of replays) {
        assert.deepEqual(JSON.parse(termless.response), { declared });
        assert.deepEqual([termless.observation.outcome, terminal.observation.outcome], ['inconclusive', 'inconclusive']);
    }
    return {
        note: replays[0].termless.note, fixtureBytes: false, bundleSha256: upstreamBundleSha256,
        registrationSha256: replays[0].registrationSha256, termless: replays[0].termless, terminal: replays[0].terminal,
        replay: replays.map(({ declared, termless, terminal }) => ({ declared, termless, terminal })),
    };
}

export async function buildPublication() {
    const history = JSON.parse(checkedBytes(join(qa, historicalDir, 'report.json'), historicalReportSha256));
    const previous = history.cases.find(item => item.caseId === caseId);
    assert.equal(previous.evidenceContract.id, original.effectContractId);
    assert.equal(previous.contractResult.outcome, 'inconclusive');

    const metadata = await rederiveMetadata();
    const lifecycle = rederiveLifecycle();
    const carriers = [bindCarrier(osc633Carrier.dir, lifecycle), bindCarrier(metadataDir, metadata)];
    assert.equal(metadata.report.pinnedBundleSha256, upstreamBundleSha256);
    const required = admitRequired(lifecycle.report, metadata.report);
    const pinned = pinnedAggregate();
    const stock = readJson(qa, stockFile);
    assert.equal(stock.source.pinnedBundleSha256, pinned.bundleSha256);
    assert.equal(stock.outcomes[caseId], 'inconclusive');

    const outcome = required.every(item => item.admittedOutcome === item.requiredOutcome) ? 'supported' : 'inconclusive';
    const result = {
        caseId, pinned,
        historical: { publication: historicalDir, reportSha256: historicalReportSha256, contractId: original.effectContractId, outcome: previous.contractResult.outcome },
        evidenceContract: { id: 'ntilde-osc633-vscode-aggregate-effect-v2', version: contractsVersion, required,
            pending: [], excluded: [...original.excluded, { scope: 'P properties other than Cwd', reason: 'The admitted property carrier measures only reported-directory metadata.' }] },
        sharedLifecycleReadback: sharedLifecycleReadback(metadata.report),
        contractResult: { outcome, attribution: scope },
        stock: { outcome: stock.outcomes[caseId], runId: stock.source.stockCollectorRunId, rerun: false },
        divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome: pinned.termless.observation.outcome, contractOutcome: outcome },
        conclusion: { outcome: pinned.termless.observation.outcome, grading: 'aggregate-pinned-replay-preserved-by-design', evidence: aggregateEvidenceTag },
    };
    return {
        summary: { suiteId, contractsVersion, projection: { carriers, historicalReportSha256 },
            stockCollector: { rerun: false, note: 'Per-case grade carried forward from ' + stockFile } },
        report: { suiteId, contractsVersion, scope, probeHash: stock.source.probeHash, carriers, cases: [result] },
    };
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    assert.ok(process.argv.length === 3 && ['--write', '--check'].includes(process.argv[2]));
    for (const [name, value] of Object.entries(await buildPublication())) {
        const file = name + '.json';
        if (process.argv[2] === '--write') writeFileSync(join(publication, file), JSON.stringify(value, null, 2) + '\n');
        else assert.deepEqual(readJson(publication, file), value);
    }
}
