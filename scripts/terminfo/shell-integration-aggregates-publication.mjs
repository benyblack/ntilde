// Retrospective projection of the two terminfo.dev@4.0.0 aggregate cases over their admitted, separately
// receipted marker publications. No collection ran for this suite and no production code changed: the
// summary and report below derive only from retained, committed bytes (the carriers' summaries and
// reports, their archive digests, and the pinned bundle shipped beside them) plus the exact pinned probe
// registrations, which are extracted from the pinned bundle by hash and replayed in a sandbox that
// refuses every use of the context the pinned code does not make.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { gunzipSync } from 'node:zlib';
import { runInNewContext } from 'node:vm';
import { loadPublication } from './qualification-inventory.mjs';
import {
    aggregateCaseIds, aggregateEvidenceSets, aggregateEvidenceTag, aggregatePinnedNotes, aggregatesSuiteId, aggregatesVersion,
    declaredCapabilityVariants, osc133Carrier, osc633Carrier,
} from './shell-integration-aggregates-contracts.mjs';

export const upstreamBundleSha256 = 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6';
const bundleName = 'terminfo-dev-4.0.0.bundle.mjs.gz';
const qaDir = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const check = (ok, message) => { if (!ok) throw new Error(`shell-integration aggregates projection: ${message}`); };
const read = url => readFileSync(new URL(url, import.meta.url));
const json = url => JSON.parse(read(url));
const carrierDir = carrier => `../../docs/qa/terminfo/${carrier.dir}/`;

// The exact pinned registrations this suite replays, pinned by their SHA-256 over the retained bundle
// (each start marker and each extracted span occurs exactly once).
const seams = {
    'extensions.osc-633-vscode': ['probe("extensions.osc-633-vscode", (ctx) => {', 'd8205e6e992759cfe820b96b1d63300cefb938c21415fc6c42d56af4f4108a5c'],
    'extensions.semantic-prompts': ['probe("extensions.semantic-prompts", (ctx) => {', '05dade3dbbc81d32508b3cfda8dc68876019267765852b7d360655d2e3855fb8'],
};
const spanEnd = '\n    }),';
const probeCollector = 'const registered = [];\nconst probe = (id, termless, terminal) => { registered.push({ id, termless, terminal }); };\n';

function extractRegistration(source, caseId) {
    const [start, digest] = seams[caseId];
    const from = source.indexOf(start);
    const to = from >= 0 ? source.indexOf(spanEnd, from) : -1;
    check(from >= 0 && to > from && source.indexOf(start, from + start.length) < 0, 'pinned source seam missing or duplicated');
    const text = source.slice(from, to + spanEnd.length);
    check(sha(Buffer.from(text, 'utf8')) === digest, 'pinned source bytes differ');
    return { text, digest };
}

/** Run the exact pinned aggregate registration from a bundle copy, once per declared-capability value.
 * The termless context exposes only `capabilities`; the terminal context is a trap that throws if the
 * pinned terminal code ever read anything from it (the pinned terminal function ignores its argument).
 * The returned `terminal` is the settled pinned terminal result (the pinned function resolves a promise). */
export async function replayAggregatePinned(caseId, declared, archive = read(`../../docs/qa/terminfo/2026-11-16-shell-integration-aggregates/${bundleName}`)) {
    const bundle = gunzipSync(archive, { maxOutputLength: 2_000_000 });
    check(sha(bundle) === upstreamBundleSha256, 'upstream bundle differs');
    const { text, digest } = extractRegistration(bundle.toString('utf8'), caseId);
    const registered = runInNewContext(`${probeCollector}${text}\nregistered`, Object.create(null), { timeout: 1000 });
    check(Array.isArray(registered) && registered.length === 1 && registered[0].id === caseId, 'pinned registration did not register exactly its own case');
    const trap = new Proxy({}, { get() { throw new Error('the pinned terminal path read the context'); } });
    // The sandbox runs in its own realm; move the results onto host objects so comparisons are structural.
    const termless = JSON.parse(JSON.stringify(registered[0].termless({ capabilities: { semanticPrompts: declared === true } })));
    const terminal = JSON.parse(JSON.stringify(await registered[0].terminal(trap)));
    return { termless, terminal, registrationSha256: digest };
}

function carrierBinding(carrier) {
    const summaryBytes = read(carrierDir(carrier) + 'summary.json');
    const summary = JSON.parse(summaryBytes);
    const { report } = loadPublication(qaDir, { layout: 'directory', dir: carrier.dir });
    const admitted = report.cases
        .filter(item => item.effectContract?.id === carrier.effectContractId)
        .map(item => ({ caseId: item.caseId, outcome: item.contractResult.outcome }));
    check(admitted.length === 4, `carrier ${carrier.suite} does not admit its four lifecycle claims`);
    const binding = {
        suite: carrier.suite, suiteId: carrier.suiteId, publication: carrier.publication, summarySha256: sha(summaryBytes),
        contractsVersion: report.contractsVersion, effectContractId: carrier.effectContractId,
        ownedRunId: summary.ownedRunId, measuredAt: summary.measuredAt, appRevision: summary.appRevision, scriptsRevision: summary.scriptsRevision,
        receiptSchemaVersion: summary.receipt.schemaVersion, gradingAdapter: report.adapter, admitted,
    };
    if (carrier === osc633Carrier) {
        const manifest = json(carrierDir(carrier) + 'manifest.json');
        check(Object.hasOwn(manifest.archives, 'owned-run.zip'), 'carrier archive missing');
        binding.archiveSha256 = manifest.archives['owned-run.zip'];
    }
    return binding;
}

async function buildCase(caseId, bindings) {
    const set = aggregateEvidenceSets[caseId];
    const carrier = set.carrier === osc133Carrier ? bindings.osc133 : bindings.osc633;
    check(carrier.suiteId === set.carrier.suiteId, 'carrier binding mismatch');
    check(carrier.effectContractId === set.carrier.effectContractId, 'carrier effect contract mismatch');
    // Contract equivalence: the admitted claims must be exactly the carrier's retained lifecycle claims
    // for this aggregate's required cases, each qualified with the required effect outcome and bound to
    // the same effect-contract identity the aggregate names. A pending case admits nothing.
    const outcomes = new Map(carrier.admitted.map(entry => [entry.caseId, entry.outcome]));
    const admitted = set.required.map(entry => {
        const outcome = outcomes.get(entry.caseId);
        const satisfied = outcome === entry.requiredOutcome;
        if (!satisfied && !set.pending.some(item => item.caseId === entry.caseId)) {
            throw new Error(`required case ${entry.caseId} is not admitted with ${entry.requiredOutcome}`);
        }
        return { caseId: entry.caseId, requiredOutcome: entry.requiredOutcome, admittedOutcome: satisfied ? outcome : null };
    });
    const pinnedVariants = [];
    let replayed = null;
    for (const value of declaredCapabilityVariants) {
        const result = await replayAggregatePinned(caseId, value);
        pinnedVariants.push({ declared: value === true, response: result.termless.response });
        if (value === false) replayed = result;
    }
    const notes = aggregatePinnedNotes[caseId];
    check(replayed.termless.note === notes.termless && replayed.terminal.note === notes.terminal, 'pinned notes differ from the pinned registration');
    check(replayed.termless.observation.outcome === 'inconclusive' && replayed.terminal.observation.outcome === 'inconclusive', 'the pinned replay must stay inconclusive');
    for (const variant of pinnedVariants) {
        check(JSON.parse(variant.response).declared === variant.declared, 'the pinned response does not report the presented capability');
    }
    const effectContract = {
        id: set.effectContractId, version: 1,
        required: admitted, pending: set.pending, excluded: set.excluded,
        attribution: 'separately identified aggregate effect contract over admitted publications; not the pinned probe, not a marker claim, not an upstream or common-harness verdict',
    };
    check(admitted.every(entry => entry.admittedOutcome === entry.requiredOutcome) === (set.outcome === 'supported'), 'the aggregate outcome does not follow its admitted evidence');
    const contractResult = set.outcome === 'supported'
        ? { outcome: 'supported', attribution: effectContract.attribution }
        : { outcome: 'inconclusive', attribution: effectContract.attribution,
            reason: `the required ${set.pending.map(entry => entry.caseId).join(' and ')} publications (${set.pending[0].owner}) do not exist yet; the admitted lifecycle evidence is necessary but not sufficient` };
    return {
        caseId,
        kind: 'aggregate-projection',
        aggregateOf: { protocol: set.protocol, ...(set.authoritativeContract ? { authoritativeContract: set.authoritativeContract } : {}), carrier: set.carrier.suiteId },
        pinnedCriterion: 'A declared-capability report (termless) and the absence of integration readback (terminal); both inconclusive by design',
        pinned: {
            note: notes.termless, fixtureBytes: false,
            termless: replayed.termless, terminal: replayed.terminal,
            replay: {
                bundle: bundleName, bundleSha256: upstreamBundleSha256,
                registrationSha256: replayed.registrationSha256, declaredVariants: pinnedVariants,
                observation: 'the declared capability value never decides a verdict: both variants resolve to the same inconclusive observation, and the terminal path read nothing',
            },
        },
        carrier: {
            suite: carrier.suite, suiteId: carrier.suiteId, publication: carrier.publication, summarySha256: carrier.summarySha256,
            ...(carrier.archiveSha256 ? { archiveSha256: carrier.archiveSha256 } : {}),
            effectContractId: carrier.effectContractId, ownedRunId: carrier.ownedRunId, appRevision: carrier.appRevision,
            measuredAt: carrier.measuredAt, receiptSchemaVersion: carrier.receiptSchemaVersion, admitted: carrier.admitted,
        },
        evidenceContract: effectContract,
        contractResult,
        ...((replayed.termless.observation.outcome === set.outcome) ? {} : { divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome: 'inconclusive', contractOutcome: set.outcome } }),
        conclusion: {
            outcome: 'inconclusive', grading: 'aggregate-pinned-replay-preserved-by-design', evidence: aggregateEvidenceTag,
            note: caseId === 'extensions.semantic-prompts'
                ? 'The declared capability was replayed over the admitted OSC 133 lifecycle publication; the aggregate effect is separately identified and not an upstream verdict.'
                : 'The declared capability was replayed over the admitted OSC 633 A-D publication; the E command-line and P property records remain pending (#983), so the aggregate effect stays inconclusive.',
        },
    };
}

export async function shellIntegrationAggregatesPublication() {
    const primarySummary = json(carrierDir(osc633Carrier) + 'summary.json');
    check(primarySummary.receipt.runId === primarySummary.ownedRunId, 'primary carrier receipt does not bind its own run');
    const bindings = { osc133: carrierBinding(osc133Carrier), osc633: carrierBinding(osc633Carrier) };
    const cases = [];
    for (const caseId of aggregateCaseIds) cases.push(await buildCase(caseId, bindings));
    const scope = 'Pinned terminfo.dev@4.0.0 aggregate replay: extensions.osc-633-vscode and extensions.semantic-prompts read only the declared '
        + 'semanticPrompts capability and perform no readback, so both pinned paths are inconclusive by design; the exact pinned registrations are replayed '
        + 'from the retained bundle beside two admitted, separately receipted lifecycle publications (OSC 133 A-D, ntilde-owned-osc133-graded-v1; '
        + 'OSC 633 A-D, ntilde-owned-osc633-graded-v1). The aggregate effect contracts are separately identified and never attributed to the pinned probes '
        + 'or to each other: ntilde-semantic-prompts-aggregate-effect-v1 is supported over the four admitted OSC 133 lifecycle claims, and '
        + 'ntilde-osc633-vscode-aggregate-effect-v1 stays inconclusive until the OSC 633 E command-line and P property publications (#983) exist. '
        + 'No new collection and no production change; no real shell, remote delivery, command exit status or commandFinished readback; no stock, Core, '
        + 'Modern or common-harness claim; the sum of the marker publications is never an upstream supported verdict.';
    const report = {
        schemaVersion: 1, suiteId: aggregatesSuiteId, contractsVersion: aggregatesVersion,
        carriers: { osc133: bindings.osc133.suiteId, osc633: bindings.osc633.suiteId },
        scope, cases,
    };
    const summary = {
        schemaVersion: 1,
        measuredAt: bindings.osc633.measuredAt, ownedRunId: bindings.osc633.ownedRunId,
        appRevision: bindings.osc633.appRevision, scriptsRevision: bindings.osc633.scriptsRevision,
        projection: {
            note: 'Retrospective projection over the two admitted carrier publications; no collection ran and no grade was rewritten. '
                + "The top-level identity fields, context, receipt and stock counts are the primary (OSC 633) carrier run's, bound here and per case; "
                + 'the OSC 133 carrier is bound in projection.carriers and per case.',
            caseIds: aggregateCaseIds, carriers: [bindings.osc133, bindings.osc633],
        },
        context: { ...primarySummary.context, projection: 'identity of the primary (OSC 633) carrier run, reused for binding only; this projection ran no collection' },
        receipt: primarySummary.receipt,
        stockCollector: { ...primarySummary.stockCollector, note: 'Stock collector counts of the primary carrier run, re-recorded; the projection collects nothing and alters no grade.' },
    };
    return { summary, report };
}
