import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { gunzipSync } from 'node:zlib';
import { commonCoreGit } from './common-core-tools.mjs';
import { pythonCommand } from './common-core-evidence.mjs';
import { derivePinnedRuntime, verifyRuntimeClaims } from './common-core-runtime.mjs';

/** Match the upstream catalogue SHA-256 binding. */
const sha = (value) => createHash('sha256').update(value).digest('hex');
/** Preserve upstream JavaScript code-unit order, independent of host locale. */
function comparePinnedStrings(a, b) {
    if (a < b) return -1;
    if (a > b) return 1;
    return 0;
}

/** Reproduce the pinned upstream canonical JSON rule. */
const canonical = (value) =>
    JSON.stringify(value, (_key, current) =>
        current !== null && typeof current === 'object' && !Array.isArray(current)
            ? Object.fromEntries(Object.entries(current).sort(([a], [b]) => comparePinnedStrings(a, b)))
            : current,
    );

/** Recompute the pinned upstream fingerprint from Git-bound source bytes, not a proof boolean. */
export function verifyRetainedSuite(proof, suite) {
    const files = Object.fromEntries(
        Object.entries(proof.files).map(([name, base64]) => [name, Buffer.from(base64, 'base64')]),
    );
    const versions = files['packages/admin/versions.ts'].toString();
    const start = versions.indexOf('const ADAPTER_SOURCE_PATHS = [');
    const end = versions.indexOf(']\n', start);
    if (start < 0 || end < 0) throw new Error('Pinned adapter inventory declaration is missing.');
    const adapters = Array.from(versions.slice(start, end).matchAll(/"([^"\r\n]+)"/g), (match) => match[1]);
    const probeFiles = proof.paths.filter((path) => /^packages\/probes\/[^/]+\.probe\.ts$/.test(path)).sort(comparePinnedStrings);
    const definitionFiles = proof.paths
        .filter((path) => /^packages\/probe-defs\/src\/[^/]+\.ts$/.test(path) && !/\.(test|spec)\.ts$/.test(path))
        .sort(comparePinnedStrings);
    const packagePath = 'packages/terminfo.dev/package.json';
    const paths = [...probeFiles, ...definitionFiles, 'packages/probes/vitest.config.ts', ...adapters, packagePath];
    if (JSON.stringify(paths) !== JSON.stringify(suite.sourcePaths))
        throw new Error('Recorded suite inventory differs from the retained source declaration.');
    const features = JSON.parse(files['content/features.json']),
        hashes = {};
    for (const [id, feature] of Object.entries(features))
        if (!id.startsWith('$'))
            hashes[id] = sha(canonical(Object.fromEntries(Object.entries(feature).filter(([key]) => key !== 'body'))));
    const ids = Object.keys(hashes).sort(comparePinnedStrings);
    if (
        JSON.stringify(ids) !== JSON.stringify(suite.catalogIds) ||
        JSON.stringify(hashes) !== JSON.stringify(suite.catalogEntrySha256)
    )
        throw new Error('Recorded catalogue identity differs from retained feature bytes.');
    const version = JSON.parse(files[packagePath]).version;
    if (version !== suite.adapterVersion) throw new Error('Retained adapter version differs.');
    if (pinnedSuiteHash(paths, files, packagePath, version, ids, hashes) !== suite.probeHash)
        throw new Error('Retained source does not reproduce the measured suite fingerprint.');
}

/** Verify source Git objects, bundle, patch and the executed suite independently of declarations. */
export function verifyCommonCoreSource(directory, source, metadataPath, subjects) {
    const verification = fileURLToPath(new URL('./verify-common-core-source.py', import.meta.url));
    const proof = JSON.parse(
        execFileSync(
            pythonCommand,
            [
                verification,
                join(directory, 'upstream-source.zip'),
                join(directory, 'source-proof.zip'),
                join(directory, 'native-adapter.bundle'),
                join(directory, 'native-adapter.patch.gz'),
                source.baseRevision,
                source.adapterRevision,
                metadataPath,
                commonCoreGit(),
            ],
            { timeout: 120000, maxBuffer: 16 * 1024 * 1024, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] },
        ),
    );
    for (const subject of subjects) verifyRetainedSuite(proof, JSON.parse(subject.sourceBytes).suite);
    verifyIdentityChecker(proof, source, subjects);
    proof.runtime = derivePinnedRuntime(proof, subjects);
    verifyRuntimeClaims(proof.runtime, subjects);
    const patch = gunzipSync(readFileSync(join(directory, 'native-adapter.patch.gz')));
    if (sha(patch) !== source.adapterPatchSha256) throw new Error('Retained patch content identity differs.');
    if (readFileSync(join(directory, 'native-adapter.patch'), 'utf8').replaceAll('\r\n', '\n') !== patch.toString())
        throw new Error('Readable patch projection differs from retained patch bytes.');
    return proof;
}

/** Reproduce the pinned byte-order hash after its inventory and catalogue have been verified. */
function pinnedSuiteHash(paths, files, packagePath, version, ids, hashes) {
    // Upstream mandates MD5 for this non-security suite label; SHA-256 artifact pins and Git-bound source verification protect evidence integrity.
    const hash = createHash('md5'); // NOSONAR — exact upstream fingerprint compatibility, never authentication or an integrity authority.
    hash.update('catalogIds');
    hash.update(JSON.stringify(ids));
    hash.update(JSON.stringify(hashes));
    for (const path of paths)
        if (path !== packagePath) {
            if (!files[path]) throw new Error('Retained source omits ' + path);
            hash.update(path);
            hash.update(files[path]);
        }
    hash.update('adapterVersion');
    hash.update(version);
    return hash.digest('hex').slice(0, 12);
}

/** Bind every identity receipt to the checker bytes verified against the retained Git tree. */
export function verifyIdentityChecker(proof, source, subjects) {
    const path = 'packages/terminfo.dev/src/identity-guard.ts';
    if (typeof proof.files[path] !== 'string') throw new Error('Retained identity checker is missing.');
    const expected = sha(Buffer.from(proof.files[path], 'base64'));
    if (source.identityGuardSha256 !== expected) throw new Error('Identity-checker source claim differs from retained bytes.');
    for (const subject of subjects) {
        if (JSON.parse(subject.identityBytes).checkerSha256 !== expected) {
            throw new Error('Identity receipt used a different retained checker.');
        }
    }
}
