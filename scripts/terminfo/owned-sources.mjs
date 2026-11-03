import { chmodSync, lstatSync, readFileSync, readdirSync, realpathSync, rmSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join } from 'node:path';
import { tmpdir } from 'node:os';
export const sourceSnapshotPolicy = 'readonly-owned-source-graph-v1';
export const ownedSourceFiles = Object.freeze(JSON.parse(readFileSync(new URL('./owned-source-inventory.json', import.meta.url))));
export const legacyV8SourceFiles = Object.freeze(Object.fromEntries(Object.entries(ownedSourceFiles)
    .filter(([key]) => key !== 'sourceInventorySha256').map(([key, file]) => [key, key === 'runnerSourceSha256' ? 'terminfo-owned.mjs' : file])));
export const ownedSourcePath = (root, file) => join(root, 'scripts', file === 'terminfo-owned-runner.mjs' || file === 'terminfo-owned.mjs' ? file : 'terminfo/' + file);
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
export function assertOwnedSourcesUnchanged(root, context) {
    for (const [key, file] of Object.entries(ownedSourceFiles)) {
        if (digest(readFileSync(ownedSourcePath(root, file))) !== context[key]) throw new Error('Owned source changed before completion: ' + file);
    }
}
export function readSourceSnapshot(root) {
    if (realpathSync(root) !== process.env.NTILDE_TERMINFO_SOURCE_SNAPSHOT
        || dirname(root) !== realpathSync(tmpdir()) || !root.split(/[\\/]/).at(-1).startsWith('ntilde-owned-sources-')) throw new Error('Expected the private captured source tree.');
    const bytes = readFileSync(join(root, 'source-snapshot.json'));
    if (digest(bytes) !== process.env.NTILDE_TERMINFO_SOURCE_SNAPSHOT_SHA256) throw new Error('Source snapshot manifest changed.');
    const manifest = JSON.parse(bytes);
    if (manifest.schemaVersion !== 1 || manifest.policy !== sourceSnapshotPolicy
        || Object.keys(manifest.hashes ?? {}).length !== Object.keys(ownedSourceFiles).length) throw new Error('Invalid source snapshot manifest.');
    assertOwnedSourcesUnchanged(root, manifest.hashes);
    return { bytes, sha256: digest(bytes), manifest };
}
function writableDirectories(root) {
    for (const entry of readdirSync(root, { withFileTypes: true })) if (entry.isDirectory()) writableDirectories(join(root, entry.name));
    chmodSync(root, 0o700);
}
export function removeSourceSnapshot(root) {
    readSourceSnapshot(root); writableDirectories(root); rmSync(root, { recursive: true, force: true });
    try { lstatSync(root); throw new Error('Captured source tree remains.'); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
}
