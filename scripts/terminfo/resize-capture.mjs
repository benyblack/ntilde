import { OwnedCellObserver, snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { makeResizeCases } from './resize-cases.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';
const same = (left, right) => snapshotHash(left) === snapshotHash(right);
const check = value => { if (!value) throw new Error('Invalid owned resize plan, observation or restoration.'); };
const geometry = screen => ({ rows: screen.rows, cols: screen.cols });

/** Only a trusted parent selects resize targets; workers can advance the fixed plan, never supply dimensions. */
export class OwnedResizeObserver {
    constructor({ performResize, ...options }) {
        check(typeof performResize === 'function');
        this.performResize = performResize;
        this.nonce = options.nonce;
        check(/^NTILDE_RZ_[a-f\d]{32}$/.test(this.nonce ?? ''));
        this.runId = this.nonce.slice(10);
        const facts = { ...options, prefix: 'resize', includeCellDetails: true,
            includeCursorMode: true, includeOverline: true, includeBufferState: true };
        // These readers never start a baseline-bound ordinary suite. The outer source-owned plan
        // checks every geometry, while ordinary OwnedCellObserver.handle keeps its fixed-size guard.
        this.before = new OwnedCellObserver(facts);
        this.after = new OwnedCellObserver(facts);
        this.frames = []; this.actions = []; this.next = 0;
    }
    get paneId() { return this.before.paneId; }
    get restored() { return this.finished === true && !this.aborted; }

    async capture(expected, budget) {
        const screen = await this.before.capture(budget);
        this.after.paneId ??= this.paneId;
        const afterScreen = await this.after.capture(budget);
        check(same(screen, afterScreen) && (!expected || same(geometry(screen), expected)));
        return { screen, afterScreen };
    }

    /** Retain each actual action before validating success; partial/mismatched results never advance the plan. */
    async apply(request, budget) {
        const stage = this.plan?.[this.next];
        check(this.bound && stage && stage.stageId !== 'control' && this.actionAtNext !== this.next
            && request.caseId === stage.caseId && request.stageId === stage.stageId
            && request.rows === undefined && request.cols === undefined && request.target === undefined);
        const observed = await this.performResize(stage.target.rows, stage.target.cols, budget);
        const action = { index: this.actions.length, caseId: stage.caseId, stageId: stage.stageId,
            target: structuredClone(stage.target), observed: structuredClone(observed) };
        this.actions.push(action);
        const actual = observed?.app;
        check(observed?.paneId === this.paneId && observed.runId === this.runId
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
        check(this.bound && !this.finished);
        this.aborted = true;
        return this.performResize(this.original.rows, this.original.cols, budget);
    }

    validateCaptureRequest(request, operation) {
        const stage = operation === 'read' ? this.plan?.[this.next] : null;
        check(request.caseId === (stage?.caseId ?? undefined) && request.stageId === (stage?.stageId ?? undefined));
        if (operation === 'start') check(!this.baseline);
        else check(this.baseline && (operation === 'bind' ? !this.bound : this.bound));
        if (operation === 'read') check(stage && (stage.stageId === 'control' || this.actionAtNext === this.next));
        if (operation === 'finish') check(this.next === this.plan.length);
        return stage;
    }

    acceptInitialPair(operation, pair) {
        if (operation === 'start') {
            check(pristineEditing(pair.screen));
            this.cases = makeResizeCases(pair.screen.rows, pair.screen.cols);
            this.baseline = pair.screen; this.original = geometry(pair.screen);
            this.plan = this.cases.flatMap(item => ['control', 'after', 'restored'].map(stageId => ({
                caseId: item.id, stageId, target: stageId === 'after' ? item.target : this.original,
            })));
        }
        if (operation === 'bind') {
            const expected = structuredClone(this.baseline);
            expected.lines[0] = this.nonce; expected.cursorCol = this.nonce.length;
            expected.bufferState.alternateScreen = true;
            check(same(pair.screen, expected)); this.bound = true;
        }
    }

    /** Pair complete screen/history reads at each exact planned grid and require pristine original restoration. */
    async handle(request, budget) {
        check(!this.busy && !this.finished && !this.aborted);
        const operation = request.kind?.startsWith('resize-') ? request.kind.slice(7) : '';
        check(['start', 'bind', 'read', 'apply', 'finish', 'abort'].includes(operation));
        this.busy = true;
        try {
            if (operation === 'abort') return await this.abort(budget);
            if (operation === 'apply') return await this.apply(request, budget);
            check(operation === 'start' ? request.barrier === null : typeof request.barrier === 'string'
                && request.barrier.length <= 128 && /^\x1b\[\?[\d;]+c$/.test(request.barrier));
            const stage = this.validateCaptureRequest(request, operation);
            const pair = await this.capture(stage?.target ?? this.original, budget);
            this.acceptInitialPair(operation, pair);
            if (operation === 'finish') { check(same(pair.screen, this.baseline)); this.finished = true; }
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
