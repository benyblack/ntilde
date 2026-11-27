import * as contract from './prompt-parts-contracts.mjs';
import { Collection as PaneCollection, completeReceipt as completePaneReceipt, deriveRun as derivePaneRun } from './osc633-metadata-owned.mjs';

export { prefix, scope } from './prompt-parts-contracts.mjs';
export const workerFile = 'prompt-parts-worker.mjs';
export const imageOwnership = true;
export class Collection extends PaneCollection {
    constructor(run) { super(run, contract); }
}
export const deriveRun = output => derivePaneRun(output, contract);
export const completeReceipt = (output, scratch) => completePaneReceipt(output, scratch, contract);
