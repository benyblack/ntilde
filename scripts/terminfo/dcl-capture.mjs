// Parent-side observer of the DECCOLM qualification, via the shared query-probe observer (probe-suite-shared.mjs): every read journals one complete, coherent state (stored cells, attributes,
// protection, cursor, margins, modes and history). A report read additionally binds the raw reply list the worker received (or its explicit absence) to the DA barrier that ordered it; any other read
// carries no report. Every frame also carries the kernel PTY grid, read by the parent with the system `/bin/stty` against the proved private PTY right after the paired App read, so the App grid (the
// paired screen's rows and columns) and the kernel grid are two independent facts about the same moment.
import { OwnedCellObserver } from './agent-observer.mjs';
import { dclPrefix } from './dcl-contracts.mjs';
import { validateDclExchange } from './dcl-report.mjs';
import { makeProbeSuiteObserver } from './probe-suite-shared.mjs';
import { sttyTool, validateOutputDescription } from './terminal-output.mjs';

const refuse = message => { throw new Error(message); };

/** Validate the extras one request may carry: a reply list, only on a read, ordered by that read's own barrier. */
function validateDclReadExtras(request) {
    if (request.report === undefined) return {};
    if (request.kind !== `${dclPrefix}-read`) refuse('Only reads carry a DECCOLM report.');
    const report = validateDclExchange(request.report);
    return report.barrier === request.barrier ? { report } : refuse('The DECCOLM report was not ordered by this read barrier.');
}

/** The kernel grid of a `/bin/stty -a` description: its row and column counts, the tool that reported them and the (still disabled) output processing. Anything else is refused: a missing or unreadable
 *  grid is never a zero or a copy of the App's. */
export function kernelGrid(output) {
    if (output?.tool !== sttyTool || output.outputProcessing !== false) refuse('The kernel PTY grid was not read by the system stty with output processing disabled.');
    validateOutputDescription(output.description);
    const match = /(?:^|[;\s])(\d+) rows;\s+(\d+) columns;/.exec(output.description);
    if (match === null) refuse('The kernel PTY description holds no grid.');
    const rows = Number(match[1]), cols = Number(match[2]);
    if (!Number.isSafeInteger(rows) || !Number.isSafeInteger(cols) || rows <= 0 || cols <= 0) refuse('The kernel PTY grid is not a positive size.');
    return { tool: output.tool, rows, cols, outputProcessing: false, description: output.description };
}

/** The cell observer options this suite needs: stored attributes, details, cursor mode, overline, buffer state, protection and the DECLRMM margins. */
const observed = Object.freeze({ includeAttributes: true, includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true,
    includeResolvedColors: true, includeProtection: true, includeHorizontalMargins: true });

const ProbeObserver = makeProbeSuiteObserver({ label: 'DECCOLM', prefix: dclPrefix, validateExtras: validateDclReadExtras, observed, OwnedCellObserver });

export class DclObserver extends ProbeObserver {
    #readKernel;
    /** `readKernel(budget)` must return the system stty description of the proved private PTY (the parent supplies it; the worker never does). */
    constructor({ readKernel, ...options }) {
        super(options);
        if (typeof readKernel !== 'function') refuse('The DECCOLM observer needs a kernel PTY reader.');
        this.#readKernel = readKernel;
    }
    async handle(request, budget) {
        const result = await super.handle(request, budget);
        this.frames.at(-1).kernel = kernelGrid(await this.#readKernel(budget));
        return result;
    }
}
