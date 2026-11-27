import { runReportWorker } from './sixel-reports-worker.mjs';
import { planFor, prefix } from './device-identity-contracts.mjs';

await runReportWorker(planFor, prefix);
