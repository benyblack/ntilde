import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { adaptCollector, upstreamReceipt, validateCollectorReceipt } from './collector.mjs';

test('refuses a different collector version or modified original', () => {
    assert.throws(() => adaptCollector('program.parse();', '/test/owner.mjs'), /Unsupported collector bundle/);
});

const bundle = fileURLToPath(new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url));

test('validates the pinned receipt against the actual bundle', { skip: !existsSync(bundle) }, () => {
    const source = readFileSync(bundle);
    assert.doesNotThrow(() => validateCollectorReceipt(source, upstreamReceipt));
    assert.throws(() => validateCollectorReceipt(Buffer.concat([source, Buffer.from('\n')]), upstreamReceipt), /Collector receipt/);
    assert.throws(() => validateCollectorReceipt(source, null), /Collector receipt/);
});

for (const [field, value] of [
    ['schemaVersion', 2],
    ['probeHash', 'a'.repeat(12)],
    ['collectorRevision', 'b'.repeat(40)],
    ['manifestSha256', 'c'.repeat(64)],
    ['bundleSha256', 'd'.repeat(64)],
]) {
    test(`rejects a stale or modified receipt ${field} with the original bundle intact`, { skip: !existsSync(bundle) }, () => {
        assert.throws(() => validateCollectorReceipt(readFileSync(bundle), { ...upstreamReceipt, [field]: value }), /Collector receipt/);
    });
}

// The package is optional tooling, not a source dependency. The live smoke run
// also exercises this seam against the installed, digest-checked bundle.
test('retains upstream gates and grading in the pinned bundle', { skip: !existsSync(bundle) }, () => {
    const original = readFileSync(bundle, 'utf8');
    const adapted = adaptCollector(original, fileURLToPath(new URL('./ownership.mjs', import.meta.url)));
    const syntax = spawnSync(process.execPath, ['--check', '--input-type=module'], { input: adapted, encoding: 'utf8' });
    assert.equal(syntax.status, 0, syntax.stderr);
    assert.ok(adapted.includes('ownedTerminalVerifiedFor(options.ownedTerminal, options.captureRunId ?? "", out)'));
    assert.ok(adapted.includes('reason: "policy-refused"'));
    assert.ok(adapted.includes('verifiedTerminalOwners.set(owner, { captureRunId, out })'));
    assert.ok(adapted.includes('export async function collectNtildeRun'));
    assert.ok(!adapted.includes('program.parse();'));
    // The actual probe definitions and validation code, before the adapter's
    // insertion point in collectProbeRun, must be byte-for-byte preserved.
    const start = original.indexOf('// src/probes/unified.ts');
    const end = original.indexOf('  let ownedTerminal;\n  if (clipboardReceipt && options.terminalAppOwner)');
    assert.ok(start > 0 && end > start);
    assert.ok(adapted.includes(original.slice(start, end)));
});
