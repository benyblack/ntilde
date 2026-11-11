import { OwnedCellObserver, snapshotHash } from './agent-observer.mjs';
import { validateCursorReport } from './cursor-report.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (ok, message) => { if (!ok) throw new Error('Core wrap observer: ' + message); };
class PairedObserver extends OwnedCellObserver {
    async capture(budget) {
        const first = await super.capture(budget), second = await super.capture(budget);
        this.lastAttempt = { first, second };
        check(snapshotHash(first) === snapshotHash(second), 'paired reads disagree');
        return first;
    }
}
/** One fixed parent-selected shrink and restoration, never worker dimensions. */
export class WrapObserver {
    constructor({ performResize, runId, expectedOriginal, ...options }) {
        check(typeof performResize === 'function' && /^[a-f\d]{32}$/.test(runId ?? ''), 'invalid configuration');
        this.runId = runId; this.performResize = performResize; this.expectedOriginal = expectedOriginal;
        const facts = { ...options, includeAttributes: true, includeCellDetails: true, includeOverline: true,
            includeBufferState: true, includeResolvedColors: true, includeHorizontalMargins: true };
        this.geometryReader = new PairedObserver(facts);
        this.reader = new PairedObserver({ ...facts, nonce: 'NTILDE_CW_' + runId, prefix: 'cw' });
        this.actions = []; this.phase = 'new';
    }
    get frames() { return this.reader.frames; }
    get paneId() { return this.geometryReader.paneId; }
    get restored() { return this.phase === 'sealed'; }
    async action(target, budget) {
        const entry = { index: this.actions.length, target, observed: null }; this.actions.push(entry);
        try { entry.observed = structuredClone(await this.performResize(target.rows, target.cols, budget)); }
        catch (error) { entry.error = String(error); throw error; }
        const o = entry.observed, a = o?.app;
        check(o?.runId === this.runId && o.paneId === this.paneId && a?.exact === true && a.layoutSettled === true
            && a.rows === target.rows && a.cols === target.cols && o.terminalOutput?.tool === '/bin/stty'
            && o.terminalOutput.outputProcessing === false, 'partial or mismatched resize');
        validateOutputGeometry(o.terminalOutput.description, target.rows, target.cols);
    }
    async captureRead(request, budget) {
        const cpr = validateCursorReport(request.cpr);
        check(cpr.barrier === request.barrier, 'CPR barrier mismatch');
        const result = await this.reader.handle(request.kind, budget, request.barrier);
        this.reader.frames.at(-1).cpr = cpr;
        return result;
    }
    async handle(request, budget) {
        check(!this.busy && !this.restored, 'busy or already sealed'); this.busy = true;
        try {
            const kind = request.kind;
            if (!['cw-bind', 'cw-read', 'cw-finish'].includes(kind)) check(request.barrier == null, 'unexpected barrier');
            if (kind !== 'cw-read') check(request.cpr === undefined, 'unexpected CPR');
            check(request.rows === undefined && request.cols === undefined && request.target === undefined, 'worker supplied dimensions');
            if (kind === 'cw-geometry-start') {
                check(this.phase === 'new', 'wrong phase');
                this.before = await this.geometryReader.capture(budget); this.original = { rows: this.before.rows, cols: this.before.cols };
                check(this.original.cols >= 80 && same(this.original, this.expectedOriginal), 'original geometry changed');
                this.reader.paneId = this.paneId; this.phase = 'original';
                return { screen: this.before, paneId: this.paneId };
            }
            if (kind === 'cw-narrow') {
                check(this.phase === 'original', 'wrong phase'); this.phase = 'narrowing';
                await this.action({ rows: this.original.rows, cols: 80 }, budget); this.phase = 'narrowed'; return {};
            }
            if (kind === 'cw-abort') {
                check(!['new', 'original', 'sealed', 'aborted'].includes(this.phase), 'wrong abort phase');
                this.phase = 'aborted'; await this.action(this.original, budget); return {};
            }
            if (kind === 'cw-restore') {
                check(this.phase === 'finished' && this.reader.restored, 'fixture not restored'); this.phase = 'restoring';
                await this.action(this.original, budget); this.phase = 'restored'; return {};
            }
            if (kind === 'cw-seal') {
                check(this.phase === 'restored', 'wrong phase'); this.after = await this.geometryReader.capture(budget);
                check(snapshotHash(this.before) === snapshotHash(this.after), 'original state not restored');
                this.phase = 'sealed'; return { screen: this.after, paneId: this.paneId };
            }
            check(['cw-start', 'cw-bind', 'cw-read', 'cw-finish'].includes(kind), 'unknown operation');
            if (kind === 'cw-start') check(this.phase === 'narrowed', 'wrong phase');
            else check(this.phase === 'active', 'wrong phase');
            if (kind === 'cw-read') return await this.captureRead(request, budget);
            check(request.cpr === undefined, 'unexpected CPR');
            const result = await this.reader.handle(kind, budget, request.barrier);
            check(result.screen.rows === this.original.rows && result.screen.cols === 80, 'observation geometry mismatch');
            if (kind === 'cw-start') this.phase = 'active';
            if (kind === 'cw-finish') this.phase = 'finished';
            return result;
        } finally { this.busy = false; }
    }
}
