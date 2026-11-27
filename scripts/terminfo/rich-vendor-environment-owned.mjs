import { execFileSync } from 'node:child_process';
import { createChildEnvironmentCollector } from './modern-child-environment-owned.mjs';
import { controlVariable, names, parentValues, parseChildOutput, scope, snapshotEnvironment } from './rich-vendor-environment.mjs';
import { capturedVendorEnvironmentSourceFiles, capturedVendorEnvironmentSourcePolicy } from './owned-sources.mjs';

function hostMetadata() {
    const [product, version, build] = ['--productName', '--productVersion', '--buildVersion']
        .map(flag => execFileSync('/usr/bin/sw_vers', [flag], { encoding: 'utf8' }).trim());
    return { product, version, build, arch: process.arch };
}

export const { workerFile, prefix, workerProfile, prepareLaunchEnvironment, Collection, completeReceipt, verifyChildEnvironmentRun } =
    createChildEnvironmentCollector({
        scope, workerFile: 'rich-vendor-environment-worker.mjs', prefix: 'rve-', names, parentValues, parseChildOutput,
        snapshotEnvironment, controlVariable, hostMetadata,
        sourceFiles: capturedVendorEnvironmentSourceFiles, sourcePolicy: capturedVendorEnvironmentSourcePolicy,
    });
export { scope };
