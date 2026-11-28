import { runReportWorker } from './sixel-reports-worker.mjs';
import { planFor, prefix } from './device-state-contracts.mjs';

await runReportWorker(planFor, prefix);
