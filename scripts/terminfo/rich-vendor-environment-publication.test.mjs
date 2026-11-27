import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { assessEnvironment, families, neutralName, parentValues, parseChildOutput, snapshotEnvironment } from './rich-vendor-environment.mjs';
import { buildPublication, pinnedCriteria, readableReport, verifyVendorEnvironmentRun, withRetainedRun } from './rich-vendor-environment-results.mjs';

const publicationRoot = new URL('../../docs/qa/terminfo/2026-11-27-rich-vendor-environment/', import.meta.url);
const readPublication = file => readFileSync(new URL(file, publicationRoot), 'utf8').replaceAll('\r\n', '\n');
const manifest = JSON.parse(readPublication('manifest.json'));
const expectedIds = ['device.env-lc-terminal', 'device.env-kitty', 'device.env-wezterm', 'device.env-wt-session'];
const childFor = control => ({ ...parentValues(control), TERM_PROGRAM: { state: 'present', value: 'Ntilde' } });

test('verifies the four manual catalogue criteria against the pinned bundle and stock baseline', () => {
    const rows = pinnedCriteria();
    assert.deepEqual(rows.map(row => row.id), expectedIds);
    assert.ok(rows.every(row => row.catalogueProbeStatus === 'manual' && row.pinnedVerdict === 'unprobed' && row.stock === null));
});

test('rederives all four applicability decisions with exact ancestry and both controls', () => {
    const publication = buildPublication();
    assert.equal(readPublication('report.json'), JSON.stringify(publication, null, 2) + '\n');
    assert.equal(readPublication('report.md'), readableReport(publication));
    assert.deepEqual(publication.rows.map(row => row.applicability), Array(4).fill('not-applicable'));
    for (const { variables } of publication.rows) {
        for (const samples of Object.values(variables)) {
            assert.equal(samples.clean.child.state, 'absent');
            assert.equal(samples.inherited.origin, 'inherited');
            assert.deepEqual(samples.inherited.child, samples.inherited.input);
        }
    }
    for (const run of publication.runs) {
        assert.equal(run.child.ppid, run.app.pid);
        assert.ok(run.fixtureRemoved);
    }
    assert.equal(publication.controls.TERM_PROGRAM.inherited.origin, 'app-provided');
    assert.equal(publication.controls[neutralName].clean.origin, 'inherited');
});

test('projects only the allowlist and distinguishes empty and absent; admits neither extra names nor fields', () => {
    const observed = snapshotEnvironment({ LC_TERMINAL: '', UNRELATED: 'fixture-only' });
    const frame = value => JSON.stringify(value) + '\n';
    assert.deepEqual(observed.LC_TERMINAL, { state: 'empty', value: '' });
    assert.deepEqual(observed.LC_TERMINAL_VERSION, { state: 'absent', value: null });
    assert.deepEqual(parseChildOutput(frame(observed)), observed);
    assert.throws(() => parseChildOutput(frame({ ...observed, UNRELATED: 'fixture-only' })), /unexpected environment names/);
    assert.throws(() => parseChildOutput(frame({ ...observed, LC_TERMINAL: { ...observed.LC_TERMINAL, extra: true } })), /unexpected fields/);
});

test('requires investigation for a new value, an empty key, or a changed seeded value in just that family', () => {
    const clean = childFor('clean'), seeded = childFor('inherited');
    for (const { id, names: [name] } of families) {
        const cases = [
            assessEnvironment({ ...clean, [name]: { state: 'present', value: 'fixture-injection' } }, seeded),
            assessEnvironment({ ...clean, [name]: { state: 'empty', value: '' } }, seeded),
            assessEnvironment(clean, { ...seeded, [name]: clean[name] }),
            assessEnvironment(clean, { ...seeded, [name]: { state: 'present', value: 'fixture-replacement' } }),
        ];
        for (const { rows } of cases) {
            assert.deepEqual(rows.filter(row => row.applicability === 'requires-investigation').map(row => row.id), [id]);
        }
    }
});

test('refuses to interpret the pair if either independent control fails', () => {
    const clean = childFor('clean'), seeded = childFor('inherited');
    assert.throws(() => assessEnvironment(clean, { ...seeded, TERM_PROGRAM: parentValues('inherited').TERM_PROGRAM }), /control failed/);
    assert.throws(() => assessEnvironment({ ...clean, [neutralName]: seeded[neutralName] }, seeded), /control failed/);
});

test('rejects tampered child output, ancestry, or release and refuses an error-marked run', () => {
    withRetainedRun(manifest.runs[0], scratch => {
        const verify = () => verifyVendorEnvironmentRun(scratch);
        function rejectMutation(file, mutate, reason) {
            const path = join(scratch, file), original = readFileSync(path);
            try {
                writeFileSync(path, mutate(original.toString()));
                assert.throws(verify, reason);
            } finally {
                writeFileSync(path, original);
            }
        }
        rejectMutation('child-environment.json', raw => raw.replace('"Ntilde"', '"invalid"'), /receipt does not rederive/);
        rejectMutation('ownership-proof.json', raw => raw.replace(/("worker": \{[^}]*"ppid": )\d+/, (_match, prefix) => prefix + '1'), /missing direct-local ownership/);
        rejectMutation('desktop-lease.jsonl', raw => raw.trimEnd().split('\n').slice(0, -1).join('\n'), /lease did not verify and release/);
        writeFileSync(join(scratch, 'cleanup-failure.json'), '{}');
        assert.throws(verify, /capture or cleanup failed/);
    });
});
