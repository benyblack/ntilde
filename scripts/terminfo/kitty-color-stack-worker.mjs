import { runReportWorker } from './sixel-reports-worker.mjs';
import { planFor, prefix, resolveWrite } from './kitty-color-stack-contracts.mjs';

await runReportWorker(planFor, prefix, resolveWrite);
