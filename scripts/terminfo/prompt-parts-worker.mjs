import * as contract from './prompt-parts-contracts.mjs';
import { runMetadataWorker } from './osc633-metadata-worker.mjs';

await runMetadataWorker(process.argv[2], contract);
