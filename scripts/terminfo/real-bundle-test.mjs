// Test-only entry point: dependency caches select optional coverage, but their bytes never execute.
import { test } from 'node:test';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { installLockedCollector, readPinnedCollectorInputs } from './runtime.mjs';
import { validateCollectorReceipt } from './collector.mjs';

const root = fileURLToPath(new URL('../../artifacts/terminfo/', import.meta.url));
const cachedBundle = join(root, 'tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs');

export function requireCollectorNode(version = process.versions.node) {
    if (Number(version.split('.')[0]) < 24) {
        throw new Error('Real pinned collector replay requires Node 24+: its locked silvery dependency uses explicit resource management (using render).');
    }
}

export function realBundleRequired(environment = process.env) {
    const value = environment.NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED;
    if (value !== undefined && !['0', '1'].includes(value)) throw new Error('NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED must be 0 or 1.');
    return value === '1';
}

/** Required CI coverage cannot skip, and every executed test imports a fresh integrity-checked npm installation. */
export function testWithPinnedCollector(name, run) {
    const required = realBundleRequired();
    return test(name, { skip: required || existsSync(cachedBundle) ? false : 'collector cache not installed' }, async () => {
        requireCollectorNode();
        const { manifestBytes, lockBytes } = readPinnedCollectorInputs();
        mkdirSync(root, { recursive: true });
        const directory = mkdtempSync(join(root, 'replay-'));
        try {
            const pkg = installLockedCollector(join(directory, 'collector'), manifestBytes, lockBytes);
            const bundle = join(pkg, 'dist/terminfo.bundle.mjs');
            validateCollectorReceipt(readFileSync(bundle), JSON.parse(readFileSync(join(pkg, 'dist/terminfo.bundle.receipt.json'))));
            await run(pathToFileURL(bundle));
        } finally { rmSync(directory, { recursive: true, force: true }); }
    });
}
