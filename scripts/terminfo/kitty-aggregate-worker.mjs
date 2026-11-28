import { runFocusedImageWorker } from './kitty-graphics-worker.mjs';
import { modeQueries, plan, prefix } from './kitty-aggregate-owned.mjs';

await runFocusedImageWorker({ modeQueries, plan, prefix, failureFile: 'aggregate-raw-failure.json' });
