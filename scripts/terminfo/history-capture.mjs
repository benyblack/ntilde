import { OwnedCellPlanObserver } from './editing-capture.mjs';
import { makeHistoryCases } from './history-cases.mjs';
/** Trusted parent-owned history plan; no worker-supplied expected data. */
export class OwnedHistoryObserver extends OwnedCellPlanObserver {
    constructor(options) { super({ ...options, prefix: 'history', casesFor: makeHistoryCases }); }
}
