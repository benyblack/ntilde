// Parent-side observer of the kitty color-control qualification. Every read journals one complete, coherent state (stored cells, attributes, protection, cursor, margins, modes and history). A report read
// additionally binds the raw reply list the worker received (or its explicit absence) to the DA barrier that ordered it; any other read carries no report.
import { OwnedCellObserver } from './agent-observer.mjs';
import { kclPrefix } from './kcl-contracts.mjs';
import { validateKclExchange } from './kcl-report.mjs';

const refuse = message => { throw new Error(message); };

/** Validate the extras one request may carry: a reply list, only on a read, ordered by that read's own barrier. */
export function validateKclReadExtras(request) {
    if (request.report === undefined) return {};
    if (request.kind !== `${kclPrefix}-read`) refuse('Only reads carry a kitty color report.');
    const report = validateKclExchange(request.report);
    return report.barrier === request.barrier ? { report } : refuse('The kitty color report was not ordered by this read barrier.');
}

/** The cell observer options this suite needs: stored attributes, details, cursor mode, overline, buffer state, resolved colors, protection and the DECLRMM margins. */
const observed = Object.freeze({ includeAttributes: true, includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true,
    includeResolvedColors: true, includeProtection: true, includeHorizontalMargins: true });

export class KclObserver {
    frames = [];
    #busy = false;
    constructor(options) { this.cells = new OwnedCellObserver({ ...options, prefix: kclPrefix, ...observed }); }
    get restored() { return this.cells.restored; }
    get paneId() { return this.cells.paneId; }
    /** Observe one request, journaling the reply list of a report read beside that same observation's frame. */
    async handle(request, budget) {
        if (this.#busy || this.restored) refuse('Kitty color observation busy or complete.');
        const { report } = validateKclReadExtras(request);
        this.#busy = true;
        try {
            const result = await this.cells.handle(request.kind, budget, request.barrier ?? null);
            this.frames.push(report === undefined ? this.cells.frames.at(-1) : { ...this.cells.frames.at(-1), report });
            return result;
        } finally { this.#busy = false; }
    }
}
