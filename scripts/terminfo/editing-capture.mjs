import { OwnedCellObserver, snapshotHash } from './agent-observer.mjs';
import { makeEditingCases } from './editing-cases.mjs';
const check = value => { if (!value) throw new Error('Invalid owned editing binding or restoration.'); };
export function pristineEditing(screen) {
    const state = screen.bufferState;
    return screen.lines.every(line => line === '') && screen.cursorRow === 0 && screen.cursorCol === 0 && screen.cursorVisible
        && screen.cursorMode.style === 2 && screen.cursorMode.blink && screen.cursorMode.isDefault
        && screen.cellDetails.every(run => run.flags === 768 && run.fg === -1 && run.bg === -1 && !run.overline && run.underlineStyle === 0)
        && state.scrollTop === 0 && state.scrollBottom === screen.rows - 1 && !state.originMode && state.autoWrap
        && !state.insertMode && !state.pendingWrap && !state.alternateScreen && state.scrollbackLines === 0 && screen.history.lines.length === 0;
}
export class OwnedCellPlanObserver {
    constructor({ prefix, casesFor, ...options }) {
        check(/^[a-z]{1,16}$/.test(prefix) && typeof casesFor === 'function');
        this.prefix = prefix; this.casesFor = casesFor;
        const facts = { ...options, prefix, includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true };
        this.before = new OwnedCellObserver(facts); this.after = new OwnedCellObserver(facts);
        this.frames = []; this.next = 0;
    }
    get paneId() { return this.before.paneId; }
    get restored() { return this.before.restored && this.frames.at(-1)?.kind === this.prefix + '-finish'; }
    async handle(request, budget) {
        check(!this.busy && !this.restored);
        const reading = request.kind === this.prefix + '-read';
        const expected = reading ? this.plan?.[this.next] : {};
        check(expected && request.caseId === expected.caseId && request.stageId === expected.stageId);
        if (request.kind === this.prefix + '-finish') check(this.next === this.plan?.length);
        this.busy = true;
        try {
            const result = await this.before.handle(request.kind, budget, request.barrier);
            this.after.paneId ??= this.paneId;
            const after = await this.after.capture(budget);
            check(snapshotHash(result.screen) === snapshotHash(after));
            if (request.kind === this.prefix + '-start') {
                check(pristineEditing(result.screen));
                this.cases = this.casesFor(result.screen.rows, result.screen.cols).filter(value => value.id.startsWith(this.prefix + '.'));
                this.plan = this.cases.flatMap(value => ['control', 'after'].map(stageId => ({ caseId: value.id, stageId })));
                check(this.plan.length + 3 <= 64);
            }
            if (request.kind === this.prefix + '-bind') {
                check(result.screen.lines.slice(1).every(line => line === '')
                    && result.screen.cursorVisible
                    && snapshotHash(result.screen.cursorMode) === snapshotHash(this.before.baseline.cursorMode)
                    && snapshotHash(result.screen.cellDetails) === snapshotHash(this.before.baseline.cellDetails)
                    && snapshotHash(result.screen.bufferState) === snapshotHash({ ...this.before.baseline.bufferState, alternateScreen: true })
                    && snapshotHash(result.screen.history) === snapshotHash(this.before.baseline.history));
            }
            this.frames.push({ ...this.before.frames.at(-1), afterScreen: after, afterSha256: snapshotHash(after),
                caseId: request.caseId ?? null, stageId: request.stageId ?? null });
            if (reading) this.next++;
            return result;
        } finally { this.busy = false; }
    }
}

/** Historical erase/editing entry point keeps its exact known plans and wire shape. */
export class OwnedEditingObserver extends OwnedCellPlanObserver {
    constructor(options) {
        check(['erase', 'editing'].includes(options.prefix));
        super({ ...options, casesFor: makeEditingCases });
    }
}
