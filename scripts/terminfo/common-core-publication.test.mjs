import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const root = fileURLToPath(new URL('../../', import.meta.url));
for (const [label, args] of [
    ['the refreshed comparison and site projection', ['--check']],
    ['the November 11 comparison and focused retest', ['--check', '--historical']],
]) {
    test(label + ' rederives from retained upstream runs and native captures', () => {
        const output = execFileSync(
            process.execPath,
            [resolve(root, 'scripts/terminfo-common-core-publication.mjs'), ...args],
            { cwd: root, encoding: 'utf8', timeout: 120000 },
        );
        assert.match(output, /verified: 80 requirements, 3 applications/);
    });
}
