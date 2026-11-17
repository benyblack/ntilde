// Versioned probe contract for the rxvt special-color qualification batch: extensions.osc5-special-color (OSC 5;0;? BEL). This is not a publication or a measurement.
//
// Pinned terminfo.dev@4.0.0 definition (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6), read from the bundle, never inferred from the name. It is a query-only probe:
//   termless  oscReplyResult(feedCapture(OSC 5;0;? BEL)): the complete frame ESC]5;0;rgb:h{1,4}/h{1,4}/h{1,4} ended by BEL or ST is supported ("Complete OSC 5 index-0 color reply"); a capture holding
//             the ESC]5;0; prefix without a complete frame is inconclusive/invalid-reply; no prefix is inconclusive/no-response.
//   terminal  queryWithSentinelOutcome(the same request, the same frame pattern): the sentinel outcome's reply goes through the same oscReplyResult (no match is inconclusive).
// It can never return unsupported. The pinned verdict therefore stays what the unchanged pinned code returns over the journaled raw bytes of its own fenced exchange (both paths); the graded
// conclusion belongs to a separately identified effect contract (ntilde-osc5-special-colors-effect-v1) and a difference between the two is recorded as a divergence, never absorbed.
//
// The effect oracle is written from rxvt's special-color text (OSC 5 ; c ; spec changes special color number c — 0 bold/colorBD, 1 underline/colorUL, 2 blink/colorBL, 3 reverse/colorRV, 4 italic/colorIT —
// "any number of c/spec pairs may be given"; a ? is answered by a control sequence of the same form; OSC 105 ; c resets special color c to its resource default, and a bare OSC 105 resets all five). The
// special colors are their own resource set: nothing here shares state with the OSC 4 palette, the OSC 10/11/12/17/19 dynamic colors or the kitty OSC 21 keys. Ntilde stores, queries and resets the five
// slots; a slot's default is the default foreground (the App has no per-attribute color resources), and rxvt's OSC 106 color-mode toggles are not modeled, so a stored special color is never render-applied
// (both recorded policy). Replies are always ST-terminated regardless of how the request ended, echo the selector back, and use sixteen-bit rgb channels like every legacy color reply.
//
// Limits: only numeric selectors 0-4 are modeled (names such as "bold" are ignored); unmodeled selectors change nothing and stay silent; a sequence over 32768 characters is dropped whole; the colors are
// reset with a bare OSC 105 at the end; raw replies and stored state only — no rendering, painted-pixel or stock-grade claim.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { colorBindingHash, copyColorBinding } from './resolved-colors.mjs';
import { selectiveEraseInk } from './selective-erase-contracts.mjs';
import { devqColorReset, devqFixture, devqMinimum, devqReseed, devqReset, devqSeed } from './devq-contracts.mjs';
export { validateDevqScreen as validateOsc5Screen } from './devq-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const osc5ContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const osc5SuiteId = 'ntilde-owned-osc5-special-colors-graded-v1';
/** The separately identified effect contract. */
export const osc5EffectContractId = 'ntilde-osc5-special-colors-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const osc5NoncePrefix = 'NTILDE_O5_';
/** Owner-protocol prefix of this suite's observation requests. */
export const osc5Prefix = 'scol';
/** The pinned case, in suite order. */
export const osc5CaseIds = Object.freeze(['extensions.osc5-special-color']);
/** The measured geometry the controls need (the shared device-query seed: margins 2..6, a nonce row and a coloured/bold/underlined seed row). */
export const osc5Minimum = devqMinimum;
export const osc5Reset = devqReset;
export const osc5Reseed = devqReseed;
export const osc5Seed = devqSeed;
/** The reset every phase ends with: every palette entry and the five dynamic colors back to the host's. */
export const osc5ColorReset = devqColorReset;
/** The run's baseline title, written by the finish so the pane never keeps a test label. */
export const osc5BaseTitle = runId => `O5-base-${runId.slice(0, 8)}`;

const esc = '\x1b';
const st = esc + '\\';
const bel = '\x07';
/** Build a regular expression from a source written with <ESC>, so no control character sits in a pattern literal. */
const re = (source, flags = '') => new RegExp(source.replaceAll('<ESC>', String.fromCodePoint(27)), flags);

// ---------- the pinned probe (read from the bundle) ----------

/** The pinned request (BEL-terminated, exactly as registered), and the frame pattern the pinned code searches the captured text for. */
export const osc5PinnedQuery = `${esc}]5;0;?${bel}`;
/** The pinned code's frame pattern spells its control characters as text escapes (the collector compares the source text exactly), so they are written as text here too. */
const escText = String.raw`\x1b`, belText = String.raw`\x07`;
export const osc5PinnedPattern = new RegExp(`${escText}\\]5;0;rgb:[0-9a-f]{1,4}/[0-9a-f]{1,4}/[0-9a-f]{1,4}(?:${belText}|${escText}\\\\)`, 'i');
const pinnedPrefix = new RegExp(`${escText}\\]5;0;`, 'i');
const pinnedExpected = 'Complete OSC 5 index-0 color reply';

/** The exact calls each pinned path makes, in order. `replies` names which journaled bytes the captured call returns. */
export const osc5PinnedScripts = Object.freeze({
    'extensions.osc5-special-color': Object.freeze({
        termless: Object.freeze([['feedCapture', osc5PinnedQuery, 'replies']]),
        terminal: Object.freeze([['queryWithSentinelOutcome', osc5PinnedQuery, osc5PinnedPattern.source]]),
        expected: pinnedExpected,
    }),
});
const pinnedCriteria = Object.freeze({
    'extensions.osc5-special-color': 'OSC 5;0;? BEL answered by one complete ESC]5;0;rgb:h{1,4}/h{1,4}/h{1,4} frame ended by BEL or ST (case-insensitive); a prefixed capture without a complete frame is inconclusive (invalid-reply) and no frame is inconclusive (no-response); it can never be unsupported',
});

/** The pinned oscReplyResult over one response text (key order is part of the retained result; the pinned code sets no note). */
function specialColorResult(response) {
    const match = osc5PinnedPattern.exec(response);
    if (match !== null) {
        return { pass: true, response, observation: { outcome: 'supported', evidence: 'query' },
            assertions: [{ kind: 'positive', expected: pinnedExpected, observed: match[0] }] };
    }
    const reason = pinnedPrefix.test(response) ? 'invalid-reply' : 'no-response';
    return { pass: false, response, observation: { outcome: 'inconclusive', evidence: 'query', reason } };
}

/** The pinned result of one path over the journaled bytes of the case's own exchange: `replies` (the captured reply text before the barrier) and `barrier`. */
export function replayOsc5Pinned(caseId, path, { replies = '', barrier = '' } = {}) {
    if (osc5PinnedScripts[caseId] === undefined) throw new Error(`Unknown special-color case ${caseId}.`);
    if (path === 'termless') return specialColorResult(replies);
    if (path !== 'terminal') throw new Error(`Unknown pinned path ${path}.`);
    return specialColorResult(replies + barrier);
}

// ---------- the independent reply oracle ----------

const slots = Object.freeze([0, 1, 2, 3, 4]);
const hex2 = value => value.toString(16).padStart(2, '0');
const channelsOf = hex => [1, 3, 5].map(at => Number.parseInt(hex.slice(at, at + 2), 16));
const narrow = hex => `rgb:${channelsOf(hex).map(hex2).join('/')}`;
const wide = value => `rgb:${value.slice(4).split('/').map(part => part.repeat(2)).join('/')}`;
const rgbDigits = '([0-9a-f]{1,4})';
const rgbForm = re(`^rgb:${rgbDigits}/${rgbDigits}/${rgbDigits}$`, 'i');
const rgbiPart = String.raw`(\d(?:\.\d+)?)`;
const rgbiForm = re(`^rgbi:${rgbiPart}/${rgbiPart}/${rgbiPart}$`);
const hexTriple = (digits, convert) => `#${[0, 1, 2].map(i => hex2(convert(Number.parseInt(digits.slice(i * digits.length / 3, (i + 1) * digits.length / 3), 16)))).join('')}`;

/** A numeric color as the legacy color text defines it, as `#rrggbb`, or null for anything else (names and an alpha suffix are policy: ignored). Channels scale exactly like the parser's. */
export function parseOsc5Color(value) {
    if (/^#[0-9a-f]{6}$/i.test(value)) return value.toLowerCase();
    if (/^#[0-9a-f]{3}$/i.test(value)) return hexTriple(value.slice(1), part => Math.floor(part * 255 / 15));
    if (/^#[0-9a-f]{9}$/i.test(value)) return hexTriple(value.slice(1), part => Math.floor(part * 255 / 4095));
    if (/^#[0-9a-f]{12}$/i.test(value)) return hexTriple(value.slice(1), part => Math.floor(part * 255 / 65535));
    const wideMatch = rgbForm.exec(value);
    if (wideMatch) return `#${wideMatch.slice(1).map(part => hex2(Math.floor(Number.parseInt(part, 16) * 255 / (16 ** part.length - 1)))).join('')}`;
    const intensity = rgbiForm.exec(value);
    if (intensity) return `#${intensity.slice(1).map(part => hex2(Math.floor(Math.min(1, Number(part)) * 255))).join('')}`;
    return null;
}

/** The oracle's state and request builder: every call applies its effect to the model in time order and returns the bytes and the reply template it demands. A template is a literal reply string, or an
 *  OSC 5 `{ selector }` reference to a slot still at its default (whose value only the baseline knows). */
class SpecialColorModel {
    constructor() { this.colors = {}; this.defaults = {}; }
    reset() { this.colors = {}; }
    /** The demand for one query: a stored color and a slot still at its default are both complete OSC 5 frames — the stored one carries its narrow value here, the default one is resolved
     *  against the measured baseline in <see resolveReply/>. Selectors 0-4 are always baseline-measurable. */
    token(selector) {
        if (this.colors[selector] === undefined) return { selector };
        return { stored: String(selector), value: wide(narrow(this.colors[selector])) };
    }
    /** One OSC 5 sequence: chained "selector;value" pairs; a ? answers, a numeric value sets, anything else is ignored.
     *  A sequence over 32768 characters is dropped whole (policy), so none of its pairs apply. */
    osc5(text, terminator = st) {
        const demanded = [];
        const fields = text.length > 32768 ? [] : text.split(';');
        for (let i = 0; i + 1 < fields.length; i += 2) {
            const selector = fields[i];
            if (!/^[0-4]$/.test(selector)) continue;
            if (fields[i + 1] === '?') demanded.push(this.token(selector));
            else if (parseOsc5Color(fields[i + 1]) !== null) this.colors[selector] = parseOsc5Color(fields[i + 1]);
        }
        return { bytes: `${esc}]5;${text}${terminator}`, reply: demanded.length === 0 ? null : { demanded } };
    }
    frameBody(selector) { return narrow(this.colors[selector] ?? this.defaults[selector]); }
    /** One special-color reset: bare resets all five, listed selectors reset exactly those. */
    reset105(text) {
        if (text === '') this.colors = {};
        else for (const item of text.split(';')) {
            const selector = Number(item);
            if (Number.isInteger(selector) && selector >= 0 && selector <= 4 && String(selector) === item) delete this.colors[selector];
        }
        return { bytes: `${esc}]105${text === '' ? '' : `;${text}`}${st}`, reply: null };
    }
}

const colorSetA = Object.freeze({ 0: '#102030', 1: '#112233', 2: '#a1b2c3', 3: '#0f1e2d', 4: '#d2e3f4' });

/** The replies one group demands, with every default reference resolved; null when a reference cannot be resolved (the baseline group was not read). */
export function resolveDemand(group, baseline) {
    const out = [];
    for (const item of group.demand) {
        const text = resolveReply(item, baseline);
        if (text === null) return null;
        out.push(text);
    }
    return out;
}

function resolveReply(item, baseline) {
    if (typeof item === 'string') return item;
    if (item.demanded !== undefined) {
        const parts = item.demanded.map(field => (typeof field === 'string' ? field : resolveReply(field, baseline)));
        return parts.includes(null) ? null : parts.join('');
    }
    if (item.selector !== undefined) return defaultField(item.selector, baseline);
    if (item.stored !== undefined) return `${esc}]5;${item.stored};${item.value}${st}`;
    // A legacy reply (OSC 10/4/21) is demanded as the baseline-measured default: the cross-service groups prove a special-color set did not change it.
    if (item.legacyRef !== undefined) {
        const frame = baseline?.[`legacy-${item.legacyRef}`];
        return typeof frame === 'string' ? frame : null;
    }
    return null;
}

const defaultField = (selector, baseline) => (typeof baseline?.[selector] === 'string' ? `${esc}]5;${selector};${baseline[selector]}${st}` : null);


/** Whether the replies are exactly the demanded OSC 5 frames, in order (the strict list match also proves framing and count). */
function listOk(demand, replies) {
    return JSON.stringify(replies) === JSON.stringify(demand);
}

/** The host-default values the baseline groups measured: a selector/`rgb:rrrr/gggg/bbbb` value map with the measured legacy frames beside it, or null while a group is missing or malformed. */
export function baselineOf(observation) {
    const read = observation['baseline-special'];
    if (!read || read.replies.length !== 5) return null;
    const map = {};
    for (const [index, reply] of read.replies.entries()) {
        const frame = re(String.raw`^<ESC>\]5;([0-4]);(rgb:[0-9a-f]{4}/[0-9a-f]{4}/[0-9a-f]{4})<ESC>\\$`).exec(reply ?? '');
        if (frame === null || Number(frame[1]) !== index) return null;
        map[index] = frame[2];
    }
    const other = observation['baseline-other'];
    if (!other || other.replies.length !== 3) return null;
    const legacy = [['10', String.raw`^<ESC>\](10);rgb:[0-9a-f]{4}/[0-9a-f]{4}/[0-9a-f]{4}<ESC>\\$`],
        ['4;200', String.raw`^<ESC>\](4;200);rgb:[0-9a-f]{4}/[0-9a-f]{4}/[0-9a-f]{4}<ESC>\\$`],
        ['21-cursor', String.raw`^<ESC>\](21;cursor=rgb:[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{2})<ESC>\\$`]];
    for (const [index, [key, pattern]] of legacy.entries()) {
        const frame = re(pattern).exec(other.replies[index] ?? '');
        if (frame === null) return null;
        map[`legacy-${key}`] = frame[0];
    }
    return map;
}

// ---------- the groups ----------

const ask = (role, kind, note, spec) => {
    const request = spec.request.map(op => op.bytes).join('');
    if (request.length > 128) throw new Error(`The ${role} request exceeds the 128-byte exchange limit.`);
    const pinsBinding = !role.endsWith('-set');
    // A set-direction isolation group declares the one binding field its setup legitimately changes
    // (the OSC 10 foreground set overrides the resolved foreground; the OSC 4 palette set changes the
    // palette and the override list); everything else stays pinned. Read-direction groups pin everything.
    const openBindingFields = role === 'cross-dynamic-set' ? ['foregroundOverride', 'foregroundArgb']
        : role === 'cross-palette-set' ? ['ansiPaletteArgb', 'paletteOverrideIndices'] : [];
    return Object.freeze({ role, kind, note, setup: Object.freeze((spec.setup ?? []).map(op => op.bytes)), request, after: Object.freeze((spec.after ?? []).map(op => op.bytes)),
        demand: Object.freeze(spec.request.map(op => op.reply).filter(reply => reply !== null).flatMap(entry => (entry.demanded ? entry.demanded : [entry]))), shape: null, pinsBinding, openBindingFields });
};

const model = new SpecialColorModel();
const q = (text, terminator) => model.osc5(text, terminator);
const setA = () => slots.map(selector => model.osc5(`${selector};${colorSetA[selector]}`));
/** Every color the batch may have touched - palette, dynamic colors and special colors - back to the host defaults. */
const resetAll = () => [model.reset105(''), { bytes: devqColorReset + `${esc}]105${st}`, reply: null }];
const baselineGroup = ask('baseline-special', 'control', 'the default of every special color is read back (five complete frames in selector order) and becomes the restoration oracle',
    { request: [q('0;?;1;?;2;?;3;?;4;?')] });

const groupList = [
    baselineGroup,
    ask('pinned', 'protocol', 'the exact pinned BEL-terminated request on the untouched default reports selector 0 as one complete OSC 5 frame ended by ST', { request: [q('0;?', bel)] }),
    ask('setup-A', 'control', 'special-color set A is written through OSC 5 and read back per slot', { setup: setA(), request: [q('0;?;1;?;2;?;3;?;4;?')], after: resetAll() }),
    ask('set-then-query', 'protocol', 'a set and a query in one OSC 5 are answered with the new color', { request: [q('0;#0a1b2c;0;?')], after: resetAll() }),
    ask('per-slot', 'protocol', 'each of the five slots sets, queries and reads back independently', { request: [q('0;#111111;1;?;1;#222222;2;?;2;#333333;3;?')] }),
    ask('encodings', 'protocol', 'the numeric encodings scale to the stored eight-bit color and the reply reports it with sixteen-bit channels', { request: [q('0;rgb:f/0/8;0;?;1;#abc;1;?;2;rgbi:1/0/0.2;2;?')], after: resetAll() }),
    ask('bel-terminated', 'protocol', 'a request ended by BEL is answered by a reply ended by ST', { request: [q('1;?', bel)] }),
    ask('two-sequences', 'protocol', 'two OSC 5 queries in one request are answered by two replies in request order', { request: [q('0;?'), q('2;?')] }),
    ask('reset-one', 'protocol', 'OSC 105 with one selector resets exactly that slot to the default', { setup: [q('0;#101010;1;#202020')], request: [model.reset105('0'), q('0;?;1;?')] }),
    ask('reset-all', 'protocol', 'a bare OSC 105 resets all five slots to the default', { setup: [q('0;#101010;1;#202020;2;#303030;3;#404040;4;#505050')], request: [model.reset105(''), q('0;?;1;?;2;?;3;?;4;?')] }),
    // The isolation groups are one-directional pairs whose single reply is always demandable: a set of the
    // other service answers the special-color baseline, and a special-color set answers the other service's
    // measured default. A theme-dependent value is never demanded literally.
    ask('baseline-other', 'control', 'the dynamic foreground, palette slot 1 and the kitty cursor read back through OSC 10, OSC 4 and OSC 21 and become the isolation oracle',
        { request: [{ bytes: `${esc}]10;?${st}`, reply: { legacyRef: '10' } }, { bytes: `${esc}]4;200;?${st}`, reply: { legacyRef: '4;200' } }, { bytes: `${esc}]21;cursor=?${st}`, reply: { legacyRef: '21-cursor' } }] }),
    ask('cross-dynamic-set', 'protocol', 'an OSC 10 foreground set does not change the OSC 5;0 readback', { setup: [{ bytes: `${esc}]10;#abcdef${st}`, reply: null }], request: [q('0;?')], after: resetAll() }),
    ask('cross-dynamic-read', 'protocol', 'an OSC 5;0 set does not change the OSC 10 readback', { setup: [q('0;#101010')], request: [{ bytes: `${esc}]10;?${st}`, reply: { legacyRef: '10' } }], after: resetAll() }),
    ask('cross-palette-set', 'protocol', 'an OSC 4 palette set (slot 200, unused by the seed) does not change the OSC 5;1 readback', { setup: [{ bytes: `${esc}]4;200;#121212${st}`, reply: null }], request: [q('1;?')], after: resetAll() }),
    ask('cross-palette-read', 'protocol', 'an OSC 5;1 set does not change the OSC 4 slot-200 readback', { setup: [q('1;#141414')], request: [{ bytes: `${esc}]4;200;?${st}`, reply: { legacyRef: '4;200' } }], after: resetAll() }),
    ask('cross-kitty-set', 'protocol', 'a kitty cursor set does not change the OSC 5;2 readback', { setup: [{ bytes: `${esc}]21;cursor=#131313${st}`, reply: null }], request: [q('2;?')], after: resetAll() }),
    ask('cross-kitty-read', 'protocol', 'an OSC 5;2 set does not change the OSC 21 cursor readback', { setup: [q('2;#151617')], request: [{ bytes: `${esc}]21;cursor=?${st}`, reply: { legacyRef: '21-cursor' } }], after: resetAll() }),
    ask('restore', 'protocol', 'after the special resets a set color reads back as the baseline', { setup: [q('0;#010101;1;#020202;2;#030303;3;#040404;4;#050505'), ...resetAll()], request: [q('0;?;1;?;2;?;3;?;4;?')] }),
    ask('unrelated-selector', 'policy', 'a selector outside 0-4 (named or numeric) changes nothing and stays silent, and valid pairs in the same sequence still apply', { request: [q('bold;#121212;5;#131313;0;#141414;0;?')], after: resetAll() }),
    ask('malformed', 'policy', 'a malformed pair is ignored without disturbing the reply framing', { request: [q('0;not-a-color;0;?;0')] }),
    ask('set-only-silent', 'policy', 'an OSC 5 without a query is not answered', { request: [q('0;#010203')], after: resetAll() }),
    ask('reset-noise', 'policy', 'OSC 105 ignores unknown selectors and resets the listed one', { setup: [q('0;#101010')], request: [model.reset105('0;bold;5;-3'), q('0;?')] }),
    ask('oversize-dropped', 'policy', 'an OSC 5 over 32768 characters is dropped whole: its set takes no effect',
        { setup: [q(`0;#999999${';1;#000000'.repeat(3400)}`)], request: [q('0;?')], after: resetAll() }),
];
model.reset();

/** The plan of the case: the reseed, an initial cells read, then for each group its uncaptured setup feeds, the captured request, its report read and its uncaptured after-feeds, and the color reset. */
function planOf(groups) {
    const plan = [planFeed(osc5Reseed), planRead('cells')];
    for (const item of groups) {
        plan.push(...item.setup.map(planFeed), { feed: item.request, capture: true }, planRead('report'), ...item.after.map(planFeed));
    }
    plan.push(planFeed(osc5ColorReset));
    return plan;
}

const appearanceScope = 'rxvt special colors (OSC 5/105) sets, queries and resets only: the pinned index-0 request, all five slots (bold, underline, blink, reverse, italic) set and read back through OSC 5, '
    + 'per-slot and bare OSC 105 resets, the numeric encodings, chained pairs, reply order and the ST terminator, and the separation from the OSC 4 palette, the OSC 10-19 dynamic colors and the kitty OSC 21 keys; '
    + 'numeric selectors 0-4 only, ignored unmodeled selectors, malformed pairs, the oversize cap and set-only silence are recorded policy; slots default to the default foreground and a stored special color is '
    + 'never render-applied; raw terminal state, no rendering or painted-pixel claim, and the pinned probe tests only the index-0 query';

// ---------- state, seed and grading ----------

const groupOf = (screen, replies) => ({ screen, ink: selectiveEraseInk(screen), cursor: [screen.cursorRow, screen.cursorCol], cursorVisible: screen.cursorVisible,
    cursorMode: screen.cursorMode, rows: screen.rows, cols: screen.cols, margins: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom],
    origin: screen.bufferState.originMode, alternate: screen.bufferState.alternateScreen, buffer: screen.bufferState,
    horizontalMargins: screen.horizontalMargins, colorBinding: colorBindingHash(screen.colorBinding), colorState: copyColorBinding(screen.colorBinding), replies });
/** Everything an exchange must leave unchanged. */
const stateKey = state => JSON.stringify([state.ink, state.cursor, state.cursorVisible, state.cursorMode, state.buffer, state.horizontalMargins]);
const printable = raw => (raw === null ? null : raw.replaceAll(esc, String.raw`\e`).replaceAll(bel, String.raw`\a`));

/** Whether the initial read holds the contracted seed: geometry, nonce at home, the attributed cells, margins and modes. */
function seedOk(state, nonce) {
    const { seedCells, seedMargins } = devqFixture;
    const rowCount = (cells, row) => cells.filter(cell => cell.row === row).length;
    const cellOk = spec => {
        const cell = state.ink.find(item => item.row === spec.row && item.col === spec.col);
        return cell !== undefined && cell.char === spec.char && cell.bold === (spec.bold ?? false)
            && (spec.fgIndex === undefined || cell.fgIndex === spec.fgIndex) && (spec.underline === undefined || cell.underline === spec.underline);
    };
    return state.rows >= osc5Minimum.rows && state.cols >= osc5Minimum.cols && state.screen.lines[0] === nonce && state.cursor.every(value => value === 0)
        && state.margins[0] === seedMargins[0] && state.margins[1] === seedMargins[1] && state.origin === false && state.alternate === true
        && [1, 3].every(row => rowCount(state.ink, row) === rowCount(seedCells, row)) && seedCells.every(cellOk);
}

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });

/** The way one exchange group differs from its demand, or null: unchanged state, then the color binding, then the reply list (baseline references resolved). The stored special colors are never
 *  render-applied, so the palette, theme and render state must equal the initial read at EVERY read - a collateral mutation of any unrelated color is rejected here, not only at the cleanup edges. */
function groupMismatch(group, state, initial, baseline) {
    if (stateKey(state) !== stateKey(initial)) {
        return `The ${group.role} request changed the screen, the cursor (position, visibility or stored mode), the rendition, the margins (vertical or horizontal), the wrap/insert modes or the active screen: a color request must change nothing but the special colors`;
    }
    // A read-direction group pins the whole color binding: its own set touches only the never-render-applied
    // special slots, so the theme and palette must equal the initial read. A set-direction group pins every
    // binding field except the ones its declared setup changes, so a collateral mutation of any unrelated
    // color is still rejected while the declared set applies.
    if (group.openBindingFields !== undefined) {
        // The declared setup may change exactly its declared fields; every other binding field must equal
        // the initial read (which the fixture expresses as an unchanged binding and a real run as one whose
        // cleanup already restored).
        const declared = new Set(group.openBindingFields);
        const left = { ...state.colorState }, right = { ...initial.colorState };
        for (const field of declared) { delete left[field]; delete right[field]; }
        if (JSON.stringify(left) !== JSON.stringify(right)) {
            return declared.size === 0
                ? `The ${group.role} request changed the palette, the theme or the render state: an OSC 5 request must not touch any color outside its own five slots`
                : `The ${group.role} request changed the palette, the theme or the render state beyond its declared setup: only ${[...declared].join(' and ')} may differ, and only through the declared set`;
        }
    } else if (group.pinsBinding && state.colorBinding !== initial.colorBinding) {
        return `The ${group.role} request changed the palette, the theme or the render state: an OSC 5 request must not touch any color outside its own five slots`;
    }
    const demand = resolveDemand(group, baseline);
    if (demand === null) return `The ${group.role} demand depends on a baseline that was not measured`;
    if (JSON.stringify(state.replies) === JSON.stringify(demand)) return null;
    if (state.replies.length === 0 && demand.length > 0) return `The ${group.role} request was not answered`;
    if (state.replies.length !== demand.length) {
        return `The ${group.role} reply count differs from the contract: ${state.replies.length} replies where ${demand.length} were demanded (count, order and framing are part of the reply)`;
    }
    return `The ${group.role} reply differs from the reply oracle: a selector echo, color value or terminator was wrong`;
}

/** The case's grade in execution order, so an earlier failure is never overridden by a later group it damaged: the seed, then each group as the plan ran it. The first failing control makes the case
 *  inconclusive; the first protocol group that mismatches makes it unsupported; policy groups never decide. */
function gradeCase(item, observation) {
    if (!seedOk(observation.initial, observation.nonce)) return inconclusive('The attributed seed (nonce, cells, margins and modes) was not measured before any request');
    const baseline = baselineOf(observation);
    for (const group of item.groups.filter(entry => entry.kind !== 'policy')) {
        const mismatch = groupMismatch(group, observation[group.role], observation.initial, baseline);
        if (mismatch === null) continue;
        return group.kind === 'control' ? inconclusive(`Setup control failed before the effect could be judged: ${mismatch}`) : unsupported(mismatch);
    }
    return { outcome: 'supported' };
}

/** The observed record of one case: every group with its demanded and received replies and whether the state was unchanged. */
function briefCase(item, observation) {
    const baseline = baselineOf(observation);
    const groups = Object.fromEntries(item.groups.map(group => {
        const state = observation[group.role], demand = resolveDemand(group, baseline);
        const bindingUnchanged = !group.pinsBinding || state.colorBinding === observation.initial.colorBinding;
        const matches = demand !== null && JSON.stringify(state.replies) === JSON.stringify(demand) && bindingUnchanged;
        return [group.role, { kind: group.kind, request: printable(group.request), ...(group.setup.length === 0 ? {} : { setup: group.setup.map(printable) }),
            ...(group.after.length === 0 ? {} : { after: group.after.map(printable) }), demand: group.note, demanded: demand === null ? null : demand.map(printable),
            received: state.replies.map(printable), matches, unchanged: stateKey(state) === stateKey(observation.initial), bindingUnchanged }];
    }));
    const { rows, cols } = observation.initial, script = item.pinnedScript;
    return { geometry: { rows, cols }, effectContract: osc5EffectContractId, pinnedScript: { termless: script.termless.map(call => call.map(printable)), terminal: script.terminal.map(call => call.map(printable)) },
        seed: { nonce: observation.nonce, margins: observation.initial.margins, origin: observation.initial.origin, alternate: observation.initial.alternate,
            cells: seedOk(observation.initial, observation.nonce) }, baseline, groups };
}

/** The contracted case. */
function makeContract(caseId) {
    const groups = Object.freeze(groupList), script = osc5PinnedScripts[caseId];
    const roles = Object.freeze(['initial', ...groups.map(group => group.role)]);
    const pinned = groups.find(group => group.role === 'pinned');
    if (pinned.request !== osc5PinnedQuery || pinned.setup.length > 0) throw new Error('The pinned group must send exactly the pinned request.');
    const contract = {
        caseId, probeId: caseId, id: 'osc5-special-color', roles, groups, plan: Object.freeze(planOf(groups)),
        pinnedPath: 'termless', pinnedRead: roles.indexOf('pinned'), pinnedExpected: script.expected, pinnedCriterion: pinnedCriteria[caseId], pinnedScript: script,
        pinnedFeeds: Object.freeze([osc5PinnedQuery]), pinnedSetup: false,
        terminalFixture: { writes: [osc5PinnedQuery], demand: 'a framed OSC 5 reply matching the pinned pattern within 2000 ms of the sentinel write', minimum: { rows: 1, cols: 1 },
            note: 'the stock collector\'s queryWithSentinelOutcome path; replayed over the same journaled bytes beside the termless verdict' },
        effectContractId: osc5EffectContractId, appearanceScope, evidence: 'owned-raw-replies-and-unchanged-state',
        grade: observation => gradeCase(contract, observation), observed: observation => briefCase(contract, observation),
    };
    return Object.freeze(contract);
}

export const osc5Contracts = Object.freeze(osc5CaseIds.map(makeContract));
/** Contract case ids in suite order. */
export const osc5ContractCaseIds = Object.freeze(osc5Contracts.map(item => item.caseId));
export { planFeeds as osc5Feeds, planReads as osc5Reads } from './graded-plan.mjs';

/** The seed bytes of the plan (the reset followed by the attributed seed and nonce). */
export function osc5PlanSeed(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid special-color fixture run identity.');
    return osc5Reset + osc5Seed(osc5NoncePrefix + runId);
}
/** The finish: the screen and colors reset, the baseline title written, the alternate screen left. */
export const osc5Finish = runId => osc5Reset + osc5ColorReset + `${esc}]0;${osc5BaseTitle(runId)}${st}` + '\x1b[?1049l';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateOsc5Feeds(item, feeds) { validateExactFeeds(item, feeds, 'contract'); }

/** Group the read screens and replies of the case by role. */
export function osc5Observation(item, screens, replies, nonce) {
    const reads = item.plan.filter(step => step.read);
    if (!Array.isArray(screens) || screens.length !== reads.length || !Array.isArray(replies) || replies.length !== reads.length || typeof nonce !== 'string') {
        throw new Error('Missing special-color read captures.');
    }
    const observation = Object.fromEntries(item.roles.map((role, index) => [role, groupOf(screens[index], replies[index])]));
    observation.nonce = nonce;
    return observation;
}

/** Independent contract grader entry. */
export function gradeOsc5Case(item, observation, revision = osc5ContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown special-color contracts revision ${revision}.`);
    if (!observation || item.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) throw new Error(`Missing ${item.caseId} read captures.`);
    return item.grade(observation);
}

/** The fixed fixture facts other modules and tests read. */
export const osc5Fixture = Object.freeze({ minimum: osc5Minimum, seedMargins: devqFixture.seedMargins, seedCells: devqFixture.seedCells, groupList, colorSetA, slots });
