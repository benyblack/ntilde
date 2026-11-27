import { runFencedWorker } from './terminal-fence.mjs';
import { da, planFor, prefix } from './font-ligatures-contracts.mjs';

await runFencedWorker({ da, planFor, prefix, label: 'Font ligature' });
