import { isDeepStrictEqual as same } from 'node:util';
import { validateDevqExchange } from './devq-report.mjs';

export const scope = Object.freeze({ id: 'owned-modern-osc-titles-direct-v1', persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'most-';
export const osc2 = 'extensions.osc2-title';
export const osc0 = 'extensions.osc0-icon-title';
export const da = '\x1b[c';
export const queries = ['\x1b[21t\x1b[20t', '\x1b[20t\x1b[21t'];
export const popLabels = '\x1b[23;0t';
const pushLabels = '\x1b[22;0t';
const labels = ['title', 'icon'];
const guid = /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i;
const decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });

export function requireTitles(condition, reason) {
    if (!condition) throw new Error('Modern OSC titles: ' + reason);
}

function step(phase, code, value) {
    return { phase, bytes: `\x1b]${code};${value}\x07`, value };
}

export function titlePlans(runId) {
    const value = phase => `M-${runId}-${phase}`;
    const incoming = { phase: 'incoming', bytes: '' };
    const save = { phase: 'save', bytes: pushLabels };
    const restore = { phase: 'restore', bytes: popLabels };
    return [
        { id: osc2, steps: [incoming, save, step('control-a', 2, value('C2A')), step('control-b', 2, value('C2B')),
            step('target-a', 2, value('T2A')), step('target-b', 2, value('T2B')), step('unhandled', 9999, value('NEG')), restore] },
        { id: osc0, steps: [incoming, save, step('icon-control', 1, value('C1')), step('title-control', 2, value('C2')),
            step('target', 0, value('T0')), restore] },
    ];
}

function replyLabel(raw) {
    requireTitles([...raw].every(char => char.codePointAt(0) <= 255), 'invalid label reply bytes');
    const text = decoder.decode(Buffer.from(raw.slice(3, -2), 'latin1'));
    requireTitles(![...text].some(char => char < '\x20' || (char >= '\x7f' && char <= '\x9f')), 'control bytes in label reply');
    return text;
}

function parseLabels(exchange, order) {
    const { replies, barrier } = validateDevqExchange(exchange);
    requireTitles(isBarrier(barrier), 'changed reply barrier');
    const values = { title: null, icon: null };
    let next = 0;
    for (const raw of replies) {
        const code = raw.startsWith('\x1b]') ? raw[2] : '';
        const index = code ? order.indexOf(code, next) : -1;
        requireTitles(index >= 0, 'unexpected, duplicated or out-of-order label replies');
        values[code === 'l' ? 'title' : 'icon'] = replyLabel(raw);
        next = index + 1;
    }
    return values;
}

function isBarrier(raw) {
    return typeof raw === 'string' && raw.length <= 128 && /^\x1b\[\?[\d;]+c$/.test(raw) && raw.endsWith('c');
}

export function readStage(frame, plan, step, pane) {
    requireTitles(frame && !frame.error && frame.caseId === plan.id && frame.phase === step.phase && same(frame.pane, pane)
        && guid.test(pane?.id ?? '') && Number.isInteger(pane.rows) && pane.rows >= 3 && Number.isInteger(pane.cols) && pane.cols >= 48,
    'changed phase, pane or geometry');
    requireTitles(frame.action?.request === (step.bytes ? step.bytes + da : '')
        && (step.bytes ? isBarrier(frame.action.barrier) : frame.action.barrier === null), 'changed action bytes or barrier');
    requireTitles(Array.isArray(frame.reads) && frame.reads.length === 2
        && frame.reads.every((read, index) => read.request === queries[index] + da), 'missing paired public label reads');
    const first = parseLabels(frame.reads[0].exchange, 'lL'), second = parseLabels(frame.reads[1].exchange, 'Ll');
    // Agreement is required separately for each channel. An empty framed label
    // is an observation. A missing or unstable label is not a mismatch verdict.
    return Object.fromEntries(labels.map(label => [label, {
        value: first[label] === second[label] ? first[label] : null, stable: first[label] === second[label],
        observed: first[label] !== null || second[label] !== null,
    }]));
}

function labelOf(stages, phase, label) {
    return stages[phase][label].value;
}

function provedRestoration(stages, label) {
    const incoming = stages.incoming[label], after = stages.restore[label];
    if (Object.values(stages).every(stage => !stage[label].observed)) return 'unobserved';
    return incoming.value !== null && incoming.value === after.value ? 'verified' : 'unproved';
}

const invalid = reason => ({ conclusion: 'inconclusive', verdict: 'probe-invalid', reason });
const pending = reason => ({ conclusion: 'inconclusive', verdict: 'to-test', reason });
const fail = reason => ({ conclusion: 'fail', verdict: 'unsupported', reason });
const pass = reason => ({ conclusion: 'pass', verdict: 'supported', reason });

function transition(value, requested, preceding) {
    if (value === null || preceding === null) return 'unobserved';
    if (requested === preceding) return 'indistinguishable';
    if (value === requested) return 'exact';
    return value === preceding ? 'unchanged' : 'unexpected';
}

function osc2Grade(stages, requested) {
    const title = phase => labelOf(stages, phase, 'title');
    const controlA = transition(title('control-a'), requested['control-a'], title('incoming'));
    const controlB = transition(title('control-b'), requested['control-b'], title('control-a'));
    const targetA = transition(title('target-a'), requested['target-a'], title('control-b'));
    const targetB = transition(title('target-b'), requested['target-b'], title('target-a'));
    const negativeHeld = title('target-b') !== null && title('unhandled') === title('target-b')
        && requested.unhandled !== title('target-b');
    const decide = () => {
        if (controlA !== 'exact' || controlB !== 'exact') return invalid('Two distinct OSC 2 reader controls did not read back exactly.');
        if (!negativeHeld) return invalid('The unhandled OSC did not leave a distinguishable, observed title unchanged.');
        if ([targetA, targetB].some(result => !['exact', 'unchanged'].includes(result))) return pending('A target title was unobserved, indistinguishable or unexpectedly changed.');
        if (targetA === 'unchanged' || targetB === 'unchanged') return fail('An OSC 2 target left the preceding qualified title unchanged.');
        return pass('Both OSC 2 targets read back exactly and the unhandled OSC left the title unchanged.');
    };
    return { ...decide(), controlA, controlB, targetA, targetB, negativeHeld };
}

function osc0Grade(stages, requested) {
    const title = phase => labelOf(stages, phase, 'title'), icon = phase => labelOf(stages, phase, 'icon');
    const titleControl = transition(title('title-control'), requested['title-control'], title('icon-control'));
    const iconControl = transition(icon('icon-control'), requested['icon-control'], icon('incoming'));
    const titleTarget = transition(title('target'), requested.target, title('title-control'));
    const iconTarget = transition(icon('target'), requested.target, icon('title-control'));
    const decide = () => {
        if (titleControl !== 'exact') return invalid('The same-attempt OSC 2 title control did not read back exactly.');
        if (titleTarget === 'unchanged' || titleTarget === 'unexpected') return fail('The delivered OSC 0 missed the qualified title clause.');
        if (titleTarget !== 'exact') return pending('The OSC 0 title clause was not observed.');
        if (iconTarget === 'unobserved') return pending('The icon label remains to-test because paired reads did not yield an observation.');
        if (iconControl !== 'exact' || icon('title-control') !== icon('icon-control')) return invalid('The independent icon control did not establish a stable baseline.');
        if (iconTarget !== 'exact') return fail('The delivered OSC 0 missed the independently qualified icon clause.');
        return pass('OSC 0 set the title and icon label to the same requested value in independent replies.');
    };
    return { ...decide(), titleControl, iconControl, titleTarget, iconTarget };
}

export function gradeTitleCase(plan, stages) {
    const requested = Object.fromEntries(plan.steps.filter(step => step.value !== undefined).map(step => [step.phase, step.value]));
    const restoration = Object.fromEntries(labels.map(label => [label, provedRestoration(stages, label)]));
    const grade = plan.id === osc2 ? osc2Grade(stages, requested) : osc0Grade(stages, requested);
    const saveHeld = same(stages.incoming, stages.save);
    const result = { id: plan.id, ...grade, requested, stages, restoration, saveHeld };
    if (!saveHeld) return { ...result, ...invalid('Labels changed while saving the incoming state.') };
    if (Object.values(restoration).includes('unproved')) return { ...result, ...invalid('Incoming label restoration was not proved.') };
    return result;
}
