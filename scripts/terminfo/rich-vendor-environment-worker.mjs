import { runEnvironmentWorker } from './modern-child-environment-worker.mjs';
import { snapshotEnvironment } from './rich-vendor-environment.mjs';

await runEnvironmentWorker(snapshotEnvironment, 'rve-environment');
