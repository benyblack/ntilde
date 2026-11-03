import { OwnedCellObserver, snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { makeSavedCases } from './saved-cases.mjs';
const same = (left, right) => snapshotHash(left) === snapshotHash(right);
const check = value => { if (!value) throw new Error('Invalid owned saved-state plan, binding or restoration.'); };

/** Parent-selected staged reads expose active state and explicit restoration witnesses, never mutable banks. */
export class OwnedSavedObserver {
    constructor(options) {
        check(/^NTILDE_SV_[a-f\d]{32}$/.test(options.nonce ?? ''));
        this.nonce = options.nonce;
        const facts = { ...options, prefix: 'saved', includeCellDetails: true,
            includeCursorMode: true, includeOverline: true, includeBufferState: true };
        this.before = new OwnedCellObserver(facts); this.after = new OwnedCellObserver(facts);
        this.frames = []; this.next = 0;
    }
    get paneId() { return this.before.paneId; }
    get restored() { return this.finished === true; }

    validateRequest(request) {
        check(!this.busy && !this.finished);
        const operation = request.kind?.startsWith('saved-') ? request.kind.slice(6) : '';
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

    acceptPair(operation, screen) {
        if (operation === 'start') {
            check(pristineEditing(screen));
            const cases = makeSavedCases(screen.rows, screen.cols);
            const plan = cases.flatMap(item => item.stages.map(stage => ({ caseId: item.id, stageId: stage.id })));
            check(plan.length + 3 <= 64);
            this.baseline = screen; this.plan = plan;
            this.before.baseline = screen; this.after.baseline = screen;
        }
        if (operation === 'bind') {
            const expected = structuredClone(this.baseline);
            expected.lines[0] = this.nonce; expected.cursorCol = this.nonce.length; expected.bufferState.alternateScreen = true;
            check(same(screen, expected)); this.bound = true;
        }
        if (operation === 'finish') { check(same(screen, this.baseline)); this.finished = true; }
    }

    async handle(request, budget) {
        const { operation, stage } = this.validateRequest(request);
        this.busy = true;
        try {
            const screen = await this.before.capture(budget);
            this.after.paneId ??= this.paneId;
            const afterScreen = await this.after.capture(budget); check(same(screen, afterScreen));
            this.acceptPair(operation, screen);
            const sha256 = snapshotHash(screen);
            this.frames.push({ index: this.frames.length, kind: request.kind, paneId: this.paneId, barrier: request.barrier,
                screen, sha256, afterScreen, afterSha256: sha256, caseId: stage?.caseId ?? null, stageId: stage?.stageId ?? null });
            if (operation === 'read') this.next++;
            return { screen, paneId: this.paneId };
        } finally { this.busy = false; }
    }
}
