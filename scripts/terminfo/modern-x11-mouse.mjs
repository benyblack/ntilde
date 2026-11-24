export const ids = Object.freeze(['input.x10-mouse', 'input.button-event-mouse', 'modes.alt-scroll-1007']);
const modes = [9, 1000, 1002, 1003, 1005, 1006, 1007, 1015, 1016, 1049];
const escape = '\x1b[';
const bytes = text => Buffer.from(text, 'ascii').toString('hex');
const decset = (list, final) => list.map(mode => escape + '?' + mode + final).join('');
const allQueries = modes.map(mode => escape + '?' + mode + '$p').join('');
export const modePlan = Object.freeze({
    incoming: modeExchange([], false),
    restore: modeExchange([]),
    control: modeExchange([1000, 1006]),
    [ids[0]]: modeExchange([9]),
    [ids[1]]: modeExchange([1002, 1006]),
    [ids[2]]: modeExchange([1007, 1049]),
});
function modeExchange(enabled, reset = true) {
    return {
        writeHex: bytes((reset ? decset(modes, 'l') + decset(enabled, 'h') : '') + allQueries),
        replyHex: bytes(modes.map(mode => escape + '?' + mode + ';' + (enabled.includes(mode) ? '1' : '2') + '$y').join('')),
    };
}

export function need(condition, message) {
    if (!condition) throw new Error('Modern X11 mouse: ' + message);
}

function point(window, fractionX, fractionY) {
    return [window.x + Math.floor(window.width * fractionX), window.y + Math.floor(window.height * fractionY)];
}

export function actionPlan(window, id) {
    const index = ids.indexOf(id);
    need(index !== -1, 'unknown case');
    const row = 0.38 + 0.12 * index;
    const control = point(window, 0.32, row);
    const source = point(window, 0.46, row);
    const destination = point(window, 0.64, row);
    const down = id === ids[2] ? 4 : 1;
    const click = (start, button) => [
        { kind: 'move', point: start, button: 0, beforeMask: 0, afterMask: 0 },
        { kind: 'press', point: start, button, beforeMask: 0, afterMask: 1 << (button + 7) },
        { kind: 'release', point: start, button, beforeMask: 1 << (button + 7), afterMask: 0 },
    ];
    const target = click(source, down);
    if (id === ids[1]) {
        target.splice(2, 0, { kind: 'move', point: destination, button: 0, beforeMask: 256, afterMask: 256 });
        target[3].point = destination;
    }
    return { control: click(control, 1), target };
}

function same(a, b) { return JSON.stringify(a) === JSON.stringify(b); }
const timestamp = value => typeof value === 'string' && /^\d{1,22}$/.test(value);
const before = (a, b) => timestamp(a) && timestamp(b) && BigInt(a) < BigInt(b);
const isHex = value => typeof value === 'string' && /^(?:[a-f\d]{2})*$/.test(value) && value.length <= 2048;

function exchange(record, plan, previous) {
    need(record?.writeHex === plan.writeHex && record.replyHex === plan.replyHex
        && before(previous, record.started) && before(record.started, record.finished), 'missing fresh mode observations');
    return record.finished;
}

function verifyAction(step, expected, previous, identity, key) {
    const { arm, native, capture } = step;
    const { point, beforeMask, afterMask, kind } = expected;
    need(arm?.key === key && capture?.key === key && before(previous, arm.armed)
        && before(arm.armed, native.started) && before(native.started, native.finished)
        && before(native.finished, capture.sealed), 'stale or misordered action window');
    need(same(native.request, expected) && native.serverPid === identity.serverPid
        && native.windowId === identity.window.id && native.appPid === identity.appPid
        && native.focus === identity.window.id && native.xtest === true
        && native.before?.mask === beforeMask && native.after?.mask === afterMask
        && native.after.window === identity.window.id && same(native.after.point, point)
        && (kind === 'move' || (native.before.window === identity.window.id && same(native.before.point, point))),
    'failed or unowned XTEST delivery');
    need(Array.isArray(capture.chunks) && capture.chunks.every((chunk, index, all) =>
        isHex(chunk.hex) && chunk.hex.length > 0 && before(all[index - 1]?.at ?? native.started, chunk.at)
        && before(chunk.at, capture.sealed)), 'stale or malformed raw reply');
    const raw = capture.chunks.map(chunk => chunk.hex).join('');
    need(isHex(raw), 'oversized observation window');
    return { sealed: capture.sealed, raw };
}

function verifySteps(recorded, planned, previous, identity, key) {
    need(Array.isArray(recorded) && recorded.length === planned.length, 'missing or extra XTEST action');
    const raw = [];
    for (const [index, step] of recorded.entries()) {
        const result = verifyAction(step, planned[index], previous, identity, key + '/' + index);
        previous = result.sealed;
        raw.push(result.raw);
    }
    return { sealed: previous, raw };
}

function sgr(hex, pane) {
    const match = /^\x1b\[<(\d{1,3});(\d{1,3});(\d{1,3})([Mm])$/.exec(Buffer.from(hex, 'hex').toString('latin1'));
    if (!match) return null;
    const [code, col, row] = match.slice(1, 4).map(Number);
    if (col < 1 || col > pane.cols || row < 1 || row > pane.rows) return null;
    return { code, cell: [col, row], press: match[4] === 'M' };
}

const leftPress = frame => frame?.code === 0 && frame.press;
const leftRelease = frame => frame?.code === 0 && !frame.press;
const invalid = reason => ({ status: 'probe-invalid', conclusion: 'inconclusive', reason });
const verdict = (supported, reason) => ({ status: supported ? 'supported' : 'unsupported', conclusion: supported ? 'pass' : 'fail', reason });

export function gradeCase(id, control, target, pane) {
    const [controlMove, controlPress, controlRelease] = control;
    const start = sgr(controlPress, pane), end = sgr(controlRelease, pane);
    if (controlMove || !leftPress(start) || !leftRelease(end) || !same(start.cell, end.cell)) {
        return invalid('Fresh SGR click delivery control failed.');
    }
    const [position, press, moveOrRelease, release] = target;
    if (position) return invalid('Bytes arrived during the neutral positioning window.');
    if (id === ids[0]) {
        if (!press && !moveOrRelease) return verdict(false, 'The delivered X10 click was silent.');
        if (press.startsWith('1b5b4d') && press.length === 12 && !moveOrRelease) return verdict(true, 'Legacy CSI M and three payload bytes arrived on the press.');
        return invalid('The X10 click produced an SGR or otherwise unclassifiable report.');
    }
    if (id === ids[1]) {
        const down = sgr(press, pane), up = sgr(release, pane);
        if (!leftPress(down) || !leftRelease(up) || same(down.cell, up.cell)) return invalid('The drag lacked ordered left-button endpoints in distinct cells.');
        if (!moveOrRelease) return verdict(false, 'The delivered drag produced press and release without motion.');
        const motion = sgr(moveOrRelease, pane);
        if (motion?.code === 32 && motion.press && same(motion.cell, up.cell)) {
            return verdict(true, 'SGR motion with the left-button motion bit arrived before the destination release.');
        }
        return invalid('The drag motion stream did not match the ordered held-button action.');
    }
    need(id === ids[2], 'unknown case');
    const raw = press + moveOrRelease;
    return verdict(raw === '1b5b41' || raw === '1b4f41', raw === '' ? 'The delivered alternate-screen wheel-up was silent.'
        : 'The alternate-screen wheel-up byte stream was ' + raw + '.');
}

export function replayMouse(record) {
    const { identity, incoming, cases, restore, terminal } = record;
    need(identity?.pane?.kind === 'local' && identity.pane.isActive === true
        && [identity.pane.rows, identity.pane.cols].every(size => Number.isSafeInteger(size) && size >= 10)
        && Array.isArray(cases) && same(cases.map(item => item.id), ids), 'missing independently owned pane or cases');
    let previous = exchange(incoming, modePlan.incoming, identity.admitted);
    const results = [];
    for (const item of cases) {
        const { control, target } = actionPlan(identity.window, item.id);
        previous = exchange(item.control.modes, modePlan.control, previous);
        const positive = verifySteps(item.control.steps, control, previous, identity, item.id + '/control');
        previous = exchange(item.target.modes, modePlan[item.id], positive.sealed);
        const trial = verifySteps(item.target.steps, target, previous, identity, item.id + '/target');
        results.push({ id: item.id, ...gradeCase(item.id, positive.raw, trial.raw, identity.pane),
            controlHex: positive.raw, targetHex: trial.raw });
        previous = trial.sealed;
    }
    const restored = exchange(restore.modes, modePlan.restore, previous);
    need(restore.paneId === identity.pane.paneId && restore.marker === identity.marker
        && Array.isArray(terminal?.original) && terminal.original.length === 7
        && same(terminal.original, terminal.restored) && before(restored, terminal.at),
    'incomplete pane, mode or kernel terminal restoration');
    return results;
}
