import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, readdirSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { publishCommonCoreJson } from './common-core-handshake.mjs';
test('only complete private records appear under the polled name, and temporary files are removed', (t) => {
    const directory = mkdtempSync(join(tmpdir(), 'common-core-handshake-'));
    t.after(() => rmSync(directory, { recursive: true, force: true }));
    const value = { runId: 'test-only', data: 'x'.repeat(100000) };
    publishCommonCoreJson(directory, 'ready.json', value);
    assert.deepEqual(JSON.parse(readFileSync(join(directory, 'ready.json'))), value);
    assert.deepEqual(readdirSync(directory), ['ready.json']);
    if (process.platform !== 'win32') assert.equal(statSync(join(directory, 'ready.json')).mode & 0o777, 0o600);
});
test('atomic publication retains exclusive creation and refuses traversal or unserializable input', (t) => {
    const directory = mkdtempSync(join(tmpdir(), 'common-core-handshake-'));
    t.after(() => rmSync(directory, { recursive: true, force: true }));
    writeFileSync(join(directory, 'ready.json'), 'original');
    assert.throws(() => publishCommonCoreJson(directory, 'ready.json', { changed: true }), { code: 'EEXIST' });
    assert.equal(readFileSync(join(directory, 'ready.json'), 'utf8'), 'original');
    assert.deepEqual(readdirSync(directory), ['ready.json']);
    assert.throws(() => publishCommonCoreJson(directory, '../escape.json', {}), /Invalid/);
    const circular = {};
    circular.self = circular;
    assert.throws(() => publishCommonCoreJson(directory, 'bad.json', circular));
    assert.deepEqual(readdirSync(directory), ['ready.json']);
});
