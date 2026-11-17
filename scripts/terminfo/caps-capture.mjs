// Parent-side observer of the iTerm2 feature-reporting qualification. Every read journals one complete, coherent state (stored cells, attributes, protection, cursor, margins, modes and history). A report
// read additionally binds the raw reply list the worker received (or its explicit absence) to the DA barrier that ordered it; any other read carries no report.
import { OwnedCellObserver } from './agent-observer.mjs';
import { capsPrefix } from './caps-contracts.mjs';
import { validateCapsExchange } from './caps-report.mjs';

const refuse = message => { throw new Error(message); };

/** Validate the extras one request may carry: a reply list, only on a read, ordered by that read's own barrier. */
export function validateCapsReadExtras(request) {
    if (request.report === undefined) return {};
    if (request.kind !== `${capsPrefix}-read`) refuse('Only reads carry a feature-reporting report.');
    const report = validateCapsExchange(request.report);
    return report.barrier === request.barrier ? { report } : refuse('The feature-reporting report was not ordered by this read barrier.');
}

/** The cell observer options this suite needs: stored attributes, details, cursor mode, overline, buffer state, resolved colors, protection and the DECLRMM margins. */
const observed = Object.freeze({ includeAttributes: true, includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true,
    includeResolvedColors: true, includeProtection: true, includeHorizontalMargins: true });

export class CapsObserver {
    frames = [];
    #busy = false;
    constructor(options) { this.cells = new OwnedCellObserver({ ...options, prefix: capsPrefix, ...observed }); }
    get restored() { return this.cells.restored; }
    get paneId() { return this.cells.paneId; }
    /** Observe one request, journaling the reply list of a report read beside that same observation's frame. */
    async handle(request, budget) {
        if (this.#busy || this.restored) refuse('Feature-reporting observation busy or complete.');
        const { report } = validateCapsReadExtras(request);
        this.#busy = true;
        try {
            const result = await this.cells.handle(request.kind, budget, request.barrier ?? null);
            this.frames.push(report === undefined ? this.cells.frames.at(-1) : { ...this.cells.frames.at(-1), report });
            return result;
        } finally { this.#busy = false; }
    }
}
