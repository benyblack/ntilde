import { agentRequest, OwnedCellObserver, snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { copyPaneMetadata, copyMetadataStatus } from './metadata-state.mjs';
import { makeMetadataCases } from './metadata-cases.mjs';
import { matchesMetadataExpected } from './metadata-expectations.mjs';
import { metadataProjection, validateMetadataSnapshot } from './metadata.mjs';
const check = value => { if (!value) throw new Error('Invalid owned metadata binding or restoration.'); };
const unavailableCodes = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);
/** Only a preflight/zero-response refusal is eligible for the initial optional marker. */
export function initialMetadataRefusal(request, error, observer) {
    const attempt = observer?.lastAttempt;
    return request.kind === 'metadata-start' && unavailableCodes.has(error.code)
        && !observer?.baseline && !observer?.frames?.length && !attempt?.before
        && !attempt?.responses?.some(record => Object.hasOwn(record, 'response'));
}
/** A partial failure must not retain a code that the worker treats as a clean initial skip. */
export function metadataFailure(request, error, observer) {
    if (!initialMetadataRefusal(request, error, observer) && unavailableCodes.has(error.code))
        return new Error('Metadata observation failed after partial responses.', { cause: error });
    return error;
}
/** Read actual host controls separately from the complete VT viewport; preserve both independent reads. */
export class OwnedMetadataObserver {
    constructor(options) {
        check(/^[a-f\d]{32}$/.test(options.runId ?? ''));
        this.runId = options.runId;
        const rpc = options.rpc ?? agentRequest;
        const facts = { ...options, rpc: (...args) => this.recordRequest(rpc, ...args), prefix: 'metadata', includeCellDetails: true,
            includeCursorMode: true, includeOverline: true, includeBufferState: true };
        this.before = new OwnedCellObserver(facts); this.after = new OwnedCellObserver(facts);
        this.frames = []; this.next = 0;
    }
    get paneId() { return this.before.paneId; }
    get restored() { return this.finished === true; }
    validateRequest(request) {
        check(!this.busy && !this.finished);
        const operation = request.kind?.startsWith('metadata-') ? request.kind.slice(9) : '';
        check(['start', 'bind', 'read', 'finish'].includes(operation));
        check(operation === 'start' ? request.barrier === null : typeof request.barrier === 'string'
            && request.barrier.length <= 128 && /^\x1b\[\?[\d;]+c$/.test(request.barrier));
        const stage = operation === 'read' ? this.plan?.[this.next] : null;
        check(request.caseId === (stage?.caseId ?? undefined) && request.stageId === (stage?.stageId ?? undefined));
        if (operation === 'start') check(!this.baseline);
        else check(this.baseline && (operation === 'bind' ? !this.bound : this.bound));
        if (operation === 'read') check(stage);
        if (operation === 'finish') check(this.next === this.plan.length);
        return { operation, stage };
    }
    async recordRequest(rpc, endpoint, method, params, timeout) {
        const record = { method, params: structuredClone(params) };
        this.lastAttempt.responses.push(record);
        try {
            const result = await rpc(endpoint, method, params, timeout), bytes = JSON.stringify(result);
            const length = Buffer.byteLength(bytes);
            check(length <= 512 * 1024 && this.lastAttempt.responseBytes + length <= 8 * 1024 * 1024);
            this.lastAttempt.responseBytes += length;
            record.response = JSON.parse(bytes);
            return result;
        } catch (error) {
            record.error = this.failure(error); throw error;
        }
    }
    failure(error) {
        return { message: String(error.message ?? error).slice(0, 4096),
            ...(typeof error.code === 'string' ? { code: error.code.slice(0, 64) } : {}) };
    }
    async capture(observer, budget) {
        const screen = await observer.capture(budget);
        const metadata = copyPaneMetadata(await observer.request('readPaneMetadata', { paneId: this.paneId }, budget));
        const rawStatus = await observer.request('getSessionStatus', { paneId: this.paneId }, budget);
        return validateMetadataSnapshot({ screen, metadata, rawStatus,
            status: copyMetadataStatus(rawStatus, this.paneId) }, this.baseline);
    }
    acceptPair(operation, snapshot) {
        if (operation === 'start') {
            check(pristineEditing(snapshot.screen));
            const cases = makeMetadataCases(snapshot.screen.rows, snapshot.screen.cols, this.runId);
            const plan = cases.flatMap(item => item.stages.map(stage => ({ caseId: item.id, stageId: stage.id })));
            check(plan.length + 3 <= 64);
            this.baseline = snapshot; this.plan = plan;
            this.before.baseline = snapshot.screen; this.after.baseline = snapshot.screen;
        }
        if (operation === 'bind' || operation === 'finish') {
            check(matchesMetadataExpected(snapshot, { type: 'baseline' }, this.baseline.screen, this.runId, operation === 'finish'));
            if (operation === 'bind') this.bound = true;
            else this.finished = true;
        }
    }
    async settledPair(budget, remaining = 2) {
        const before = await this.capture(this.before, budget); this.lastAttempt.before = before;
        this.after.paneId ??= this.paneId;
        const after = await this.capture(this.after, budget); this.lastAttempt.after = after;
        if (snapshotHash(metadataProjection(before)) === snapshotHash(metadataProjection(after))) return { before, after };
        this.lastAttempt.settling ??= [];
        this.lastAttempt.settling.push({ before, after });
        check(remaining > 0);
        // Only agreement is retried, never an expected effect or a malformed/failed RPC.
        // The original caller deadline, per-observer 64-capture and byte budgets still apply.
        budget();
        await new Promise(resolve => setTimeout(resolve, 10));
        return this.settledPair(budget, remaining - 1);
    }
    async handle(request, budget) {
        const { operation, stage } = this.validateRequest(request); this.busy = true;
        this.lastAttempt = { operation, caseId: stage?.caseId ?? null, stageId: stage?.stageId ?? null,
            responseBytes: 0, responses: [] };
        try {
            const { before, after } = await this.settledPair(budget);
            const sha256 = snapshotHash(metadataProjection(before));
            check(sha256 === snapshotHash(metadataProjection(after)));
            this.acceptPair(operation, before);
            this.frames.push({ index: this.frames.length, kind: request.kind, paneId: this.paneId, barrier: request.barrier,
                before, sha256, after, afterSha256: sha256, caseId: stage?.caseId ?? null, stageId: stage?.stageId ?? null,
                ...(this.lastAttempt.settling ? { settling: this.lastAttempt.settling } : {}) });
            if (operation === 'read') this.next++;
            return { screen: before.screen, paneId: this.paneId };
        } catch (error) { this.lastAttempt.error = this.failure(error); throw error; }
        finally { this.busy = false; }
    }
}
