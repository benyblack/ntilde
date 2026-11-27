import { before, exchange, modePlan, need, verifyAction } from './modern-x11-mouse.mjs';

export const caseId = 'input.kitty-click-events';
const bytes = data => Buffer.from(data, 'ascii').toString('hex');
const text = hex => Buffer.from(hex, 'hex').toString('latin1');
const csi = '\x1b[';
const osc = data => '\x1b]133;' + data + '\x1b\\';
const cup = row => csi + row + ';1H';
const clear = csi + '2J' + cup(1);
const sgr = (column, row, final = 'M') => csi + '<0;' + column + ';' + row + final;

const clickNames = ['above', 'primary', 'secondary', 'continuation', 'below'];
const inside = ['primary', 'secondary', 'continuation'];
const fractions = { above: 0.24, primary: 0.37, secondary: 0.51, continuation: 0.64, below: 0.78 };

function clickPlan(window, name) {
    const point = [window.x + Math.floor(window.width * 0.34), window.y + Math.floor(window.height * fractions[name])];
    return [
        { kind: 'move', point, button: 0, beforeMask: 0, afterMask: 0 },
        { kind: 'press', point, button: 1, beforeMask: 0, afterMask: 256 },
        { kind: 'release', point, button: 1, beforeMask: 256, afterMask: 0 },
    ];
}

// The native helper admits za through zs. Consecutive windows always use distinct
// fences as well as unique arm keys and non-overlapping monotonic time bounds.
function actionIdentity(phase, name, index) {
    const ordinal = clickNames.indexOf(name) * 3 + index;
    return { key: caseId + '/' + phase + '/' + name + '/' + index, fence: 'z' + 'abcdefghijklmno'[ordinal] };
}

function replayClick(click, phase, name, identity, previous) {
    need(click?.name === name && click.steps?.length === 3, 'missing planned prompt click');
    const planned = clickPlan(identity.window, name);
    const frames = [];
    for (const [index, step] of click.steps.entries()) {
        const { key, fence } = actionIdentity(phase, name, index);
        const result = verifyAction(step, planned[index], previous, identity, key, fence);
        previous = result.sealed;
        frames.push(result.raw);
    }
    return { frames, finished: previous, point: planned[0].point };
}

function replayCalibration(clicks, identity, previous) {
    need(clicks?.length === clickNames.length, 'missing prompt click delivery controls');
    const calibration = {};
    for (const [index, name] of clickNames.entries()) {
        const observed = replayClick(clicks[index], 'calibration', name, identity, previous);
        const [move, press, release] = observed.frames;
        const match = /^\x1b\[<0;([1-9]\d*);([1-9]\d*)M$/.exec(text(press));
        need(!move && match, 'missing SGR delivery press at ' + name);
        const column = Number(match[1]), row = Number(match[2]);
        need(column <= identity.pane.cols && row <= identity.pane.rows && release === bytes(sgr(column, row, 'm')),
            'wrong SGR delivery coordinates or release at ' + name);
        calibration[name] = { column, row, ...observed };
        previous = observed.finished;
    }
    const { above, primary, secondary, continuation, below } = calibration;
    need(above.row > 1 && above.row < primary.row - 1 && primary.row < secondary.row
        && secondary.row < continuation.row && continuation.row < below.row, 'prompt fixture rows overlap');
    return { calibration, finished: previous };
}

function fixtures(marker, cells) {
    const primary = cells.primary.row - 1, secondary = cells.secondary.row, cursor = cells.continuation.row;
    const lines = [
        [cells.above.row, 'previous output'],
        [primary, 'primary prompt'],
        [cells.primary.row, 'primary input'],
        [secondary, 'secondary prompt'],
        [cursor, 'continuation input'],
        [cells.below.row, 'outside prompt'],
    ];
    function draw(mode) {
        return clear + marker + lines.map(([row, line]) => {
            const mark = mode === null ? '' : row === primary ? osc('A;click_events=' + mode)
                : row === secondary ? osc('A;k=s') : '';
            return cup(row) + mark + line;
        }).join('') + cup(cursor);
    }
    const cpr = { replyHex: bytes(csi + cursor + ';1R') };
    const phase = (name, write, clicks, linesExpected = lines) => ({ name, clicks, lines: linesExpected,
        exchange: { ...cpr, writeHex: bytes(write + csi + '6n') } });

    const updatedLines = lines.map(([row, line]) => [row, row === secondary ? 'fresh primary' : line]);
    return { primary, secondary, cursor, phases: [
        phase('disabled', draw(0), ['secondary']),
        phase('absolute', draw(1), inside),
        phase('relative', draw(2), clickNames),
        phase('new-prompt', cup(secondary) + osc('A') + csi + '2Kfresh primary' + cup(cursor), ['secondary'], updatedLines),
        phase('before-submit', draw(2), []),
        phase('after-submit', osc('C'), ['secondary']),
        phase('before-reset', draw(2), []),
        phase('reset', '\x1bc' + draw(null), ['secondary']),
    ] };
}

function restorePlan(marker) {
    return { ...modePlan.restore, writeHex: bytes(clear + marker) + modePlan.restore.writeHex };
}

async function collectPhase(record, phase, runClick, read, screen) {
    const trial = { name: phase.name, clicks: [] };
    record.phases.push(trial);
    trial.setup = await read(phase.exchange);
    trial.screen = await screen();
    for (const name of phase.clicks) {
        await runClick(trial.clicks, phase.name, name); // NOSONAR: each input action owns an isolated child capture.
    }
}

export async function collectClicks(record, read, action, screen) {
    async function runClick(group, phase, name) {
        const click = { name, steps: [] };
        group.push(click);
        for (const [index, request] of clickPlan(record.identity.window, name).entries()) {
            const step = {};
            click.steps.push(step);
            const { key, fence } = actionIdentity(phase, name, index);
            await action(step, request, key, fence); // NOSONAR: an armed window must seal before the next action.
        }
    }
    const captured = { calibration: [], phases: [] };
    record.promptClicks = captured;
    captured.incoming = await read(modePlan.incoming);
    captured.controlModes = await read(modePlan.control);
    for (const name of clickNames) {
        await runClick(captured.calibration, 'calibration', name); // NOSONAR: controls run on the same owned pane.
    }
    const { calibration } = replayCalibration(captured.calibration, record.identity, captured.controlModes.finished);
    captured.neutral = await read(modePlan.restore);
    const { phases } = fixtures(record.identity.marker, calibration);
    for (const phase of phases) {
        await collectPhase(captured, phase, runClick, read, screen); // NOSONAR: marker state and input are ordered.
    }
    record.restore = { modes: await read(restorePlan(record.identity.marker)) };
}

function checkScreen(screen, phase, identity, cursor) {
    need(screen?.rows === identity.pane.rows && screen.cols === identity.pane.cols
        && screen.cursorRow === cursor - 1 && screen.cursorCol === 0 && screen.cursorVisible === true
        && Array.isArray(screen.lines) && screen.lines[0] === identity.marker
        && phase.lines.every(([row, line]) => screen.lines[row - 1] === line), 'unexpected marked prompt screen at ' + phase.name);
}

function expectedFrame(phase, cell, name, plan) {
    if (!inside.includes(name)) return '';
    if (phase === 'absolute') return bytes(sgr(cell.column, cell.row));
    if (phase !== 'relative') return '';
    const anchor = cell.row < plan.secondary ? plan.primary : plan.secondary;
    return bytes(sgr(cell.column, cell.row - anchor + 1));
}

export function replayClicks(record) {
    const { identity, promptClicks: captured, restore, terminal } = record;
    need(identity?.pane.kind === 'local' && identity.pane.isActive === true && record.cases?.length === 0,
        'wrong prompt-click pane identity');
    let previous = exchange(captured.incoming, modePlan.incoming, identity.admitted);
    previous = exchange(captured.controlModes, modePlan.control, previous);
    const { calibration, finished } = replayCalibration(captured.calibration, identity, previous);
    previous = exchange(captured.neutral, modePlan.restore, finished);
    const plan = fixtures(identity.marker, calibration);
    need(captured.phases?.length === plan.phases.length, 'missing prompt-click phases');
    const samples = [];
    for (const [index, phase] of plan.phases.entries()) {
        const trial = captured.phases[index];
        need(trial?.name === phase.name && trial.clicks?.length === phase.clicks.length, 'wrong prompt-click phase or actions');
        previous = exchange(trial.setup, phase.exchange, previous);
        checkScreen(trial.screen, phase, identity, plan.cursor);
        for (const [clickIndex, name] of phase.clicks.entries()) {
            const click = replayClick(trial.clicks[clickIndex], phase.name, name, identity, previous);
            previous = click.finished;
            const { column, row } = calibration[name];
            const expectedHex = expectedFrame(phase.name, { column, row }, name, plan);
            const [move, press, release] = click.frames;
            samples.push({ phase: phase.name, position: name, point: click.point, column, row, childHex: click.frames,
                expectedHex, matched: !move && !press && release === expectedHex });
        }
    }
    previous = exchange(restore.modes, restorePlan(identity.marker), previous);
    need(restore.paneId === identity.pane.paneId && restore.marker === identity.marker && before(previous, terminal?.at)
        && terminal.original?.length === 7 && JSON.stringify(terminal.original) === JSON.stringify(terminal.restored),
        'incomplete prompt-click pane or terminal restoration');
    return { id: caseId, pinned: 'unprobed', effect: samples.every(sample => sample.matched) ? 'supported' : 'unsupported',
        anchors: { primary: plan.primary, secondary: plan.secondary, cursor: plan.cursor },
        deliveryControls: calibration, samples };
}
