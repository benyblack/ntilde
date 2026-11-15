// Versioned probe contracts for the xterm SGR-stack qualification batch: modes.xtpushsgr (CSI # {) and modes.xtpopsgr (CSI # }). This is not a publication or a measurement.
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6), read from the bundle, never inferred from the names.
// Both probes can never return supported or unsupported (`pass: false`, outcome inconclusive):
//   modes.xtpushsgr  termless: feedCapture(CSI # {) (any output is inconclusive/invalid-reply), feedCapture(CSI c), feed(CSI # }); otherwise inconclusive/insufficient-evidence
//                    (evidence consumed) with response = the DA1 reply. terminal: write(CSI # {), await queryCursorPosition(), finally write(CSI # }); inconclusive/insufficient-evidence
//                    (consumed) with response = JSON of the position, or inconclusive/no-response without one.
//   modes.xtpopsgr   termless: feed(CSI # {), feedCapture(CSI # }) (any output is inconclusive/invalid-reply), feedCapture(CSI c); same verdict. terminal: write(CSI # {), write(CSI # }),
//                    await queryCursorPosition(); same verdicts.
// The pinned verdict therefore stays what the unchanged pinned code returns over the journaled raw bytes of its own fenced exchange. The graded conclusion belongs to a separately identified
// effect contract (ntilde-xterm-sgr-stack-effect-v1), and a difference between the two is recorded as a divergence, never absorbed.
//
// The effect oracle is written from xterm's control-sequence text (ctlseqs, patch 411): XTPUSHSGR "Push video attributes onto stack", whose optional parameters "correspond to the SGR encoding for
// video attributes, except for colors": 1 bold, 2 faint, 3 italicized, 4 underlined, 5 blink, 7 inverse, 8 invisible, 9 crossed-out, 21 doubly-underlined, 30 foreground color, 31 background color; "If no
// parameters are given, all of the video attributes are saved. The stack is limited to 10 levels." XTPOPSGR "Popping restores the video-attributes which were saved using XTPUSHSGR to their previous
// state." Only that documented behavior decides: after each stack operation a probe character is written and the STORED attributes of its cell are read back (bold, faint, italic, underline style,
// blink, inverse, hidden, strikethrough, and the stored foreground and background: default, palette index or literal RGB), so a push must change nothing, a pop must restore exactly the
// attributes the matching push selected (and leave the others as they were), levels come back in LIFO order, ten levels hold, and both sequences are silent and change nothing else on the
// screen. The text names neither the eleventh push, a pop with nothing saved, a value it does not list, nor the attributes it does not list (overline, underline color, character protection), so those
// are `policy` groups: recorded, never deciding.
// Roles: `protocol` groups decide a mismatch (unsupported); `control` groups prove the setup took effect (a failed control is inconclusive, never a verdict); `policy` groups record a deliberate
// Ntilde choice or an unspecified xterm behavior.
//
// Limits: Ntilde has one underline pen where xterm keeps a flag for the single and one for the double line, so the parameters 4 and 21 are exercised only with pens where the other is clear. Every
// case drains the stack (ten pops) and resets the pen at its end. Stored cell attributes only: no rendering, painted-pixel or host-presentation claim; XTREPORTSGR (CSI # |) and the CSI Pm # p /
// CSI # q aliases are different spellings and are not measured here.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset } from './sgr-attrs-contracts.mjs';
import { selectiveEraseInk } from './selective-erase-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const xssContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const xssSuiteId = 'ntilde-owned-xterm-sgr-stack-graded-v1';
/** The separately identified effect contract. */
export const xssEffectContractId = 'ntilde-xterm-sgr-stack-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const xssNoncePrefix = 'NTILDE_XSS_';
/** Owner-protocol prefix of this suite's observation requests. */
export const xssPrefix = 'xss';
/** The pinned cases, in suite order. */
export const xssCaseIds = Object.freeze(['modes.xtpushsgr', 'modes.xtpopsgr']);

/** The measured geometry the controls need (the seed uses margins 2..6, a nonce row, a coloured/bold/underlined seed row and the probe row 8). */
export const xssMinimum = Object.freeze({ rows: 8, cols: 48 });
const seedMargins = Object.freeze([1, 5]);
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
/** The zero-based row the probe characters are written on (below the scroll region, so no group scrolls anything). */
export const xssProbeRow = 7;
export { validateSelectiveEraseScreen as validateXssScreen } from './selective-erase-contracts.mjs';

const esc = '\x1b';
const st = esc + '\\';
const csiPush = `${esc}[#{`, csiPop = `${esc}[#}`, da1 = `${esc}[c`;

// ---------- the pinned scripts (read from the bundle) ----------

/** The exact calls each pinned path makes, in order. `replies` and `barrier` name which journaled bytes a captured call returns; `cursor` is the cursor-position query. */
export const xssPinnedScripts = Object.freeze({
    'modes.xtpushsgr': Object.freeze({
        termless: Object.freeze([['feedCapture', csiPush, 'replies'], ['feedCapture', da1, 'barrier'], ['feed', csiPop]]),
        terminal: Object.freeze([['write', csiPush], ['cursor'], ['write', csiPop]]),
        expected: 'CSI # { is accepted silently; the pinned probe measures only DA1 and cursor responsiveness',
    }),
    'modes.xtpopsgr': Object.freeze({
        termless: Object.freeze([['feed', csiPush], ['feedCapture', csiPop, 'replies'], ['feedCapture', da1, 'barrier']]),
        terminal: Object.freeze([['write', csiPush], ['write', csiPop], ['cursor']]),
        expected: 'CSI # } is accepted silently; the pinned probe measures only DA1 and cursor responsiveness',
    }),
});
const pinnedNotes = Object.freeze({
    invalid: 'Unexpected output during stack operation',
    termless: 'DA1 responsiveness does not measure saved/restored stack state',
    terminal: 'Cursor responsiveness does not measure saved/restored state',
});
const pinnedCriteria = Object.freeze({
    'modes.xtpushsgr': 'CSI # { produces no output (any output is invalid-reply); the probe then only checks DA1 / cursor responsiveness and is always inconclusive (insufficient-evidence)',
    'modes.xtpopsgr': 'CSI # { then CSI # } produces no output (any output is invalid-reply); the probe then only checks DA1 / cursor responsiveness and is always inconclusive (insufficient-evidence)',
});

/** What each pinned verdict looks like: always inconclusive, differing only in its reason, evidence and note. */
const pinnedVerdict = (reason, evidence, note) => ({ outcome: 'inconclusive', reason, ...(evidence === null ? {} : { evidence }), ...(note === null ? {} : { note }) });

/** The pinned result of one path over the journaled bytes of the case's own exchange: `replies` (the captured reply text before the barrier), `barrier` and the read cursor position. */
export function replayXssPinned(caseId, path, { replies = '', barrier = '', position = null } = {}) {
    if (xssPinnedScripts[caseId] === undefined) throw new Error(`Unknown xterm SGR-stack case ${caseId}.`);
    if (path !== 'termless' && path !== 'terminal') throw new Error(`Unknown pinned path ${path}.`);
    const answered = path === 'termless' && replies.length > 0, lost = path === 'terminal' && position === null;
    if (answered) return { pass: false, response: replies, observation: pinnedVerdict('invalid-reply', 'query', pinnedNotes.invalid) };
    if (lost) return { pass: false, observation: pinnedVerdict('no-response', 'query', null) };
    return { pass: false, response: path === 'termless' ? barrier : JSON.stringify(position), observation: pinnedVerdict('insufficient-evidence', 'consumed', pinnedNotes[path]) };
}

// ---------- the independent oracle: a pen and a stack written from the control-sequence text ----------

/** The attributes of the pen the stack can hold, in the documented order of its parameters. */
const stackable = Object.freeze(['bold', 'faint', 'italic', 'underline', 'blink', 'inverse', 'hidden', 'strike', 'fg', 'bg']);
/** The attribute each documented parameter selects (Ntilde has one underline pen, so 4 and 21 both select it). */
const parameterAttributes = Object.freeze({ 1: ['bold'], 2: ['faint'], 3: ['italic'], 4: ['underline'], 5: ['blink'], 7: ['inverse'], 8: ['hidden'], 9: ['strike'],
    21: ['underline'], 30: ['fg'], 31: ['bg'] });
/** Ten levels, as the text states. */
const stackLimit = 10;
const defaultColor = Object.freeze({ kind: 'default' });
const indexed = index => Object.freeze({ kind: 'index', index });
const literal = (r, g, b) => Object.freeze({ kind: 'rgb', r, g, b });
/** Overline and the underline color are not stackable attributes: they only matter to the policy group. */
const neutralPen = Object.freeze({ bold: false, faint: false, italic: false, underline: 'none', blink: false, inverse: false, hidden: false, strike: false,
    fg: defaultColor, bg: defaultColor, overline: false, underlineColor: -1 });
const pen = (overrides = {}) => Object.freeze({ ...neutralPen, ...overrides });

/** Every stackable attribute set, a single underline, an indexed foreground and a literal background. */
const penAll = pen({ bold: true, faint: true, italic: true, underline: 'single', blink: true, inverse: true, hidden: true, strike: true, fg: indexed(196), bg: literal(10, 20, 30) });
/** Every attribute clear, a literal foreground and an indexed background: the complement of penAll. */
const penClear = pen({ fg: literal(200, 100, 50), bg: indexed(22) });
/** A third distinguishable pen. */
const penMid = pen({ bold: true, italic: true, fg: indexed(5), bg: indexed(6) });
const penDouble = pen({ ...penAll, underline: 'double' });
const penLevel = n => pen({ fg: indexed(40 + n) });

/** xterm's video-attribute stack as its text documents it: a pen, a bounded list of saved selections, push at the top and pop from it. The documented behavior decides;
 *  the model's choices where the text is silent (a full stack ignores a push, an empty stack ignores a pop, an unlisted value saves an empty selection) only feed policy groups. */
export class XtermSgrStackModel {
    constructor() { this.pen = neutralPen; this.stack = []; }
    set(next) { this.pen = next; }
    push(parameters) {
        if (this.stack.length >= stackLimit) return;
        const attributes = parameters.length === 0 ? stackable : [...new Set(parameters.flatMap(value => parameterAttributes[value] ?? []))];
        this.stack.push({ attributes, pen: this.pen });
    }
    pop() {
        const top = this.stack.pop();
        if (top === undefined) return;
        this.pen = { ...this.pen, ...Object.fromEntries(top.attributes.map(name => [name, top.pen[name]])) };
    }
}

const colorParams = (selector, color) => (color.kind === 'index' ? `${selector};5;${color.index}` : color.kind === 'rgb' ? `${selector};2;${color.r};${color.g};${color.b}` : null);
/** The SGR that establishes exactly this pen from any other: SGR 0 first, then each attribute. */
function penBytes(value) {
    const parts = ['0'];
    if (value.bold) parts.push('1');
    if (value.faint) parts.push('2');
    if (value.italic) parts.push('3');
    if (value.underline === 'single') parts.push('4');
    if (value.underline === 'double') parts.push('21');
    if (value.blink) parts.push('5');
    if (value.inverse) parts.push('7');
    if (value.hidden) parts.push('8');
    if (value.strike) parts.push('9');
    if (value.overline) parts.push('53');
    if (value.underlineColor >= 0) parts.push(`58;5;${value.underlineColor}`);
    for (const text of [colorParams(38, value.fg), colorParams(48, value.bg)]) if (text !== null) parts.push(text);
    return `${esc}[${parts.join(';')}m`;
}
const pushBytes = parameters => `${esc}[${parameters.join(';')}#{`;

/** The stored cell the pen demands for a probe character. */
const colorDemand = color => (color.kind === 'default' ? { index: -1, argb: null } : color.kind === 'index' ? { index: color.index, argb: null }
    : { index: -1, argb: 0xff000000 + color.r * 65536 + color.g * 256 + color.b });
function demandedCell(value, char) {
    return { char, bold: value.bold, dim: value.faint, italic: value.italic, underline: value.underline === 'none' ? false : value.underline, blink: value.blink, inverse: value.inverse,
        hidden: value.hidden, strikethrough: value.strike, overline: value.overline, underlineColorIndex: value.underlineColor, fg: colorDemand(value.fg), bg: colorDemand(value.bg) };
}
const cellColor = (index, argb) => ({ index, argb: argb ?? null });
/** The stored fields the contract reads from an observed cell, in the demand's own shape. */
const observedCell = cell => ({ char: cell.char, bold: cell.bold, dim: cell.dim, italic: cell.italic, underline: cell.underline, blink: cell.blink, inverse: cell.inverse, hidden: cell.hidden,
    strikethrough: cell.strikethrough, overline: cell.overline, underlineColorIndex: cell.underlineColorIndex, fg: cellColor(cell.fgIndex, cell.foregroundArgb), bg: cellColor(cell.bgIndex, cell.backgroundArgb) });
const fieldNames = Object.freeze(['char', 'bold', 'dim', 'italic', 'underline', 'blink', 'inverse', 'hidden', 'strikethrough', 'overline', 'underlineColorIndex', 'fg', 'bg']);
/** The fields in which an observed cell differs from the demanded one. */
const cellDifferences = (observed, demanded) => fieldNames.filter(name => JSON.stringify(observed[name]) !== JSON.stringify(demanded[name]));
/** A cell as published: the demand and the observation in the same compact spelling. */
const colorText = color => (color.argb !== null ? `rgb #${(color.argb & 0xffffff).toString(16).padStart(6, '0')}` : color.index >= 0 ? `index ${color.index}` : 'default');
const cellText = cell => ({ char: cell.char, attributes: ['bold', 'dim', 'italic', 'blink', 'inverse', 'hidden', 'strikethrough', 'overline'].filter(name => cell[name]),
    underline: cell.underline, underlineColorIndex: cell.underlineColorIndex, fg: colorText(cell.fg), bg: colorText(cell.bg) });

/** The measured reset the bind begins with: protection, SGR and origin off, margins cleared, screen cleared, cursor home, and a drained stack. */
export const xssDrain = csiPop.repeat(stackLimit);
export const xssReset = '\x1b[0"q' + sgrAttrsReset + '\x1b[?6l\x1b[r\x1b[2J\x1b[H' + xssDrain;
/** The plan's static first feed re-establishes the attributed seed, erases the probe row the previous case filled and homes the cursor, without clearing the nonce row. */
export const xssReseed = `\x1b[2;1H\x1b[1mNtilde\x1b[31mXT\x1b[0m\x1b[4;1H\x1b[4:3mU\x1b[0m\x1b[${xssProbeRow + 1};1H\x1b[2K\x1b[2;6r\x1b[1;1H`;
/** The attributed seed the bind writes, then the nonce at home. */
export const xssSeed = nonce => xssReseed + '\x1b[1;1H' + nonce;
const seedCells = Object.freeze([
    ...[...'Ntilde'].map((char, col) => Object.freeze({ row: 1, col, char, bold: true })),
    Object.freeze({ row: 1, col: 6, char: 'X', bold: true, fgIndex: 1 }),
    Object.freeze({ row: 1, col: 7, char: 'T', bold: true, fgIndex: 1 }),
    Object.freeze({ row: 3, col: 0, char: 'U', underline: 'curly' }),
]);
/** What each case ends with: the stack drained and the pen reset, so the next case starts from the measured baseline. */
export const xssCaseEnd = xssDrain + `${esc}[0m`;
/** The run's baseline title, written by the finish so the pane never keeps a test label. */
export const xssBaseTitle = runId => `XSS-base-${runId.slice(0, 8)}`;

// ---------- the exchange groups ----------

/** One exchange group. `kind` is protocol (decides), control (setup proof) or policy (recorded only). `setup` is a list of uncaptured feeds written one by one before the request (each at most 64
 *  bytes), `request` the captured exchange, `after` an uncaptured feed after the read, `probe` the probe cell the request ends by writing (`{ col, char, pen }`) or null. */
const ask = (role, kind, note, { request, setup = [], after = null, probe = null, cursor }) => {
    const oversized = [request, ...setup].some(text => text.length > 64);
    if (oversized) throw new Error(`The ${role} request or setup exceeds the 64-byte exchange limit.`);
    return Object.freeze({ role, kind, note, request, after, probe, setup: Object.freeze([...setup]), cursor: Object.freeze([...cursor]) });
};

/** Build one case's groups by applying every step to the model in order, so each demanded cell is the model's pen at the moment its probe character is written. */
function builder() {
    const model = new XtermSgrStackModel();
    let probes = 0, cursor = [0, 0];
    const apply = op => {
        if (op.set !== undefined) { model.set(op.set); return penBytes(op.set); }
        if (op.push !== undefined) { model.push(op.push); return op.push.length === 0 ? csiPush : pushBytes(op.push); }
        if (op.pop !== undefined) { for (let at = 0; at < op.pop; at++) model.pop(); return csiPop.repeat(op.pop); }
        throw new Error('Unknown xterm SGR-stack op.');
    };
    const feed = ops => ops.map(apply).join('');
    return {
        model,
        /** The pinned push/pop pair applied to the model (the exchange is silent and moves nothing). */
        cycle() { model.push([]); model.pop(); },
        /** A probe group: each `pre` item is one uncaptured feed of ops, `main` ops and then the probe character are the captured request. */
        probe(role, kind, note, pre, main) {
            const setup = pre.map(item => feed(Array.isArray(item) ? item : [item]));
            const col = probes++, char = String.fromCharCode(97 + col);
            const request = `${esc}[${xssProbeRow + 1};${col + 1}H` + feed(main) + char;
            cursor = [xssProbeRow, col + 1];
            return ask(role, kind, note, { request, setup, probe: { col, char, pen: model.pen }, cursor });
        },
        /** A group without a probe: the pinned request exactly, the cursor where it was. */
        plain(role, kind, note, request, { setup = [], after = null } = {}) { return ask(role, kind, note, { request, setup, after, cursor }); },
    };
}

const baselineGroup = b => b.probe('baseline', 'control', 'a probe character written under the reset pen is stored with no attribute and default colors (the baseline every later demand is judged against)', [], []);
const levelOps = n => [{ set: penLevel(n) }, { push: [30] }];

/** The groups of modes.xtpushsgr. */
function pushGroups() {
    const b = builder();
    const groups = [
        baselineGroup(b),
        (b.cycle(), b.plain('pinned', 'protocol', 'the exact pinned CSI # { is accepted silently (its fenced exchange holds no reply) and the pinned CSI # } that follows leaves the screen unchanged', csiPush, { after: csiPop })),
        b.probe('after-pinned', 'protocol', 'after the pinned push/pop pair a probe character is stored with the baseline attributes', [], []),
        b.probe('set-A', 'control', 'pen A (every stackable attribute, a palette foreground and a literal background) is set and read back from a probe cell', [{ set: penAll }], []),
        b.probe('push-silent', 'protocol', 'a push is silent and changes no attribute of the current pen', [], [{ push: [] }]),
        b.probe('set-B', 'control', 'pen B (every attribute clear, a literal foreground and a palette background) is set and read back', [{ set: penClear }], []),
        b.probe('pop-recovers-pushed', 'protocol', 'a pop returns all ten attributes and both colors to pen A, the snapshot taken by the argument-less push', [], [{ pop: 1 }]),
        b.probe('snapshot-is-copy', 'protocol', 'a pushed snapshot is a copy: two later pen changes (C then B) do not alter it, and a pop returns pen A',
            [[{ push: [] }, { set: penMid }], { set: penClear }], [{ pop: 1 }]),
    ];
    for (const value of [1, 2, 3, 4, 5, 7, 8, 9, 21, 30, 31]) {
        const saved = value === 21 ? penDouble : penAll, attribute = parameterAttributes[value][0];
        groups.push(b.probe(`numbered-${value}`, 'protocol', `a push of parameter ${value} alone saves only the ${attribute} of pen ${value === 21 ? 'A with a double underline' : 'A'}: after the pen is cleared a pop restores that attribute and leaves every other as it was`,
            [{ set: saved }, { push: [value] }, { set: penClear }], [{ pop: 1 }]));
    }
    groups.push(
        b.probe('depth-top', 'protocol', 'ten pushes (palette foregrounds 41..50) all hold: the first pop restores the tenth level', Array.from({ length: 10 }, (_, n) => levelOps(n + 1)).concat([[{ set: neutralPen }]]), [{ pop: 1 }]),
        b.probe('depth-bottom', 'protocol', 'after eight more pops the next one restores the first level (foreground 41): the bottom of a ten-level stack survived', [[{ pop: 8 }]], [{ pop: 1 }]),
        b.probe('overflow-eleventh', 'policy', 'an eleventh push is ignored in Ntilde (the text only says the stack is limited to ten levels): ten pops after eleven pushes end at the first level',
            Array.from({ length: 11 }, (_, n) => levelOps(n + 1)).concat([[{ set: neutralPen }], [{ pop: 9 }]]), [{ pop: 1 }]),
        b.probe('unlisted-value', 'policy', 'a value the text does not list (99) saves a level that restores nothing in Ntilde', [{ set: penAll }, { push: [99] }, { set: penClear }], [{ pop: 1 }]),
        b.probe('unstacked-state', 'policy', 'the overline and the underline color are not listed by the text and are not part of the stack in Ntilde: a pop restores neither',
            [{ set: pen({ underline: 'single', overline: true, underlineColor: 77 }) }, { push: [] }, { set: pen({ underline: 'single', overline: true, underlineColor: 99 }) }], [{ pop: 1 }]),
    );
    return groups;
}

/** The groups of modes.xtpopsgr. */
function popGroups() {
    const b = builder();
    return [
        baselineGroup(b),
        (b.cycle(), b.plain('pinned', 'protocol', 'the exact pinned CSI # } after the pinned CSI # { is accepted silently (its fenced exchange holds no reply) and leaves the screen unchanged', csiPop, { setup: [csiPush] })),
        b.probe('after-pinned', 'protocol', 'after the pinned push/pop pair a probe character is stored with the baseline attributes', [], []),
        b.probe('push-baseline', 'control', 'a push of the untouched baseline pen is silent and changes nothing', [], [{ push: [] }]),
        b.probe('set-A', 'control', 'pen A is set and read back', [{ set: penAll }], []),
        b.probe('pop-restores-default', 'protocol', 'a pop restores the pushed baseline (no attribute, default colors), not the pen in force or any set literal', [], [{ pop: 1 }]),
        b.probe('push-A', 'control', 'pen A is set and pushed; the push is silent', [{ set: penAll }], [{ push: [] }]),
        b.probe('push-B', 'control', 'pen B is set and pushed on top of A', [{ set: penClear }], [{ push: [] }]),
        b.probe('set-C', 'control', 'pen C is set and read back', [{ set: penMid }], []),
        b.probe('pop-lifo-1', 'protocol', 'the first pop is silent and restores pen B, the most recent push', [], [{ pop: 1 }]),
        b.probe('pop-lifo-2', 'protocol', 'the second pop restores pen A, the earlier push', [], [{ pop: 1 }]),
        b.probe('pop-empty', 'policy', 'a pop with nothing saved changes no attribute (the text does not describe this; Ntilde ignores it)', [], [{ pop: 1 }]),
        b.probe('pop-only-saved', 'protocol', 'a pop restores only what the push saved: pen C is pushed with parameter 1, pen B is set, and the pop restores the bold of C and leaves italic and both colors as pen B has them',
            [{ set: penMid }, { push: [1] }, { set: penClear }], [{ pop: 1 }]),
    ];
}

/** The plan of one case: the reseed, an initial cells read, then for each group its uncaptured setup feeds, the captured request, its report read and an optional uncaptured after-feed. The case
 *  ends with the stack drained and the pen reset, so the next case starts from the measured baseline. */
function planOf(groups) {
    const plan = [planFeed(xssReseed), planRead('cells')];
    for (const item of groups) {
        for (const feed of item.setup) plan.push(planFeed(feed));
        plan.push({ feed: item.request, capture: true }, planRead('report'));
        if (item.after !== null) plan.push(planFeed(item.after));
    }
    plan.push(planFeed(xssCaseEnd));
    return plan;
}

const appearanceScopes = Object.freeze({
    'modes.xtpushsgr': 'xterm XTPUSHSGR (CSI # {) only: silence, an unchanged screen, that the push changes no attribute of the current pen, the snapshot of the bold, faint, italic, underline, blink, inverse, '
        + 'hidden and strikethrough attributes and the foreground and background read back from the stored cell of a probe character after a pop, snapshot copy semantics, the selection of each documented '
        + 'parameter alone (1, 2, 3, 4, 5, 7, 8, 9, 21, 30, 31) and ten levels holding; the eleventh push, an unlisted value and the overline and underline color are policy groups; stored cell attributes, no '
        + 'rendering or painted-pixel claim, no XTREPORTSGR claim, and the pinned probe measures only DA1 and cursor responsiveness',
    'modes.xtpopsgr': 'xterm XTPOPSGR (CSI # }) only: silence, an unchanged screen, restoration of the stored bold, faint, italic, underline, blink, inverse, hidden and strikethrough attributes and the '
        + 'foreground and background of a probe character\'s cell, restoration of the untouched baseline pen, LIFO order and that only the attributes a push saved are restored; a pop with nothing saved is a '
        + 'policy group; stored cell attributes, no rendering or painted-pixel claim, no XTREPORTSGR claim, and the pinned probe measures only DA1 and cursor responsiveness',
});

/** The group state: everything an exchange must leave unchanged apart from the probe row. */
const groupOf = (screen, replies) => ({ screen, ink: selectiveEraseInk(screen), cursor: [screen.cursorRow, screen.cursorCol], cursorVisible: screen.cursorVisible,
    cursorMode: screen.cursorMode, rows: screen.rows, cols: screen.cols, margins: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom],
    origin: screen.bufferState.originMode, alternate: screen.bufferState.alternateScreen, buffer: screen.bufferState,
    horizontalMargins: screen.horizontalMargins, replies });
const outsideProbe = state => state.ink.filter(cell => cell.row !== xssProbeRow);
const probeInk = state => state.ink.filter(cell => cell.row === xssProbeRow);
/** The state key: everything but the cells of the probe row, the cursor (compared separately) and the replies. */
const stateKey = state => JSON.stringify([outsideProbe(state), state.cursorVisible, state.cursorMode, state.buffer, state.horizontalMargins]);
const printable = raw => (raw === null ? null : raw.replaceAll(esc, String.raw`\e`).replaceAll('\x07', String.raw`\a`));

/** Whether the initial read holds the contracted seed: geometry, nonce at home, the attributed cells, margins and modes, and an empty probe row. */
function seedOk(state, nonce) {
    const count = row => state.ink.filter(cell => cell.row === row).length;
    const planted = row => seedCells.filter(cell => cell.row === row).length;
    const roomy = state.rows >= xssMinimum.rows && state.cols >= xssMinimum.cols;
    const homed = state.screen.lines[0] === nonce && state.cursor.every(at => at === 0);
    const framed = same(state.margins, seedMargins) && state.origin === false && state.alternate === true;
    return roomy && homed && framed && count(1) === planted(1) && count(3) === planted(3) && count(xssProbeRow) === 0 && seedCells.every(spec => seedCellOk(state.ink, spec));
}

/** Whether the stored cell at a seed spec's position holds its character, boldness and (when specified) indexed color and underline style. */
function seedCellOk(ink, spec) {
    const cell = ink.find(item => item.row === spec.row && item.col === spec.col);
    const expected = { char: spec.char, bold: spec.bold ?? false, ...(spec.fgIndex === undefined ? {} : { fgIndex: spec.fgIndex }), ...(spec.underline === undefined ? {} : { underline: spec.underline }) };
    return cell !== undefined && Object.entries(expected).every(([name, value]) => cell[name] === value);
}

const verdict = outcome => note => ({ outcome, note });
const inconclusive = verdict('inconclusive');
const unsupported = verdict('unsupported');

/** The cells of the probe row an exchange must hold: every earlier probe cell exactly as it was last observed, and this group's own cell once (when it has one). */
function probeRowMismatch(group, state, priorProbes) {
    const cells = probeInk(state);
    const expected = group.probe === null ? priorProbes.length : priorProbes.length + 1;
    if (cells.length !== expected) return 'the probe row holds a different number of cells than the earlier probes plus this one';
    for (const [at, before] of priorProbes.entries()) {
        const now = cells[at];
        if (now === undefined || now.col !== before.col || cellDifferences(observedCell(now), observedCell(before)).length > 0) return 'a stack operation changed a cell written by an earlier probe';
    }
    return null;
}

/** The way one exchange group differs from its demand, or null: silence and unchanged state first, then the probe cell against the model's pen. */
function groupMismatch(group, state, initial, priorProbes) {
    if (stateKey(state) !== stateKey(initial)) {
        return `The ${group.role} request changed the screen outside the probe row, the cursor visibility or stored mode, the margins (vertical or horizontal), the wrap/insert modes or the active screen: a video-attribute stack operation must change nothing but the pen`;
    }
    if (state.replies.length > 0) return `The ${group.role} request was answered with ${state.replies.length} reply(ies): a push or pop must be silent`;
    if (state.cursor[0] !== group.cursor[0] || state.cursor[1] !== group.cursor[1]) return `The ${group.role} request left the cursor at ${state.cursor.join(',')} instead of ${group.cursor.join(',')}`;
    const rowProblem = probeRowMismatch(group, state, priorProbes);
    if (rowProblem !== null) return `The ${group.role} request: ${rowProblem}`;
    if (group.probe === null) return null;
    const cell = state.ink.find(item => item.row === xssProbeRow && item.col === group.probe.col);
    if (cell === undefined) return `The ${group.role} probe character was not stored`;
    const wrong = cellDifferences(observedCell(cell), demandedCell(group.probe.pen, group.probe.char));
    return wrong.length === 0 ? null : `The ${group.role} probe cell differs from the oracle in ${wrong.join(', ')}: the pen a stack operation left was wrong`;
}

/** The case's grade, evaluated in execution order so an earlier failure is never overridden by a later group it damaged: the seed first, then each group as the plan ran it. The first control that fails
 *  makes the case inconclusive (the setup did not take, so nothing after it can be judged); the first protocol group that mismatches makes it unsupported; policy groups never decide. */
function gradeCase(item, observation) {
    if (!seedOk(observation.initial, observation.nonce)) return inconclusive('The attributed seed (nonce, cells, margins and modes) was not measured before any request');
    const priorProbes = [];
    for (const group of item.groups) {
        const state = observation[group.role];
        const mismatch = group.kind === 'policy' ? null : groupMismatch(group, state, observation.initial, priorProbes);
        if (mismatch !== null) return group.kind === 'control' ? inconclusive(`Setup control failed before the effect could be judged: ${mismatch}`) : unsupported(mismatch);
        priorProbes.splice(0, priorProbes.length, ...probeInk(state));
    }
    return { outcome: 'supported' };
}

/** The observed record of one case: every group with its demanded and received cell and whether the state was unchanged. */
function briefCase(item, observation) {
    const priorProbes = [];
    const groups = Object.fromEntries(item.groups.map(group => {
        const state = observation[group.role];
        const cell = group.probe === null ? undefined : state.ink.find(entry => entry.row === xssProbeRow && entry.col === group.probe.col);
        const demanded = group.probe === null ? null : demandedCell(group.probe.pen, group.probe.char);
        const observed = cell === undefined ? null : observedCell(cell);
        const matches = groupMismatch(group, state, observation.initial, priorProbes) === null;
        priorProbes.splice(0, priorProbes.length, ...probeInk(state));
        return [group.role, { kind: group.kind, request: printable(group.request), ...(group.setup.length === 0 ? {} : { setup: group.setup.map(printable) }),
            ...(group.after === null ? {} : { after: printable(group.after) }), demand: group.note, demanded: demanded === null ? null : cellText(demanded),
            received: observed === null ? null : cellText(observed), replies: state.replies.map(printable), cursor: state.cursor, matches, unchanged: stateKey(state) === stateKey(observation.initial) }];
    }));
    const { rows, cols } = observation.initial;
    return { geometry: { rows, cols }, effectContract: xssEffectContractId, pinnedScript: printableScript(item.pinnedScript),
        seed: { nonce: observation.nonce, margins: observation.initial.margins, origin: observation.initial.origin, alternate: observation.initial.alternate,
            cells: seedOk(observation.initial, observation.nonce) }, groups };
}

/** The pinned script with its bytes written printably. */
const printableScript = script => ({ termless: script.termless.map(call => call.map(part => printable(part))), terminal: script.terminal.map(call => call.map(part => printable(part))) });

const groupsByCase = Object.freeze({ 'modes.xtpushsgr': pushGroups(), 'modes.xtpopsgr': popGroups() });

/** The contracted cases, in suite order. */
function makeContract(caseId) {
    const groups = groupsByCase[caseId], script = xssPinnedScripts[caseId];
    const roles = Object.freeze(['initial', ...groups.map(group => group.role)]);
    const contract = {
        caseId, probeId: caseId, id: caseId.replace('modes.', ''), roles, groups: Object.freeze(groups), plan: Object.freeze(planOf(groups)),
        pinnedPath: 'termless', pinnedRead: roles.indexOf('pinned'), pinnedExpected: script.expected, pinnedCriterion: pinnedCriteria[caseId], pinnedScript: script,
        terminalFixture: { writes: script.terminal.filter(([kind]) => kind === 'write').map(([, text]) => text), demand: 'no output from the stack sequences and a cursor-position reply',
            minimum: { rows: 1, cols: 1 }, note: 'the stock collector\'s write plus queryCursorPosition path; replayed over the same journaled cursor beside the termless verdict' },
        effectContractId: xssEffectContractId, appearanceScope: appearanceScopes[caseId], evidence: 'owned-raw-replies-and-stored-cells',
        grade: observation => gradeCase(contract, observation), observed: observation => briefCase(contract, observation),
    };
    return Object.freeze(contract);
}

export const xssContracts = Object.freeze(xssCaseIds.map(makeContract));
/** Contract case ids in suite order. */
export const xssContractCaseIds = Object.freeze(xssContracts.map(item => item.caseId));
export { planFeeds as xssFeeds, planReads as xssReads } from './graded-plan.mjs';

/** The seed bytes of the plan (the reset followed by the attributed seed and nonce). */
export function xssPlanSeed(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid xterm SGR-stack fixture run identity.');
    return xssReset + xssSeed(xssNoncePrefix + runId);
}
/** The finish: the screen reset, the stack drained, the baseline title written, the alternate screen left. */
export const xssFinish = runId => xssReset + `${esc}]0;${xssBaseTitle(runId)}${st}` + '\x1b[?1049l';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateXssFeeds(item, feeds) { validateExactFeeds(item, feeds, 'contract'); }

/** Group the read screens and replies of the case by role. */
export function xssObservation(item, screens, replies, nonce) {
    const reads = item.plan.filter(step => step.read);
    if (!Array.isArray(screens) || screens.length !== reads.length || !Array.isArray(replies) || replies.length !== reads.length || typeof nonce !== 'string') {
        throw new Error('Missing xterm SGR-stack read captures.');
    }
    const observation = Object.fromEntries(item.roles.map((role, index) => [role, groupOf(screens[index], replies[index])]));
    observation.nonce = nonce;
    return observation;
}

/** Independent contract grader entry. */
export function gradeXssCase(item, observation, revision = xssContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown xterm SGR-stack contracts revision ${revision}.`);
    if (!observation || item.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) throw new Error(`Missing ${item.caseId} read captures.`);
    return item.grade(observation);
}

/** The fixed fixture facts other modules and tests read. */
export const xssFixture = Object.freeze({ minimum: xssMinimum, seedMargins, seedCells, groupsByCase, pens: { neutral: neutralPen, all: penAll, clear: penClear, mid: penMid, double: penDouble, level: penLevel }, penBytes,
    demandedCell, stackLimit });
