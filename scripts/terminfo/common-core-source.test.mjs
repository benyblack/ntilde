import test, { before } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, copyFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { verifyCommonCoreSource, verifyRetainedSuite } from './common-core-source.mjs';
import { pythonCommand } from './common-core-evidence.mjs';
const directory = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-11-common-core-comparison/', import.meta.url));
const source = JSON.parse(readFileSync(join(directory, 'upstream-source.json')));
const metadataPath = join(directory, 'ntilde/source.json'),
    sourceBytes = readFileSync(metadataPath),
    suite = JSON.parse(sourceBytes).suite;
let proof;
before(() => {
    proof = verifyCommonCoreSource(directory, source, metadataPath, [{ sourceBytes }]);
});
test('retained source reproduces the commit/tree, bundle, patch and executed suite identity', () => {
    assert.equal(proof.head, source.adapterRevision);
    verifyRetainedSuite(proof, suite);
});
test('suite inventory, catalogue content and fingerprint cannot be substituted', () => {
    const inventory = structuredClone(suite);
    inventory.sourcePaths.pop();
    assert.throws(() => verifyRetainedSuite(proof, inventory), /inventory/);
    const fingerprint = structuredClone(suite);
    fingerprint.probeHash = '0'.repeat(12);
    assert.throws(() => verifyRetainedSuite(proof, fingerprint), /fingerprint/);
    const changed = structuredClone(proof),
        features = JSON.parse(Buffer.from(changed.files['content/features.json'], 'base64'));
    const id = Object.keys(features).find((id) => !id.startsWith('$'));
    features[id].sequence = 'substituted';
    changed.files['content/features.json'] = Buffer.from(JSON.stringify(features)).toString('base64');
    assert.throws(() => verifyRetainedSuite(changed, suite), /catalogue/);
});
for (const [name, filename, mode] of [
    ['source bytes', 'upstream-source.zip', 'source'],
    ['Git object', 'source-proof.zip', 'object'],
    ['bundle head', 'native-adapter.bundle', 'bundle'],
    ['patch bytes', 'native-adapter.patch.gz', 'patch'],
]) {
    test('a self-authored proof flag cannot admit substituted ' + name, (t) => {
        const temporary = mkdtempSync(join(tmpdir(), 'common-core-proof-test-'));
        t.after(() => rmSync(temporary, { recursive: true, force: true }));
        for (const file of [
            'upstream-source.zip',
            'source-proof.zip',
            'native-adapter.bundle',
            'native-adapter.patch.gz',
            'native-adapter.patch',
        ])
            copyFileSync(join(directory, file), join(temporary, file));
        const path = join(temporary, filename);
        if (mode === 'bundle') {
            const data = readFileSync(path),
                at = data.indexOf(source.adapterRevision + ' HEAD');
            assert.ok(at >= 0);
            Buffer.from('0'.repeat(40)).copy(data, at);
            writeFileSync(path, data);
        } else
            execFileSync(
                pythonCommand,
                [
                    '-c',
                    `import sys,zipfile,gzip
p,mode=sys.argv[1:]
if mode=='patch':
    open(p,'wb').write(gzip.compress(b'not the Git diff',mtime=0))
else:
    with zipfile.ZipFile(p) as z:
        names=z.namelist();data={n:z.read(n) for n in names};comment=z.comment
    target='packages/terminfo.dev/src/serve.ts' if mode=='source' else next(n for n in names if n.startswith('objects/'))
    data[target]+=b'changed'
    with zipfile.ZipFile(p,'w',zipfile.ZIP_DEFLATED) as z:
        z.comment=comment
        for n in names:z.writestr(n,data[n])`,
                    path,
                    mode,
                ],
                { stdio: ['ignore', 'pipe', 'pipe'] },
            );
        assert.throws(() =>
            verifyCommonCoreSource(temporary, { ...source, probeDefinitionsUnchanged: true }, metadataPath, [
                { sourceBytes },
            ]),
        );
    });
}
