// Parent-side observer of the xterm-sgr-stack qualification. Every read journals one complete, coherent state (stored cells, attributes, protection, cursor,
// margins, modes and history). A report read additionally binds the raw reply list the worker received (or its explicit absence) to the DA barrier that
// ordered it; any other read carries no report.
import { OwnedCellObserver } from './agent-observer.mjs';
import { xssPrefix } from './xss-contracts.mjs';
import { validateXssExchange } from './xss-report.mjs';

const fail = message => { throw new Error(message); };

/** Validate the extras one request may carry: a reply list, only on a read, ordered by that read's own barrier. */
export function validateXssReadExtras(request) {
    if (request.report === undefined) return {};
    if (request.kind !== `${xssPrefix}-read`) fail('Only reads carry a xterm-sgr-stack report.');
    const report = validateXssExchange(request.report);
    if (report.barrier !== request.barrier) fail('The xterm-sgr-stack report was not ordered by this read barrier.');
    return { report };
}

export class XssObserver {
    constructor(options) {
        // includeCursorMode keeps the stored style/blink/default state, and includeHorizontalMargins the DECLRMM margins, in the observed state, so the
        // unchanged-state key sees them.
        this.cells = new OwnedCellObserver({ ...options, prefix: xssPrefix, includeAttributes: true, includeCellDetails: true, includeCursorMode: true,
            includeOverline: true, includeBufferState: true, includeResolvedColors: true, includeProtection: true, includeHorizontalMargins: true });
        this.frames = [];
        this.busy = false;
    }
    get restored() { return this.cells.restored; }
    get paneId() { return this.cells.paneId; }
    /** Observe one request, journaling the reply list of a report read beside that same observation's frame. */
    async handle(request, budget) {
        if (this.busy || this.restored) fail('Xterm SGR-stack observation busy or complete.');
        const extras = validateXssReadExtras(request);
        this.busy = true;
        try {
            const result = await this.cells.handle(request.kind, budget, request.barrier ?? null);
            this.frames.push({ ...this.cells.frames.at(-1), ...(extras.report ? { report: extras.report } : {}) });
            return result;
        } finally { this.busy = false; }
    }
}
