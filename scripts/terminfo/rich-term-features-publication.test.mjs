import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { withEnvironmentArchive } from './child-environment-archives.mjs';
import { assessPair, da, neutralName, parentValues, parseChildOutput, request } from './rich-term-features.mjs';
import { verifyChildEnvironmentRun } from './rich-term-features-owned.mjs';
import { buildPublication, directory, readableReport } from './rich-term-features-results.mjs';

const text = file => readFileSync(join(directory, file), 'utf8').replaceAll('\r\n', '\n');
const frame = value => JSON.stringify(value) + '\n';
const barrier = '\x1b[?62;4;22c';
const reply = '\x1b]1337;Capabilities=T3LrMSc7UTs3BFGsGoSyHNoP\x1b\\';
function sample(control) {
    return { environment: { ...parentValues(control), TERM_PROGRAM: { state: 'present', value: 'Ntilde' } },
        preflight: barrier, sent: request + da, received: reply + barrier };
}

test('reissues both receipts and reproduces the two reports including the unchanged callback', async () => {
    const publication = await buildPublication();
    assert.equal(text('report.json'), JSON.stringify(publication, null, 2) + '\n');
    assert.equal(text('report.md'), readableReport(publication));
    assert.equal(publication.pinned.termless, null);
    assert.equal(publication.pinned.stock, 'inconclusive');
    assert.deepEqual(publication.runs.map(run => run.replay.note), ['TERM_FEATURES not advertised', 'TERM_FEATURES advertised; source unverified']);
    for (const { replay, measurement } of publication.runs) {
        assert.equal(replay.pass, false);
        assert.deepEqual(replay.observation, { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy' });
        assert.equal(measurement.child.ppid, measurement.app.pid);
    }
});

test('requires exactly one matching framed reply in both samples and the DA controls', () => {
    const original = sample('clean');
    assert.deepEqual(parseChildOutput(frame(original)), original);
    for (const changed of [{ sent: request }, { preflight: reply + barrier }, { received: reply },
        { received: reply + '\x1b[?1c' }, { received: reply + barrier + reply }]) {
        assert.throws(() => parseChildOutput(frame({ ...original, ...changed })));
    }
    for (const received of [barrier, reply + reply + barrier, reply.replace('T3', 'T3Cw') + barrier]) {
        const observed = parseChildOutput(frame({ ...original, received }));
        assert.throws(() => assessPair(observed, sample('inherited')), /one complete setting-disabled capabilities reply/);
    }
});

test('refuses incorrect inheritance or either missing independent control', () => {
    const clean = sample('clean'), inherited = sample('inherited');
    assert.equal(assessPair(clean, inherited).id, 'device.term-features');
    for (const [example, name, replacement] of [
        [clean, 'TERM_FEATURES', { value: '', state: 'empty' }],
        [inherited, 'TERM_FEATURES', clean.environment.TERM_FEATURES],
        [inherited, 'TERM_PROGRAM', parentValues('inherited').TERM_PROGRAM],
        [clean, neutralName, inherited.environment[neutralName]],
    ]) {
        const changed = { ...example, environment: { ...example.environment, [name]: replacement } };
        assert.throws(() => assessPair(example === clean ? changed : clean, example === inherited ? changed : inherited));
    }
});

test('a same-shape replacement for a retained child response breaks its receipt', () => {
    const [entry] = JSON.parse(text('manifest.json')).runs;
    withEnvironmentArchive(directory, entry, scratch => {
        const path = join(scratch, 'child-environment.json');
        writeFileSync(path, readFileSync(path, 'utf8').replace('T3Lr', 'T3CwLr'));
        assert.throws(() => verifyChildEnvironmentRun(scratch), /receipt does not rederive/);
    });
});
