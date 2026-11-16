// Versioned probe contract for the kitty color-control qualification batch: extensions.osc21-kitty-color (OSC 21;foreground=? ST). This is not a publication or a measurement.
//
// Pinned terminfo.dev@4.0.0 definition (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6), read from the bundle, never inferred from the name. It is a query-only probe:
//   termless  kittyForegroundResult(feedCapture(OSC 21;foreground=? ST)): the first `OSC 21;<body>` frame in the captured text; exactly one `foreground=` field whose value is empty or
//             rgb:h{1,4}/h{1,4}/h{1,4} is supported ("Foreground query recognized; setting colors and visible rendering were not tested"); a frame without exactly one valid foreground field is
//             inconclusive; no frame is inconclusive/no-response.
//   terminal  queryWithSentinelOutcome(the same request, the same frame pattern): no match is inconclusive/no-response, a match goes through the same kittyForegroundResult.
// It can never return unsupported. The pinned verdict therefore stays what the unchanged pinned code returns over the journaled raw bytes of its own fenced exchange (both paths); the graded
// conclusion belongs to a separately identified effect contract (ntilde-kitty-color-control-effect-v1) and a difference between the two is recorded as a divergence, never absorbed.
//
// The effect oracle is written from kitty's color-control text (https://sw.kovidgoyal.net/kitty/color-stack/#setting-and-querying-colors) and shares no code with the parser: a query value `?` is answered
// by one OSC 21 whose `key=value` fields follow the request order and are ended by ST; a color with no defined value is an empty value; a key the terminal has no color for is `unknown=<Base64 of
// the key, no padding>`; a bare key resets a color to the value it has when never set; a numeric color sets it; the encodings are rgb:h|hh|hhh|hhhh (scaled) and #RGB..#RRRRGGGGBBBB (most significant
// bits) and rgbi:. Colors are read back through OSC 21 itself and through the legacy OSC 4/10/11/12/17/19 queries (one shared state, sixteen-bit replies), and every group leaves the complete screen,
// cursor, rendition, margins, modes and active screen unchanged. Host defaults are never assumed: the baseline groups read every modeled slot first and later demands that rest on an untouched
// slot are baseline-relative. Roles: `protocol` groups decide a mismatch (unsupported); `control` groups prove the setup took effect (a failed control is inconclusive, never a verdict); `policy`
// groups record deliberate Ntilde choices (the modeled key set, ignored empty/named/alpha values, the sequence caps, kitty's short #RGB reading) and never decide.
//
// Limits: only the foreground, background, cursor, selection colors and palette entries 5 and 200 are read back; the keys cursor_text, visual_bell and transparent_background_color* are not modeled and
// answer `unknown`; no rendering or painted-pixel claim; the colors are reset with OSC 104/110/111/112/117/119 at the end, and an overridden slot returns to the host default (the baseline replies).
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { selectiveEraseInk } from './selective-erase-contracts.mjs';
import { devqColorReset, devqFixture, devqMinimum, devqReseed, devqReset, devqSeed } from './devq-contracts.mjs';
export { validateDevqScreen as validateKclScreen } from './devq-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const kclContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const kclSuiteId = 'ntilde-owned-kitty-color-graded-v1';
/** The separately identified effect contract. */
export const kclEffectContractId = 'ntilde-kitty-color-control-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const kclNoncePrefix = 'NTILDE_KC_';
/** Owner-protocol prefix of this suite's observation requests. */
export const kclPrefix = 'kcl';
/** The pinned case, in suite order. */
export const kclCaseIds = Object.freeze(['extensions.osc21-kitty-color']);
/** The measured geometry the controls need (the shared device-query seed: margins 2..6, a nonce row and a coloured/bold/underlined seed row). */
export const kclMinimum = devqMinimum;
export const kclReset = devqReset;
export const kclReseed = devqReseed;
export const kclSeed = devqSeed;
/** The reset every phase ends with: every palette entry and the five dynamic colors back to the host's. */
export const kclColorReset = devqColorReset;
/** The run's baseline title, written by the finish so the pane never keeps a test label. */
export const kclBaseTitle = runId => `KC-base-${runId.slice(0, 8)}`;

const esc = '\x1b';
const st = esc + '\\';
const bel = '\x07';
/** Build a regular expression from a source written with <ESC>, so no control character sits in a pattern literal. */
const re = (source, flags = '') => new RegExp(source.replaceAll('<ESC>', String.fromCodePoint(27)), flags);

// ---------- the pinned probe (read from the bundle) ----------

/** The pinned request, and the frame pattern both pinned paths search the captured text for. */
export const kclPinnedQuery = `${esc}]21;foreground=?${st}`;
/** The pinned code's frame pattern spells its control characters as text escapes (the collector compares the source text exactly), so they are written as text here too. */
const escText = String.raw`\x1b`, belText = String.raw`\x07`;
export const kclPinnedPattern = new RegExp(`${escText}\\]21;([^${belText}${escText}]*)(?:${belText}|${escText}\\\\)`);
const pinnedNoReply = 'No OSC 21 reply; color rendering was not tested';
const pinnedValueOk = re(String.raw`^rgb:[a-f\d]{1,4}/[a-f\d]{1,4}/[a-f\d]{1,4}$`, 'i');

/** The exact calls each pinned path makes, in order. `replies` names which journaled bytes the captured call returns. */
export const kclPinnedScripts = Object.freeze({
    'extensions.osc21-kitty-color': Object.freeze({
        termless: Object.freeze([['feedCapture', kclPinnedQuery, 'replies']]),
        terminal: Object.freeze([['queryWithSentinelOutcome', kclPinnedQuery, kclPinnedPattern.source]]),
        expected: 'OSC 21 foreground query returns RGB or an explicitly undefined value',
    }),
});
const pinnedCriteria = Object.freeze({
    'extensions.osc21-kitty-color': 'OSC 21;foreground=? ST returns an OSC 21 frame holding exactly one foreground field whose value is empty or rgb:h{1,4}/h{1,4}/h{1,4}; no frame is inconclusive (no-response) and no frame can be unsupported',
});

/** The pinned unansweredQuery result: the raw text answered with no frame (key order is part of the retained result). */
const unanswered = raw => ({ pass: false, response: raw, note: pinnedNoReply, observation: { outcome: 'inconclusive', evidence: 'query', reason: 'no-response', note: pinnedNoReply } });

/** The pinned kittyForegroundResult over one response text. */
function foregroundResult(response) {
    const match = kclPinnedPattern.exec(response);
    if (match === null) return unanswered(response);
    const values = match[1].split(';').filter(field => field.startsWith('foreground='));
    const value = values.length === 1 ? values[0].slice('foreground='.length) : undefined;
    const pass = value === '' || (value !== undefined && pinnedValueOk.test(value));
    const note = pass ? 'Foreground query recognized; setting colors and visible rendering were not tested' : 'OSC 21 replied without one verifiable foreground value; no support conclusion';
    const result = { pass, response, note, observation: { outcome: pass ? 'supported' : 'inconclusive', evidence: 'query', note } };
    if (pass) result.assertions = [{ kind: 'positive', expected: kclPinnedScripts['extensions.osc21-kitty-color'].expected, observed: match[0] }];
    return result;
}

/** The pinned result of one path over the journaled bytes of the case's own exchange: `replies` (the captured reply text before the barrier) and `barrier`. */
export function replayKclPinned(caseId, path, { replies = '', barrier = '' } = {}) {
    if (kclPinnedScripts[caseId] === undefined) throw new Error(`Unknown kitty color case ${caseId}.`);
    if (path === 'termless') return foregroundResult(replies);
    if (path !== 'terminal') throw new Error(`Unknown pinned path ${path}.`);
    const raw = replies + barrier, match = kclPinnedPattern.exec(raw);
    return match === null ? unanswered(raw) : foregroundResult(match[0]);
}

// ---------- the independent reply oracle ----------

const names = Object.freeze(['foreground', 'background', 'cursor', 'selection_background', 'selection_foreground']);
/** The slots the contract reads back, in baseline order, and the legacy OSC that reads or writes the same state. */
const baselineKeys = Object.freeze([...names, '5', '200']);
const legacyOsc = Object.freeze({ foreground: '10', background: '11', cursor: '12', selection_background: '17', selection_foreground: '19', 5: '4;5', 200: '4;200' });
const isKey = key => names.includes(key) || (/^\d{1,3}$/.test(key) && Number(key) < 256 && String(Number(key)) === key);
const base64 = text => Buffer.from(text, 'utf8').toString('base64').replaceAll('=', '');
const hex2 = value => value.toString(16).padStart(2, '0');
const channelsOf = hex => [1, 3, 5].map(at => Number.parseInt(hex.slice(at, at + 2), 16));
const narrow = hex => `rgb:${channelsOf(hex).map(hex2).join('/')}`;
const wide = value => `rgb:${value.slice(4).split('/').map(part => part.repeat(2)).join('/')}`;
const rgbDigits = '([0-9a-f]{1,4})';
const rgbForm = re(`^rgb:${rgbDigits}/${rgbDigits}/${rgbDigits}$`, 'i');
const rgbiPart = String.raw`(\d(?:\.\d+)?)`;
const rgbiForm = re(`^rgbi:${rgbiPart}/${rgbiPart}/${rgbiPart}$`);
const hexTriple = (digits, convert) => `#${[0, 1, 2].map(i => hex2(convert(Number.parseInt(digits.slice(i * digits.length / 3, (i + 1) * digits.length / 3), 16)))).join('')}`;

/** A numeric color as kitty's text defines it, as `#rrggbb`, or null for anything else (names, an alpha suffix and the empty value are policy: ignored). Kitty's text does not say how a
 *  scaled channel that is not a whole byte is rounded (Ntilde truncates, like its OSC 10 parser); every deciding group therefore uses values that scale exactly, so the choice cannot decide a verdict. */
export function parseKittyColor(value) {
    if (/^#[0-9a-f]{6}$/i.test(value)) return value.toLowerCase();
    if (/^#[0-9a-f]{3}$/i.test(value)) return hexTriple(value.slice(1), part => part * 16); // most significant bits: #3a7 is #3000a0007000
    if (/^#[0-9a-f]{9}$/i.test(value)) return hexTriple(value.slice(1), part => part >> 4);
    const wideMatch = rgbForm.exec(value);
    if (wideMatch) return `#${wideMatch.slice(1).map(part => hex2(Math.round(Number.parseInt(part, 16) * 255 / (16 ** part.length - 1)))).join('')}`;
    const intensity = rgbiForm.exec(value);
    if (intensity) return `#${intensity.slice(1).map(part => hex2(Math.round(Math.min(1, Number(part)) * 255))).join('')}`;
    return null;
}

/** The oracle's state and request builder: every call applies its effect to the model in time order and returns the bytes and the reply template it demands. A template is a literal reply string, an
 *  OSC 21 `{ fields }` list (a field is a literal, or `{ base: key }` for a slot still at the host default) or a legacy `{ legacy, key }` reply (the baseline's sixteen-bit spelling). */
class ColorModel {
    constructor() { this.colors = {}; }
    reset() { this.colors = {}; }
    token(key) {
        if (!baselineKeys.includes(key) && this.colors[key] === undefined) throw new Error(`No baseline for key ${key}.`);
        return this.colors[key] === undefined ? { base: key } : `${key}=${narrow(this.colors[key])}`;
    }
    /** An OSC 21 sequence: sets, resets and queries apply in order; sequences over 4096 characters are dropped and only the first 256 fields count (both policy). */
    osc21(text, terminator = st) {
        const fields = [];
        let seen = 0;
        for (const field of text.length > 4096 ? [] : text.split(';')) {
            if (field === '') continue;
            if (++seen > 256) break;
            this.oscField(field, fields);
        }
        return { bytes: `${esc}]21;${text}${terminator}`, reply: fields.length === 0 ? null : { fields } };
    }
    /** One OSC 21 field: a query answers, a bare key resets, a numeric value sets (anything else leaves the color in force). */
    oscField(field, fields) {
        const at = field.indexOf('='), key = at < 0 ? field : field.slice(0, at), value = at < 0 ? null : field.slice(at + 1);
        if (!isKey(key)) fields.push(`unknown=${base64(key)}`);
        else if (value === '?') fields.push(this.token(key));
        else if (value === null) delete this.colors[key];
        else if (parseKittyColor(value) !== null) this.colors[key] = parseKittyColor(value);
    }
    /** A legacy OSC set (no reply). */
    legacySet(key, hex) {
        this.colors[key] = hex;
        return { bytes: `${esc}]${legacyOsc[key]};${hex}${st}`, reply: null };
    }
    /** A legacy OSC query: a sixteen-bit reply. A selection slot at its host default may legitimately stay silent, so it is never asked. */
    legacyAsk(key) {
        if (this.colors[key] === undefined && key.startsWith('selection')) throw new Error('A host-default selection color may have no reply.');
        const text = this.colors[key] === undefined ? { legacy: legacyOsc[key], key } : `${esc}]${legacyOsc[key]};${wide(narrow(this.colors[key]))}${st}`;
        return { bytes: `${esc}]${legacyOsc[key]};?${st}`, reply: text };
    }
    legacyReset() { this.reset(); return { bytes: devqColorReset, reply: null }; }
}

/** One legacy-OSC set of every slot of a color set (A: distinguishable from every other value in the contract). */
const colorSetA = Object.freeze({ foreground: '#102030', background: '#112233', cursor: '#a1b2c3', selection_background: '#0f1e2d', selection_foreground: '#d2e3f4', 5: '#445566', 200: '#123456' });

/** The replies one group demands, with every baseline reference resolved; null when a reference cannot be resolved (the baseline groups were not read). */
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
    if (item.fields !== undefined) {
        const parts = item.fields.map(field => (typeof field === 'string' ? field : baselineField(field.base, baseline)));
        return parts.includes(null) ? null : `${esc}]21;${parts.join(';')}${st}`;
    }
    const value = baseline?.[item.key];
    return typeof value === 'string' && value !== '' ? `${esc}]${item.legacy};${wide(value)}${st}` : null;
}

const baselineField = (key, baseline) => (typeof baseline?.[key] === 'string' ? `${key}=${baseline[key]}` : null);

const oscFrame = re(String.raw`^<ESC>\]21;([^<ESC>]*)<ESC>\\$`);
/** The [key, value] fields of one OSC 21 reply, or null when it is not exactly one complete OSC 21 of `key=value` fields. */
export function parseOsc21(reply) {
    const frame = oscFrame.exec(reply ?? '');
    if (frame === null) return null;
    const entries = [];
    for (const field of frame[1].split(';')) {
        const at = field.indexOf('=');
        if (at <= 0) return null;
        entries.push([field.slice(0, at), field.slice(at + 1)]);
    }
    return entries;
}

/** Whether the replies are exactly one OSC 21 whose keys are `keys` in order and whose values are rgb:rr/gg/bb (a selection color may be empty, being undefined at the host default). */
function shapeOk(keys, replies) {
    const entries = replies.length === 1 ? parseOsc21(replies[0]) : null;
    if (entries === null || entries.length !== keys.length) return false;
    return entries.every(([key, value], index) => key === keys[index] && (/^rgb:[0-9a-f]{2}\/[0-9a-f]{2}\/[0-9a-f]{2}$/.test(value) || (value === '' && key.startsWith('selection'))));
}

/** The host-default values the baseline groups measured: a key/value map, or null while a group is missing or malformed. */
export function baselineOf(observation) {
    const map = {};
    for (const role of ['baseline-main', 'baseline-selection', 'baseline-palette']) {
        const entries = observation[role]?.replies.length === 1 ? parseOsc21(observation[role].replies[0]) : null;
        if (entries === null) return null;
        for (const [key, value] of entries) map[key] = value;
    }
    return map;
}

// ---------- the groups ----------

const ask = (role, kind, note, spec) => {
    const request = spec.request.map(op => op.bytes).join('');
    if (request.length > 64) throw new Error(`The ${role} request exceeds the 64-byte exchange limit.`);
    return Object.freeze({ role, kind, note, setup: Object.freeze((spec.setup ?? []).map(op => op.bytes)), request, after: Object.freeze((spec.after ?? []).map(op => op.bytes)),
        demand: Object.freeze(spec.request.map(op => op.reply).filter(reply => reply !== null)), shape: spec.shape ?? null });
};

const model = new ColorModel();
const q = text => model.osc21(text);
const setA = () => baselineKeys.map(key => model.legacySet(key, colorSetA[key]));
/** The two baseline-restoring ends of a group: the legacy reset of every color, and a single slot's legacy reset. */
const resetAll = () => [model.legacyReset()];
const baselineGroup = (role, keys) => ask(role, 'control', `the host default of ${keys.join(', ')} is read back (one OSC 21, one complete rgb value per key in order; a selection color may be undefined) and becomes the restoration oracle`,
    { request: [q(keys.map(key => `${key}=?`).join(';'))], shape: keys });
const keyGroup = key => ask(`key-${key}`, 'protocol', `OSC 21 ${key}=? reports the color the legacy OSC ${legacyOsc[key]} set, as rgb:rr/gg/bb`, { request: [q(`${key}=?`)] });
const encoding = (role, value, note, kind = 'protocol') => ask(role, kind, note, { request: [q(`background=${value};background=?`)], after: kind === 'policy' ? resetAll() : [] });

const groupList = [
    baselineGroup('baseline-main', ['foreground', 'background', 'cursor']),
    baselineGroup('baseline-selection', ['selection_background', 'selection_foreground']),
    baselineGroup('baseline-palette', ['5', '200']),
    ask('pinned', 'protocol', 'the exact pinned request on the untouched host default reports the foreground as one OSC 21 field ended by ST', { request: [q('foreground=?')] }),
    ask('setup-A-main', 'control', 'color set A is written through the legacy OSC 10/11/12/17/19/4 sequences and the foreground, background and cursor read back through OSC 10/11/12 replies', { setup: setA(), request: ['foreground', 'background', 'cursor'].map(key => model.legacyAsk(key)) }),
    ask('setup-A-rest', 'control', 'the selection colors and palette entries 5 and 200 of set A read back through OSC 17/19 and OSC 4 replies', { request: ['selection_background', 'selection_foreground', '5', '200'].map(key => model.legacyAsk(key)) }),
    ...baselineKeys.map(keyGroup),
    ask('combined-order', 'protocol', 'five keys asked in one OSC 21 are answered in one OSC 21 whose fields follow the request order', { request: [q('cursor=?;5=?;foreground=?;200=?;background=?')] }),
    ask('bel-terminated', 'protocol', 'a request ended by BEL is answered by a reply ended by ST', { request: [model.osc21('background=?', bel)] }),
    ask('two-sequences', 'protocol', 'two OSC 21 queries in one request are answered by two replies in request order', { request: [q('foreground=?'), q('cursor=?')] }),
    ask('set-then-query', 'protocol', 'a set and a query in one OSC 21 are answered with the new color', { request: [q('foreground=#0a1b2c;foreground=?')] }),
    ask('set-readback-legacy', 'protocol', 'the color OSC 21 set is the color the legacy OSC 10 reports (one shared state, sixteen-bit reply)', { request: [model.legacyAsk('foreground')] }),
    encoding('encoding-rrggbb', '#223344', '#RRGGBB sets the background to its eight-bit channels'),
    encoding('encoding-rgb-1', 'rgb:f/0/8', 'rgb:h/h/h scales four-bit channels to eight bits (f is ff, 8 is 88)'),
    encoding('encoding-rgb-2', 'rgb:12/ab/ef', 'rgb:hh/hh/hh sets the eight-bit channels'),
    encoding('encoding-rgb-4', 'rgb:1234/5678/9abc', 'rgb:hhhh/hhhh/hhhh scales sixteen-bit channels to eight bits'),
    encoding('encoding-rgbi', 'rgbi:1/0/0.2', 'rgbi:r/g/b scales intensities in [0,1] to eight bits'),
    encoding('encoding-sharp-12', '#123456789', '#RRRGGGBBB keeps the most significant eight bits of each twelve-bit channel'),
    ask('palette-set', 'protocol', 'a palette index set through OSC 21 is reported by OSC 21', { request: [q('5=#667788;5=?')] }),
    ask('palette-readback-legacy', 'protocol', 'the palette color OSC 21 set is the color the legacy OSC 4 reports', { request: [model.legacyAsk('5')] }),
    ask('selection-set', 'control', 'the selection colors set through OSC 21 are reported by OSC 21', { setup: [q('selection_background=#1f2e3d;selection_foreground=#4c5b6a')], request: [q('selection_background=?;selection_foreground=?')] }),
    ask('selection-readback-legacy', 'protocol', 'the selection colors OSC 21 set are the colors the legacy OSC 17 and OSC 19 report', { request: [model.legacyAsk('selection_background'), model.legacyAsk('selection_foreground')] }),
    ask('reset-bare-key', 'protocol', 'a bare key resets the color to the host default: after resets the queries report the baseline',
        { setup: [q('foreground=#aa0011;background=#00aa11')], request: [q('foreground;background'), q('foreground=?;background=?')] }),
    ask('reset-palette', 'protocol', 'a bare palette index resets the entry to the host default', { setup: [q('5=#010203;200=#030201')], request: [q('5;200'), q('5=?;200=?')] }),
    ask('restore-main', 'protocol', 'after the legacy resets (OSC 104/110/111/112/117/119) a set color reads back as the baseline: the foreground, background and cursor',
        { setup: [q('foreground=#010101;background=#020202;cursor=#030303;selection_background=#040404;selection_foreground=#050505;5=#060606;200=#070707'), ...resetAll()], request: [q('foreground=?;background=?;cursor=?')] }),
    ask('restore-selection', 'protocol', 'the legacy resets restore the selection colors to the baseline', { request: [q('selection_background=?;selection_foreground=?')] }),
    ask('restore-palette', 'protocol', 'the legacy resets restore palette entries 5 and 200 to the baseline', { request: [q('5=?;200=?')] }),
    ask('unknown-key', 'protocol', 'a key the terminal has no color for is answered by unknown=<Base64 of the key>', { request: [q('nonsense=?')] }),
    ask('unknown-index', 'protocol', 'a palette index past 255 is not a color key and is answered as unknown', { request: [q('256=?')] }),
    ask('unknown-padding', 'protocol', 'the Base64 of an unknown key carries no padding', { request: [q('a=?;ab=?;abc=?')] }),
    ask('unknown-order', 'protocol', 'every unknown key gets one unknown field, in the order received', { request: [q('zeta=?;alpha=?')] }),
    ask('interleaved-unknown', 'policy', 'a known, an unknown and a known key keep the request order in one reply (kitty\'s text orders only the unknown fields among themselves)', { request: [q('foreground=?;nonsense=?;background=?')] }),
    ask('unmodeled-keys', 'policy', 'cursor_text and visual_bell are kitty keys Ntilde does not model and answers as unknown', { request: [q('cursor_text=?;visual_bell=?')] }),
    ask('unmodeled-transparent', 'policy', 'transparent_background_color1 is a kitty key Ntilde does not model and answers as unknown', { request: [q('transparent_background_color1=?')] }),
    ask('key-case', 'policy', 'keys are case-sensitive: Foreground is not foreground (kitty\'s text does not say)', { request: [q('Foreground=?')] }),
    encoding('encoding-sharp-4', '#3a7', 'kitty\'s text reads #RGB as most significant bits (#3a7 is #3000a0007000, eight-bit 30/a0/70); Ntilde scales short forms like its OSC 10 parser', 'policy'),
    encoding('encoding-rgbi-range', 'rgbi:2/0/0', 'kitty\'s text clips rgbi intensities to 0..1 (2 is 1, ff0000); Ntilde refuses an out-of-range intensity and keeps the color in force', 'policy'),
    ask('empty-value', 'policy', 'an empty value (kitty\'s "dynamic" color) is ignored: the color in force stays', { setup: [model.legacySet('foreground', '#0a0b0c')], request: [q('foreground=;foreground=?')], after: resetAll() }),
    ask('color-name', 'policy', 'a color name is not modeled and is ignored: the color in force stays', { setup: [model.legacySet('foreground', '#0a0b0c')], request: [q('foreground=red;foreground=?')], after: resetAll() }),
    ask('alpha-suffix', 'policy', 'an @alpha suffix is not modeled and the field is ignored: the color in force stays', { setup: [model.legacySet('foreground', '#0a0b0c')], request: [q('foreground=#102030@0.5;foreground=?')], after: resetAll() }),
    ask('set-only-silent', 'policy', 'an OSC 21 without a query is not answered', { request: [q('foreground=#010203;background')], after: resetAll() }),
    ask('oversize-dropped', 'policy', 'an OSC 21 over 4096 characters is dropped whole: neither its foreground nor its palette set takes effect',
        { setup: [model.legacySet('foreground', '#0d0e0f'), q(`foreground=#999999${';5=#000000'.repeat(420)}`)], request: [q('foreground=?;5=?')], after: resetAll() }),
    ask('field-cap', 'policy', 'only the first 256 fields of an OSC 21 are processed: the 257th palette set is ignored', { setup: [q(`${'5=#000001;'.repeat(256)}5=#000002`)], request: [q('5=?')], after: resetAll() }),
];
// The restoring ends above make every later group start from the host defaults; the model state after the last group is therefore the reset one.
model.reset();

/** The plan of the case: the reseed, an initial cells read, then for each group its uncaptured setup feeds, the captured request, its report read and its uncaptured after-feeds, and the color reset. */
function planOf(groups) {
    const plan = [planFeed(kclReseed), planRead('cells')];
    for (const item of groups) {
        plan.push(...item.setup.map(planFeed), { feed: item.request, capture: true }, planRead('report'), ...item.after.map(planFeed));
    }
    plan.push(planFeed(kclColorReset));
    return plan;
}

const appearanceScope = 'kitty color-control (OSC 21) queries only, plus the sets and resets that feed them: the pinned foreground request, every modeled key (foreground, background, cursor, selection colors and '
    + 'palette entries 5 and 200) read back through OSC 21 and the legacy OSC 4/10/11/12/17/19 replies, one shared state, reply order and the ST terminator, the numeric encodings, bare-key resets and '
    + 'restoration of the host default, unknown-key replies with unpadded Base64; the modeled key set, ignored empty/named/alpha values, the sequence caps and kitty\'s short #RGB reading are recorded policy; '
    + 'raw terminal state, no rendering or painted-pixel claim, and the pinned probe tests only the foreground query';

// ---------- state, seed and grading ----------

const groupOf = (screen, replies) => ({ screen, ink: selectiveEraseInk(screen), cursor: [screen.cursorRow, screen.cursorCol], cursorVisible: screen.cursorVisible,
    cursorMode: screen.cursorMode, rows: screen.rows, cols: screen.cols, margins: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom],
    origin: screen.bufferState.originMode, alternate: screen.bufferState.alternateScreen, buffer: screen.bufferState,
    horizontalMargins: screen.horizontalMargins, replies });
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
    return state.rows >= kclMinimum.rows && state.cols >= kclMinimum.cols && state.screen.lines[0] === nonce && state.cursor.every(value => value === 0)
        && state.margins[0] === seedMargins[0] && state.margins[1] === seedMargins[1] && state.origin === false && state.alternate === true
        && [1, 3].every(row => rowCount(state.ink, row) === rowCount(seedCells, row)) && seedCells.every(cellOk);
}

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });

/** The way one exchange group differs from its demand, or null: unchanged state first, then the reply list (or the baseline shape). */
function groupMismatch(group, state, initial, baseline) {
    if (stateKey(state) !== stateKey(initial)) {
        return `The ${group.role} request changed the screen, the cursor (position, visibility or stored mode), the rendition, the margins (vertical or horizontal), the wrap/insert modes or the active screen: a color request must change nothing but colors`;
    }
    if (group.shape !== null) return shapeOk(group.shape, state.replies) ? null : `The ${group.role} reply is not one complete OSC 21 with one rgb value per queried key in order`;
    const demand = resolveDemand(group, baseline);
    if (demand === null) return `The ${group.role} demand depends on a baseline that was not measured`;
    if (JSON.stringify(state.replies) === JSON.stringify(demand)) return null;
    if (state.replies.length === 0 && demand.length > 0) return `The ${group.role} request was not answered`;
    if (state.replies.length !== demand.length) {
        return `The ${group.role} reply count differs from the contract: ${state.replies.length} replies where ${demand.length} were demanded (count, order and framing are part of the reply)`;
    }
    return `The ${group.role} reply differs from the reply oracle: a field, key order, color value or terminator was wrong`;
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
        const state = observation[group.role], demand = group.shape === null ? resolveDemand(group, baseline) : null;
        const matches = group.shape === null ? demand !== null && JSON.stringify(state.replies) === JSON.stringify(demand) : shapeOk(group.shape, state.replies);
        return [group.role, { kind: group.kind, request: printable(group.request), ...(group.setup.length === 0 ? {} : { setup: group.setup.map(printable) }),
            ...(group.after.length === 0 ? {} : { after: group.after.map(printable) }), demand: group.note, demanded: demand === null ? null : demand.map(printable),
            received: state.replies.map(printable), matches, unchanged: stateKey(state) === stateKey(observation.initial) }];
    }));
    const { rows, cols } = observation.initial, script = item.pinnedScript;
    return { geometry: { rows, cols }, effectContract: kclEffectContractId, pinnedScript: { termless: script.termless.map(call => call.map(printable)), terminal: script.terminal.map(call => call.map(printable)) },
        seed: { nonce: observation.nonce, margins: observation.initial.margins, origin: observation.initial.origin, alternate: observation.initial.alternate,
            cells: seedOk(observation.initial, observation.nonce) }, baseline, groups };
}

/** The contracted case. */
function makeContract(caseId) {
    const groups = Object.freeze(groupList), script = kclPinnedScripts[caseId];
    const roles = Object.freeze(['initial', ...groups.map(group => group.role)]);
    const pinned = groups.find(group => group.role === 'pinned');
    if (pinned.request !== kclPinnedQuery || pinned.setup.length > 0) throw new Error('The pinned group must send exactly the pinned request.');
    const contract = {
        caseId, probeId: caseId, id: 'osc21-kitty-color', roles, groups, plan: Object.freeze(planOf(groups)),
        pinnedPath: 'termless', pinnedRead: roles.indexOf('pinned'), pinnedExpected: script.expected, pinnedCriterion: pinnedCriteria[caseId], pinnedScript: script,
        pinnedFeeds: Object.freeze([kclPinnedQuery]), pinnedSetup: false,
        terminalFixture: { writes: [kclPinnedQuery], demand: 'a framed OSC 21 reply matching the pinned pattern within 2000 ms of the sentinel write', minimum: { rows: 1, cols: 1 },
            note: 'the stock collector\'s queryWithSentinelOutcome path; replayed over the same journaled bytes beside the termless verdict' },
        effectContractId: kclEffectContractId, appearanceScope, evidence: 'owned-raw-replies-and-unchanged-state',
        grade: observation => gradeCase(contract, observation), observed: observation => briefCase(contract, observation),
    };
    return Object.freeze(contract);
}

export const kclContracts = Object.freeze(kclCaseIds.map(makeContract));
/** Contract case ids in suite order. */
export const kclContractCaseIds = Object.freeze(kclContracts.map(item => item.caseId));
export { planFeeds as kclFeeds, planReads as kclReads } from './graded-plan.mjs';

/** The seed bytes of the plan (the reset followed by the attributed seed and nonce). */
export function kclPlanSeed(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid kitty color fixture run identity.');
    return kclReset + kclSeed(kclNoncePrefix + runId);
}
/** The finish: the screen and colors reset, the baseline title written, the alternate screen left. */
export const kclFinish = runId => kclReset + kclColorReset + `${esc}]0;${kclBaseTitle(runId)}${st}` + '\x1b[?1049l';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateKclFeeds(item, feeds) { validateExactFeeds(item, feeds, 'contract'); }

/** Group the read screens and replies of the case by role. */
export function kclObservation(item, screens, replies, nonce) {
    const reads = item.plan.filter(step => step.read);
    if (!Array.isArray(screens) || screens.length !== reads.length || !Array.isArray(replies) || replies.length !== reads.length || typeof nonce !== 'string') {
        throw new Error('Missing kitty color read captures.');
    }
    const observation = Object.fromEntries(item.roles.map((role, index) => [role, groupOf(screens[index], replies[index])]));
    observation.nonce = nonce;
    return observation;
}

/** Independent contract grader entry. */
export function gradeKclCase(item, observation, revision = kclContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown kitty color contracts revision ${revision}.`);
    if (!observation || item.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) throw new Error(`Missing ${item.caseId} read captures.`);
    return item.grade(observation);
}

/** The fixed fixture facts other modules and tests read. */
export const kclFixture = Object.freeze({ minimum: kclMinimum, seedMargins: devqFixture.seedMargins, seedCells: devqFixture.seedCells, groupList, colorSetA, baselineKeys, legacyOsc });
