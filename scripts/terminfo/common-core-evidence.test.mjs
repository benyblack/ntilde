import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, symlinkSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import {
    verifiedEvidenceFile,
    verifyCommonCoreArchive,
    readCommonCoreRaw,
    sha256,
    pythonCommand,
} from './common-core-evidence.mjs';

/** Isolate filesystem fixtures and clean them after each test. */
function temporary(t) {
    const directory = mkdtempSync(join(tmpdir(), 'common-core-evidence-'));
    t.after(() => rmSync(directory, { recursive: true, force: true }));
    return directory;
}
/** Write a small synthetic archive for retention refusal tests. */
function archive(directory, members) {
    const path = join(directory, 'run.zip');
    execFileSync(
        pythonCommand,
        [
            '-c',
            `import zipfile,json,sys
with zipfile.ZipFile(sys.argv[1], 'w') as z:
    for name,content in json.loads(sys.argv[2]): z.writestr(name, content)`,
            path,
            JSON.stringify(members),
        ],
        { stdio: ['ignore', 'pipe', 'pipe'] },
    );
    return path;
}
test('evidence paths are bound to exact retained bytes', (t) => {
    const directory = temporary(t),
        bytes = Buffer.from('retained evidence');
    writeFileSync(join(directory, 'run.json'), bytes);
    assert.deepEqual(verifiedEvidenceFile(directory, { path: 'run.json', sha256: sha256(bytes) }).bytes, bytes);
    assert.throws(
        () => verifiedEvidenceFile(directory, { path: 'run.json', sha256: '0'.repeat(64) }),
        /digest differs/,
    );
    assert.throws(() => verifiedEvidenceFile(directory, { path: 'run.json', sha256: null }), /Invalid/);
});
test('evidence traversal and symlink escapes are refused', (t) => {
    const directory = temporary(t),
        outside = temporary(t),
        bytes = Buffer.from('outside');
    writeFileSync(join(outside, 'outside.json'), bytes);
    try {
        symlinkSync(join(outside, 'outside.json'), join(directory, 'link.json'));
    } catch (error) {
        if (process.platform === 'win32' && error.code === 'EPERM')
            return t.skip('Windows runner does not allow file symlinks');
        throw error;
    }
    assert.throws(() => verifiedEvidenceFile(directory, { path: 'link.json', sha256: sha256(bytes) }), /escapes/);
    assert.throws(
        () => verifiedEvidenceFile(directory, { path: join(outside, 'outside.json'), sha256: sha256(bytes) }),
        /escapes|Invalid/,
    );
});
test('every cited screenshot must retain its exact digest-addressed bytes', (t) => {
    const directory = temporary(t),
        image = 'test bytes, not a measured image',
        hash = sha256(Buffer.from(image));
    const raw = Buffer.from(JSON.stringify({ screenshotRefs: ['sha256:' + hash] }));
    const path = archive(directory, [
        ['raw.json', raw.toString()],
        [`artifacts/${hash}.png`, image],
    ]);
    assert.equal(verifyCommonCoreArchive(path, raw)[`artifacts/${hash}.png`], hash);
    archive(directory, [
        ['raw.json', raw.toString()],
        [`artifacts/${hash}.png`, 'altered'],
    ]);
    assert.throws(() => verifyCommonCoreArchive(path, raw), /Screenshot bytes/);
    archive(directory, [['raw.json', '{}']]);
    assert.throws(() => verifyCommonCoreArchive(path, raw), /different raw run/);
});
test('duplicate and unsafe archive entries cannot hide substituted evidence', (t) => {
    const directory = temporary(t),
        raw = Buffer.from('{}');
    for (const members of [
        [
            ['raw.json', '{}'],
            ['raw.json', 'changed'],
        ],
        [
            ['raw.json', '{}'],
            ['../escape', 'bad'],
        ],
    ]) {
        const path = archive(directory, members);
        assert.throws(() => verifyCommonCoreArchive(path, raw));
    }
});

test('frame-level references cannot bypass the run screenshot index or retained bytes', (t) => {
    const directory = temporary(t),
        image = 'test-only bytes',
        hash = sha256(Buffer.from(image));
    const raw = Buffer.from(
        JSON.stringify({ screenshotRefs: [], results: [{ frames: [{ kind: 'pixels', ref: 'sha256:' + hash }] }] }),
    );
    const path = archive(directory, [
        ['raw.json', raw.toString()],
        [`artifacts/${hash}.png`, image],
    ]);
    assert.deepEqual(readCommonCoreRaw(path), raw);
    assert.throws(() => verifyCommonCoreArchive(path, raw), /absent from the run index/);
});

test('completed-looking raw data cannot hide a retained parent validation failure', (t) => {
    const directory = temporary(t),
        raw = Buffer.from('{}'),
        path = archive(directory, [
            ['raw.json', '{}'],
            ['parent-error.json', '{"message":"source changed"}'],
        ]);
    assert.throws(() => verifyCommonCoreArchive(path, raw), /parent or worker failure/);
});
