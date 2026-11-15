// Versioned probe contracts for the xterm color-stack qualification batch: modes.xtpushcolors (CSI # P) and modes.xtpopcolors (CSI # Q). This is not a publication or a measurement.
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6), read from the bundle, never inferred from the names.
// Both probes can never return supported or unsupported (`pass: false`, outcome inconclusive):
//   modes.xtpushcolors  termless: feedCapture(CSI # P) (any output is inconclusive/invalid-reply), feedCapture(CSI c), feed(CSI # Q); otherwise inconclusive/insufficient-evidence
//                       (evidence consumed) with response = the DA1 reply. terminal: write(CSI # P), await queryCursorPosition(), finally write(CSI # Q); inconclusive/insufficient-evidence
//                       (consumed) with response = JSON of the position, or inconclusive/no-response without one.
//   modes.xtpopcolors   termless: feed(CSI # P), feedCapture(CSI # Q) (any output is inconclusive/invalid-reply), feedCapture(CSI c); same verdict. terminal: write(CSI # P), write(CSI # Q),
//                       await queryCursorPosition(); same verdicts.
// The pinned verdict therefore stays what the unchanged pinned code returns over the journaled raw bytes of its own fenced exchange. The graded conclusion belongs to a separately identified
// effect contract (ntilde-xterm-color-stack-effect-v1), and a difference between the two is recorded as a divergence, never absorbed.
//
// The effect oracle is written from xterm's control-sequence text (ctlseqs, patch 411): XTPUSHCOLORS "push[es] current dynamic- and ANSI-palette colors onto stack", parameters 1..10 "store the
// palette into the stack without pushing"; XTPOPCOLORS "pop[s] stack to set dynamic- and ANSI-palette colors", parameters 1..10 "restore the palette from the stack without popping". Only that documented
// behavior decides: the colors read back through OSC 10, 11, 12 and OSC 4 replies after a pop or numbered restore (foreground, background, cursor and ANSI palette entry 5), LIFO order, a snapshot that is
// a copy, slots that are independent, silence of both sequences, and the unchanged screen. The control sequences name neither the dynamic colors, the slot count, an empty pop, nor the report bytes, so
// the extended palette entry 200 and a pop with nothing stored are `policy` groups: recorded, never deciding. Depth bookkeeping (XTREPORTCOLORS) is not an observer here.
// Roles: `protocol` groups decide a mismatch (unsupported); `control` groups prove the setup took effect (a failed control is inconclusive, never a verdict); `policy` groups record a deliberate
// Ntilde choice or an unspecified xterm behavior.
//
// Limits: the stack's slot contents and deepest-depth counter cannot be reset without RIS; every sequence here is balanced (numbered stores use slots 1 and 2 and are restored back to depth 0), and
// the colors are reset with OSC 104/110/111/112 at the end of each case. No rendering or painted-pixel claim; the kitty color stack (OSC 30001/30101) is a different spelling and is not measured here.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset } from './sgr-attrs-contracts.mjs';
import { selectiveEraseInk } from './selective-erase-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const xcsContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const xcsSuiteId = 'ntilde-owned-xterm-color-stack-graded-v1';
/** The separately identified effect contract. */
export const xcsEffectContractId = 'ntilde-xterm-color-stack-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const xcsNoncePrefix = 'NTILDE_XCS_';
/** Owner-protocol prefix of this suite's observation requests. */
export const xcsPrefix = 'xcs';
/** The pinned cases, in suite order. */
export const xcsCaseIds = Object.freeze(['modes.xtpushcolors', 'modes.xtpopcolors']);

/** The measured geometry the controls need (the seed uses margins 2..6, a nonce row and a coloured/bold/underlined seed row). */
export const xcsMinimum = Object.freeze({ rows: 8, cols: 48 });
const seedMargins = Object.freeze([1, 5]);
export { validateSelectiveEraseScreen as validateXcsScreen } from './selective-erase-contracts.mjs';

const esc = '\x1b';
const st = esc + '\\';
const csiPush = `${esc}[#P`, csiPop = `${esc}[#Q`, da1 = `${esc}[c`;

// ---------- the pinned scripts (read from the bundle) ----------

/** The exact calls each pinned path makes, in order. `replies` and `barrier` name which journaled bytes a captured call returns; `cursor` is the cursor-position query. */
export const xcsPinnedScripts = Object.freeze({
    'modes.xtpushcolors': Object.freeze({
        termless: Object.freeze([['feedCapture', csiPush, 'replies'], ['feedCapture', da1, 'barrier'], ['feed', csiPop]]),
        terminal: Object.freeze([['write', csiPush], ['cursor'], ['write', csiPop]]),
        expected: 'CSI # P is accepted silently; the pinned probe measures only DA1 and cursor responsiveness',
    }),
    'modes.xtpopcolors': Object.freeze({
        termless: Object.freeze([['feed', csiPush], ['feedCapture', csiPop, 'replies'], ['feedCapture', da1, 'barrier']]),
        terminal: Object.freeze([['write', csiPush], ['write', csiPop], ['cursor']]),
        expected: 'CSI # Q is accepted silently; the pinned probe measures only DA1 and cursor responsiveness',
    }),
});
const pinnedNotes = Object.freeze({
    invalid: 'Unexpected output during stack operation',
    termless: 'DA1 responsiveness does not measure saved/restored stack state',
    terminal: 'Cursor responsiveness does not measure saved/restored state',
});
const pinnedCriteria = Object.freeze({
    'modes.xtpushcolors': 'CSI # P produces no output (any output is invalid-reply); the probe then only checks DA1 / cursor responsiveness and is always inconclusive (insufficient-evidence)',
    'modes.xtpopcolors': 'CSI # P then CSI # Q produces no output (any output is invalid-reply); the probe then only checks DA1 / cursor responsiveness and is always inconclusive (insufficient-evidence)',
});

/** The pinned result of one path over the journaled bytes of the case's own exchange: `replies` (the captured reply text before the barrier), `barrier` and the read cursor position. */
export function replayXcsPinned(caseId, path, { replies = '', barrier = '', position = null } = {}) {
    if (xcsPinnedScripts[caseId] === undefined) throw new Error(`Unknown xterm color-stack case ${caseId}.`);
    if (path === 'termless') {
        if (replies.length > 0) {
            return { pass: false, response: replies, observation: { outcome: 'inconclusive', reason: 'invalid-reply', evidence: 'query', note: pinnedNotes.invalid } };
        }
        return { pass: false, response: barrier, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed', note: pinnedNotes.termless } };
    }
    if (path !== 'terminal') throw new Error(`Unknown pinned path ${path}.`);
    if (position === null) return { pass: false, observation: { outcome: 'inconclusive', reason: 'no-response', evidence: 'query' } };
    return { pass: false, response: JSON.stringify(position), observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed', note: pinnedNotes.terminal } };
}

// ---------- the independent reply oracle ----------

const channelHex = byte => byte.toString(16).padStart(2, '0').repeat(2);
/** The X11 rgb: spelling of an 8-bit colour: each channel's two digits doubled (0x11 is 1111). */
const rgbSpelling = hex => `rgb:${[1, 3, 5].map(at => channelHex(Number.parseInt(hex.slice(at, at + 2), 16))).join('/')}`;
/** The color channels the contract reads: the three dynamic colors and ANSI palette entry 5 (decided), then extended palette entry 200 (policy). */
/** A reply shape for one color query code: ESC ] code ; rgb:RRRR/GGGG/BBBB ST (built from a string so no control character sits in a pattern literal). */
const colorShape = code => new RegExp(String.raw`^<ESC>\]${code};rgb:[0-9a-f]{4}/[0-9a-f]{4}/[0-9a-f]{4}<ESC>\\$`.replaceAll('<ESC>', String.fromCharCode(27)));
const channels = Object.freeze({
    fg: { set: hex => `${esc}]10;${hex}${st}`, ask: `${esc}]10;?${st}`, reply: hex => `${esc}]10;${rgbSpelling(hex)}${st}`, shape: colorShape('10') },
    bg: { set: hex => `${esc}]11;${hex}${st}`, ask: `${esc}]11;?${st}`, reply: hex => `${esc}]11;${rgbSpelling(hex)}${st}`, shape: colorShape('11') },
    cursor: { set: hex => `${esc}]12;${hex}${st}`, ask: `${esc}]12;?${st}`, reply: hex => `${esc}]12;${rgbSpelling(hex)}${st}`, shape: colorShape('12') },
    p5: { set: hex => `${esc}]4;5;${hex}${st}`, ask: `${esc}]4;5;?${st}`, reply: hex => `${esc}]4;5;${rgbSpelling(hex)}${st}`, shape: colorShape('4;5') },
    x200: { set: hex => `${esc}]4;200;${hex}${st}`, ask: `${esc}]4;200;?${st}`, reply: hex => `${esc}]4;200;${rgbSpelling(hex)}${st}`, shape: colorShape('4;200') },
});
const coreChannels = Object.freeze(['fg', 'bg', 'cursor', 'p5']);
/** Three distinguishable color sets over the four core channels, and two values for the extended entry. */
const sets = Object.freeze({
    A: Object.freeze({ fg: '#102030', bg: '#112233', cursor: '#a1b2c3', p5: '#445566' }),
    B: Object.freeze({ fg: '#c0d0e0', bg: '#aabbcc', cursor: '#3a2b1c', p5: '#ddeeff' }),
    C: Object.freeze({ fg: '#050607', bg: '#010203', cursor: '#f0e0d0', p5: '#0a0b0c' }),
});
const extended = Object.freeze({ X: '#123456', Y: '#fedcba' });

/** xterm's color stack as its text documents it: a bounded list of stored palettes, push/pop at the top, numbered slots stored/restored without moving the top. A channel whose current value
 *  is the host default (never set since the baseline) is `null`: the oracle demands the baseline's own reply for it. Depth bookkeeping beyond what the documented effects need is not modelled. */
export class XtermColorStackModel {
    constructor() { this.colors = Object.fromEntries(Object.keys(channels).map(name => [name, null])); this.slots = Array.from({ length: 10 }, () => null); this.used = 0; }
    set(update) { Object.assign(this.colors, update); }
    push(slot) {
        const at = slot <= 0 ? this.used : slot - 1;
        if (at < 0 || at >= 10) return;
        this.slots[at] = { ...this.colors };
        this.used = slot <= 0 ? this.used + 1 : slot;
    }
    pop(slot) {
        const at = slot <= 0 ? this.used - 1 : slot - 1;
        if (at < 0 || at >= 10 || this.slots[at] === null) return;
        this.colors = { ...this.slots[at] };
        this.used = at;
    }
    /** The demanded replies of one query: a reply string, or `{ baseline: index }` for a channel still at the host default. */
    query(names) { return names.map(name => (this.colors[name] === null ? { baseline: coreChannels.indexOf(name) } : channels[name].reply(this.colors[name]))); }
}

/** The measured reset the bind begins with: protection, SGR and origin off, margins cleared, screen cleared, cursor home. */
export const xcsReset = '\x1b[0"q' + sgrAttrsReset + '\x1b[?6l\x1b[r\x1b[2J\x1b[H';
/** The plan's static first feed re-establishes the attributed seed and homes the cursor without clearing the nonce row. */
export const xcsReseed = '\x1b[2;1H\x1b[1mNtilde\x1b[31mXT\x1b[0m\x1b[4;1H\x1b[4:3mU\x1b[0m\x1b[2;6r\x1b[1;1H';
/** The attributed seed the bind writes, then the nonce at home. */
export const xcsSeed = nonce => xcsReseed + '\x1b[1;1H' + nonce;
const seedCells = Object.freeze([
    ...[...'Ntilde'].map((char, col) => Object.freeze({ row: 1, col, char, bold: true })),
    Object.freeze({ row: 1, col: 6, char: 'X', bold: true, fgIndex: 1 }),
    Object.freeze({ row: 1, col: 7, char: 'T', bold: true, fgIndex: 1 }),
    Object.freeze({ row: 3, col: 0, char: 'U', underline: 'curly' }),
]);
/** The colour reset each case ends with and the finish applies: every palette entry and the three dynamic colours back to the host's. */
export const xcsColorReset = [104, 110, 111, 112].map(code => `${esc}]${code}${st}`).join('');
/** The run's baseline title, written by the finish so the pane never keeps a test label. */
export const xcsBaseTitle = runId => `XCS-base-${runId.slice(0, 8)}`;

// ---------- the exchange groups ----------

/** One exchange group. `kind` is protocol (decides), control (setup proof) or policy (recorded only). `setup` is a list of uncaptured feeds written one by one before the request (the fence limits one
 *  request to 64 bytes), `after` an uncaptured feed after the read. `demand` is the replies to demand (strings, or { baseline } references), `shape` a per-reply pattern list instead (the baseline only). */
const ask = (role, kind, note, { request, demand = [], shape = null, setup = [], after = null }) => {
    if (request.length > 64) throw new Error(`The ${role} request exceeds the 64-byte exchange limit.`);
    return Object.freeze({ role, kind, setup: Object.freeze([...setup]), request, after, demand: Object.freeze([...demand]), shape, note });
};

/** Build one case's groups by applying every step to the model in order. */
function builder() {
    const model = new XtermColorStackModel();
    const askQuery = names => names.map(name => channels[name].ask).join('');
    const apply = op => {
        if (op.push !== undefined) { model.push(op.push); return op.push === 0 ? csiPush : `${esc}[${op.push}#P`; }
        if (op.pop !== undefined) { model.pop(op.pop); return op.pop === 0 ? csiPop : `${esc}[${op.pop}#Q`; }
        if (op.set !== undefined) { model.set(op.set); return Object.entries(op.set).map(([name, hex]) => channels[name].set(hex)).join(''); }
        throw new Error('Unknown xterm color-stack op.');
    };
    return {
        model, askQuery,
        /** The pinned push/pop pair applied to the model (it returns the empty demand of the silent pinned exchange). */
        cycle() { model.push(0); model.pop(0); return []; },
        /** A group: `pre` ops are written uncaptured first (each its own feed), `main` ops and then the query of `names` are the captured request, with the demand from the model. */
        run(pre, main, names = coreChannels) {
            const setup = pre.map(apply);
            const request = main.map(apply).join('') + askQuery(names);
            return { setup, request, demand: model.query(names) };
        },
    };
}

const baselineGroup = b => ask('baseline', 'control', 'the host default colors are read back (one concrete reply per channel, in query order) and become the restoration oracle',
    { request: b.askQuery(coreChannels), shape: coreChannels.map(name => channels[name].shape) });
const extendedGroup = () => ask('extended-palette', 'policy', 'palette entry 200 (outside the 16 ANSI colors the text names) is saved by a push and restored by a pop in Ntilde; xterm\'s text does not say',
    { setup: [channels.x200.set(extended.X), csiPush, channels.x200.set(extended.Y), csiPop], request: channels.x200.ask, demand: [channels.x200.reply(extended.X)] });

/** The groups of modes.xtpushcolors. */
function pushGroups() {
    const b = builder();
    return [
        baselineGroup(b),
        ask('pinned', 'protocol', 'the exact pinned CSI # P is accepted silently (its fenced exchange holds no reply) and the pinned CSI # Q that follows leaves the screen unchanged',
        { request: csiPush, demand: b.cycle(), after: csiPop }),
        ask('after-pinned', 'protocol', 'after the pinned push/pop pair every channel reads back as the baseline', b.run([], [])),
        ask('set-A', 'control', 'colors A (foreground, background, cursor, palette 5) are set and read back', b.run([{ set: sets.A }], [])),
        ask('push-silent', 'protocol', 'a push is silent and changes no current color', b.run([], [{ push: 0 }])),
        ask('set-B', 'control', 'colors B are set and read back', b.run([{ set: sets.B }], [])),
        ask('pop-recovers-pushed', 'protocol', 'a pop returns every channel to colors A, the snapshot taken by the push', b.run([], [{ pop: 0 }])),
        ask('snapshot-is-copy', 'protocol', 'a pushed snapshot is a copy: two later color changes (B then C) do not alter it, and a pop returns colors A',
        b.run([{ push: 0 }, { set: sets.B }, { set: sets.C }], [{ pop: 0 }])),
        ask('store-slot-2', 'protocol', 'a numbered push stores colors A in slot 2 silently and without altering the current colors', b.run([], [{ push: 2 }])),
        ask('set-B-again', 'control', 'colors B are set and read back', b.run([{ set: sets.B }], [])),
        ask('store-slot-1', 'protocol', 'a second numbered push stores colors B in slot 1 silently and without altering the current colors', b.run([], [{ push: 1 }])),
        ask('set-C', 'control', 'colors C are set and read back', b.run([{ set: sets.C }], [])),
        ask('restore-slot-2', 'protocol', 'a numbered pop of slot 2 restores colors A, the colors stored there (slots are independent of order)', b.run([], [{ pop: 2 }])),
        ask('restore-slot-1', 'protocol', 'a numbered pop of slot 1 restores colors B, the colors stored there', b.run([], [{ pop: 1 }])),
        extendedGroup(),
    ];
}

/** The groups of modes.xtpopcolors. */
function popGroups() {
    const b = builder();
    return [
        baselineGroup(b),
        ask('pinned', 'protocol', 'the exact pinned CSI # Q after the pinned CSI # P is accepted silently (its fenced exchange holds no reply) and leaves the screen unchanged',
        { setup: [csiPush], request: csiPop, demand: b.cycle() }),
        ask('after-pinned', 'protocol', 'after the pinned push/pop pair every channel reads back as the baseline', b.run([], [])),
        ask('push-baseline', 'control', 'a push of the untouched host default colors is silent and changes nothing', b.run([], [{ push: 0 }])),
        ask('set-A', 'control', 'colors A are set and read back', b.run([{ set: sets.A }], [])),
        ask('pop-restores-default', 'protocol', 'a pop restores the pushed host default colors (the baseline replies), not the colors in force or any set literal', b.run([], [{ pop: 0 }])),
        ask('push-A', 'control', 'colors A are set and pushed; the push is silent', b.run([{ set: sets.A }], [{ push: 0 }])),
        ask('push-B', 'control', 'colors B are set and pushed on top of A', b.run([{ set: sets.B }], [{ push: 0 }])),
        ask('set-C', 'control', 'colors C are set and read back', b.run([{ set: sets.C }], [])),
        ask('pop-lifo-1', 'protocol', 'the first pop is silent and restores colors B, the most recent push', b.run([], [{ pop: 0 }])),
        ask('pop-lifo-2', 'protocol', 'the second pop restores colors A, the earlier push', b.run([], [{ pop: 0 }])),
        ask('pop-empty', 'policy', 'a pop with nothing stored below changes no color (xterm\'s text does not describe this; Ntilde ignores it)', b.run([], [{ pop: 0 }])),
        ask('store-slot-1', 'control', 'colors A are set again (isolating this control from the policy probe before it) and stored in slot 1 by a numbered push, silently and without altering the current colors', b.run([{ set: sets.A }], [{ push: 1 }])),
        ask('set-B', 'control', 'colors B are set and read back', b.run([{ set: sets.B }], [])),
        ask('restore-slot-1', 'protocol', 'a numbered pop of slot 1 restores colors A without needing a prior unnumbered push', b.run([], [{ pop: 1 }])),
        extendedGroup(),
    ];
}

/** The plan of one case: the reseed, an initial cells read, then for each group its uncaptured setup feeds, the captured request, its report read and an optional uncaptured after-feed. The case
 *  ends with an explicit colour reset, so the next case starts from the measured baseline. */
function planOf(groups) {
    const plan = [planFeed(xcsReseed), planRead('cells')];
    for (const item of groups) {
        for (const feed of item.setup) plan.push(planFeed(feed));
        plan.push({ feed: item.request, capture: true }, planRead('report'));
        if (item.after !== null) plan.push(planFeed(item.after));
    }
    plan.push(planFeed(xcsColorReset));
    return plan;
}

const appearanceScopes = Object.freeze({
    'modes.xtpushcolors': 'xterm XTPUSHCOLORS (CSI # P) only: silence, an unchanged screen, that the push changes no current color, the snapshot of the foreground, background, cursor and ANSI palette entry 5 '
        + 'read back through OSC 10/11/12 and OSC 4 replies after a pop, snapshot copy semantics and numbered store slots; not every dynamic color, no rendering or painted-pixel claim, no depth-report claim, '
        + 'and the pinned probe measures only DA1 and cursor responsiveness',
    'modes.xtpopcolors': 'xterm XTPOPCOLORS (CSI # Q) only: silence, an unchanged screen, restoration of the foreground, background, cursor and ANSI palette entry 5 read back through OSC 10/11/12 and '
        + 'OSC 4 replies, restoration of the untouched host default, LIFO order and a numbered restore; not every dynamic color, no rendering or painted-pixel claim, no depth-report claim, and the pinned '
        + 'probe measures only DA1 and cursor responsiveness',
});

/** The group state key: everything an exchange must leave unchanged. */
const groupOf = (screen, replies) => ({ screen, ink: selectiveEraseInk(screen), cursor: [screen.cursorRow, screen.cursorCol], cursorVisible: screen.cursorVisible,
    cursorMode: screen.cursorMode, rows: screen.rows, cols: screen.cols, margins: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom],
    origin: screen.bufferState.originMode, alternate: screen.bufferState.alternateScreen, buffer: screen.bufferState,
    horizontalMargins: screen.horizontalMargins, replies });
const stateKey = state => JSON.stringify([state.ink, state.cursor, state.cursorVisible, state.cursorMode, state.buffer, state.horizontalMargins]);
const printable = raw => (raw === null ? null : raw.replaceAll(esc, String.raw`\e`).replaceAll('\x07', String.raw`\a`));

/** Whether the initial read holds the contracted seed: geometry, nonce at home, the attributed cells, margins and modes. */
function seedOk(state, nonce) {
    const inRow = row => state.ink.filter(cell => cell.row === row).length, seeded = row => seedCells.filter(cell => cell.row === row).length;
    const geometry = state.rows >= xcsMinimum.rows && state.cols >= xcsMinimum.cols;
    const home = state.screen.lines[0] === nonce && state.cursor[0] === 0 && state.cursor[1] === 0;
    const modes = state.margins[0] === seedMargins[0] && state.margins[1] === seedMargins[1] && state.origin === false && state.alternate === true;
    return geometry && home && modes && inRow(1) === seeded(1) && inRow(3) === seeded(3) && seedCells.every(spec => seedCellOk(state.ink, spec));
}

/** Whether the stored cell at a seed spec's position holds its character, boldness and (when specified) indexed color and underline style. */
function seedCellOk(ink, spec) {
    const cell = ink.find(item => item.row === spec.row && item.col === spec.col);
    if (cell === undefined || cell.char !== spec.char || cell.bold !== (spec.bold ?? false)) return false;
    return (spec.fgIndex === undefined || cell.fgIndex === spec.fgIndex) && (spec.underline === undefined || cell.underline === spec.underline);
}

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });

/** The replies a group demands, with every baseline reference resolved from the baseline group's own replies; null when a reference cannot be resolved. */
export function resolveDemand(group, baselineReplies) {
    const out = [];
    for (const item of group.demand) {
        if (typeof item === 'string') out.push(item);
        else if (typeof baselineReplies?.[item.baseline] === 'string') out.push(baselineReplies[item.baseline]);
        else return null;
    }
    return out;
}

/** The way one exchange group differs from its demand, or null: unchanged state first, then the exact reply list (or the shape list of the baseline). */
function groupMismatch(group, state, initial, baselineReplies) {
    if (stateKey(state) !== stateKey(initial)) {
        return `The ${group.role} request changed the screen, the cursor (position, visibility or stored mode), the rendition, the margins (vertical or horizontal), the wrap/insert modes or the active screen: a color-stack operation must change nothing but colors`;
    }
    if (group.shape !== null) {
        const ok = state.replies.length === group.shape.length && group.shape.every((pattern, index) => pattern.test(state.replies[index]));
        return ok ? null : `The ${group.role} replies are not one complete color reply per channel in query order`;
    }
    const demand = resolveDemand(group, baselineReplies);
    if (demand === null) return `The ${group.role} demand depends on a baseline that was not measured`;
    if (JSON.stringify(state.replies) === JSON.stringify(demand)) return null;
    if (state.replies.length === 0 && demand.length > 0) return `The ${group.role} request was not answered`;
    if (state.replies.length !== demand.length) {
        return `The ${group.role} reply count differs from the contract: ${state.replies.length} replies where ${demand.length} were demanded (a push or pop must be silent; count, order and framing are part of the reply)`;
    }
    return `The ${group.role} reply differs from the reply oracle: a restored or stored color was wrong`;
}

/** The case's grade, evaluated in execution order so an earlier failure is never overridden by a later group it damaged: the seed first, then each group as the plan ran it. The first control that fails
 *  makes the case inconclusive (the setup did not take, so nothing after it can be judged); the first protocol group that mismatches makes it unsupported; policy groups never decide. */
function gradeCase(item, observation) {
    if (!seedOk(observation.initial, observation.nonce)) return inconclusive('The attributed seed (nonce, cells, margins and modes) was not measured before any request');
    const baselineReplies = observation.baseline.replies;
    for (const group of item.groups) {
        if (group.kind === 'policy') continue;
        const mismatch = groupMismatch(group, observation[group.role], observation.initial, baselineReplies);
        if (mismatch === null) continue;
        return group.kind === 'control' ? inconclusive(`Setup control failed before the effect could be judged: ${mismatch}`) : unsupported(mismatch);
    }
    return { outcome: 'supported' };
}

/** The observed record of one case: every group with its demanded and received replies and whether the state was unchanged. */
function briefCase(item, observation) {
    const baselineReplies = observation.baseline.replies;
    const groups = Object.fromEntries(item.groups.map(group => {
        const state = observation[group.role];
        const demand = group.shape === null ? resolveDemand(group, baselineReplies) : null;
        const matches = group.shape === null ? demand !== null && JSON.stringify(state.replies) === JSON.stringify(demand)
            : state.replies.length === group.shape.length && group.shape.every((pattern, index) => pattern.test(state.replies[index]));
        return [group.role, { kind: group.kind, request: printable(group.request), ...(group.setup.length === 0 ? {} : { setup: group.setup.map(printable) }),
            ...(group.after === null ? {} : { after: printable(group.after) }), demand: group.note, demanded: demand === null ? null : demand.map(printable),
            received: state.replies.map(printable), matches, unchanged: stateKey(state) === stateKey(observation.initial) }];
    }));
    const { rows, cols } = observation.initial;
    return { geometry: { rows, cols }, effectContract: xcsEffectContractId, pinnedScript: printableScript(item.pinnedScript),
        seed: { nonce: observation.nonce, margins: observation.initial.margins, origin: observation.initial.origin, alternate: observation.initial.alternate,
            cells: seedOk(observation.initial, observation.nonce) }, groups };
}

/** The pinned script with its bytes written printably. */
const printableScript = script => ({ termless: script.termless.map(call => call.map(part => printable(part))), terminal: script.terminal.map(call => call.map(part => printable(part))) });

const groupsByCase = Object.freeze({ 'modes.xtpushcolors': pushGroups(), 'modes.xtpopcolors': popGroups() });

/** The contracted cases, in suite order. */
function makeContract(caseId) {
    const groups = groupsByCase[caseId], script = xcsPinnedScripts[caseId];
    const roles = Object.freeze(['initial', ...groups.map(group => group.role)]);
    const contract = {
        caseId, probeId: caseId, id: caseId.replace('modes.', ''), roles, groups: Object.freeze(groups), plan: Object.freeze(planOf(groups)),
        pinnedPath: 'termless', pinnedRead: roles.indexOf('pinned'), pinnedExpected: script.expected, pinnedCriterion: pinnedCriteria[caseId], pinnedScript: script,
        terminalFixture: { writes: script.terminal.filter(([kind]) => kind === 'write').map(([, text]) => text), demand: 'no output from the stack sequences and a cursor-position reply',
            minimum: { rows: 1, cols: 1 }, note: 'the stock collector\'s write plus queryCursorPosition path; replayed over the same journaled cursor beside the termless verdict' },
        effectContractId: xcsEffectContractId, appearanceScope: appearanceScopes[caseId], evidence: 'owned-raw-replies-and-unchanged-state',
        grade: observation => gradeCase(contract, observation), observed: observation => briefCase(contract, observation),
    };
    return Object.freeze(contract);
}

export const xcsContracts = Object.freeze(xcsCaseIds.map(makeContract));
/** Contract case ids in suite order. */
export const xcsContractCaseIds = Object.freeze(xcsContracts.map(item => item.caseId));
export { planFeeds as xcsFeeds, planReads as xcsReads } from './graded-plan.mjs';

/** The seed bytes of the plan (the reset followed by the attributed seed and nonce). */
export function xcsPlanSeed(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid xterm color-stack fixture run identity.');
    return xcsReset + xcsSeed(xcsNoncePrefix + runId);
}
/** The finish: the screen and colours reset, the baseline title written, the alternate screen left. */
export const xcsFinish = runId => xcsReset + xcsColorReset + `${esc}]0;${xcsBaseTitle(runId)}${st}` + '\x1b[?1049l';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateXcsFeeds(item, feeds) { validateExactFeeds(item, feeds, 'contract'); }

/** Group the read screens and replies of the case by role. */
export function xcsObservation(item, screens, replies, nonce) {
    const reads = item.plan.filter(step => step.read);
    if (!Array.isArray(screens) || screens.length !== reads.length || !Array.isArray(replies) || replies.length !== reads.length || typeof nonce !== 'string') {
        throw new Error('Missing xterm color-stack read captures.');
    }
    const observation = Object.fromEntries(item.roles.map((role, index) => [role, groupOf(screens[index], replies[index])]));
    observation.nonce = nonce;
    return observation;
}

/** Independent contract grader entry. */
export function gradeXcsCase(item, observation, revision = xcsContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown xterm color-stack contracts revision ${revision}.`);
    if (!observation || item.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) throw new Error(`Missing ${item.caseId} read captures.`);
    return item.grade(observation);
}

/** The fixed fixture facts other modules and tests read. */
export const xcsFixture = Object.freeze({ minimum: xcsMinimum, seedMargins, seedCells, groupsByCase, channels, sets, extended });
