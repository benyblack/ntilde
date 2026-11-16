import assert from 'node:assert/strict';
import { test } from 'node:test';
import { syntheticOsc633Evidence } from './fixtures/osc633-evidence.mjs';
import { buildOsc633GradingReport, osc633Counts } from './osc633-grading.mjs';

const report = evidence => buildOsc633GradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);

test('distinct synthetic supported and ignored worlds preserve the pinned verdict and distinguish effects', () => {
    for (const supported of [false, true]) {
        const evidence = syntheticOsc633Evidence({ supported });
        const result = report(evidence);
        assert.deepEqual(osc633Counts(result), { [`inconclusive/${supported ? 'supported' : 'unsupported'}`]: 4 });
        for (const item of result.cases) {
            assert.equal(item.pinned.termless.result.observation.outcome, 'inconclusive');
            assert.equal(item.pinned.terminal.result.observation.outcome, 'inconclusive');
            assert.ok(item.effectContract.validity.every(control => control.held));
        }
    }
});

test('a bad prior, positive control, ordinary control or wrong-marker gate prevents a conclusive grade', () => {
    const cases = [
        ['extensions.osc633-a/effect', 'prior', state => { state.status = 'prompt'; }],
        ['extensions.osc633-b/positive-control', 'positive', state => { state.accepting = false; }],
        ['extensions.osc633-c/effect', 'ordinary', state => { state.status = 'running'; }],
        ['extensions.osc633-d/gate-b', 'post', state => { state.status = 'prompt'; state.accepting = false; state.commandStart = null; state.outputStart = null; }],
    ];
    for (const [id, label, alter] of cases) {
        const evidence = syntheticOsc633Evidence({ adjust: (experiment, read, state) => { if (experiment === id && read === label) alter(state); } });
        const item = report(evidence).cases.find(value => id.startsWith(value.caseId));
        assert.equal(item.effectContract.outcome, 'inconclusive', `${id}/${label}`);
        assert.ok(item.effectContract.validity.some(control => !control.held));
    }
});

test('A may clear anchors as well as returning to the prompt without invalidating the D effect control', () => {
    const evidence = syntheticOsc633Evidence({ supported: true, adjust: (experiment, read, state) => {
        if (experiment === 'extensions.osc633-d/shared-a' && read === 'post') {
            state.accepting = false; state.commandStart = null; state.outputStart = null;
        }
    } });
    const completion = report(evidence).cases.find(item => item.caseId === 'extensions.osc633-d');
    assert.equal(completion.effectContract.outcome, 'supported');
    assert.ok(completion.effectContract.validity.every(value => value.held));
});

test('changed fixture, stale barrier, wrong pinned result, damaged frame and incomplete cleanup are refused', () => {
    const mutations = [
        e => { e.run.experiments[0].feeds.at(-1); e.run.experiments[0].feeds[e.run.experiments[0].feeds.length - 1] += 'x'; },
        e => { e.run.experiments[0].barriers[e.run.experiments[0].barriers.length - 1] = '\x1b[?9c'; },
        e => { e.run.pinned[0].termless.result.pass = true; },
        e => { e.frames[2].before.screen.lines[0] = 'bad'; },
        e => { e.frames.pop(); },
        e => { e.adapter.bundleSha256 = 'bad'; },
    ];
    for (const mutate of mutations) {
        const evidence = syntheticOsc633Evidence();
        mutate(evidence);
        assert.throws(() => report(evidence));
    }
});
