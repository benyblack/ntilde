import { OwnedCellObserver, agentRequest, observerEndpoint } from './agent-observer.mjs';
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
    get restored() { return this.cells.restored; }
    async handle(request, budget) {
        if (this.busy || this.restored) throw new Error('Cell-size observation busy or complete.');
        const report = request.report === undefined ? null : validateCellSizeExchange(request.report);
        if ((request.kind === 'fnt-read') !== Boolean(report) || (report && report.barrier !== request.barrier)) throw new Error('Unbound font-size exchange.');
        this.busy = true;
        try {
            const result = await this.cells.handle(request.kind, budget, request.barrier ?? null);
            const rpc = this.options.rpc ?? agentRequest;
            const metadata = await rpc((this.options.endpoint ?? observerEndpoint)(this.options.appData, this.options.ownerPid), 'readPaneMetadata', { paneId: result.paneId }, Math.min(1000, budget()));
            if (metadata.paneId !== result.paneId) throw new Error('Font geometry pane identity changed.');
            const geometry = fontSizeGeometry({ ...metadata.cellGeometry, ...metadata.fontGeometry }, result.screen.rows, result.screen.cols);
            this.frames.push({ ...this.cells.frames.at(-1), geometry, ...(report ? { report } : {}) });
            return result;
        } finally { this.busy = false; }
    }
}
