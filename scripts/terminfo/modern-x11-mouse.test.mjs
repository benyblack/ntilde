import assert from 'node:assert/strict';
import test from 'node:test';
import { actionPlan, gradeCase, ids, modePlan, replayMouse } from './modern-x11-mouse.mjs';

const pane = { paneId: 'pane', kind: 'local', isActive: true, cols: 120, rows: 40 };
const hex = text => Buffer.from(text).toString('hex');
const sgr = (code, x, y, suffix) => hex(`\x1b[<${code};${x};${y}${suffix}`);
const control = ['', sgr(0, 4, 4, 'M'), sgr(0, 4, 4, 'm')];
const drag = ['', sgr(0, 8, 4, 'M'), sgr(32, 14, 4, 'M'), sgr(0, 14, 4, 'm')];
const target = { [ids[0]]: ['', '1b5b4d20662f', ''], [ids[1]]: drag, [ids[2]]: ['', '1b5b41', ''] };

test('X10 distinguishes an exact legacy report, verified silence and invalid delivery', () => {
    assert.equal(gradeCase(ids[0], control, target[ids[0]], pane).status, 'supported');
    assert.equal(gradeCase(ids[0], control, ['', '', ''], pane).status, 'unsupported');
    assert.equal(gradeCase(ids[0], control, ['', control[1], ''], pane).status, 'probe-invalid');
    assert.equal(gradeCase(ids[0], control, ['', '1b5b4d2066', ''], pane).status, 'probe-invalid');
    assert.equal(gradeCase(ids[0], ['', '', ''], target[ids[0]], pane).status, 'probe-invalid');
});

test('button-event requires an ordered held-button motion and delivered press and release', () => {
    assert.equal(gradeCase(ids[1], control, drag, pane).status, 'supported');
    assert.equal(gradeCase(ids[1], control, [drag[0], drag[1], '', drag[3]], pane).status, 'unsupported');
    assert.equal(gradeCase(ids[1], control, [drag[0], drag[1], drag[3], drag[2]], pane).status, 'probe-invalid');
    assert.equal(gradeCase(ids[1], control, [drag[0], drag[1], drag[2], ''], pane).status, 'probe-invalid');
    assert.equal(gradeCase(ids[1], control, ['', '', '', ''], pane).status, 'probe-invalid');
});

test('alternate scroll accepts CSI A or SS3 A and rejects other delivered streams', () => {
    assert.equal(gradeCase(ids[2], control, target[ids[2]], pane).status, 'supported');
    assert.equal(gradeCase(ids[2], control, ['', '1b4f41', ''], pane).status, 'supported');
    assert.equal(gradeCase(ids[2], control, ['', '1b5b42', ''], pane).status, 'unsupported');
    assert.equal(gradeCase(ids[2], control, ['', '', ''], pane).status, 'unsupported');
    assert.equal(gradeCase(ids[2], [control[0], '', control[2]], target[ids[2]], pane).status, 'probe-invalid');
});

function fixture() {
    const window = { id: 5, width: 800, height: 600, x: 0, y: 0 };
    let time = 100n;
    const tick = () => (++time).toString();
    const identity = { pane, window, marker: 'fixture', appPid: 200, serverPid: 201, admitted: tick() };
    const exchange = key => ({ ...modePlan[key], started: tick(), finished: tick() });
    const steps = (sequence, outputs, label) => sequence.map((request, index) => {
        const key = label + '/' + index, armed = tick(), started = tick(), finished = tick();
        const chunks = outputs[index] ? [{ hex: outputs[index], at: tick() }] : [];
        time += 240000000n;
        return {
            arm: { key, armed },
            native: { request, started, finished, windowId: window.id, appPid: identity.appPid, serverPid: identity.serverPid, focus: window.id, xtest: true,
                before: { mask: request.beforeMask, window: window.id, point: request.point },
                after: { mask: request.afterMask, window: window.id, point: request.point } },
            capture: { key, chunks, sealed: tick() },
        };
    });
    const incoming = exchange('incoming');
    const cases = ids.map(id => {
        const plan = actionPlan(window, id);
        return { id, control: { modes: exchange('control'), steps: steps(plan.control, control, id + '/control') },
            target: { modes: exchange(id), steps: steps(plan.target, target[id], id + '/target') } };
    });
    const restore = { modes: exchange('restore'), marker: identity.marker, paneId: pane.paneId };
    const original = [3, 4, 5, 6, 7, 8, [0]];
    return { identity, incoming, cases, restore, terminal: { original, restored: [...original], at: tick() } };
}

test('replay refuses stale control/target bytes or a capture sealed before delivery', () => {
    const record = fixture();
    assert.deepEqual(replayMouse(record).map(result => result.conclusion), ['pass', 'pass', 'pass']);
    const step = record.cases[0].target.steps[1];
    step.capture.chunks[0].at = step.arm.armed;
    assert.throws(() => replayMouse(record), /stale or malformed raw reply/);
    const early = fixture();
    const press = early.cases[0].target.steps[1];
    press.capture.sealed = press.native.started;
    assert.throws(() => replayMouse(early), /misordered action window/);
    const short = fixture();
    const silent = short.cases[0].target.steps[0];
    silent.capture.sealed = (BigInt(silent.native.finished) + 1000n).toString();
    assert.throws(() => replayMouse(short), /silence observation budget/);
});

test('replay refuses failed native delivery and changed pane or kernel restoration', () => {
    const wrong = fixture();
    wrong.cases[2].target.steps[1].native.after.mask = 0;
    assert.throws(() => replayMouse(wrong), /failed or unowned XTEST delivery/);
    const restored = fixture();
    restored.restore.marker = 'wrong primary screen';
    assert.throws(() => replayMouse(restored), /incomplete pane, mode or kernel terminal restoration/);
    const modes = fixture();
    modes.restore.modes.replyHex = modePlan.control.replyHex;
    assert.throws(() => replayMouse(modes), /missing fresh mode observations/);
    const tty = fixture();
    tty.terminal.restored = [];
    assert.throws(() => replayMouse(tty), /incomplete pane, mode or kernel terminal restoration/);
});
