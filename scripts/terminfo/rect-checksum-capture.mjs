// Parent-side observer of the rectangle-checksum qualification. Every read journals one complete, coherent state (stored cells,
// attributes, protection, cursor, margins, modes and history). A report read additionally binds the raw DECRQCRA reply the worker
// received (or its explicit absence) to the DA barrier that ordered it; any other read carries no report.
import { OwnedCellObserver } from './agent-observer.mjs';
import { validateChecksumReport } from './checksum-report.mjs';

const fail = message => { throw new Error(message); };

/** Validate the extras one request may carry: a report, only on a read, ordered by that read's own barrier. */
export function validateChecksumReadExtras(request) {
    if (request.report === undefined) return {};
    if (request.kind !== 'csum-read') fail('Only reads carry a rectangle-checksum report.');
    const report = validateChecksumReport(request.report);
    if (report.barrier !== request.barrier) fail('The rectangle-checksum report was not ordered by this read barrier.');
    return { report };
}

export class RectChecksumObserver {
    constructor(options) {
        this.cells = new OwnedCellObserver({ ...options, prefix: 'csum', includeAttributes: true, includeCellDetails: true, includeOverline: true,
            includeBufferState: true, includeResolvedColors: true, includeProtection: true });
        this.frames = [];
        this.busy = false;
    }
    get restored() { return this.cells.restored; }
    get paneId() { return this.cells.paneId; }
    async handle(request, budget) {
        if (this.busy || this.restored) fail('Rectangle-checksum observation busy or complete.');
        const extras = validateChecksumReadExtras(request);
        this.busy = true;
        try {
            const result = await this.cells.handle(request.kind, budget, request.barrier ?? null);
            this.frames.push({ ...this.cells.frames.at(-1), ...(extras.report ? { report: extras.report } : {}) });
            return result;
        } finally { this.busy = false; }
    }
}
