import { OwnedCellObserver, snapshotHash } from './agent-observer.mjs';
import { pristineEditing } from './editing-capture.mjs';
import { makeInputCases } from './input-cases.mjs';
import { boundInputScreen, buildInputReport, checkInput, defaultInputModes, inputModeReplyHex, sameInputFact,
    validateInputAction, validateInputBytes } from './input-state.mjs';

/** Four phase readers keep paired before/after controls below each reader's64-read cap. */
export class OwnedInputObserver {
    constructor({ performInput, ...options }) {
        checkInput(typeof performInput === 'function' && /^NTILDE_IN_[a-f\d]{32}$/.test(options.nonce ?? ''));
        this.performInput = performInput; this.runId = options.nonce.slice(10); this.frames = []; this.actions = [];
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
        validateInputAction(action.observed, item, this.paneId, this.runId, this.geometry);
        this.phase = 'read';
        return { actionIndex: action.index };
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
                this.report = buildInputReport(this.frames, this.actions, this.seal, this.runId);
                this.phase = 'complete'; return { sealed: true };
            }
            const expectedHex = this.phase === 'control' ? inputModeReplyHex(item.modes)
                : this.phase === 'read' ? item.expectedHex : this.phase === 'finish' ? inputModeReplyHex(defaultInputModes) : '';
            if (this.phase !== 'start') validateInputBytes(request.capture, expectedHex, this.challenges);
            const pair = await this.capture(this.phase === 'read', budget);
            this.lastAttempt.pair = structuredClone(pair);
            if (this.phase === 'start') {
                checkInput(pristineEditing(pair.screen));
                this.cases = makeInputCases(pair.screen.rows, pair.screen.cols);
                this.baseline = pair.screen; this.geometry = { rows: pair.screen.rows, cols: pair.screen.cols };
                this.bound = boundInputScreen(this.baseline, this.runId);
            } else checkInput(sameInputFact(pair.screen, this.phase === 'finish' ? this.baseline : this.bound));
            this.frames.push({ index: this.frames.length, kind: request.kind, paneId: this.paneId, ...pair,
                caseId: item?.id ?? null, actionIndex: this.phase === 'read' ? this.actions.at(-1).index : null,
                capture: structuredClone(request.capture ?? null) });
            if (this.phase === 'start') this.phase = 'bind';
            else if (this.phase === 'bind') this.phase = 'control';
            else if (this.phase === 'control') this.phase = 'apply';
            else if (this.phase === 'read') { this.next++; this.phase = this.next === this.cases.length ? 'finish' : 'control'; }
            else if (this.phase === 'finish') this.phase = 'seal';
            return { screen: pair.screen, paneId: this.paneId };
        } catch (error) { this.failed = true; this.lastAttempt.error = error instanceof Error ? error.message : String(error); throw error; }
        finally { this.busy = false; }
    }
}
