import { OwnedCellObserver, agentRequest, observerEndpoint, snapshotHash } from './agent-observer.mjs';
import { fontSizeGeometry, fontSizePrefix } from './font-size-contracts.mjs';
import { validateCellSizeExchange } from './cell-size-frame.mjs';

export class FontSizeObserver {
    constructor(options) {
        this.options = options;
        this.cells = new OwnedCellObserver({ ...options, prefix: fontSizePrefix, includeAttributes: true, includeCellDetails: true,
            includeCursorMode: true, includeOverline: true, includeBufferState: true, includeResolvedColors: true,
            includeProtection: true, includeHorizontalMargins: true });
        this.frames = [];
        this.busy = false;
    }
    get restored() { return Boolean(this.cells.restored && this.frames.at(-1)?.kind === 'fnt-finish'); }
    async observeCells(request, budget) {
        if (request.kind !== 'fnt-finish' || !this.cells.baseline || this.cells.bound)
            return this.cells.handle(request.kind, budget, request.barrier ?? null);
        if (typeof request.barrier !== 'string' || request.barrier.length > 128 || !/^\x1b\[\?[\d;]+c$/.test(request.barrier))
            throw new Error('Missing aborted font restoration ordering barrier.');
        const screen = await this.cells.capture(budget);
        this.lastAttempt.snapshot = { kind: request.kind, paneId: this.cells.paneId,
            barrier: request.barrier, screen, sha256: snapshotHash(screen) };
        this.lastAttempt.stage = 'aborted-restoration';
        this.lastAttempt.restoredMainScreen = snapshotHash(screen) === snapshotHash(this.cells.baseline);
        throw new Error(this.lastAttempt.restoredMainScreen
            ? 'Aborted font fixture main screen restored; nonce binding never completed.'
            : 'Aborted font fixture main-screen restoration failed.');
    }
    async handle(request, budget) {
        if (this.busy || this.restored) throw new Error('Font-size observation busy or complete.');
        this.lastAttempt = null;
        const report = request.report === undefined ? null : validateCellSizeExchange(request.report);
        if ((request.kind === 'fnt-read') !== Boolean(report) || (report && report.barrier !== request.barrier)) throw new Error('Unbound font-size exchange.');
        this.busy = true;
        this.lastAttempt = { schemaVersion: 1, kind: request.kind, barrier: request.barrier ?? null,
            ...(report ? { report } : {}), stage: 'cell-snapshot' };
        try {
            const result = await this.observeCells(request, budget);
            this.lastAttempt.snapshot = this.cells.frames.at(-1);
            this.lastAttempt.stage = 'pane-metadata';
            const rpc = this.options.rpc ?? agentRequest;
            const metadata = await rpc((this.options.endpoint ?? observerEndpoint)(this.options.appData, this.options.ownerPid), 'readPaneMetadata', { paneId: result.paneId }, Math.min(1000, budget()));
            this.lastAttempt.stage = 'font-geometry';
            if (metadata.paneId !== result.paneId) throw new Error('Font geometry pane identity changed.');
            const geometry = fontSizeGeometry({ ...metadata.cellGeometry, ...metadata.fontGeometry }, result.screen.rows, result.screen.cols);
            this.frames.push({ ...this.cells.frames.at(-1), geometry, ...(report ? { report } : {}) });
            return result;
        } catch (error) {
            this.lastAttempt.error = { name: String(error?.name ?? 'Error').slice(0, 128),
                message: String(error?.message ?? error).slice(0, 2048) };
            throw error;
        } finally { this.busy = false; }
    }
}
