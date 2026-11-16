// Retrospective projection of the two terminfo.dev@4.0.0 aggregate cases over their admitted, separately
// receipted marker publications. No collection ran for this suite and no production code changed: the
// summary and report below derive only from retained, committed bytes (the carriers' summaries and
// reports, their archive digests, and the pinned bundle shipped beside them) plus the exact pinned probe
// registrations, which are extracted from the pinned bundle by hash and executed in a disposable child
// process that refuses every use of the context the pinned code does not make. The child process is the
// isolation boundary: the parent never evaluates the pinned bytes in its own address space, and every
// carrier file the projection admits from is checked against the digest expectations the caller pins.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gunzipSync } from 'node:zlib';
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
const read = (root, rel) => readFileSync(join(root, rel));
// Serialized evidence keys are checkout-independent (like the inventory's evidence digest): a Windows
// checkout joins with backslashes, but the retained bytes and their keys are POSIX in git.
const posix = path => path.split(sep).join('/');
const json = (root, rel) => JSON.parse(read(root, rel));
const carrierDir = carrier => `${carrier.dir}/`;

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
 * The verified span is written to a disposable file and executed by a child Node process (a stronger
 * isolation boundary than an in-process dynamic evaluation, and no dynamic evaluation in this module).
 * The termless context exposes only `capabilities`; the terminal context is a trap that throws if the
 * pinned terminal code ever read anything from it (the pinned terminal function ignores its argument). */
export function replayAggregatePinned(caseId, declared, archive = read(qaDir, join('2026-11-16-shell-integration-aggregates', bundleName))) {
    const bundle = gunzipSync(archive, { maxOutputLength: 2_000_000 });
    check(sha(bundle) === upstreamBundleSha256, 'upstream bundle differs');
    const { text } = extractRegistration(bundle.toString('utf8'), caseId);
    const root = mkdtempSync(join(tmpdir(), 'nt-aggregate-pinned-'));
    try {
        const declaredLiteral = declared === true ? 'true' : 'false';
        // The span keeps its bundle bytes byte for byte, including the trailing comma of the array
        // element it is in the bundle - so the child evaluates it inside an array literal.
        const child = `${probeCollector}[\n${text}\n];\n`
            + `if (registered.length !== 1 || registered[0].id !== ${JSON.stringify(caseId)}) { throw new Error('pinned registration did not register exactly its own case'); }\n`
            + `const trap = new Proxy({}, { get() { throw new Error('the pinned terminal path read the context'); } });\n`
            + `const termless = registered[0].termless({ capabilities: { semanticPrompts: ${declaredLiteral} } });\n`
            + `const terminal = await registered[0].terminal(trap);\n`
            + `process.stdout.write(JSON.stringify({ termless, terminal }));\n`;
        const file = join(root, 'pinned-aggregate.mjs');
        writeFileSync(file, child);
        const run = spawnSync(process.execPath, [file], { encoding: 'utf8', timeout: 10_000, cwd: root });
        check(run.status === 0, `pinned replay child failed: ${(run.stderr ?? '').trim().slice(-400)}`);
        const { termless, terminal } = JSON.parse(run.stdout);
        check(termless && terminal, 'the pinned replay returned no results');
        return { termless, terminal, registrationSha256: seams[caseId][1] };
    } finally { rmSync(root, { recursive: true, force: true }); }
}

/** The digest expectations the projection validates the carrier files against. The defaults pin the
 * retained bytes; a caller (the regression test) pins the committed ones against a mutated copy so the
 * projection refuses evidence that changed while its retained outcome stayed the same. */
export function aggregateSourceDigests(root = qaDir) {
    const osc133Report = loadPublication(root, { layout: 'directory', dir: osc133Carrier.dir });
    const osc633Report = loadPublication(root, { layout: 'directory', dir: osc633Carrier.dir });
    const osc133CaseIds = osc133Report.report.cases
        .filter(item => item.effectContract?.id === osc133Carrier.effectContractId).map(item => item.caseId);
    check(osc133CaseIds.length === 4, 'the OSC 133 carrier does not retain its four lifecycle claims');
    return {
        osc133: {
            summarySha256: sha(read(root, join(osc133Carrier.dir, 'summary.json'))),
            admittedEvidence: Object.fromEntries([
                [posix(join(osc133Carrier.dir, carrierHeader(osc133Carrier, root))), sha(read(root, join(osc133Carrier.dir, carrierHeader(osc133Carrier, root))))],
                ...osc133CaseIds.map(caseId => [posix(join(osc133Carrier.dir, `case-${caseId}.json`)), sha(read(root, join(osc133Carrier.dir, `case-${caseId}.json`)))]),
            ]),
        },
        osc633: {
            summarySha256: sha(read(root, join(osc633Carrier.dir, 'summary.json'))),
            archiveSha256: json(root, join(osc633Carrier.dir, 'manifest.json')).archives['owned-run.zip'],
            adapterId: osc633Report.report.adapter.id,
            admittedEvidence: { [posix(join(osc633Carrier.dir, carrierHeader(osc633Carrier, root)))]: sha(read(root, join(osc633Carrier.dir, carrierHeader(osc633Carrier, root)))) },
        },
    };
}

// The header file loadPublication selects for a directory-layout carrier (same precedence order), and a
// refusal of alternate layouts: an unpinned report.json appearing beside the retained journal would
// otherwise supply the admitted outcomes without being digest-checked.
function carrierHeader(carrier, root) {
    const candidates = ['report.json', 'report-common.json', 'journal-common.json'];
    const present = candidates.filter(name => readdirSync(join(root, carrier.dir)).includes(name));
    check(present.length === 1, `carrier ${carrier.suite} retains an alternate report layout: ${present.join(', ') || 'none'}`);
    return present[0];
}

function carrierBinding(carrier, root, sources) {
    const expected = sources[carrier.suite];
    // The layout is refused before loadPublication runs: an added report.json must not be the file that
    // supplies the admitted outcomes, even once.
    const header = carrierHeader(carrier, root);
    const summaryBytes = read(root, join(carrier.dir, 'summary.json'));
    check(sha(summaryBytes) === expected.summarySha256, `carrier ${carrier.suite} summary differs from the pinned expectation`);
    const summary = JSON.parse(summaryBytes);
    const { report } = loadPublication(root, { layout: 'directory', dir: carrier.dir });
    const admitted = report.cases
        .filter(item => item.effectContract?.id === carrier.effectContractId)
        .map(item => ({ caseId: item.caseId, outcome: item.contractResult.outcome }));
    check(admitted.length === 4, `carrier ${carrier.suite} does not admit its four lifecycle claims`);
    // The exact bytes each admitted outcome is read from are pinned per file, so a mutation that keeps
    // the retained outcome but alters the evidence is refused here instead of being silently admitted.
    // A journal header means loadPublication reads the admitted outcomes from the per-case files beside it;
    // a report header carries them inline, so the header itself is the only case source to pin.
    const caseEntries = header === 'journal-common.json'
        ? admitted.map(({ caseId }) => [posix(join(carrier.dir, `case-${caseId}.json`)), sha(read(root, join(carrier.dir, `case-${caseId}.json`)))])
        : [];
    const admittedEvidence = Object.fromEntries([[posix(join(carrier.dir, header)), sha(read(root, join(carrier.dir, header)))], ...caseEntries]);
    for (const [rel, digest] of Object.entries(admittedEvidence)) {
        const pinned = expected.admittedEvidence?.[rel];
        if (pinned === undefined) check(false, `carrier ${carrier.suite} evidence is not pinned by the caller: ${rel}`);
        check(pinned === digest, `carrier ${carrier.suite} evidence differs from the pinned expectation: ${rel}`);
    }
    const binding = {
        suite: carrier.suite, suiteId: carrier.suiteId, publication: carrier.publication, summarySha256: expected.summarySha256,
        contractsVersion: report.contractsVersion, effectContractId: carrier.effectContractId,
        ownedRunId: summary.ownedRunId, measuredAt: summary.measuredAt, appRevision: summary.appRevision, scriptsRevision: summary.scriptsRevision,
        receiptSchemaVersion: summary.receipt.schemaVersion, gradingAdapter: report.adapter, admitted, admittedEvidence,
    };
    if (carrier === osc633Carrier) {
        check(expected.archiveSha256 === json(root, join(carrier.dir, 'manifest.json')).archives['owned-run.zip'], 'carrier OSC 633 archive differs from the pinned expectation');
        check(expected.adapterId === report.adapter.id, 'carrier OSC 633 grading adapter differs from the pinned expectation');
        binding.archiveSha256 = expected.archiveSha256;
    }
    return binding;
}

function buildCase(caseId, bindings) {
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
        const result = replayAggregatePinned(caseId, value);
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

export function shellIntegrationAggregatesPublication(root = qaDir, sources = aggregateSourceDigests(root)) {
    const primarySummary = json(root, join(osc633Carrier.dir, 'summary.json'));
    check(sha(read(root, join(osc633Carrier.dir, 'summary.json'))) === sources.osc633.summarySha256, 'primary carrier summary differs from the pinned expectation');
    check(primarySummary.receipt.runId === primarySummary.ownedRunId, 'primary carrier receipt does not bind its own run');
    const bindings = { osc133: carrierBinding(osc133Carrier, root, sources), osc633: carrierBinding(osc633Carrier, root, sources) };
    const cases = [];
    for (const caseId of aggregateCaseIds) cases.push(buildCase(caseId, bindings));
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
        // The admitted lifecycle evidence was graded by the OSC 633 carrier's grading adapter; naming it here keeps the
        // inventory record from inheriting an unrelated adapter through the context fallback.
        adapter: bindings.osc633.gradingAdapter,
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
