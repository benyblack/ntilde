import assert from 'node:assert/strict';
import test from 'node:test';
import { da, gradeTitleCase, osc0, osc2, queries, readStage, titlePlans } from './modern-osc-titles.mjs';

const plans = titlePlans('0123456789abcdef0123456789abcdef');
const pane = { id: '00000000-0000-0000-0000-000000000001', rows: 40, cols: 140 };
const barrier = '\x1b[?62;22c';
const reply = (code, value) => value === null ? [] : [`\x1b]${code}${value}\x1b\\`];

function grade(id, override = (_, value) => value) {
    const plan = plans.find(item => item.id === id);
    const staged = {};
    let labels = { title: '', icon: '' };
    for (const step of plan.steps) {
        if (step.phase === 'restore') labels = { title: '', icon: '' };
        if (step.bytes.startsWith('\x1b]2;')) labels.title = step.value;
        if (step.bytes.startsWith('\x1b]1;')) labels.icon = step.value;
        if (step.bytes.startsWith('\x1b]0;')) labels = { title: step.value, icon: step.value };
        const reads = queries.map((query, index) => {
            const { title, icon } = override(step.phase, { ...labels }, index);
            const titleReply = reply('l', title), iconReply = reply('L', icon);
            return { request: query + da, exchange: { barrier, replies: index ? iconReply.concat(titleReply) : titleReply.concat(iconReply) } };
        });
        staged[step.phase] = readStage({ pane, caseId: id, phase: step.phase, action: {
            request: step.bytes ? step.bytes + da : '', barrier: step.bytes ? barrier : null,
        }, reads }, plan, step, pane);
    }
    return gradeTitleCase(plan, staged);
}

test('both exact OSC 2 transitions and the unhandled OSC hold produce support', () => {
    const result = grade(osc2);
    assert.equal(result.verdict, 'supported');
    assert.equal(result.negativeHeld, true);
    assert.deepEqual(result.restoration, { title: 'verified', icon: 'verified' });
});

test('stale members of a paired title read cannot establish a target effect', () => {
    const firstControl = plans[0].steps.find(step => step.phase === 'control-b').value;
    const result = grade(osc2, (phase, value, index) => phase === 'target-a' && index === 0 ? { ...value, title: firstControl } : value);
    assert.equal(result.conclusion, 'inconclusive');
    assert.equal(result.targetA, 'unobserved');
    assert.deepEqual(result.stages['target-a'].title, { stable: false, observed: true, value: null });
});

test('a consistently unchanged qualified OSC 2 target is unsupported, an unexpected change is to-test', () => {
    const targetA = plans[0].steps.find(step => step.phase === 'target-a').value;
    const withTargetB = title => grade(osc2, (phase, value) => ['target-b', 'unhandled'].includes(phase) ? { ...value, title } : value);
    assert.equal(withTargetB(targetA).verdict, 'unsupported');
    assert.equal(withTargetB('unexpected').verdict, 'to-test');
});

test('failed title controls and a changed negative control are probe-invalid', () => {
    const lostTitle = (stage, value) => stage === 'title-control' ? { ...value, title: '' } : value;
    const failedNegative = (stage, value) => stage === 'unhandled' ? { ...value, title: 'unexpected' } : value;
    assert.equal(grade(osc0, lostTitle).verdict, 'probe-invalid');
    assert.equal(grade(osc2, failedNegative).verdict, 'probe-invalid');
});

test('OSC 0 keeps observations separate and either qualified one-sided effect is unsupported', () => {
    assert.equal(grade(osc0).verdict, 'supported');
    const iconControl = plans[1].steps.find(step => step.phase === 'icon-control').value;
    const titleControl = plans[1].steps.find(step => step.phase === 'title-control').value;
    const onlyTitle = grade(osc0, (phase, value) => phase === 'target' ? { ...value, icon: iconControl } : value);
    assert.equal(onlyTitle.verdict, 'unsupported');
    assert.equal(onlyTitle.titleTarget, 'exact');
    assert.equal(onlyTitle.iconTarget, 'unchanged');
    const onlyIcon = grade(osc0, (phase, value) => phase === 'target' ? { ...value, title: titleControl } : value);
    assert.equal(onlyIcon.verdict, 'unsupported');
    assert.equal(onlyIcon.titleTarget, 'unchanged');
    assert.equal(onlyIcon.iconTarget, 'exact');
});

test('missing icon reads leave the icon to-test and do not supply negative evidence', () => {
    const missing = grade(osc0, (_, value) => ({ ...value, icon: null }));
    assert.equal(missing.verdict, 'to-test');
    assert.equal(missing.titleTarget, 'exact');
    const missedTitle = grade(osc0, (phase, value) => ({ icon: null, title: phase === 'target' ? 'wrong title' : value.title }));
    assert.equal(missedTitle.verdict, 'unsupported');
    assert.equal(missedTitle.iconTarget, 'unobserved');
});

test('a failed pop readback invalidates either verdict and retains both effect results', () => {
    for (const icon of ['left changed', null]) {
        const result = grade(osc0, (phase, value) => phase === 'restore' ? { ...value, icon } : value);
        assert.equal(result.verdict, 'probe-invalid');
        assert.equal(result.iconTarget, 'exact');
        assert.deepEqual(result.restoration, { title: 'verified', icon: 'unproved' });
    }
});

test('a stale exchange from the preceding opposite-order query is refused', () => {
    const [plan] = plans, [step] = plan.steps, exchange = { barrier, replies: ['\x1b]L\x1b\\', '\x1b]l\x1b\\'] };
    const reads = queries.map(query => ({ request: query + da, exchange }));
    assert.throws(() => readStage({ pane, caseId: plan.id, phase: step.phase, action: { request: '', barrier: null }, reads },
        plan, step, pane), /out-of-order label replies/);
});
