import { OwnedCellObserver, snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { inputPlanV1 } from './input-plan.mjs';
import { boundInputScreen, buildInputReport, checkInput, defaultInputModes, inputModeReplyHex, sameInputFact,
    validateInputAction, validateInputBytes } from './input-state.mjs';

/** Four phase readers keep paired before/after controls below each reader's64-read cap. */
export class OwnedInputObserver {
    constructor({ performInput, plan = inputPlanV1, ...options }) {
        checkInput(typeof performInput === 'function' && /^NTILDE_IN_[a-f\d]{32}$/.test(options.nonce ?? ''));
        this.performInput = performInput; this.plan = plan; this.runId = options.nonce.slice(10); this.frames = []; this.actions = [];
        this.next = 0; this.phase = 'start'; this.challenges = new Set();
        const facts = { ...options, includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true };
        this.readers = Array.from({ length: 4 }, () => new OwnedCellObserver(facts));
    }
    get paneId() { return this.readers[0].paneId; }
    get restored() { return this.phase === 'complete' && !this.failed; }
    async capture(afterAction, budget) {
        const [before, after] = this.readers.slice(afterAction ? 2 : 0, afterAction ? 4 : 2);
        before.paneId ??= this.paneId;
        const screen = await before.capture(budget); after.paneId ??= before.paneId;
        const afterScreen = await after.capture(budget);
        checkInput(sameInputFact(screen, afterScreen));
        return { screen, afterScreen, sha256: snapshotHash(screen), afterSha256: snapshotHash(afterScreen) };
    }
    async apply(request, budget) {
        const item = this.cases[this.next];
        const action = { index: this.actions.length, caseId: item.id, observed: null };
        this.actions.push(action);
        try { action.observed = structuredClone(await this.performInput(item.event, budget)); }
        catch (error) {
            action.observed = error?.inputAttempt ? structuredClone(error.inputAttempt) : null;
            action.error = error instanceof Error ? error.message : String(error); throw error;
        }
        validateInputAction(action.observed, item, this.paneId, this.runId, this.geometry, this.readers[0].profileName);
        this.phase = 'read';
        return { actionIndex: action.index };
    }
    expectedHex(item) {
        if (this.phase === 'control') return inputModeReplyHex(item.modes);
        if (this.phase === 'read') return item.expectedHex;
        if (this.phase === 'finish') return inputModeReplyHex(defaultInputModes);
        return '';
    }
    acceptScreen(pair) {
        if (this.phase !== 'start') {
            checkInput(sameInputFact(pair.screen, this.phase === 'finish' ? this.baseline : this.bound));
            return;
        }
        checkInput(pristineEditing(pair.screen));
        this.cases = this.plan.make(pair.screen.rows, pair.screen.cols);
        this.baseline = pair.screen; this.geometry = { rows: pair.screen.rows, cols: pair.screen.cols };
        this.bound = boundInputScreen(this.baseline, this.runId);
    }
    advancePhase() {
        const phases = { start: 'bind', bind: 'control', control: 'apply', finish: 'seal' };
        if (this.phase === 'read') { this.next++; this.phase = this.next === this.cases.length ? 'finish' : 'control'; }
        else this.phase = phases[this.phase];
    }
    async handle(request, budget) {
        checkInput(!this.busy && !this.failed && !this.restored && request.kind === 'input-' + this.phase);
        const item = ['control', 'apply', 'read'].includes(this.phase) ? this.cases[this.next] : null;
        checkInput(request.caseId === (item?.id ?? undefined)
            && Object.keys(request).every(key => ['kind', 'caseId', 'capture', 'captureStatus', 'tty', 'barrier', 'token', 'runId', 'workerPid'].includes(key)));
        this.busy = true; this.lastAttempt = { kind: request.kind, caseId: item?.id ?? null, capture: structuredClone(request.capture ?? null) };
        try {
            if (this.phase === 'apply') return await this.apply(request, budget);
            if (this.phase === 'seal') {
                this.seal = structuredClone(request.captureStatus);
                this.report = buildInputReport(this.frames, this.actions, this.seal, this.runId, this.plan);
                this.phase = 'complete'; return { sealed: true };
            }
            if (this.phase !== 'start') validateInputBytes(request.capture, this.expectedHex(item), this.challenges, this.phase !== 'read' || this.plan.exactEvents);
            const pair = await this.capture(this.phase === 'read', budget);
            this.lastAttempt.pair = structuredClone(pair);
            this.acceptScreen(pair);
            this.frames.push({ index: this.frames.length, kind: request.kind, paneId: this.paneId, ...pair,
                caseId: item?.id ?? null, actionIndex: this.phase === 'read' ? this.actions.at(-1).index : null,
                capture: structuredClone(request.capture ?? null) });
            this.advancePhase();
            return { screen: pair.screen, paneId: this.paneId };
        } catch (error) { this.failed = true; this.lastAttempt.error = error instanceof Error ? error.message : String(error); throw error; }
        finally { this.busy = false; }
    }
}
