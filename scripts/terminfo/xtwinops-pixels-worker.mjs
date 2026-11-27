import { runReportWorker } from './sixel-reports-worker.mjs';
import { planFor, prefix } from './xtwinops-pixels-contracts.mjs';

await runReportWorker(planFor, prefix);
