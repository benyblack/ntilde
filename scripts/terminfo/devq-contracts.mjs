// Versioned probe contracts for the device-query qualification batch: device.xtgetxres (DCS + Q), device.xtreportcolors (CSI # R),
// device.xtwinops-20 (CSI 20 t) and device.xtwinops-21 (CSI 21 t). device.term-features is documented only: its pinned probe reads
// process.env.TERM_FEATURES, emits no request and can never return supported, so it has no case here. This is not a publication or a measurement.
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e),
// read from the bundle, never inferred from the names. All four share deviceReplyResult(spec, raw, matchedFrame, reason): a frame matching
// spec.valid is supported; one matching spec.refusal is inconclusive (insufficient-evidence); one matching spec.contradicts is unsupported;
// anything else is inconclusive (invalid-reply when only spec.malformed matches, else no-response). A frame that follows a primary-DA reply is
// not a reply to the request.
//   device.xtgetxres        query DCS + Q 7465726d4e616d65 ST; valid /DCS 1 + R 7465726d4e616d65 = hex+ ST/i, refusal /DCS 0 + R 7465726d4e616d65 ST/i
//   device.xtreportcolors   query CSI # R; valid /CSI [0-9;]* # Q/, malformed /CSI [0-9;]* # | # Q/. The pinned valid frame has NO `?` marker.
//   device.xtwinops-20      termless: feed OSC 1;test-icon BEL, then CSI 20 t; valid /OSC L test-icon (BEL|ST)/, contradicts any other OSC L label
//   device.xtwinops-21      termless: feed OSC 2;test-title BEL, then CSI 21 t; valid /OSC l test-title (BEL|ST)/
// Both paths of each probe are replayed over the journaled raw bytes of the case's own fenced exchange.
//
// xterm's own XTREPORTCOLORS reply is CSI ? depth ; deepest # Q (misc.c xtermReportColors sets a_pintro to '?'). The pinned valid pattern cannot
// match it, so with the xterm-correct reply the pinned verdict stays inconclusive (invalid-reply) and the separately identified effect contract
// carries the graded conclusion. This is recorded as a divergence; the reply is not changed to score on the pinned pattern.
//
// The effect contract (ntilde-device-query-report-effect-v1) is separately identified and graded by an independent oracle written from xterm's
// control-sequence text and source (report_win_label; do_dcs case Q; xtermPushColors / xtermPopColors / xtermReportColors), sharing no code with the
// parser. Every exchange is one fenced request list: the raw replies (or their explicit absence) before a DA barrier are retained byte for byte,
// and every exchange must leave the complete screen, cursor, rendition, margins, modes and active screen unchanged. Roles: `protocol` groups decide a
// mismatch (unsupported); `control` groups prove the setup took effect (a failed control is inconclusive, never a verdict); `policy` groups record a
// deliberate Ntilde choice that xterm does not make and never decide a verdict.
//
// Limits: the title reports run with AllowTitleReport on (it is off by default and the receipt binds the run's initial settings); the default-off gate is a
// unit-tested policy, not a measurement here. termName is demanded as the documented session TERM (xterm-256color, set for every direct pane's child by the
// native PTY layer and the SSH options); it is not read back from the child's environment. The xterm palette stack's deepest-depth counter cannot be reset
// without RIS, so it remains at its measured value in the disposable App after the suite (the depth is drained to 0 and the colors are reset, and the start
// and finish screens are compared). The pane title is left at this suite's baseline, not restored to the pre-run original.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset } from './sgr-attrs-contracts.mjs';
import { selectiveEraseInk } from './selective-erase-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const devqContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const devqSuiteId = 'ntilde-owned-device-queries-graded-v1';
/** The separately identified effect contract. */
export const devqEffectContractId = 'ntilde-device-query-report-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const devqNoncePrefix = 'NTILDE_DQ_';
/** Owner-protocol prefix of this suite's observation requests. */
export const devqPrefix = 'dvq';
/** The pinned cases, in suite order. */
export const devqCaseIds = Object.freeze(['device.xtgetxres', 'device.xtreportcolors', 'device.xtwinops-20', 'device.xtwinops-21']);
/** The documented-only entry: no request, no case, never a verdict. */
export const termFeaturesCaseId = 'device.term-features';

/** The measured geometry the controls need (the seed uses margins 2..6, a nonce row and a coloured/bold/underlined seed row). */
export const devqMinimum = Object.freeze({ rows: 8, cols: 48 });
/** The margins the seed sets (one-based 2..6; zero-based 1..5). */
const seedMargins = Object.freeze([1, 5]);
export { validateSelectiveEraseScreen as validateDevqScreen } from './selective-erase-contracts.mjs';

const esc = '\x1b';
const st = esc + '\\';
const bel = '\x07';
/** Build a regular expression from a source written with <ESC>, so no control character sits in a pattern literal. */
const re = (source, flags = '') => new RegExp(source.replaceAll('<ESC>', String.fromCharCode(27)), flags);

/** The documented session TERM the native PTY layer and the SSH options give every direct pane's child. */
export const devqSessionTerm = 'xterm-256color';
const hexUpper = text => [...text].map(char => char.codePointAt(0).toString(16).toUpperCase().padStart(2, '0')).join('');
/** The hex spelling of the resource name the pinned probe asks for. */
export const termNameHex = '7465726d4e616d65';

// ---------- the pinned probe specifications (read from the bundle) ----------

const specs = Object.freeze({
    'device.xtgetxres': Object.freeze({
        query: `${esc}P+Q${termNameHex}${st}`, setup: null,
        valid: re(`<ESC>P1\\+R${termNameHex}=[0-9A-Fa-f]+<ESC>\\\\`, 'i'), refusal: re(`<ESC>P0\\+R${termNameHex}<ESC>\\\\`, 'i'), contradicts: null,
        malformed: re('<ESC>P[01]\\+R'), expected: 'XTGETXRES returns a complete status-1 termName resource value',
    }),
    'device.xtreportcolors': Object.freeze({
        query: `${esc}[#R`, setup: null,
        valid: re('<ESC>\\[[0-9;]*#Q'), refusal: null, contradicts: null,
        malformed: re('<ESC>\\[[0-9;]*#|#Q'), expected: 'CSI # R returns a complete CSI Pm # Q frame',
    }),
    'device.xtwinops-20': Object.freeze({
        query: `${esc}[20t`, setup: `${esc}]1;test-icon${bel}`,
        valid: re('<ESC>\\]Ltest-icon(?:\\x07|<ESC>\\\\)'), refusal: null, contradicts: re('<ESC>\\]L[^\\x07<ESC>]*(?:\\x07|<ESC>\\\\)'),
        malformed: re('<ESC>\\]L'), expected: 'OSC 1 + CSI 20 t round trip returns the exact icon label',
    }),
    'device.xtwinops-21': Object.freeze({
        query: `${esc}[21t`, setup: `${esc}]2;test-title${bel}`,
        valid: re('<ESC>\\]ltest-title(?:\\x07|<ESC>\\\\)'), refusal: null, contradicts: null,
        malformed: re('<ESC>\\]l'), expected: 'CSI 21 t returns the exact window title set by OSC 2',
    }),
});
/** The pinned refusal note, verbatim. */
const refusalNote = 'Requested setting or name refused; other settings or names unmeasured';

/** The pinned deviceReplyResult for one spec over the captured bytes, key order included. */
export function replayDevqPinned(caseId, raw = '') {
    const spec = specs[caseId];
    if (spec === undefined) throw new Error(`Unknown device-query case ${caseId}.`);
    const response = raw;
    const sources = [spec.valid.source, spec.refusal?.source, spec.contradicts?.source].filter(source => source !== undefined);
    let matched = new RegExp(sources.join('|'), spec.valid.flags).exec(response)?.[0] ?? null;
    const da1At = response.search(re('<ESC>\\[\\?[0-9;]*c'));
    if (matched && da1At !== -1 && response.indexOf(matched) > da1At) matched = null;
    const valid = matched ? spec.valid.exec(matched) : null;
    if (valid?.[0]) {
        return { pass: true, response, observation: { outcome: 'supported', evidence: 'query' },
            assertions: [{ kind: 'positive', expected: spec.expected, observed: valid[0] }] };
    }
    const refusal = matched ? spec.refusal?.exec(matched) : null;
    if (refusal?.[0]) {
        return { pass: false, response, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query', note: refusalNote } };
    }
    const contradiction = matched ? spec.contradicts?.exec(matched) ?? null : null;
    if (contradiction?.[0]) {
        return { pass: false, response, observation: { outcome: 'unsupported', evidence: 'query' },
            assertions: [{ kind: 'negative', expected: spec.expected, observed: contradiction[0] }] };
    }
    const complete = spec.valid.test(response) || spec.refusal?.test(response) === true || spec.contradicts?.test(response) === true;
    const reason = !complete && spec.malformed.test(response) ? 'invalid-reply' : 'no-response';
    return { pass: false, response, observation: { outcome: 'inconclusive', reason, evidence: 'query' } };
}

// ---------- the independent reply oracle ----------

/** Decode one hex-encoded resource name (printable ASCII, at most 32 characters), or null. */
function decodeName(encoded) {
    if (encoded.length === 0 || encoded.length % 2 !== 0 || encoded.length > 64 || !/^[0-9A-Fa-f]+$/.test(encoded)) return null;
    let name = '';
    for (let index = 0; index < encoded.length; index += 2) {
        const char = Number.parseInt(encoded.slice(index, index + 2), 16);
        if (char < 0x21 || char > 0x7e) return null;
        name += String.fromCodePoint(char);
    }
    return name;
}

/** The replies xterm's do_dcs case Q demands for one name list: nothing when the list is not entirely valid; otherwise ONE DCS whose status is the first
 *  name's, each name echoed as spelled with `=` and the uppercase hex value when known, stopping after the first name that is not known. */
export function xresOracle(nameList, term = devqSessionTerm) {
    const encodedNames = nameList.split(';');
    if (nameList === '' || encodedNames.length > 16) return [];
    const decoded = encodedNames.map(decodeName);
    if (decoded.includes(null)) return [];
    let body = '', first = true;
    for (const [index, name] of decoded.entries()) {
        const known = name === 'termName';
        if (first) { body += known ? '1+R' : '0+R'; first = false; }
        body += encodedNames[index];
        if (!known) break;
        body += '=' + hexUpper(term);
        if (index + 1 < decoded.length) body += ';';
    }
    return [`${esc}P${body}${st}`];
}

/** The DCS + Q request for a name list. */
export const xresRequest = nameList => `${esc}P+Q${nameList}${st}`;

/** A reply of OSC l / OSC L with the given label, terminated by ST as xterm's report_win_label always does. */
export const titleReply = (code, label) => `${esc}]${code}${label}${st}`;

const channelHex = byte => byte.toString(16).padStart(2, '0').repeat(2);
/** The X11 rgb: spelling of an 8-bit colour: each channel's two digits doubled (0x11 is 1111). */
const rgbSpelling = hex => `rgb:${[1, 3, 5].map(at => channelHex(Number.parseInt(hex.slice(at, at + 2), 16))).join('/')}`;

/** xterm's palette stack, written from xtermPushColors / xtermPopColors / xtermReportColors: ten slots, `used` and `last`; no parameter or 0 pushes onto / pops
 *  from the top; 1..10 stores into / restores from that slot, setting the depth to it (push) or to the slot below (pop). */
export class XtermColorModel {
    constructor() { this.colors = { bg: null, p5: null }; this.slots = Array.from({ length: 10 }, () => null); this.used = 0; this.last = 0; }
    set(target, hex) { this.colors[target] = hex; }
    push(value) {
        const slot = value <= 0 ? this.used : value - 1;
        if (slot < 0 || slot >= 10) return;
        this.slots[slot] = { ...this.colors };
        if (value <= 0) { this.used++; this.last = Math.max(this.last, this.used); } else this.used = value;
    }
    pop(value) {
        const slot = value <= 0 ? this.used - 1 : value - 1;
        if (slot < 0 || slot >= 10 || this.slots[slot] === null) return;
        this.colors = { ...this.slots[slot] };
        this.used = slot;
    }
    report() { return `${esc}[?${this.used};${this.last}#Q`; }
    query(target) {
        return target === 'bg' ? `${esc}]11;${rgbSpelling(this.colors.bg)}${st}` : `${esc}]4;5;${rgbSpelling(this.colors.p5)}${st}`;
    }
}

// ---------- the measured seed and plan ----------

/** The measured reset the bind begins with: protection, SGR and origin off, margins cleared, screen cleared, cursor home. */
export const devqReset = '\x1b[0"q' + sgrAttrsReset + '\x1b[?6l\x1b[r\x1b[2J\x1b[H';
/** The plan's static first feed re-establishes the attributed seed and homes the cursor without clearing the nonce row. */
export const devqReseed = '\x1b[2;1H\x1b[1mNtilde\x1b[31mXT\x1b[0m\x1b[4;1H\x1b[4:3mU\x1b[0m\x1b[2;6r\x1b[1;1H';
/** The attributed seed the bind writes, then the nonce at home. */
export const devqSeed = nonce => devqReseed + '\x1b[1;1H' + nonce;
/** The seed cells (row, col, char) the initial read must find. */
const seedCells = Object.freeze([
    ...[...'Ntilde'].map((char, col) => Object.freeze({ row: 1, col, char, bold: true })),
    Object.freeze({ row: 1, col: 6, char: 'X', bold: true, fgIndex: 1 }),
    Object.freeze({ row: 1, col: 7, char: 'T', bold: true, fgIndex: 1 }),
    Object.freeze({ row: 3, col: 0, char: 'U', underline: 'curly' }),
]);
/** The colour reset the finish applies: every palette entry and every dynamic colour back to the host's. */
export const devqColorReset = [104, 110, 111, 112, 117, 119].map(code => `${esc}]${code}${st}`).join('');
/** The run's baseline title, written by the finish so the pane never keeps a test label. */
export const devqBaseTitle = runId => `DQ-base-${runId.slice(0, 8)}`;

/** One exchange group. `kind` is `protocol` (decides), `control` (setup proof) or `policy` (recorded only). `setup` bytes are fed without capture first. */
const ask = (role, kind, request, replies, demand, setup = null) => Object.freeze({ role, kind, setup, request, demand: Object.freeze([...replies]), note: demand });

// ----- device.xtgetxres groups -----
const valueFor = nameList => xresOracle(nameList);
const xresGroups = [
    ask('pinned', 'protocol', xresRequest(termNameHex), valueFor(termNameHex), 'the exact pinned request returns the status-1 termName value (uppercase hex of the session TERM)'),
    ask('echo-upper', 'protocol', xresRequest('7465726D4E616D65'), valueFor('7465726D4E616D65'), 'the name is echoed as the request spelled it'),
    ask('unknown', 'protocol', xresRequest('666f6f'), valueFor('666f6f'), 'an unknown resource is DCS 0 + R with the name echoed'),
    ask('case-and-class', 'protocol', xresRequest('7465726d6e616d65') + xresRequest('5465726d4e616d65'), [...valueFor('7465726d6e616d65'), ...valueFor('5465726d4e616d65')],
        'resource names are case-sensitive: termname and the class name TermName are not resources'),
    ask('list-known', 'protocol', xresRequest(`${termNameHex};${termNameHex}`), valueFor(`${termNameHex};${termNameHex}`), 'two known names make one DCS with both entries separated by ;'),
    ask('list-stop', 'protocol', xresRequest(`${termNameHex};666f6f;${termNameHex}`), valueFor(`${termNameHex};666f6f;${termNameHex}`),
        'the status is the first name\'s and the reply stops after the first unknown name'),
    ask('list-first-unknown', 'protocol', xresRequest(`666f6f;${termNameHex}`), valueFor(`666f6f;${termNameHex}`), 'an unknown first name makes the whole reply status 0 and stops there'),
    ask('malformed-odd', 'protocol', xresRequest('5') + xresRequest(termNameHex), valueFor(termNameHex), 'an odd-length name is not answered and the parser then answers normally'),
    ask('malformed-hex', 'protocol', xresRequest('zz') + xresRequest(termNameHex), valueFor(termNameHex), 'a non-hex name is not answered and the parser then answers normally'),
    ask('malformed-empty', 'protocol', xresRequest('') + xresRequest(termNameHex), valueFor(termNameHex), 'an empty name list is not answered and the parser then answers normally'),
];

// ----- device.xtreportcolors groups: replies are produced by the model while the ops are applied -----
const colorBase = { bg: '#112233', p5: '#445566' }, colorSecond = { bg: '#aabbcc', p5: '#ddeeff' }, colorThird = { bg: '#010203', p5: '#0a0b0c' }, colorLoud = { bg: '#ff0000', p5: '#00ff00' };
const setBytes = colors => `${esc}]11;${colors.bg}${st}${esc}]4;5;${colors.p5}${st}`;
const queryBytes = `${esc}]11;?${st}${esc}]4;5;?${st}`;
const model = new XtermColorModel();
const pushBytes = value => (value === 0 ? `${esc}[#P` : `${esc}[${value}#P`);
const popBytes = value => (value === 0 ? `${esc}[#Q` : `${esc}[${value}#Q`);
const reportBytes = `${esc}[#R`;
/** Apply a colour change to the model and return its setup bytes. */
function change(colors) { model.set('bg', colors.bg); model.set('p5', colors.p5); return setBytes(colors); }
/** One exchange that applies ops to the model in order, returning its request bytes and the replies the model demands. */
function exchange(ops) {
    let request = '';
    const replies = [];
    for (const op of ops) {
        if (op === 'report') { request += reportBytes; replies.push(model.report()); }
        else if (op === 'query') { request += queryBytes; replies.push(model.query('bg'), model.query('p5')); }
        else if (op.push !== undefined) { request += pushBytes(op.push); model.push(op.push); }
        else if (op.pop !== undefined) { request += popBytes(op.pop); model.pop(op.pop); }
        else if (op.raw !== undefined) { request += op.raw; }
        else throw new Error('Unknown colour-stack op.');
    }
    return { request, replies };
}
/** One colour-stack group: the optional setup colours are applied to the model first, then the ops produce the request and the demanded replies. */
const colorGroup = (role, kind, demand, ops, applySetup = null) => {
    const setup = applySetup === null ? null : applySetup();
    const { request, replies } = exchange(ops);
    return ask(role, kind, request, replies, demand, setup);
};
const colorGroups = [
    colorGroup('pinned', 'protocol', 'the exact pinned request on an empty stack reports depth 0 and deepest 0 with the ? marker', ['report']),
    colorGroup('set-base', 'control', 'the base colours (background and palette 5) are set and read back', ['query'], () => change(colorBase)),
    colorGroup('push-1', 'protocol', 'a push is silent and the report is depth 1, deepest 1', [{ push: 0 }, 'report']),
    colorGroup('set-second', 'control', 'a different pair of colours is set and read back', ['query'], () => change(colorSecond)),
    colorGroup('push-2', 'protocol', 'the second push reports depth 2, deepest 2', [{ push: 0 }, 'report']),
    colorGroup('set-third', 'control', 'a third pair of colours is set and read back', ['query'], () => change(colorThird)),
    colorGroup('pop-1', 'protocol', 'a pop restores the second pair and reports depth 1, deepest 2', [{ pop: 0 }, 'report', 'query']),
    colorGroup('pop-2', 'protocol', 'the next pop restores the base pair and reports depth 0, deepest 2', [{ pop: 0 }, 'report', 'query']),
    colorGroup('pop-empty', 'protocol', 'a pop with nothing stored changes neither the colours nor the report', [{ pop: 0 }, 'report', 'query']),
    colorGroup('slot-store', 'protocol', 'a numbered push stores the current colours in that slot and moves the depth to it, leaving the deepest alone', [{ push: 4 }, 'report']),
    colorGroup('set-loud', 'control', 'a loud pair of colours is set and read back', ['query'], () => change(colorLoud)),
    colorGroup('slot-restore', 'protocol', 'a numbered pop restores that slot and sets the depth to the slot below it', [{ pop: 4 }, 'report', 'query']),
    colorGroup('kitty-independent', 'protocol', 'the kitty colour stack (OSC 30001/30101) neither moves the xterm depth nor shares its slots',
        ['report', { raw: `${esc}]30001${st}${esc}]11;#0f0f0f${st}${esc}]30101${st}` }, 'report', 'query']),
    colorGroup('out-of-range', 'protocol', 'slots past ten are ignored by both push and pop', [{ push: 11 }, { pop: 99 }, 'report']),
    colorGroup('fill', 'protocol', 'slot 3 is stored, then ten slots hold ten entries and a push past the tenth does nothing',
        [{ push: 3 }, ...Array.from({ length: 8 }, () => ({ push: 0 })), 'report']),
    colorGroup('spellings', 'policy', 'a report spelled with a leader or a parameter is not answered (xterm ignores the parameters; Ntilde admits only the bare spelling)',
        [{ raw: `${esc}[?#R${esc}[>#R${esc}[1#R` }, 'report']),
    colorGroup('drain', 'protocol', 'ten pops return the depth to 0 and the deepest stays at 10', Array.from({ length: 10 }, () => ({ pop: 0 })).concat(['report'])),
];

// ----- device.xtwinops-20 / 21 groups -----
/** The title/icon groups for one report (20 icon, 21 title). */
function titleGroups(report) {
    const icon = report === 20, code = icon ? 'L' : 'l', setOwn = icon ? 1 : 2, other = icon ? 2 : 1, otherCode = icon ? 'l' : 'L';
    const own = icon ? 'icon' : 'title', request = `${esc}[${report}t`, otherRequest = `${esc}[${icon ? 21 : 20}t`;
    const set = (osc, text) => `${esc}]${osc};${text}${bel}`;
    return [
        ask('pinned', 'protocol', request, [titleReply(code, `test-${own}`)], `the exact pinned round trip returns the ${own} label as OSC ${code}`, set(setOwn, `test-${own}`)),
        ask('distinct', 'protocol', request + otherRequest, icon ? [titleReply('L', 'icon-a1'), titleReply('l', 'title-b2')] : [titleReply('l', 'title-b2'), titleReply('L', 'icon-a1')],
            'the icon label and the window title are separate state and are reported by their own request, in request order', set(1, 'icon-a1') + set(2, 'title-b2')),
        ask('osc0-both', 'protocol', request + otherRequest, [titleReply(code, 'both-c3'), titleReply(otherCode, 'both-c3')], 'OSC 0 sets both and both are reported', set(0, 'both-c3')),
        ask('empty', 'protocol', request, [titleReply(code, '')], 'an emptied label is reported as an empty label', set(setOwn, '')),
        ask('semicolons', 'protocol', request, [titleReply(code, 'a;b c')], 'the label keeps later semicolons and spaces', set(setOwn, 'a;b c')),
        ask('stack', 'protocol', request + `${esc}[22;${setOwn}t` + set(setOwn, 'stk-2') + request + `${esc}[23;${setOwn}t` + request,
            [titleReply(code, 'stk-1'), titleReply(code, 'stk-2'), titleReply(code, 'stk-1')], 'a title-stack push, a new label and a pop are each reported as the label then in force', set(setOwn, 'stk-1')),
        ask('stable', 'protocol', request + request, [titleReply(code, 'same-d4'), titleReply(code, 'same-d4')], 'a report is not a set: asking twice gives the same label', set(setOwn, 'same-d4')),
        ask('control-character', 'policy', request, [titleReply(code, '')], 'a label holding a control character is answered as an empty label so the reply can carry no control (an Ntilde safety choice; xterm sends the bytes)', set(setOwn, 'a\tb')),
        ask('spellings', 'policy', `${esc}[${report};1t${esc}[?${report}t${esc}[>${report}t` + request, [titleReply(code, `test-${own}`)],
            'a report with an extra parameter or a leader is not answered (an Ntilde bound; xterm ignores extra parameters)', set(setOwn, `test-${own}`)),
    ];
}

/** Whether each contracted group set needs a captured pinned exchange as its first request group. */
const groupsByCase = Object.freeze({
    'device.xtgetxres': xresGroups,
    'device.xtreportcolors': colorGroups,
    'device.xtwinops-20': titleGroups(20),
    'device.xtwinops-21': titleGroups(21),
});

/** The plan of one case: the reseed, an initial cells read, then for each group optional uncaptured setup, the captured request and its report read. A case that recolors the
 *  pane ends with an explicit colour reset (popping the palette stack restores the saved colours, not the host's), so the next case starts from the measured baseline. */
function planOf(groups, recolors) {
    const plan = [planFeed(devqReseed), planRead('cells')];
    for (const item of groups) {
        if (item.setup !== null) plan.push(planFeed(item.setup));
        plan.push({ feed: item.request, capture: true }, planRead('report'));
    }
    if (recolors) plan.push(planFeed(devqColorReset));
    return plan;
}

const appearanceScopes = Object.freeze({
    'device.xtgetxres': 'exact XTGETXRES termName replies only: the pinned request, the uppercase-hex value of the documented session TERM, the echoed spelling, the case-sensitive '
        + 'unknown names, multi-name reply shape and stop rule, and unanswered malformed requests with verified recovery; no other resource, X resource database, TERM read back out of '
        + 'the child, remote-delivery or other-platform claim',
    'device.xtreportcolors': 'xterm palette-stack XTPUSHCOLORS / XTPOPCOLORS / XTREPORTCOLORS only: depth and deepest reports with the ? marker, push and pop of the background and one ANSI '
        + 'palette entry read back through OSC 11 and OSC 4 replies, numbered slots, empty pops, out-of-range slots, the ten-slot bound and independence from the kitty stack; not every dynamic colour, '
        + 'no rendering or painted-pixel claim, and the pinned probe\'s no-? pattern is not satisfied by the xterm reply',
    'device.xtwinops-20': 'CSI 20 t icon-label reports only, with AllowTitleReport on: the exact pinned round trip, distinct icon and title state, OSC 0, empty and semicolon labels, title-stack '
        + 'interplay and stability; the raw OSC 1 state, not a host icon presentation; the default-off gate is unit-tested, not measured here',
    'device.xtwinops-21': 'CSI 21 t window-title reports only, with AllowTitleReport on: the exact pinned round trip, distinct icon and title state, OSC 0, empty and semicolon labels, title-stack '
        + 'interplay and stability; the raw OSC 0/2 state, not the presented host caption; the default-off gate is unit-tested, not measured here',
});
const pinnedCriteria = Object.freeze({
    'device.xtgetxres': 'XTGETXRES (DCS + Q 7465726d4e616d65 ST) returns a framed DCS 1 + R 7465726d4e616d65 = <hex> ST (status 1, case-insensitive); the DCS 0 + R refusal is inconclusive',
    'device.xtreportcolors': 'CSI # R returns a framed CSI <digits and ;> # Q (no ? marker); a frame that only matches # Q is an invalid reply',
    'device.xtwinops-20': 'after OSC 1;test-icon BEL, CSI 20 t returns exactly OSC L test-icon (BEL or ST); any other OSC L label contradicts',
    'device.xtwinops-21': 'after OSC 2;test-title BEL, CSI 21 t returns exactly OSC l test-title (BEL or ST)',
});

/** The group state key: everything an exchange must leave unchanged. */
const groupOf = (screen, replies) => ({ screen, ink: selectiveEraseInk(screen), cursor: [screen.cursorRow, screen.cursorCol], cursorVisible: screen.cursorVisible,
    cursorMode: screen.cursorMode, rows: screen.rows, cols: screen.cols, margins: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom],
    origin: screen.bufferState.originMode, alternate: screen.bufferState.alternateScreen, buffer: screen.bufferState,
    horizontalMargins: screen.horizontalMargins, replies });
const stateKey = state => JSON.stringify([state.ink, state.cursor, state.cursorVisible, state.cursorMode, state.buffer, state.horizontalMargins]);
const printable = raw => (raw === null ? null : raw.replaceAll(esc, String.raw`\e`).replaceAll(bel, String.raw`\a`).replaceAll('\t', String.raw`\t`));

/** Whether the initial read holds the contracted seed: geometry, nonce at home, the attributed cells, margins and modes. */
function seedOk(state, nonce) {
    const boldCells = state.ink.filter(cell => cell.row === 1), underlineCells = state.ink.filter(cell => cell.row === 3);
    return state.rows >= devqMinimum.rows && state.cols >= devqMinimum.cols
        && state.screen.lines[0] === nonce && state.cursor[0] === 0 && state.cursor[1] === 0
        && state.margins[0] === seedMargins[0] && state.margins[1] === seedMargins[1] && state.origin === false && state.alternate === true
        && boldCells.length === seedCells.filter(cell => cell.row === 1).length && underlineCells.length === seedCells.filter(cell => cell.row === 3).length
        && seedCells.every(spec => {
            const cell = state.ink.find(item => item.row === spec.row && item.col === spec.col);
            return cell !== undefined && cell.char === spec.char && cell.bold === (spec.bold ?? false)
                && (spec.fgIndex === undefined || cell.fgIndex === spec.fgIndex) && (spec.underline === undefined || cell.underline === spec.underline);
        });
}

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });

/** The way one exchange group differs from its demand, or null: unchanged state first, then the exact reply list. */
function groupMismatch(group, state, initial) {
    if (stateKey(state) !== stateKey(initial)) {
        return `The ${group.role} request changed the screen, the cursor (position, visibility or stored mode), the rendition, the margins (vertical or horizontal), the wrap/insert modes or the active screen: a device report must change nothing`;
    }
    if (JSON.stringify(state.replies) === JSON.stringify(group.demand)) return null;
    if (state.replies.length === 0) return `The ${group.role} request was not answered`;
    if (state.replies.length !== group.demand.length) {
        return `The ${group.role} reply count differs from the contract: ${state.replies.length} replies where ${group.demand.length} were demanded (count, order and framing are part of the reply)`;
    }
    return `The ${group.role} reply differs from the reply oracle: a status, name, value, label, colour or reply order was wrong`;
}

/** The case's grade: the seed, then every control (a failed control is inconclusive), then every protocol group (a mismatch is unsupported); policy groups never decide. */
function gradeCase(item, observation) {
    if (!seedOk(observation.initial, observation.nonce)) return inconclusive('The attributed seed (nonce, cells, margins and modes) was not measured before any request');
    const reads = item.groups.map(group => [group, observation[group.role]]);
    for (const [group, state] of reads.filter(([group]) => group.kind === 'control')) {
        const mismatch = groupMismatch(group, state, observation.initial);
        if (mismatch !== null) return inconclusive(`Setup control failed before the effect could be judged: ${mismatch}`);
    }
    for (const [group, state] of reads.filter(([group]) => group.kind === 'protocol')) {
        const mismatch = groupMismatch(group, state, observation.initial);
        if (mismatch !== null) return unsupported(mismatch);
    }
    return { outcome: 'supported' };
}

/** The observed record of one case: every group with its demanded and received replies and whether the state was unchanged. */
function briefCase(item, observation) {
    const groups = Object.fromEntries(item.groups.map(group => {
        const state = observation[group.role];
        return [group.role, { kind: group.kind, request: printable(group.request), ...(group.setup === null ? {} : { setup: printable(group.setup) }), demand: group.note,
            demanded: group.demand.map(printable), received: state.replies.map(printable),
            matches: JSON.stringify(state.replies) === JSON.stringify(group.demand), unchanged: stateKey(state) === stateKey(observation.initial) }];
    }));
    const { rows, cols } = observation.initial;
    return { geometry: { rows, cols }, effectContract: devqEffectContractId, pinnedFeeds: item.pinnedFeeds.map(printable),
        seed: { nonce: observation.nonce, margins: observation.initial.margins, origin: observation.initial.origin, alternate: observation.initial.alternate,
            cells: seedOk(observation.initial, observation.nonce) }, groups };
}

/** The contracted cases, in suite order. */
function makeContract(caseId) {
    const groups = groupsByCase[caseId], spec = specs[caseId];
    const roles = Object.freeze(['initial', ...groups.map(group => group.role)]);
    const pinnedFeeds = Object.freeze(spec.setup === null ? [spec.query] : [spec.setup, spec.query]);
    const contract = {
        caseId, probeId: caseId, id: caseId.replace('device.', ''), roles, groups: Object.freeze(groups), plan: Object.freeze(planOf(groups, caseId === 'device.xtreportcolors')),
        pinnedPath: 'termless', pinnedRead: roles.indexOf('pinned'), pinnedExpected: spec.expected, pinnedCriterion: pinnedCriteria[caseId], pinnedFeeds,
        pinnedSetup: spec.setup !== null,
        terminalFixture: { writes: [...pinnedFeeds], demand: 'a framed reply matching the pinned pattern within 2000 ms of the sentinel write', minimum: { rows: 1, cols: 1 },
            note: 'the stock collector\'s queryWithSentinelOutcome path; replayed over the same journaled bytes beside the termless verdict' },
        effectContractId: devqEffectContractId, appearanceScope: appearanceScopes[caseId], evidence: 'owned-raw-replies-and-unchanged-state',
        grade: observation => gradeCase(contract, observation), observed: observation => briefCase(contract, observation),
    };
    return Object.freeze(contract);
}

export const devqContracts = Object.freeze(devqCaseIds.map(makeContract));
/** Contract case ids in suite order. */
export const devqContractCaseIds = Object.freeze(devqContracts.map(item => item.caseId));
export { planFeeds as devqFeeds, planReads as devqReads } from './graded-plan.mjs';

/** The seed bytes of the plan (the reset followed by the attributed seed and nonce). */
export function devqPlanSeed(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid device-query fixture run identity.');
    return devqReset + devqSeed(devqNoncePrefix + runId);
}
/** The finish: the screen and colours reset, the baseline title written, the alternate screen left. */
export const devqFinish = runId => devqReset + devqColorReset + `${esc}]0;${devqBaseTitle(runId)}${st}` + '\x1b[?1049l';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateDevqFeeds(item, feeds) { validateExactFeeds(item, feeds, 'contract'); }

/** Group the read screens and replies of the case by role. */
export function devqObservation(item, screens, replies, nonce) {
    const reads = item.plan.filter(step => step.read);
    if (!Array.isArray(screens) || screens.length !== reads.length || !Array.isArray(replies) || replies.length !== reads.length || typeof nonce !== 'string') {
        throw new Error('Missing device-query read captures.');
    }
    const observation = Object.fromEntries(item.roles.map((role, index) => [role, groupOf(screens[index], replies[index])]));
    observation.nonce = nonce;
    return observation;
}

/** Independent contract grader entry. */
export function gradeDevqCase(item, observation, revision = devqContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown device-query contracts revision ${revision}.`);
    if (!observation || item.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) throw new Error(`Missing ${item.caseId} read captures.`);
    return item.grade(observation);
}

/** The fixed fixture facts other modules and tests read. */
export const devqFixture = Object.freeze({ minimum: devqMinimum, seedMargins, seedCells, specs, groupsByCase, sessionTerm: devqSessionTerm });
