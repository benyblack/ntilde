import { createChildEnvironmentCollector, macHostMetadata } from './modern-child-environment-owned.mjs';
import { controlVariable, names, parentValues, parseChildOutput, scope, snapshotEnvironment } from './rich-term-features.mjs';
import { capturedTermFeaturesSourceFiles, capturedTermFeaturesSourcePolicy } from './owned-sources.mjs';

export const { workerFile, prefix, workerProfile, prepareLaunchEnvironment, Collection, completeReceipt, verifyChildEnvironmentRun } =
    createChildEnvironmentCollector({
        scope, workerFile: 'rich-term-features-worker.mjs', prefix: 'rtf-', names, parentValues, parseChildOutput,
        snapshotEnvironment, controlVariable, hostMetadata: macHostMetadata, extraBinaries: ['Ntilde.VT.dll'],
        sourceFiles: capturedTermFeaturesSourceFiles, sourcePolicy: capturedTermFeaturesSourcePolicy,
    });
export { scope };
