import { isDeepStrictEqual as same } from 'node:util';
import { parseColorReply, querySequence } from './color-query.mjs';
import { da, leave, rawText } from './sixel-reports-contracts.mjs';

export const scope = Object.freeze({ id: 'ntilde-owned-kitty-color-stack-v1', persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'kitty-colors-';
export const caseIds = Object.freeze(['extensions.osc30001-color-stack-push', 'extensions.osc30101-color-stack-pop']);
export const effectId = 'ntilde-kitty-color-stack-nested-v1';
export const foregroundQuery = '\x1b]10;?\x07';
export const da1 = '\x1b[?62;4;22c';
const osc = body => '\x1b]' + body + '\x1b\\';
const push = osc('30001'), pop = osc('30101');
const modeIds = [1, 2004];
const modeQueries = modeIds.map(id => '\x1b[?' + id + '$p').join('');
const modeReplies = enabled => modeIds.map(id => '\x1b[?' + id + ';' + (enabled ? 1 : 2) + '$y').join('');
const modes = enabled => modeIds.map(id => '\x1b[?' + id + (enabled ? 'h' : 'l')).join('');
const channels = [[10, 'foreground'], [11, 'background'], [12, 'cursor'], [17, 'selectionBackground'], [19, 'selectionForeground']];
const dynamicQueries = channels.map(([code]) => querySequence({ code })).join('');
const paletteIndices = Array.from({ length: 256 }, (_, index) => index);
const sampleIndices = [1, 5, 9, 200, 255];
const palettes = {
    A: { 1: '703010', 5: '704010', 200: '705010' },
    B: { 1: '103070', 9: '104070', 200: '105070' },
    C: { 1: '107030', 5: '107040', 9: '107050', 200: '107060', 255: '107080' },
};
const sets = {
    A: ['162738', '263748', '364758', '465768', '566778'],
    B: ['a1b2c3', 'b1c2d3', 'c1d2e3', 'd1e2f3', 'e10213'],
    C: ['f1d2b3', 'e1c2a3', 'd1b293', 'c1a283', 'b19273'],
};
const resetColors = ['104', '110', '111', '112', '117', '119'].map(osc).join('');
export const nonce = runId => 'NTILDE_KCS_' + runId;
export const witness = 'Aa Bb';

export function requireStack(condition, detail) {
    if (!condition) throw new Error('Kitty color stack: ' + detail);
}

const paletteQuery = indices => osc('4;' + indices.map(index => index + ';?').join(';'));
// Sixteen OSC 4 replies fit inside the existing 512-byte raw-input budget.
const fullPalette = Array.from({ length: 16 }, (_, batch) => paletteIndices.slice(batch * 16, (batch + 1) * 16));
const observe = (stage, state, full, enabled = true) => {
    // At most four reply batches per owner request, within its existing 8 KiB limit.
    const batches = full ? Array.from({ length: 4 }, (_, index) => fullPalette.slice(index * 4, (index + 1) * 4)) : [[sampleIndices]];
    return batches.map((group, index) => ({
        stage: index ? stage + '-palette-' + index : stage, state, sample: true, batches: group, enabled, continuation: index > 0,
        queries: [...(index ? [] : [dynamicQueries + modeQueries]), ...group.map(paletteQuery)],
    }));
};
const setColors = state => osc('104') + channels.map(([code], index) => osc(code + ';#' + sets[state][index])).join('')
    + Object.entries(palettes[state]).map(([index, rgb]) => osc('4;' + index + ';#' + rgb)).join('');

function pinnedSteps(id) {
    const start = id + '/initial';
    const query = step => ({ stage: id + '/' + step, pinned: id, sample: true, queries: [foregroundQuery + da] });
    return [query('initial'), { stage: id + '/push', pinned: id, write: push },
        { stage: id + '/mutate', pinned: id, write: 'mutate', from: start }, query('changed'),
        { stage: id + '/pop', pinned: id, write: pop }, query('restored'),
        { stage: id + '/cleanup', pinned: id, write: 'cleanup', from: start },
        { stage: id + '/reset-override', write: osc('110'), state: 'original' }];
}

export function planFor(runId) {
    return [
        { stage: 'start' }, ...observe('original', 'original', true, false),
        { stage: 'bind', write: '\x1b[?1049h\x1b[0m\x1b[2J\x1b[H' + nonce(runId)
            + '\x1b[2;1H\x1b[1;31mAa\x1b[0m Bb' + modes(true), state: 'original' },
        ...caseIds.flatMap(pinnedSteps),
        { stage: 'set-a', write: setColors('A'), state: 'A' }, ...observe('a', 'A', false),
        { stage: 'push-a', write: push, state: 'A' }, ...observe('pushed-a', 'A', false),
        { stage: 'set-b', write: setColors('B'), state: 'B' }, ...observe('b', 'B', false),
        { stage: 'push-b', write: push, state: 'B' }, ...observe('pushed-b', 'B', false),
        { stage: 'set-c', write: setColors('C'), state: 'C' }, ...observe('c', 'C', false),
        { stage: 'pop-b', write: pop, state: 'B' }, ...observe('restored-b', 'B', true),
        { stage: 'pop-a', write: pop, state: 'A' }, ...observe('restored-a', 'A', true),
        { stage: 'empty-pop', write: pop, state: 'A' }, ...observe('after-empty-pop', 'A', false),
        { stage: 'reset', write: resetColors + modes(false), state: 'original' }, ...observe('restored-original', 'original', true, false),
        { stage: 'finish', write: leave, state: 'original' }, { stage: 'seal' },
    ];
}

export function pinnedReply(raw) {
    requireStack(raw.endsWith(da1), 'missing exact DA1 sentinel');
    const reply = raw.slice(0, -da1.length);
    if (reply) parseColorReply(reply, { code: 10 });
    return reply;
}

export function resolveWrite(step, samples) {
    if (!step.from) return step.write;
    const capture = samples.get(step.from)?.[0]?.sample;
    requireStack(capture, 'missing original foreground read');
    const reply = pinnedReply(rawText(capture, new Set()));
    requireStack(reply, 'no original OSC 10 reply');
    const before = reply.slice('\x1b]10;'.length, -2);
    const requested = before.toLowerCase() === 'rgb:aaaa/bbbb/cccc' ? 'rgb:12/34/56' : 'rgb:aa/bb/cc';
    return '\x1b]10;' + (step.write === 'mutate' ? requested : before) + '\x07';
}

const codes = channels.map(([, key]) => key);
const opaque = value => Number.isSafeInteger(value) && value >= 0xff000000 && value <= 0xffffffff;

export function colorState(theme, binding) {
    const colors = Object.fromEntries(codes.map(key => [key, theme[key + 'Argb']]));
    const overrides = Object.fromEntries(codes.map(key => [key, theme[key + 'Override']]));
    requireStack(codes.every(key => opaque(colors[key]) || (key.startsWith('selection') && colors[key] === null))
        && codes.every(key => typeof overrides[key] === 'boolean')
        && ['foreground', 'background'].every(key => colors[key] === binding[key + 'Argb'] && overrides[key] === binding[key + 'Override'])
        && same(theme.ansiPaletteArgb, binding.ansiPaletteArgb) && same(theme.paletteOverrideIndices, binding.paletteOverrideIndices)
        && !binding.reverseVideo, 'App color observations disagreed or were incomplete');
    return { colors, overrides, palette: binding.ansiPaletteArgb, indices: binding.paletteOverrideIndices };
}

const argb = hex => Number.parseInt('ff' + hex, 16);
export function expectedState(initial, label) {
    if (label === 'original') return initial;
    const palette = [...initial.palette];
    const indices = Object.keys(palettes[label]).map(Number);
    for (const index of indices) palette[index] = argb(palettes[label][index]);
    return {
        colors: Object.fromEntries(codes.map((key, index) => [key, argb(sets[label][index])])),
        overrides: Object.fromEntries(codes.map(key => [key, true])),
        palette, indices,
    };
}

const rgb = color => (color & 0xffffff).toString(16).padStart(6, '0');
function replyOf(code, color) {
    if (color === null) return '';
    const [r, g, b] = rgb(color).match(/../g);
    return osc(code + ';rgb:' + r + r + '/' + g + g + '/' + b + b);
}

export function verifySamples(step, samples, used, state) {
    requireStack(samples?.length === step.queries.length, 'incomplete color query stage');
    const observed = step.queries.map((request, index) => {
        requireStack(samples[index].requestBytes === request, 'query bytes or order changed');
        return rawText(samples[index].sample, used);
    });
    if (step.pinned) {
        const reply = pinnedReply(observed[0]);
        requireStack(reply === replyOf(10, state.colors.foreground), 'pinned OSC 10 reply disagreed with the App foreground');
        return { type: 'query', request: step.queries[0], reply, raw: observed[0] };
    }
    if (!step.continuation) {
        const dynamic = channels.map(([code, key]) => replyOf(code, state.colors[key])).join('');
        requireStack(observed.shift() === dynamic + modeReplies(step.enabled), 'dynamic OSC or unrelated mode replies disagreed with the observed state');
    }
    requireStack(step.batches.every((batch, at) => observed[at] === batch.map(index => replyOf('4;' + index, state.palette[index])).join('')),
        'OSC 4 palette replies disagreed with the App readback');
    return { stage: step.stage, ...state, queriedPaletteEntries: step.batches.flat().length, modes: step.enabled ? 'set' : 'reset' };
}
