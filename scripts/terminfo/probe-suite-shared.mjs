// Shared machinery for owned query-probe suites: suites whose pinned probe is one simple OSC query
// (registered through the pinned collector's oscSimpleQueryProbe factory) replay both pinned paths over
// the journaled raw replies of their own fenced exchange, beside a separately identified effect contract,
// and publish the graded projection of one cleaned-up owned run. The rxvt special-color suite carries its
// own copy (its retained receipts pin those bytes); this module is the parameterized form for later suites,
// and every required parameter is validated so a suite cannot silently inherit another's identity.
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const esc = '\x1b', bel = '\x07';
const fail = message => { throw new Error(message); };
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const hashOf = bytes => createHash('sha256').update(bytes).digest('hex');
const printable = raw => (raw === null ? null : raw.replaceAll(esc, String.raw`\e`).replaceAll(bel, String.raw`\a`));

// ---------- the bounded framing of one exchange ----------

/** The bounded framing of one owned exchange: a bounded sequence of complete OSC replies, each ended by BEL or
 *  ST, followed by the DA barrier that orders them; a request with no reply earns the barrier alone. The pinned
 *  probe's own frame pattern may accept both terminators, so this parser accepts both and the pinned verdict
 *  never depends on the stricter effect-contract terminator. Every refusal names the suite's label. */
export function probeSuiteFraming({ label, replyLimit, bodyLimit = 128, exchangeLimit = 1024 }) {
    if (typeof label !== 'string' || !label) fail('probeSuiteFraming requires a label.');
    for (const [name, value] of [['replyLimit', replyLimit], ['bodyLimit', bodyLimit], ['exchangeLimit', exchangeLimit]]) {
        if (!Number.isSafeInteger(value) || value < 1) fail(`probeSuiteFraming requires a positive ${name}.`);
    }
    const isBarrier = text => typeof text === 'string' && text.startsWith(esc) && /^\[\?[\d;]+c$/.test(text.slice(1));
    const replyEnd = rest => {
        if (!rest.startsWith(esc + ']')) fail(`Unexpected ${label} reply bytes.`);
        const stAt = rest.indexOf(esc + '\\'), belAt = rest.indexOf(bel);
        const endsWithSt = stAt >= 0 && (belAt < 0 || stAt < belAt);
        const end = endsWithSt ? stAt : belAt;
        let body = rest.slice(2, end < 0 ? undefined : end);
        // A chunk can stop halfway through ST. An embedded ESC is otherwise forbidden.
        if (end < 0 && body.endsWith(esc)) body = body.slice(0, -1);
        if (body.length > bodyLimit || body.includes(esc)) fail(`Unexpected ${label} reply bytes.`);
        if (end < 0) return -1;
        return end + (endsWithSt ? 2 : 1);
    };
    const parseReply = raw => {
        if (typeof raw !== 'string' || raw.length > bodyLimit + 4 || replyEnd(raw) !== raw.length) fail(`Unexpected ${label} reply.`);
        return raw;
    };
    const parseFrame = bytes => {
        if (typeof bytes !== 'string' || bytes.length > exchangeLimit) fail(`Oversized ${label} exchange.`);
        const replies = [];
        let rest = bytes;
        for (;;) {
            if (!rest.startsWith(esc + ']')) {
                // The exchange ends when the remaining bytes are exactly the DA barrier. An incomplete barrier
                // prefix parses to null and anything else is refused.
                if (!rest || (esc + '[?').startsWith(rest) || (rest.length <= 128 && /^\x1b\[\?[\d;]*$/.test(rest))) return null;
                if (isBarrier(rest)) return { barrier: rest, replies };
                fail(`Unexpected ${label} barrier bytes.`);
            }
            if (replies.length === replyLimit) fail(`Too many ${label} replies.`);
            const end = replyEnd(rest);
            if (end === -1) return null;
            replies.push(rest.slice(0, end));
            rest = rest.slice(end);
        }
    };
    const validateExchange = value => {
        if (!value || typeof value !== 'object' || Object.keys(value).length !== 2 || !isBarrier(value.barrier)
            || value.barrier.length > 128 || !Array.isArray(value.replies) || value.replies.length > replyLimit) {
            fail(`Invalid retained ${label} report.`);
        }
        return { barrier: value.barrier, replies: value.replies.map(raw => parseReply(raw)) };
    };
    return { parseReply, parseFrame, validateExchange };
}

// ---------- the pinned probe's own result shape ----------

/** The pinned collector's oscReplyResult over one captured text: a frame matching the valid pattern is
 *  supported; a capture holding only the reply prefix is an invalid reply; nothing else is a no-response.
 *  Key order is part of the retained result; the pinned code sets no note. */
export function pinnedOscReply({ pattern, prefix, expected }, response) {
    const match = pattern.exec(response);
    if (match !== null) {
        return { pass: true, response, observation: { outcome: 'supported', evidence: 'query' },
            assertions: [{ kind: 'positive', expected, observed: match[0] }] };
    }
    return { pass: false, response,
        observation: { outcome: 'inconclusive', reason: prefix.test(response) ? 'invalid-reply' : 'no-response', evidence: 'query' } };
}

/** Replay one pinned path over the journaled bytes of the case's own exchange: the termless path sees the
 *  captured replies, the terminal path replies plus the DA barrier that follows them. `criterion` is the
 *  suite's bound copy of the pinned result function. */
export function replayPinnedPath(path, { replies = '', barrier = '' } = {}, criterion) {
    if (path !== 'termless' && path !== 'terminal') fail(`Unknown pinned path ${path}.`);
    return criterion(path === 'termless' ? replies : replies + barrier);
}

// ---------- the parent-side observer over the shared cell observer ----------

/** Build the suite's parent-side observer class: every read journals one complete coherent state; a report read
 *  additionally binds the raw reply list (validated, ordered by that read's own barrier) to the same frame.
 *  `OwnedCellObserver` is injected by the suite module so the shared module owns none of its wiring. */
export function makeProbeSuiteObserver({ label, prefix, validateExtras, observed, OwnedCellObserver }) {
    if (typeof label !== 'string' || !label || typeof prefix !== 'string' || !prefix) fail('makeProbeSuiteObserver requires a label and prefix.');
    if (typeof validateExtras !== 'function' || observed === undefined || typeof OwnedCellObserver !== 'function') {
        fail('makeProbeSuiteObserver requires validateExtras, observed and OwnedCellObserver.');
    }
    return class ProbeSuiteObserver {
        frames = [];
        #busy = false;
        #cells;
        constructor(options) { this.#cells = new OwnedCellObserver({ ...options, prefix, ...observed }); }
        get restored() { return this.#cells.restored; }
        get paneId() { return this.#cells.paneId; }
        async handle(request, budget) {
            if (this.#busy || this.restored) fail(`${label} observation busy or complete.`);
            const { report } = validateExtras(request);
            this.#busy = true;
            try {
                const result = await this.#cells.handle(request.kind, budget, request.barrier ?? null);
                this.frames.push(report === undefined ? this.#cells.frames.at(-1) : { ...this.#cells.frames.at(-1), report });
                return result;
            } finally { this.#busy = false; }
        }
    };
}

// ---------- the worker-side collection driver ----------

/** Build the suite's worker-side driver: the case's exact plan runs through the owned fence — uncaptured setup
 *  feeds, one fenced exchange per capture step with its replies retained before the DA barrier, a parent-journal
 *  read per step — and both pinned paths then replay over the journaled bytes of the pinned request's own
 *  exchange, making exactly the contracted calls. `deps` must provide the shared plan helpers { devqRecordingContext,
 *  planFeeds, planReads, validateExactFeeds }; the suite binds its own contracts, seed, finish, frame parser,
 *  pattern and recording context. */
export function makeProbeSuiteCollector({ label, prefix, contracts, planSeed, finish, pinnedPattern, parseFrame, deps }) {
    if (typeof label !== 'string' || !label || typeof prefix !== 'string' || !prefix) fail('makeProbeSuiteCollector requires a label and prefix.');
    if (!Array.isArray(contracts) || contracts.length === 0 || typeof planSeed !== 'function' || typeof finish !== 'function'
        || !(pinnedPattern instanceof RegExp) || typeof parseFrame !== 'function' || deps === undefined) {
        fail('makeProbeSuiteCollector requires contracts, planSeed, finish, pinnedPattern, parseFrame and deps.');
    }
    const { devqRecordingContext, planFeeds, planReads, validateExactFeeds } = deps;

    /** Serve the journaled exchange of the pinned request to one pinned probe path. The pinned code may only call
     *  feedCapture with its own request and queryWithSentinelOutcome with its own frame pattern; every other
     *  member stays refused by the underlying recording context, and an absent reply is the empty capture. */
    function recordingContext(contract, exchange) {
        const inner = devqRecordingContext(contract, exchange);
        const query = (text, pattern) => {
            const samePattern = pattern instanceof RegExp && pattern.source === pinnedPattern.source && pattern.flags === pinnedPattern.flags;
            if (!samePattern) return Promise.reject(new Error(`Pinned ${contract.caseId} terminal path searched for a different frame pattern.`));
            return inner.queryWithSentinelOutcome(text, pattern);
        };
        return Object.create(inner, { queryWithSentinelOutcome: { value: query, enumerable: true } });
    }

    /** Run the work, then the restoration; when both fail, report both instead of dropping either. */
    async function guarded(work, restore) {
        let result, primary = null, restoreError = null;
        try { result = await work(); }
        catch (error) { primary = error; }
        try { await restore(); }
        catch (error) { restoreError = error; }
        if (primary !== null && restoreError !== null) throw new AggregateError([primary, restoreError], `${label} collection and restoration failed.`);
        if (primary !== null) throw primary;
        if (restoreError !== null) throw restoreError;
        return result;
    }

    /** One plan step of the case. A capture step is a fenced exchange whose replies the next read journals; any other feed clears them. */
    async function runStep(step, record, io) {
        const { fence, owner, caseId } = io;
        if (step.feed === undefined) {
            if (step.read !== 'report' && step.read !== 'cells') fail(`${caseId} unknown plan step ${step.read}.`);
            if (step.read === 'report' && record.report === null) fail(`${caseId} report read without an exchange.`);
            const observed = await owner(`${prefix}-read`, record.barriers.at(-1), step.read === 'report' ? { report: record.report } : {});
            record.reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol], ...(step.read === 'report' ? { report: record.report } : {}) });
            record.report = null;
            return;
        }
        const capture = step.capture === true;
        record.feeds.push(step.feed);
        record.report = capture ? await fence.exchange(step.feed, parseFrame) : null;
        record.barriers.push(capture ? record.report.barrier : await fence.write(step.feed));
    }

    async function collectCase(contract, io) {
        const record = { feeds: [], barriers: [], reads: [], report: null };
        for (const step of contract.plan) await runStep(step, record, { ...io, caseId: contract.caseId }); // NOSONAR - serial by design
        if (record.feeds.length !== planFeeds(contract).length || record.reads.length !== planReads(contract)) fail(`Incomplete ${contract.caseId} ${label} fixture.`);
        validateExactFeeds(contract, record.feeds);
        return { caseId: contract.caseId, feeds: record.feeds, barriers: record.barriers, reads: record.reads };
    }

    async function replayPinned(gradingModule, contract, collected) {
        const exchange = collected.reads[contract.pinnedRead]?.report ?? null;
        const termlessContext = recordingContext(contract, exchange);
        const termless = gradingModule.runTermless(contract.caseId, termlessContext);
        const terminalContext = recordingContext(contract, exchange);
        const terminal = await gradingModule.runTerminal(contract.caseId, terminalContext);
        const calls = `${termlessContext.fedCount},${termlessContext.askedCount},${terminalContext.fedCount},${terminalContext.askedCount}`;
        if (calls !== '1,0,1,1') fail(`Pinned ${contract.caseId} replay made a different number of calls (${calls}).`);
        return { termless, terminal };
    }

    /** Run the contracted case with parent-journaled reads and replies, then replay both pinned paths. The
     *  grading module must expose its adapter as `adapter` and its pinned runners as `runTermless`/`runTerminal`. */
    async function collectGrading({ config, fence, owner, gradingModule }) {
        const adapter = gradingModule.adapter;
        const io = { fence, owner };
        await owner(`${prefix}-start`);
        const collected = await guarded(
            async () => {
                await owner(`${prefix}-bind`, await fence.write('\x1b[?1049h' + planSeed(config.runId)));
                const done = [];
                for (const contract of contracts) done.push(await collectCase(contract, io)); // NOSONAR - serial by design
                return done;
            },
            async () => { await owner(`${prefix}-finish`, await fence.write(finish(config.runId))); });
        const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
        for (const item of collected) {
            const contract = contracts.find(entry => entry.caseId === item.caseId); // NOSONAR - serial by design: each pinned replay settles in case order
            run.cases.push({ ...item, pinned: await replayPinned(gradingModule, contract, item) }); // NOSONAR - preserve sequential replay and failure ordering
        }
        return run;
    }

    /** The exact bind bytes of the suite, exposed so tests can mirror the measured fixture. */
    const bindOf = runId => '\x1b[?1049h' + planSeed(runId);
    return { recordingContext, collectGrading, bindOf };
}

// ---------- graded-report helpers ----------

/** The state every exchange must leave unchanged, and the printable rendering of raw bytes. */
export function probeSuiteStateHelpers({ screenInkOf, validateScreen }) {
    const groupOf = (screen, replies) => ({ screen, ink: screenInkOf(screen), cursor: [screen.cursorRow, screen.cursorCol], cursorVisible: screen.cursorVisible,
        cursorMode: screen.cursorMode, rows: screen.rows, cols: screen.cols, margins: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom],
        origin: screen.bufferState.originMode, alternate: screen.bufferState.alternateScreen, buffer: screen.bufferState,
        horizontalMargins: screen.horizontalMargins, replies });
    const stateKey = state => JSON.stringify([state.ink, state.cursor, state.cursorVisible, state.cursorMode, state.buffer, state.horizontalMargins]);
    return { groupOf, stateKey, printable, validateScreen };
}

/** The case's grade in execution order, so an earlier failure is never overridden by a later group it damaged:
 *  the seed, then each group as the plan ran it. A failed control makes the case inconclusive; a mismatching
 *  protocol group makes it unsupported; policy groups never decide. */
export function gradeProbeSuiteCase({ groups, seedOk }, observation) {
    if (!seedOk(observation.initial, observation.nonce)) return { outcome: 'inconclusive', note: 'The attributed seed (nonce, cells, margins and modes) was not measured before any request' };
    for (const group of groups.filter(entry => entry.kind !== 'policy')) {
        const mismatch = group.mismatch(observation[group.role], observation.initial);
        if (mismatch !== null) return group.kind === 'control' ? { outcome: 'inconclusive', note: `Setup control failed before the effect could be judged: ${mismatch}` } : { outcome: 'unsupported', note: mismatch };
    }
    return { outcome: 'supported' };
}

/** The reply lists of one case's reads, in plan order. A cells read carries no report; a report read's journaled
 *  replies must equal the worker's record and be ordered by that read's own barrier. */
export function suiteReplyLists({ label, contract, recorded, readFrames, validateExchange }) {
    const reads = contract.plan.filter(step => step.read), out = [];
    for (const [index, step] of reads.entries()) {
        const frame = readFrames[index], read = recorded.reads[index];
        if (step.read !== 'report') {
            if (frame.report !== undefined || read.report !== undefined) fail(`Unexpected ${contract.caseId} report on a cells read.`);
            out.push([]);
            continue;
        }
        const journaled = validateExchange(frame.report), worker = validateExchange(read.report);
        if (!sameJson(journaled, worker) || journaled.barrier !== frame.barrier) fail(`${label} ${contract.caseId} replies differ from their read frame.`);
        out.push(journaled.replies);
    }
    return out;
}

/** Verify and replay one case's pinned results over its journaled exchange; returns the verified pair. */
export function verifySuitePinnedReplays({ label, contract, recorded, exchange, replayModule = null, replayTermless, replayTerminal, recordingContextOf, runTermless, runTerminal, requirePinned, requireReplay }) {
    const { termless, terminal } = recorded.pinned ?? {};
    if (termless?.probeId !== contract.caseId || terminal?.probeId !== contract.caseId || !termless.observation || !terminal.observation) fail(`Missing pinned ${contract.caseId} grading results.`);
    const replies = exchange.replies.join('');
    requirePinned(contract, termless, replayTermless({ replies }), label);
    requirePinned(contract, terminal, replayTerminal({ replies, barrier: exchange.barrier }), label);
    if (replayModule) {
        const context = recordingContextOf(contract, exchange);
        requireReplay(contract, runTermless(replayModule, contract.caseId, context), termless);
        if (context.fedCount !== contract.pinnedFeeds.length || context.askedCount !== 0) fail(`Pinned ${contract.caseId} replay made a different number of calls.`);
    }
    return { termless, terminal };
}

function requireAgreement(actual, expected, label) {
    if (!sameJson({ ...actual }, { ...expected })) fail(`${label} pinned result differs from its replay.`);
}

/** A difference between the pinned verdict and the effect verdict is a recorded divergence, never absorbed. */
export function reconcilePinnedWithEffect(pinnedOutcome, effect) {
    return pinnedOutcome === effect.outcome ? null : { pinnedOutcome, contractOutcome: effect.outcome, kind: `pinned-${pinnedOutcome}-effect-${effect.outcome}` };
}

/** The published record of one pinned result: only the fields the projection carries. */
export const suitePinnedResult = pinned => ({ pass: pinned.pass, response: pinned.response ?? null, note: pinned.note ?? null, observation: pinned.observation, assertions: pinned.assertions ?? null });

// ---------- retained artifacts ----------

/** The suite's retained-artifact reader: anything but a regular file (a symlink could point at valid evidence
 *  that later disappears) is a refusal naming the suite. */
export function retainedArtifactReader(output, label) {
    return name => {
        const path = join(output, name), stat = lstatSync(path);
        if (!stat.isFile()) fail(`${label} artifact is not a regular file: ${name}`);
        return readFileSync(path);
    };
}


// ---------- the publication projection of one cleaned-up owned run ----------

/** The publication projection of one query-probe suite's cleaned-up owned run: admits only files bound to this
 *  receipt, requires the exact-query witness, and binds the stock collector's own run identity beside the run's
 *  context and receipt. `deps` must provide { label, caseIds, witnessId, note, observationsFile, evidenceKey }. */
export function makeQueryProbePublication({ label, caseIds, witnessId, note, observationsFile, evidenceKey }) {
    if (typeof label !== 'string' || !label || !Array.isArray(caseIds) || caseIds.length === 0
        || typeof witnessId !== 'string' || typeof note !== 'string' || typeof observationsFile !== 'string'
        || typeof evidenceKey !== 'string') {
        fail('makeQueryProbePublication requires label, caseIds, witnessId, note, observationsFile and evidenceKey.');
    }
    const readJson = (dir, file) => JSON.parse(readFileSync(join(dir, file), 'utf8'));
    const digest = (dir, file) => hashOf(readFileSync(join(dir, file)));
    const stockObservation = (raw, caseId) => {
        const observation = raw.observations.find(entry => entry.featureId === caseId);
        if (observation === undefined) fail(`Missing stock outcome: ${caseId}`);
        return observation;
    };
    const queryWitness = dir => {
        const observations = readJson(dir, 'query-observations.json').observations;
        const witness = observations.find(item => item.id === witnessId);
        if (witness === undefined) fail(`Missing the ${label} exact-query witness.`);
        return { id: witness.id, expectedEvidence: witness.expectedEvidence, receivedHex: witness.receivedHex, contractMatched: witness.contractMatched };
    };
    const publishedCase = (item, raw, witness) => ({ caseId: item.caseId, pinnedCriterion: item.pinnedCriterion, pinnedScript: item.pinnedScript,
        effectContract: item.effectContract, observed: item.observed, pinned: item.pinned, contractResult: item.contractResult,
        stock: stockObservation(raw, item.caseId), queryWitness: witness,
        ...(item.divergence !== undefined ? { divergence: item.divergence } : {}), conclusion: item.conclusion });
    return (dir, revisions = {}) => {
        const context = readJson(dir, 'context.json'), stock = readJson(dir, 'summary.json');
        const measured = readJson(dir, observationsFile), raw = readJson(dir, 'raw.json'), receipt = readJson(dir, 'cell-receipt.json');
        const check = value => { if (!value) fail(`Inconsistent ${label} publication run or receipt binding.`); };
        check(receipt.fixtureRemoved === true && receipt.runId === context.runId && measured.runId === context.runId);
        check(receipt.contextSha256 === digest(dir, 'context.json') && receipt.rawSha256 === digest(dir, 'raw.json')
            && receipt.ownershipProofSha256 === digest(dir, 'ownership-proof.json'));
        const suiteBinding = receipt[evidenceKey];
        check(suiteBinding?.runId === context.runId && suiteBinding.file === observationsFile
            && suiteBinding.sha256 === digest(dir, observationsFile));
        const query = readJson(dir, 'query-observations.json');
        check(query.runId === context.runId && receipt.queryEvidence?.runId === context.runId && receipt.queryEvidence.file === 'query-observations.json'
            && receipt.queryEvidence.sha256 === digest(dir, 'query-observations.json'));
        const counts = raw.observations.reduce((out, item) => ({ ...out, [item.outcome]: (out[item.outcome] ?? 0) + 1 }), {});
        check(stock.runId === raw.runId && stock.rawSha256 === receipt.rawSha256 && sameJson(stock.counts, counts)
            && stock.observations === raw.observations.length && stock.suiteComplete === raw.suiteComplete && stock.measuredAt === raw.measuredAt);
        const cases = measured.cases.map(item => publishedCase(item, raw, queryWitness(dir)));
        if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...caseIds])) fail('The published cases differ from the contracted cases.');
        const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, note, cases };
        const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId,
            appRevision: revisions.appRevision, scriptsRevision: revisions.scriptsRevision, context, receipt,
            stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash,
                sourceRevision: raw.sourceRevision, note } };
        return { summary, report };
    };
}

export { esc, bel, fail, isDigest, sameJson, hashOf, printable };
