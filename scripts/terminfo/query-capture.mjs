// The coordinator supplies fresh kernel/private-pane proofs under the same request deadline.
import { OwnedCellObserver, snapshotHash } from './agent-observer.mjs';
import { inspectQueryWitness } from './query-witness-frame.mjs';
import { queryAction, queryBinding, queryBound, checkQueries as check,
    queryPlan, queryPrior, queryScreen, validateQueryOwnership, pristineQuery, buildQueryReport, queryContext } from './queries.mjs';

export class OwnedQueryObserver {
    #binding; #attest; #readers; #prior; #used; #initial; #baseline; #expected; #checksumReport; #marginsReport;
    #actions = []; #frames = []; #pending; #seal; #report; #failed = false; #busy = false; #attempt; #failure;
    constructor(options) {
        const { binding, attest, rpc, endpoint, appData, priorChallenges = [], checksumReport = false, marginsReport = false } = options;
        check(typeof checksumReport === 'boolean' && typeof marginsReport === 'boolean'); this.#checksumReport = checksumReport; this.#marginsReport = marginsReport;
        this.#binding = queryBinding(binding); this.#prior = queryPrior(priorChallenges);
        check(this.#prior.length === 110 && typeof attest === 'function'); this.#attest = attest; this.#used = new Set(this.#prior);
        const facts = { appData, ownerPid: this.#binding.ownerPid, profileName: this.#binding.profileName,
            expectedPaneId: this.#binding.paneId, includeAttributes: true, includeCellDetails: true,
            includeCursorMode: true, includeOverline: true, includeBufferState: true };
        if (rpc) facts.rpc = async (...args) => structuredClone(await rpc(...args));
        if (endpoint) facts.endpoint = endpoint;
        this.#readers = [new OwnedCellObserver(facts), new OwnedCellObserver(facts)];
    }
    get restored() { return Boolean(this.#report) && !this.#failed; }
    get failed() { return this.#failed; }
    get journal() { return structuredClone({ binding: this.#binding, priorChallenges: this.#prior, initial: this.#initial,
        actions: this.#actions, frames: this.#frames, seal: this.#seal, pending: this.#pending, lastAttempt: this.#attempt,
        readerCounts: this.#readers.map(reader => reader.snapshots), failed: this.#failed, failure: this.#failure }); }
    get report() { check(this.restored); return structuredClone(this.#report); }
    /** Trusted coordinator calls this on worker/socket/controller exit; there is no recovery/reset API. */
    abort(error, rejectedOwnerRequest) {
        if (this.#failed) return;
        this.#failed = true; this.#attempt ??= {};
        this.#attempt.error = String(error?.message ?? error).slice(0, 1024);
        if (rejectedOwnerRequest !== undefined) this.#attempt.rejectedOwnerRequest = structuredClone(rejectedOwnerRequest);
        const rawFailure = error?.queryFailure;
        if (rawFailure !== undefined) this.#attempt.rawFailure = structuredClone(rawFailure);
        this.#failure = structuredClone(this.#attempt);
    }
    async #sample(reader, budget) {
        const sample = { beforeOwnership: structuredClone(await this.#attest(budget)) };
        this.#attempt.sample = sample;
        sample.screen = queryScreen(await this.#readers[reader].capture(budget));
        sample.sha256 = snapshotHash(sample.screen);
        sample.afterOwnership = structuredClone(await this.#attest(budget));
        validateQueryOwnership(sample.beforeOwnership, this.#binding, sample.screen);
        validateQueryOwnership(sample.afterOwnership, this.#binding, sample.screen);
        for (const side of ['app', 'worker']) check(snapshotHash(sample.beforeOwnership[side]) === snapshotHash(sample.afterOwnership[side]));
        if (this.#initial?.before) for (const side of ['app', 'worker'])
            check(snapshotHash(sample.beforeOwnership[side]) === snapshotHash(this.#initial.before.beforeOwnership[side]));
        check(!this.#failed); return sample;
    }
    async #start(budget) {
        check(!this.#initial);
        this.#initial = { before: await this.#sample(0, budget) };
        this.#initial.after = await this.#sample(0, budget);
        check(this.#initial.before.sha256 === this.#initial.after.sha256 && this.#initial.before.screen.rows >= 8
            && pristineQuery(this.#initial.before.screen));
        this.#baseline = this.#initial.before.screen; this.#expected = this.#baseline;
        return { priorChallenges: this.#prior, cursorVisible: this.#baseline.cursorVisible };
    }
    async #admit(index, budget) {
        check(this.#initial?.after && !this.#pending && index === this.#actions.length);
        const entry = queryPlan[index]; check(entry);
        this.#pending = { index, before: await this.#sample(entry.reader, budget) };
        check(this.#pending.before.sha256 === snapshotHash(this.#expected));
        const action = queryAction(entry, this.#binding, this.#baseline.cursorVisible); this.#actions.push(action); return action;
    }
    async #read(index, capture, budget) {
        check(this.#pending?.index === index && index === this.#frames.length);
        const entry = queryPlan[index], before = this.#pending.before.screen;
        this.#pending.capture = capture;
        const comparison = inspectQueryWitness(queryContext(before, this.#checksumReport, this.#marginsReport), entry.id ?? queryPlan[1].id, capture);
        const nonceKey = comparison.orderingNonces.join(','); check(!this.#used.has(nonceKey)); this.#used.add(nonceKey);
        if (entry.kind !== 'query') {
            check(capture.byteLength === 0);
            this.#expected = entry.kind === 'finish' ? this.#baseline : queryBound(this.#baseline, this.#binding, entry.kind === 'margins');
        }
        this.#pending.after = await this.#sample(entry.reader, budget);
        check(this.#pending.after.sha256 === snapshotHash(this.#expected));
        this.#frames.push(this.#pending); this.#pending = undefined; return { recorded: index };
    }
    async #finish(status, budget) {
        check(this.#frames.length === 44 && !this.#pending && !this.#seal);
        this.#seal = { status, ownership: structuredClone(await this.#attest(budget)) };
        const input = { binding: this.#binding, priorChallenges: this.#prior, initial: this.#initial,
            actions: this.#actions, frames: this.#frames, seal: this.#seal, ...(this.#checksumReport ? { checksumReport: true } : {}), ...(this.#marginsReport ? { marginsReport: true } : {}) };
        this.#report = buildQueryReport(input);
        check(snapshotHash(this.#report.readerCounts) === snapshotHash(this.#readers.map(reader => reader.snapshots)));
        return { sealed: true };
    }
    async handle(input, budget) {
        let entered = false;
        try {
            check(!this.#failed && !this.#busy && !this.#report && typeof budget === 'function');
            this.#busy = true; entered = true;
            const remaining = () => { const value = budget(); check(Number.isSafeInteger(value) && value >= 1 && value <= 4000); return value; };
            const request = structuredClone(input); this.#attempt = { request };
            const keys = { 'queries-start': ['kind'], 'queries-admit': ['kind', 'index'],
                'queries-read': ['kind', 'index', 'capture'], 'queries-seal': ['kind', 'captureStatus'] }[request.kind];
            check(Object.keys(request).length === keys?.length && keys.every(key => Object.hasOwn(request, key)));
            let result;
            if (request.kind === 'queries-start') result = await this.#start(remaining);
            else if (request.kind === 'queries-admit') result = await this.#admit(request.index, remaining);
            else if (request.kind === 'queries-read') result = await this.#read(request.index, request.capture, remaining);
            else result = await this.#finish(request.captureStatus, remaining);
            check(!this.#failed); return structuredClone(result);
        } catch (error) {
            this.abort(error);
            throw error;
        } finally { if (entered) this.#busy = false; }
    }
}

/** Authenticated common-dispatch failures settle the run with its original error; main retains the failed journal. */
export function rejectQueryOwnerRequest({ authenticated, observer, error, request, reject }) {
    if (authenticated !== true || !observer) return false;
    const keys = ['kind', 'workerPid', 'tty', 'index', 'barrier', 'capture', 'captureStatus'];
    const retained = Object.fromEntries(keys.filter(key => Object.hasOwn(request, key)).map(key => [key, request[key]]));
    observer.abort(error, retained); reject(error); return true;
}
