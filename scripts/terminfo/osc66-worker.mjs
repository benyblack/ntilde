import { runFencedWorker } from './terminal-fence.mjs';
import { da, planFor, prefix } from './osc66-contracts.mjs';

await runFencedWorker({ da, planFor, prefix, label: 'OSC 66' });
