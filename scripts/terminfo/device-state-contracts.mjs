import { isDeepStrictEqual as same } from 'node:util';
import { expandCellDetails } from './cell-details.mjs';
import { da, leave, rawText } from './sixel-reports-contracts.mjs';

export const scope = Object.freeze({ id: 'ntilde-owned-device-state-v1', persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'device-state-';
export const caseIds = Object.freeze(['device.decrqss', 'device.dsr-996-color-scheme']);
export const requests = Object.freeze(['\x1bP$qm\x1b\\', '\x1b[?996n']);
export const da1 = '\x1b[?62;4;22c';
const unknown = '\x1bP$qunknown\x1b\\';
const mode = '\x1b[?2031$p';
const osc = '\x1b]11;?\x1b\\';
const reject = '\x1bP0$r\x1b\\';
const neutral = '\x1bP1$r0m\x1b\\';
// The pen contains bold, curly underline, ANSI red, indexed background and a literal underline color.
const styled = '\x1bP1$r0;1;4:3;31;48;5;200;58;2;9;8;7m\x1b\\';
const backgrounds = { dark: 0xff101010, light: 0xfff0f0f0 };
const nonce = runId => 'NTILDE_QSTATE_' + runId;

export function requireState(condition, detail) {
    if (!condition) throw new Error('Device state: ' + detail);
}

export function planFor(runId) {
    const steps = [{ stage: 'start' }];
    let text = '';
    const controls = stage => ({ stage, sample: true, queries: [mode, osc] });
    const witness = (stage, write, char) => {
        text += char;
        return { stage, write: write + char, text };
    };
    const query = (stage, request, expected, id) => ({ stage, expected, id, sample: true, queries: [id ? request + da : request] });
    steps.push(controls('initial-colors'),
        { stage: 'bind', write: '\x1b[?1049h\x1b[0m\x1b[2J\x1b[H' + nonce(runId) + '\x1b[2;1H', text },
        witness('neutral-before', '', 'A'), query('neutral', requests[0], neutral, caseIds[0]), witness('neutral-after', '', 'a'),
        witness('styled-before', '\x1b[1;4:3;31;48;5;200;58;2;9;8;7m', 'B'),
        query('styled', requests[0], styled, caseIds[0]), witness('styled-after', '', 'b'),
        witness('reset-before', '\x1b[0m', 'C'), query('reset', requests[0], neutral, caseIds[0]), witness('reset-after', '', 'c'),
        query('unknown', unknown, reject), witness('unknown-after', '', 'u'));
    const colorStages = [
        ['dark', '\x1b]11;#101010\x1b\\', 'd'],
        ['light', '\x1b]11;#f0f0f0\x1b\\', 'l'],
        ['restored', '\x1b]111\x1b\\', 'r'],
    ];
    for (const [stage, write, char] of colorStages) {
        steps.push({ stage: stage + '-set', write, color: stage },
            controls(stage + '-before'), query(stage, requests[1], stage, caseIds[1]), controls(stage + '-after'),
            witness(stage + '-witness', '', char));
    }
    steps.push({ stage: 'finish', write: leave }, { stage: 'seal' });
    return steps;
}

function verifyWitnesses(screen, text, runId) {
    const { lines, bufferState } = screen;
    requireState(bufferState.alternateScreen && lines[0] === nonce(runId) && lines[1] === text
        && lines.slice(2).every(line => line === '') && screen.cursorRow === 1 && screen.cursorCol === text.length,
    'nonce, witness text or cursor changed');
    const cells = expandCellDetails(screen.cellDetails, screen.rows * screen.cols, true);
    const neutralCell = { overline: false, flags: 768, fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null,
        underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null };
    const styledCell = { ...neutralCell, flags: 49161, fg: 1, bg: 200, underlineStyle: 3, underlineArgb: 0xff090807 };
    for (const [column, char] of [...text].entries()) {
        requireState(same(cells[screen.cols + column], /[Bb]/.test(char) ? styledCell : neutralCell),
            'stored witness attributes for ' + char + ' differ from the independently specified pen');
    }
}

export function backgroundOf(screen) {
    return screen.colorBinding.backgroundArgb;
}

export function verifyWrite(step, screen, initial, previous, runId) {
    if (step.text !== undefined) {
        verifyWitnesses(screen, step.text, runId);
        requireState(same(screen.colorBinding, previous.colorBinding), 'SGR or witness write changed the pane colors');
        return;
    }
    const expected = backgrounds[step.color] ?? backgroundOf(initial);
    const binding = { ...previous.colorBinding, backgroundArgb: expected, backgroundOverride: step.color !== 'restored' };
    const resolvedColors = previous.resolvedColors.map(run => run.bg.source === 'default'
        ? { ...run, bg: { ...run.bg, argb: expected } } : run);
    requireState(same(screen, { ...previous, colorBinding: binding, resolvedColors }),
        'OSC 11 transition did not change only the effective background and override flag');
}

const rgb = argb => [(argb >>> 16) & 255, (argb >>> 8) & 255, argb & 255];
const colorReply = argb => '\x1b]11;rgb:' + rgb(argb).map(byte => byte.toString(16).padStart(2, '0').repeat(2)).join('/') + '\x1b\\';

export function schemeReply(stage, argb) {
    // The two overrides are unambiguous grayscale controls. For the restored host color use the documented luma policy.
    const [r, g, b] = rgb(argb);
    const restoredDark = 299 * r + 587 * g + 114 * b < 127500;
    return '\x1b[?997;' + ((stage === 'restored' ? restoredDark : stage === 'dark') ? '1' : '2') + 'n';
}

export function verifySamples(step, samples, used, screen) {
    requireState(samples?.length === step.queries.length, 'incomplete query stage');
    const observed = step.queries.map((request, index) => {
        requireState(samples[index].requestBytes === request, 'query bytes or order changed');
        return rawText(samples[index].sample, used);
    });
    const background = backgroundOf(screen);
    if (!step.expected) {
        requireState(same(observed, ['\x1b[?2031;2$y', colorReply(background)]),
            'mode 2031 was enabled or OSC 11 disagreed with the App background');
        return { stage: step.stage, background, responses: observed };
    }
    const [raw] = observed;
    if (step.id) requireState(raw.endsWith(da1), 'missing exact DA1 sentinel');
    const reply = step.id ? raw.slice(0, -da1.length) : raw;
    const expected = step.id === caseIds[1] ? schemeReply(step.expected, background) : step.expected;
    return { stage: step.stage, ...(step.id && { id: step.id }), request: step.queries[0], raw, reply, expected,
        ...(step.id === caseIds[1] && { background }), outcome: reply === expected ? 'passed' : 'failed' };
}
