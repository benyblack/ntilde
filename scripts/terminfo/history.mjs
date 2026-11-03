import { buildCellPlanReport } from './editing.mjs';
import { historySuiteId, makeHistoryCases } from './history-cases.mjs';
const spec = { prefix: 'history', casesFor: makeHistoryCases, nonce: 'NTILDE_HS_', suiteId: historySuiteId,
    controlDescription: 'known full viewport, cursor, modes and actual complete ordered history before the operation',
    scope: 'Owned ASCII history accumulation, SU/SD/RI, margins and origin-relative homing. Complete viewport cells and actual bounded ordered history text, with before/after state and exact restoration. Invalid-region cases qualify margins only with a cursor already home. No resize/reflow, history attribute, Unicode editing, pixel or stock-grade claim.' };
export const buildHistoryReport = (frames, runId) => buildCellPlanReport(frames, runId, spec);
