import { createChildEnvironmentCollector, macHostMetadata } from './modern-child-environment-owned.mjs';
import { controlVariable, names, parentValues, parseChildOutput, scope, snapshotEnvironment } from './rich-vendor-environment.mjs';
import { capturedVendorEnvironmentSourceFiles, capturedVendorEnvironmentSourcePolicy } from './owned-sources.mjs';

export const { workerFile, prefix, workerProfile, prepareLaunchEnvironment, Collection, completeReceipt, verifyChildEnvironmentRun } =
    createChildEnvironmentCollector({
        scope, workerFile: 'rich-vendor-environment-worker.mjs', prefix: 'rve-', names, parentValues, parseChildOutput,
        snapshotEnvironment, controlVariable, hostMetadata: macHostMetadata,
        sourceFiles: capturedVendorEnvironmentSourceFiles, sourcePolicy: capturedVendorEnvironmentSourcePolicy,
    });
export { scope };
