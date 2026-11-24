import assert from 'node:assert/strict';
import test from 'node:test';
import { checkAction, cwdPlan, expectedAction, gradeCwd, readPublicPath } from './modern-osc7-cwd.mjs';

const plan = cwdPlan('0123456789abcdef0123456789abcdef');
const phases = Object.fromEntries(plan.map(step => [step.phase, step]));
const path = phase => phases[phase].path;
const reading = publicPath => ({ publicPath: readPublicPath([{ text: publicPath }]), metadataPath: publicPath });
const baseline = () => ({
    launch: { publicPath: null, metadataPath: null },
    fixture: reading(path('fixture')),
    incoming: reading(path('fixture')),
    control: reading(path('control')),
    'target-a': reading(path('target-a')),
    'target-b': reading(path('target-b')),
    unhandled: reading(path('target-b')),
    restore: reading(path('fixture')),
    restored: reading(path('fixture')),
});

test('a qualified reading distinguishes the full path of each target with the same basename', () => {
    assert.equal(path('target-a').split('/').at(-1), path('target-b').split('/').at(-1));
    assert.notEqual(path('target-a'), path('target-b'));
    assert.equal(gradeCwd(plan, baseline()).conclusion, 'pass');
});

test('a stale control does not qualify the tooltip and a stale target cannot pass', () => {
    const staleControl = baseline();
    staleControl.control.publicPath = path('fixture');
    assert.equal(gradeCwd(plan, staleControl).verdict, 'probe-invalid');
    const staleTarget = baseline();
    staleTarget['target-a'].publicPath = path('control');
    assert.equal(gradeCwd(plan, staleTarget).conclusion, 'fail');
});

test('basenames and ambiguous reads cannot stand in for full paths even if metadata is exact', () => {
    const basename = baseline();
    basename['target-a'].publicPath = readPublicPath([{ text: 'repo' }]);
    basename['target-b'].publicPath = readPublicPath([{ text: 'repo' }]);
    assert.equal(gradeCwd(plan, basename).conclusion, 'inconclusive');
    assert.equal(readPublicPath([{ text: path('target-a') }, { text: path('target-b') }]), null);
});

test('a split OCR target remains unqualified even with exact pane metadata', () => {
    const stages = baseline();
    const prefix = path('target-a').slice(0, -'/repo'.length);
    stages['target-a'].publicPath = readPublicPath([{ text: prefix }, { text: 'repo' }]);
    assert.equal(stages['target-a'].publicPath, null);
    assert.equal(readPublicPath([{ text: prefix }]), null);
    assert.equal(gradeCwd(plan, stages).verdict, 'to-test');
});

test('an unhandled OSC that changes the public path invalidates the attempt', () => {
    const stages = baseline();
    stages.unhandled = reading(path('unhandled'));
    const result = gradeCwd(plan, stages);
    assert.equal(result.negativeHeld, false);
    assert.equal(result.verdict, 'probe-invalid');
});

test('an acknowledgement without independent public restoration invalidates the attempt', () => {
    const stages = baseline();
    for (const publicPath of [null, path('target-b')]) {
        stages.restored.publicPath = publicPath;
        assert.equal(gradeCwd(plan, stages).verdict, 'probe-invalid');
    }
    delete stages.restored;
    assert.equal(gradeCwd(plan, stages).verdict, 'probe-invalid');
});

test('exact setter and negative control bytes and a DA fence bind each observation', () => {
    const step = phases.unhandled;
    const action = { request: expectedAction(step), barrier: '\x1b[?62;4;22c' };
    assert.match(action.request, /^\x1b\]9999;file:\/\/localhost\/.+\x07\x1b\[c$/);
    checkAction(step, action);
    assert.throws(() => checkAction(step, { ...action, request: action.request.replace('9999', '7') }), /changed action/);
    assert.throws(() => checkAction(step, { ...action, barrier: null }), /missing DA fence/);
});
