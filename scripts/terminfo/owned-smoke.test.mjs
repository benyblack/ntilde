// Lifecycle checks. A real app is used only when explicitly selected by the caller.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const prepared = process.platform === 'darwin' && existsSync(resolve(repo, 'artifacts/terminfo/tool/package-lock.json'));

function launch(executable, interrupt) {
    return new Promise((resolve, reject) => {
        const child = spawn(process.execPath, ['scripts/terminfo-owned.mjs', '--executable', executable, '--app-version', '0.0.0'], {
            cwd: repo, stdio: ['ignore', 'pipe', 'pipe'],
        });
        let output = '';
        let errors = '';
        let sent = false;
        const timer = setTimeout(() => { child.kill('SIGTERM'); reject(new Error('Adapter lifecycle did not finish.')); }, 15000);
        child.on('error', reject);
        child.stdout.on('data', chunk => {
            output += chunk;
            if (interrupt && !sent && output.includes('results:')) {
                sent = true;
                child.kill('SIGINT');
            }
        });
        child.stderr.on('data', chunk => { errors += chunk; });
        child.on('exit', code => { clearTimeout(timer); resolve({ code, output, errors }); });
    });
}

function assertCleaned(result) {
    assert.equal(result.code, 1, result.output + result.errors);
    const match = /pid (\d+); results: ([^\n]+)/.exec(result.output);
    assert.ok(match, result.output + result.errors);
    const pid = Number(match[1]);
    const directory = match[2];
    const settings = JSON.parse(readFileSync(resolve(directory, 'initial-settings.json')));
    const scratch = dirname(settings.Profiles[0].Command);
    assert.ok(!existsSync(scratch), 'Private app-data and owner socket must be removed.');
    assert.throws(() => process.kill(pid, 0), /ESRCH/, 'Only the launched app should have been terminated.');
    const proof = JSON.parse(readFileSync(resolve(directory, 'ownership-proof.json')));
    assert.equal(proof.completed, false);
    assert.ok(existsSync(resolve(directory, 'failure.json')));
}

test('an early app exit fails promptly and removes the private fixture', { skip: !prepared }, async () => {
    const result = await launch('/usr/bin/true', false);
    assert.match(result.errors, /exited before collection/);
    assertCleaned(result);
});

test('interrupting a launched Ntilde instance tears it down', {
    skip: !prepared || !process.env.NTILDE_TERMINFO_TEST_EXECUTABLE,
}, async () => {
    const result = await launch(process.env.NTILDE_TERMINFO_TEST_EXECUTABLE, true);
    assert.match(result.errors, /Interrupted by SIGINT/);
    assertCleaned(result);
});
