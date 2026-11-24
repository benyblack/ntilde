import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { verifyChildEnvironmentRun } from './modern-child-environment-owned.mjs';
import { buildModernChildEnvironmentPublication, childEnvironmentPublication, leaseReleasedAt,
    modernChildEnvironmentOutputPaths, readableModernChildEnvironment } from './modern-child-environment-results.mjs';
import { gradeEnvironment, ids, parseChildOutput, snapshotEnvironment } from './modern-child-environment.mjs';
import { modernRoot, serializeModern } from './modern-results.mjs';

test('rederives the published 69 rows and preserves all non-environment rows', () => {
    const report = buildModernChildEnvironmentPublication();
    const paths = modernChildEnvironmentOutputPaths(modernRoot);
    const previous = JSON.parse(readFileSync(join(modernRoot, report.predecessor.publication, 'report.json')));
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableModernChildEnvironment(report));
    assert.equal(report.requirementsCount, 69);
    assert.deepEqual(report.totals, { pass: 59, fail: 0, inconclusive: 10 });
    assert.deepEqual(report.predecessor.totals, previous.totals);
    const changed = report.rows.filter((row, index) => JSON.stringify(row) !== JSON.stringify(previous.rows[index]));
    assert.deepEqual(new Set(changed.map(row => row.id)), new Set(ids));
    for (const row of changed) {
        const old = previous.rows.find(item => item.id === row.id);
        assert.equal(row.requirement, old.requirement);
        assert.equal(row.probeStatus, 'manual');
        assert.equal(row.stockVerdict, old.stockVerdict);
    }
    const verdicts = new Map(changed.map(row => [row.id, row.conclusion]));
    assert.equal(verdicts.get('device.env-no-color'), 'pass');
    assert.equal(verdicts.get('device.env-vte-version'), 'inconclusive');
});

test('distinguishes absent and empty values and refuses unrelated environment data', () => {
    const observed = snapshotEnvironment({ COLORTERM: '', TERM_PROGRAM: 'Ntilde', IGNORED: 'fixture-only' });
    assert.deepEqual(observed.COLORTERM, { state: 'empty', value: '' });
    assert.deepEqual(observed.NO_COLOR, { state: 'absent', value: null });
    assert.deepEqual(parseChildOutput(JSON.stringify(observed) + '\n'), observed);
    assert.throws(() => parseChildOutput(JSON.stringify({ ...observed, IGNORED: 'fixture-only' }) + '\n'), /unexpected environment names/);
    assert.throws(() => parseChildOutput(JSON.stringify({ ...observed, COLORTERM: { state: 'absent', value: '' } }) + '\n'), /invalid child value state/);
});

test('grades unsupported identity and a stripped user preference independently', () => {
    const clean = snapshotEnvironment({ COLORTERM: 'truecolor', TERM_PROGRAM: 'Ntilde' });
    const inherited = snapshotEnvironment({ COLORTERM: 'parent-color', TERM_PROGRAM: 'parent-program', NO_COLOR: '' });
    const results = gradeEnvironment(clean, inherited);
    assert.deepEqual(results.map(row => row.conclusion), ['fail', 'fail', 'fail', 'inconclusive']);
});

test('refuses changed launch, child, lineage and lease records', () => {
    const directory = join(modernRoot, childEnvironmentPublication);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-modern-env-refusal-'));
    try {
        const manifest = JSON.parse(readFileSync(join(directory, 'manifest.json')));
        const [entry] = manifest.runs;
        extractOwnedArchive(directory, { archives: { 'owned-run.zip': entry.sha256 } }, scratch, join(directory, entry.archive));
        const verify = () => verifyChildEnvironmentRun(scratch);
        const { context, ownership } = verify();
        const rejectMutation = (file, mutate, check, reason) => {
            const path = join(scratch, file), original = readFileSync(path);
            try {
                writeFileSync(path, mutate(original.toString()));
                assert.throws(check, reason);
            } finally {
                writeFileSync(path, original);
            }
        };
        rejectMutation('launch-inputs.json', raw => raw.replace('"clean"', '"inherited"'), verify, /invalid controlled launch/);
        rejectMutation('child-environment.json', raw => raw.replace('truecolor', '24bit'), verify, /receipt does not rederive/);
        rejectMutation('child-pane.json', raw => raw.replace(/"workerPid": \d+/, '"workerPid": 1'), verify, /missing direct-local ownership/);
        rejectMutation('desktop-lease.jsonl', raw => raw.trimEnd().split('\n').slice(0, -1).join('\n'),
            () => leaseReleasedAt(scratch, context, ownership), /desktop lease did not verify and release/);
        writeFileSync(join(scratch, 'failure.json'), '{}');
        assert.throws(verify, /capture or cleanup failed/);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
});
