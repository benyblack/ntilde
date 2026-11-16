// Parent-side observer of the xterm-saved-modes qualification. Every read journals one complete, coherent state (stored cells, attributes, protection, cursor,
// margins, modes and history). A report read additionally binds the raw reply list the worker received (or its explicit absence) to the DA barrier that
// ordered it; any other read carries no report.
import { OwnedCellObserver } from './agent-observer.mjs';
import { xsvPrefix } from './xsv-contracts.mjs';
import { validateXsvExchange } from './xsv-report.mjs';

const fail = message => { throw new Error(message); };

/** Validate the extras one request may carry: a reply list, only on a read, ordered by that read's own barrier. */
export function validateXsvReadExtras(request) {
    if (request.report === undefined) return {};
    if (request.kind !== `${xsvPrefix}-read`) fail('Only reads carry a xterm-saved-modes report.');
    const report = validateXsvExchange(request.report);
    if (report.barrier !== request.barrier) fail('The xterm-saved-modes report was not ordered by this read barrier.');
    return { report };
}

export class XsvObserver {
    constructor(options) {
        // includeCursorMode keeps the stored style/blink/default state, and includeHorizontalMargins the DECLRMM margins, in the observed state, so the
        // unchanged-state key sees them.
        this.cells = new OwnedCellObserver({ ...options, prefix: xsvPrefix, includeAttributes: true, includeCellDetails: true, includeCursorMode: true,
            includeOverline: true, includeBufferState: true, includeResolvedColors: true, includeProtection: true, includeHorizontalMargins: true });
        this.frames = [];
        this.busy = false;
    }
    get restored() { return this.cells.restored; }
    get paneId() { return this.cells.paneId; }
    /** Observe one request, journaling the reply list of a report read beside that same observation's frame. */
    async handle(request, budget) {
        if (this.busy || this.restored) fail('Xterm saved-mode observation busy or complete.');
        const extras = validateXsvReadExtras(request);
        this.busy = true;
        try {
            const result = await this.cells.handle(request.kind, budget, request.barrier ?? null);
            this.frames.push({ ...this.cells.frames.at(-1), ...(extras.report ? { report: extras.report } : {}) });
            return result;
        } finally { this.busy = false; }
    }
}
