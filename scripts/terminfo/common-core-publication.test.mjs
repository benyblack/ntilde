import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const root = fileURLToPath(new URL('../../', import.meta.url));
test('the public comparison and site projection rederive from retained upstream runs and native captures', () => {
    const output = execFileSync(
        process.execPath,
        [resolve(root, 'scripts/terminfo-common-core-publication.mjs'), '--check'],
        { cwd: root, encoding: 'utf8', timeout: 120000 },
    );
    assert.match(output, /verified: 80 requirements, 3 applications/);
});
