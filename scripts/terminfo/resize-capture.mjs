import { OwnedCellObserver, snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { makeResizeCases } from './resize-cases.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';
const same = (left, right) => snapshotHash(left) === snapshotHash(right);
const geometry = screen => ({ rows: screen.rows, cols: screen.cols });

/**
 * Only a trusted parent selects resize targets; workers can advance the fixed plan, never supply dimensions.
 * The paired-read, parent-selected-action, restoration protocol is shared by every resize-driven suite: a subclass names only what
 * differs (the error label, the request-kind and nonce prefixes and the case builder), never a copy of the protocol.
 */
export class OwnedResizeObserver {
    static label = 'resize';
    static kindPrefix = 'resize';
    static noncePrefix = 'NTILDE_RZ_';
    static makeCases = makeResizeCases;

    constructor({ performResize, ...options }) {
        const { kindPrefix, noncePrefix } = this.constructor;
        this.check(typeof performResize === 'function');
        this.performResize = performResize;
        this.nonce = options.nonce;
        this.check(typeof this.nonce === 'string' && this.nonce.startsWith(noncePrefix) && /^[a-f\d]{32}$/.test(this.nonce.slice(noncePrefix.length)));
        this.runId = this.nonce.slice(noncePrefix.length);
        const facts = { ...options, prefix: kindPrefix, includeCellDetails: true,
            includeCursorMode: true, includeOverline: true, includeBufferState: true };
        // These readers never start a baseline-bound ordinary suite. The outer source-owned plan
        // checks every geometry, while ordinary OwnedCellObserver.handle keeps its fixed-size guard.
        this.before = new OwnedCellObserver(facts);
        this.after = new OwnedCellObserver(facts);
        this.frames = []; this.actions = []; this.next = 0;
    }
    check(value) { if (!value) throw new Error(`Invalid owned ${this.constructor.label} plan, observation or restoration.`); }
    get paneId() { return this.before.paneId; }
    get restored() { return this.finished === true && !this.aborted; }

    async capture(expected, budget) {
        const screen = await this.before.capture(budget);
        this.after.paneId ??= this.paneId;
        const afterScreen = await this.after.capture(budget);
        this.check(same(screen, afterScreen) && (!expected || same(geometry(screen), expected)));
        return { screen, afterScreen };
    }

    /** Retain each actual action before validating success; partial/mismatched results never advance the plan. */
    async apply(request, budget) {
        const stage = this.plan?.[this.next];
        this.check(this.bound && stage && stage.stageId !== 'control' && this.actionAtNext !== this.next
            && request.caseId === stage.caseId && request.stageId === stage.stageId
            && request.rows === undefined && request.cols === undefined && request.target === undefined);
        const action = { index: this.actions.length, caseId: stage.caseId, stageId: stage.stageId,
            target: structuredClone(stage.target), observed: null };
        this.actions.push(action);
        try { action.observed = structuredClone(await this.performResize(stage.target.rows, stage.target.cols, budget)); }
        catch (error) {
            action.error = error instanceof Error ? error.message : String(error);
            throw error;
        }
        const observed = action.observed;
        const actual = observed?.app;
        this.check(observed?.paneId === this.paneId && observed.runId === this.runId
            && typeof observed.observedAt === 'string' && Number.isFinite(Date.parse(observed.observedAt))
            && new Date(observed.observedAt).toISOString() === observed.observedAt
            && actual?.exact === true && actual.layoutSettled === true && same(geometry(actual), stage.target)
            && observed.terminalOutput?.tool === '/bin/stty' && observed.terminalOutput.outputProcessing === false);
        validateOutputGeometry(observed.terminalOutput.description, stage.target.rows, stage.target.cols);
        this.actionAtNext = this.next;
        return action;
    }

    /** Abort may restore only the original grid; any abort permanently prevents a completion receipt. */
    async abort(budget) {
        this.check(this.bound && !this.finished);
        this.aborted = true;
        return this.performResize(this.original.rows, this.original.cols, budget);
    }

    validateCaptureRequest(request, operation) {
        const stage = operation === 'read' ? this.plan?.[this.next] : null;
        this.check(request.caseId === (stage?.caseId ?? undefined) && request.stageId === (stage?.stageId ?? undefined));
        if (operation === 'start') this.check(!this.baseline);
        else this.check(this.baseline && (operation === 'bind' ? !this.bound : this.bound));
        if (operation === 'read') this.check(stage && (stage.stageId === 'control' || this.actionAtNext === this.next));
        if (operation === 'finish') this.check(this.next === this.plan.length);
        return stage;
    }

    acceptInitialPair(operation, pair) {
        if (operation === 'start') {
            this.check(pristineEditing(pair.screen));
            this.cases = this.constructor.makeCases(pair.screen.rows, pair.screen.cols);
            this.baseline = pair.screen; this.original = geometry(pair.screen);
            this.plan = this.cases.flatMap(item => ['control', 'after', 'restored'].map(stageId => ({
                caseId: item.id, stageId, target: stageId === 'after' ? item.target : this.original,
            })));
        }
        if (operation === 'bind') {
            const expected = structuredClone(this.baseline);
            expected.lines[0] = this.nonce; expected.cursorCol = this.nonce.length;
            expected.bufferState.alternateScreen = true;
            this.check(same(pair.screen, expected)); this.bound = true;
        }
    }

    /** Pair complete screen/history reads at each exact planned grid and require pristine original restoration. */
    async handle(request, budget) {
        const { kindPrefix } = this.constructor;
        this.check(!this.busy && !this.finished && !this.aborted);
        const operation = request.kind?.startsWith(`${kindPrefix}-`) ? request.kind.slice(kindPrefix.length + 1) : '';
        this.check(['start', 'bind', 'read', 'apply', 'finish', 'abort'].includes(operation));
        this.busy = true;
        try {
            if (operation === 'abort') return await this.abort(budget);
            if (operation === 'apply') return await this.apply(request, budget);
            this.check(operation === 'start' ? request.barrier === null : typeof request.barrier === 'string'
                && request.barrier.length <= 128 && /^\x1b\[\?[\d;]+c$/.test(request.barrier));
            const stage = this.validateCaptureRequest(request, operation);
            const pair = await this.capture(stage?.target ?? this.original, budget);
            this.acceptInitialPair(operation, pair);
            if (operation === 'finish') { this.check(same(pair.screen, this.baseline)); this.finished = true; }
            const frame = { index: this.frames.length, kind: request.kind, paneId: this.paneId, barrier: request.barrier,
                ...pair, sha256: snapshotHash(pair.screen), afterSha256: snapshotHash(pair.afterScreen),
                caseId: stage?.caseId ?? null, stageId: stage?.stageId ?? null,
                actionIndex: stage && stage.stageId !== 'control' ? this.actions.at(-1).index : null };
            this.frames.push(frame);
            if (operation === 'read') this.next++;
            return { screen: pair.screen, paneId: this.paneId };
        } finally { this.busy = false; }
    }
}
