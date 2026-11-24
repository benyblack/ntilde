// Parent-side observer of the DECSCLM qualification, via the shared query-probe observer (probe-suite-shared.mjs): every read journals one complete, coherent state (stored cells, attributes,
// protection, cursor, margins, modes and history). A report read additionally binds the raw reply list the worker received (or its explicit absence) to the DA barrier that ordered it; any other read
// carries no report. Every frame also carries the parent's own timing: the whole milliseconds, on the parent's monotonic clock, between the end of the previous journaled observation and the arrival of this
// read request. That interval spans the fenced exchange (and the seeding before it) the worker ran in between, so it is an upper bound of what the terminal needed: the worker cannot make it shorter.
import { OwnedCellObserver } from './agent-observer.mjs';
import { dslPrefix } from './dsl-contracts.mjs';
import { validateDslExchange } from './dsl-report.mjs';
import { makeProbeSuiteObserver } from './probe-suite-shared.mjs';

const refuse = message => { throw new Error(message); };

/** Validate the extras one request may carry: a reply list, only on a read, ordered by that read's own barrier. */
function validateDslReadExtras(request) {
    if (request.report === undefined) return {};
    if (request.kind !== `${dslPrefix}-read`) refuse('Only reads carry a DECSCLM report.');
    const report = validateDslExchange(request.report);
    return report.barrier === request.barrier ? { report } : refuse('The DECSCLM report was not ordered by this read barrier.');
}

/** The timing record of one frame: whole milliseconds on the parent's monotonic clock, between the end of the previous journaled observation and the arrival of this read request. Anything else (a
 *  fraction, a negative, an absurd value, another clock) is refused: a missing or unreadable timing is never a zero or a guess. */
export const timingClock = 'parent-monotonic';
const timingCeilingMs = 600_000;
export function validateTiming(value) {
    const keys = value && typeof value === 'object' ? Object.keys(value) : [];
    if (keys.length !== 2 || value.clock !== timingClock || !Number.isSafeInteger(value.elapsedMs) || value.elapsedMs < 0 || value.elapsedMs > timingCeilingMs) refuse('Invalid DECSCLM timing record.');
    return { elapsedMs: value.elapsedMs, clock: value.clock };
}

/** The cell observer options this suite needs: stored attributes, details, cursor mode, overline, buffer state, protection and the DECLRMM margins. */
const observed = Object.freeze({ includeAttributes: true, includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true,
    includeResolvedColors: true, includeProtection: true, includeHorizontalMargins: true });

const ProbeObserver = makeProbeSuiteObserver({ label: 'DECSCLM', prefix: dslPrefix, validateExtras: validateDslReadExtras, observed, OwnedCellObserver });

export class DslObserver extends ProbeObserver {
    #now;
    #lastDone = null;
    /** `now()` is the parent's monotonic clock in milliseconds (the worker never supplies it). */
    constructor({ now = () => performance.now(), ...options } = {}) {
        super(options);
        if (typeof now !== 'function') refuse('The DECSCLM observer needs a monotonic clock.');
        this.#now = now;
    }
    async handle(request, budget) {
        const arrived = this.#now();
        const result = await super.handle(request, budget);
        this.frames.at(-1).timing = validateTiming({ elapsedMs: this.#lastDone === null ? 0 : Math.max(0, Math.round(arrived - this.#lastDone)), clock: timingClock });
        this.#lastDone = this.#now();
        return result;
    }
}
