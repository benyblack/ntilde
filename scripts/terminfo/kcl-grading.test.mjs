import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildKclGradingReport, validateKclEvidence } from './kcl-grading.mjs';
import { kclContracts, kclSuiteId } from './kcl-contracts.mjs';
import { kclRecordingContext } from './kcl-collector.mjs';
import { syntheticKclEvidence } from './fixtures/kcl-evidence.mjs';
import { paletteBinding } from './fixtures/col-edit-evidence.mjs';

const osc21 = (...fields) => `\x1b]21;${fields.join(';')}\x1b\\`;
const build = (options, extra) => {
    const { frames, run, runId, adapter } = syntheticKclEvidence(options);
    return buildKclGradingReport(frames, run, runId, adapter, extra);
};
const onlyPinned = replies => (caseId, role, read) => (role === 'pinned' ? { ...read, replies } : null);
const refuses = (mutate, pattern) => {
    const evidence = syntheticKclEvidence();
    mutate(evidence);
    assert.throws(() => buildKclGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), pattern);
};

test('a complete synthetic run derives the report: pinned supported, effect supported, no divergence', () => {
    const report = build();
    assert.equal(report.suiteId, kclSuiteId);
    assert.deepEqual(report.cases.map(item => item.caseId), kclContracts.map(item => item.caseId));
    const [item] = report.cases;
    assert.equal(item.conclusion.outcome, 'supported');
    assert.equal(item.conclusion.reason, null);
    assert.equal(item.effectContract.outcome, 'supported');
    assert.equal(item.divergence, undefined);
    assert.equal(item.pinned.termless.observation.evidence, 'query');
    assert.equal(item.pinned.terminal.response, osc21('foreground=rgb:cc/cc/cc'), 'the terminal path grades the matched frame');
    assert.equal(item.pinned.termless.assertions[0].kind, 'positive');
    assert.ok(item.observed.groups.pinned.matches && item.observed.groups.pinned.unchanged);
    assert.deepEqual(Object.keys(item.observed.groups), kclContracts[0].groups.map(group => group.role));
    assert.equal(item.observed.baseline.selection_foreground, '');
    assert.deepEqual(item.reads.filter(read => read.kind === 'report').length, kclContracts[0].groups.length);
    assert.equal(report.restoredSnapshot, report.snapshots.length - 1);
});

test('the real replay of the pinned termless code over the journal agrees with the recorded result', () => {
    const replayModule = { ntildeRunKclTermless: (probeId, ctx) => ({ probeId, ...kclContractsReplay(ctx) }) };
    assert.doesNotThrow(() => build({}, { replayModule }));
    const lying = { ntildeRunKclTermless: (probeId, ctx) => ({ probeId, ...kclContractsReplay(ctx), pass: false }) };
    assert.throws(() => build({}, { replayModule: lying }), /replay does not reproduce the live grading/);
    const skipping = { ntildeRunKclTermless: probeId => ({ probeId, ...kclContractsReplay(kclRecordingContext(kclContracts[0], { barrier: '', replies: [osc21('foreground=rgb:cc/cc/cc')] })) }) };
    assert.throws(() => build({}, { replayModule: skipping }), /different number of calls/);
});
function kclContractsReplay(ctx) {
    const replies = ctx.feedCapture('\x1b]21;foreground=?\x1b\\');
    return { pass: true, response: replies, note: 'Foreground query recognized; setting colors and visible rendering were not tested',
        observation: { outcome: 'supported', evidence: 'query', note: 'Foreground query recognized; setting colors and visible rendering were not tested' },
        assertions: [{ kind: 'positive', expected: 'OSC 21 foreground query returns RGB or an explicitly undefined value', observed: replies }] };
}

test('a wrong key reply becomes an unsupported effect beside an unchanged supported pinned verdict', () => {
    const wrong = (caseId, role, read) => (role === 'key-background' ? { ...read, replies: [osc21('background=rgb:11/22/34')] } : null);
    const [item] = build({ mutate: wrong }).cases;
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'supported');
    assert.equal(item.divergence.kind, 'pinned-supported-effect-unsupported');
    assert.equal(item.observed.groups['key-background'].matches, false);
});

test('a pinned foreground answered with a bad value is inconclusive, recorded beside an unsupported effect', () => {
    const [item] = build({ mutate: onlyPinned([osc21('foreground=red')]) }).cases;
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.pinned.termless.observation.note, 'OSC 21 replied without one verifiable foreground value; no support conclusion');
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.equal(item.divergence.kind, 'pinned-inconclusive-effect-unsupported');
});

test('a silent pinned request is inconclusive with the pinned no-response result on both paths', () => {
    const [item] = build({ mutate: onlyPinned([]) }).cases;
    assert.equal(item.conclusion.reason, 'no-response');
    assert.equal(item.pinned.termless.response, '');
    assert.equal(item.pinned.terminal.response, '\x1b[?62;4;22c', 'the terminal path saw only the DA barrier');
    assert.equal(item.pinned.terminal.observation.reason, 'no-response');
    assert.equal(item.effectContract.outcome, 'unsupported');
});

test('refuses tampered evidence: restoration, binding, adapter identity, pinned results, reports and frames', () => {
    let evidence = syntheticKclEvidence();
    evidence.frames.at(-1).sha256 = '0'.repeat(64);
    assert.throws(() => validateKclEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Changed kitty-color snapshot evidence/);
    const recolored = syntheticKclEvidence({ finishBinding: paletteBinding({ background: 0xff010203 }) });
    assert.throws(() => validateKclEvidence(recolored.frames, recolored.run, recolored.runId, recolored.adapter), /fixture was not restored|palette or theme/);
    evidence = syntheticKclEvidence();
    evidence.adapter.id = 'other';
    assert.throws(() => validateKclEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Invalid kitty-color adapter identity/);
    evidence = syntheticKclEvidence();
    evidence.adapter.derivedFromAdapterId = 'other';
    assert.throws(() => validateKclEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Invalid kitty-color adapter identity/);
    evidence = syntheticKclEvidence();
    evidence.run.probeIds = ['other'];
    assert.throws(() => validateKclEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /does not cover the contracted cases/);
    refuses(({ run }) => { run.cases[0].pinned.termless.observation.reason = 'no-response'; }, /does not match its observed evidence/);
    refuses(({ run }) => { run.cases[0].pinned.terminal.response = 'edited'; }, /does not match its observed evidence/);
    refuses(({ run }) => { run.cases[0].pinned.terminal.pass = false; }, /does not match its observed evidence/);
    refuses(({ run }) => { delete run.cases[0].pinned.terminal; }, /Missing pinned/);
    refuses(({ run }) => { run.cases[0].pinned.termless.probeId = 'other'; }, /Missing pinned/);
    refuses(({ frames }) => { frames[5].report.replies = [osc21('background=rgb:00/00/00')]; }, /replies differ from their read frame/);
    refuses(({ frames }) => { frames[0].report = { barrier: '\x1b[?62;4;22c', replies: [] }; }, /Unexpected kitty-color report frame|Changed/);
    refuses(({ frames }) => { frames[2].report = { barrier: '\x1b[?62;4;22c', replies: [] }; }, /Unexpected .* report on a cells read|replies differ|Changed/);
    refuses(({ run }) => { run.cases[0].feeds[2] = 'edited'; }, /do not match the pinned contract/);
    refuses(({ run }) => { run.cases[0].reads[1].kind = 'cells'; }, /Wrong .* read kind/);
    refuses(({ run }) => { run.cases[0].barriers[1] = '\x1b[?1;1c'; }, /barrier differs from its read frame/);
    refuses(({ frames }) => { frames[1].screen.lines[0] = 'NTILDE_KC_other'; }, /Changed|not bound to this run/);
});

test('a stray or cross-case reply never reaches the pinned replay: it is the reply of the pinned read only', () => {
    const stray = (caseId, role, read) => (role === 'key-foreground' ? { ...read, replies: [osc21('foreground=rgb:cc/cc/cc'), ...read.replies] } : null);
    const [item] = build({ mutate: stray }).cases;
    assert.equal(item.conclusion.outcome, 'supported', 'the pinned verdict rests on the pinned exchange only');
    assert.equal(item.effectContract.outcome, 'unsupported', 'the extra reply is a count mismatch in its own group');
});
