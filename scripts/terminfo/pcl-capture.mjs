// Parent-side observer of the pointer-color qualification, via the shared query-probe observer
// (probe-suite-shared.mjs): every read journals one complete, coherent state (stored cells, attributes,
// protection, cursor, margins, modes and history). A report read additionally binds the raw reply list the
// worker received (or its explicit absence) to the DA barrier that ordered it; any other read carries no report.
import { OwnedCellObserver } from './agent-observer.mjs';
import { pclPrefix } from './pcl-contracts.mjs';
import { validatePclExchange } from './pcl-report.mjs';
import { makeProbeSuiteObserver } from './probe-suite-shared.mjs';

const refuse = message => { throw new Error(message); };

/** Validate the extras one request may carry: a reply list, only on a read, ordered by that read's own barrier. */
function validatePclReadExtras(request) {
    if (request.report === undefined) return {};
    if (request.kind !== `${pclPrefix}-read`) refuse('Only reads carry a pointer-color report.');
    const report = validatePclExchange(request.report);
    return report.barrier === request.barrier ? { report } : refuse('The pointer-color report was not ordered by this read barrier.');
}

/** The cell observer options this suite needs: stored attributes, details, cursor mode, overline, buffer state, resolved colors, protection and the DECLRMM margins. */
const observed = Object.freeze({ includeAttributes: true, includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true,
    includeResolvedColors: true, includeProtection: true, includeHorizontalMargins: true });

export const PclObserver = makeProbeSuiteObserver({ label: 'Pointer-color', prefix: pclPrefix, validateExtras: validatePclReadExtras, observed, OwnedCellObserver });
