// Versioned probe contracts for the xterm saved-mode qualification batch: modes.xtsave (CSI ? 7 s) and modes.xtrestore (CSI ? 7 r). This is not a publication or a measurement.
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6), read from the bundle, never inferred from the names.
// Both probes can never return supported or unsupported (`pass: false`, outcome inconclusive):
//   modes.xtsave     termless: feedCapture(CSI ? 7 s) (any output is inconclusive/invalid-reply), feedCapture(CSI c), feed(CSI ? 7 r); otherwise inconclusive/insufficient-evidence
//                    (evidence consumed) with response = the DA1 reply. terminal: write(CSI ? 7 s), await queryCursorPosition(), finally write(CSI ? 7 r); inconclusive/insufficient-evidence
//                    (consumed) with response = JSON of the position, or inconclusive/no-response without one.
//   modes.xtrestore  termless: feed(CSI ? 7 s), feedCapture(CSI ? 7 r) (any output is inconclusive/invalid-reply), feedCapture(CSI c); same verdict. terminal: write(CSI ? 7 s), write(CSI ? 7 r),
//                    await queryCursorPosition(); same verdicts.
// The pinned verdict therefore stays what the unchanged pinned code returns over the journaled raw bytes of its own fenced exchange. The graded conclusion belongs to a separately identified
// effect contract (ntilde-xterm-saved-modes-effect-v1), and a difference between the two is recorded as a divergence, never absorbed.
//
// The effect oracle is written from xterm's control-sequence text (ctlseqs, patch 411): XTSAVE "Save DEC Private Mode Values ... Ps values are the same as for DECSET. Like Save Cursor (DECSC), this
// uses a one-level cache. Unlike Save Cursor, specific settings can be saved and restored independently. Only those modes listed as parameters are saved." XTRESTORE "Restore DEC Private Mode Values.
// The value of Ps previously saved is restored ... Only those modes listed as parameters are restored." The text also says the pair is unrelated to SCOSC/SCORC ("that is coincidental") and DECSTBM.
// Only that documented behavior decides. The modes the contract reads back are the ones the App's owner API reports as stored state: DECAWM (?7, the stored auto-wrap flag and, physically, whether a
// character written in the last column leaves a pending wrap), DECOM (?6), DECLRMM (?69) and DECTCEM (?25, the stored cursor visibility). A group saves a mode, changes it, restores it and the stored
// state must be the saved value in both polarities; the other listed modes must stay as they were set (independence, lists of modes, a second save replacing the first); the sequences are silent and
// change nothing else on the screen; and neither one is SCOSC/SCORC or DECSTBM. xterm's own restore of ?6 homes the cursor, ?1048 is the cursor save, restoring twice and the values the text does not
// list are `policy` groups: recorded, never deciding. The pinned sequences are also repeated in the groups so a divergence from the pinned bytes cannot hide.
// Roles: `protocol` groups decide a mismatch (unsupported); `control` groups prove the setup took effect (a failed control is inconclusive, never a verdict); `policy` groups record a deliberate
// Ntilde choice or an unspecified xterm behavior.
//
// Limits: only the four read-back modes are measured (the input-side modes ?1, ?66, ?1004, ?1007, ?2004, the mouse selectors ?9/?1000..?1015, ?45 and ?80 are covered by unit tests, not by this
// measurement); a restore with nothing saved is not measured (the run cannot prove an empty slot, xterm itself restores a zero); the alternate-screen modes are deliberately not saved. The run leaves
// the saved values of the last group in the disposable App's slots (they cannot be emptied without RIS). Stored state, not rendering or host presentation.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset } from './sgr-attrs-contracts.mjs';
import { selectiveEraseInk } from './selective-erase-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const xsvContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const xsvSuiteId = 'ntilde-owned-xterm-saved-modes-graded-v1';
/** The separately identified effect contract. */
export const xsvEffectContractId = 'ntilde-xterm-saved-modes-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const xsvNoncePrefix = 'NTILDE_XSV_';
/** Owner-protocol prefix of this suite's observation requests. */
export const xsvPrefix = 'xsv';
/** The pinned cases, in suite order. */
export const xsvCaseIds = Object.freeze(['modes.xtsave', 'modes.xtrestore']);

/** The measured geometry the controls need (the seed uses margins 2..6, a nonce row, a coloured/bold/underlined seed row and the probe row 8). */
export const xsvMinimum = Object.freeze({ rows: 8, cols: 48 });
const seedMargins = Object.freeze([1, 5]);
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
/** The zero-based row the wrap probe character is written on (below the scroll region, so nothing scrolls). */
export const xsvProbeRow = 7;
/** The character written in the last column to expose a pending wrap. */
export const xsvProbeChar = 'z';
export { validateSelectiveEraseScreen as validateXsvScreen } from './selective-erase-contracts.mjs';

const esc = '\x1b';
const st = esc + '\\';
const da1 = `${esc}[c`;
const csi = (parameters, final) => `${esc}[${parameters}${final}`;

// ---------- the pinned scripts (read from the bundle) ----------

const pinnedSave = csi('?7', 's'), pinnedRestore = csi('?7', 'r');
/** The exact calls each pinned path makes, in order. `replies` and `barrier` name which journaled bytes a captured call returns; `cursor` is the cursor-position query. */
export const xsvPinnedScripts = Object.freeze({
    'modes.xtsave': Object.freeze({
        termless: Object.freeze([['feedCapture', pinnedSave, 'replies'], ['feedCapture', da1, 'barrier'], ['feed', pinnedRestore]]),
        terminal: Object.freeze([['write', pinnedSave], ['cursor'], ['write', pinnedRestore]]),
        expected: 'CSI ? 7 s is accepted silently; the pinned probe measures only DA1 and cursor responsiveness',
    }),
    'modes.xtrestore': Object.freeze({
        termless: Object.freeze([['feed', pinnedSave], ['feedCapture', pinnedRestore, 'replies'], ['feedCapture', da1, 'barrier']]),
        terminal: Object.freeze([['write', pinnedSave], ['write', pinnedRestore], ['cursor']]),
        expected: 'CSI ? 7 s then CSI ? 7 r are accepted silently; the pinned probe measures only DA1 and cursor responsiveness',
    }),
});
const pinnedNotes = Object.freeze({
    invalid: 'Unexpected output during stack operation',
    termless: 'DA1 responsiveness does not measure saved/restored stack state',
    terminal: 'Cursor responsiveness does not measure saved/restored state',
});
const pinnedCriteria = Object.freeze({
    'modes.xtsave': 'CSI ? 7 s produces no output (any output is invalid-reply); the probe then only checks DA1 / cursor responsiveness and is always inconclusive (insufficient-evidence)',
    'modes.xtrestore': 'CSI ? 7 s then CSI ? 7 r produces no output (any output is invalid-reply); the probe then only checks DA1 / cursor responsiveness and is always inconclusive (insufficient-evidence)',
});

/** What each pinned verdict looks like: always inconclusive, differing only in its reason, evidence and note. */
const pinnedVerdict = (reason, evidence, note) => ({ outcome: 'inconclusive', reason, ...(evidence === null ? {} : { evidence }), ...(note === null ? {} : { note }) });

/** The pinned result of one path over the journaled bytes of the case's own exchange: `replies` (the captured reply text before the barrier), `barrier` and the read cursor position. */
export function replayXsvPinned(caseId, path, { replies = '', barrier = '', position = null } = {}) {
    if (xsvPinnedScripts[caseId] === undefined) throw new Error(`Unknown xterm saved-mode case ${caseId}.`);
    if (path !== 'termless' && path !== 'terminal') throw new Error(`Unknown pinned path ${path}.`);
    const answered = path === 'termless' && replies.length > 0, lost = path === 'terminal' && position === null;
    if (answered) return { pass: false, response: replies, observation: pinnedVerdict('invalid-reply', 'query', pinnedNotes.invalid) };
    if (lost) return { pass: false, observation: pinnedVerdict('no-response', 'query', null) };
    return { pass: false, response: path === 'termless' ? barrier : JSON.stringify(position), observation: pinnedVerdict('insufficient-evidence', 'consumed', pinnedNotes[path]) };
}

// ---------- the independent oracle: the four read-back modes and a one-level save cache written from the control-sequence text ----------

/** The read-back modes, by DECSET number, and the state each starts a group in (the baseline every group begins and ends in). */
const modeNames = Object.freeze({ 7: 'wrap', 6: 'origin', 69: 'margin-mode', 25: 'cursor' });
export const xsvBaseline = Object.freeze({ 7: true, 6: false, 69: false, 25: true });
const readBackModes = Object.freeze(Object.keys(modeNames).map(Number));

/** xterm's saved DEC private modes as its text documents them: a live value per read-back mode and a one-level cache holding one saved value per mode. A mode the model does not read back is ignored;
 *  a restore of a mode nothing was saved for is refused (the contract never demands it). */
export class XtermSavedModesModel {
    constructor() { this.live = { ...xsvBaseline }; this.cache = {}; }
    rebase() { this.live = { ...xsvBaseline }; }
    put(mode, value) { if (mode in this.live) this.live[mode] = value; }
    save(mode) { if (mode in this.live) this.cache[mode] = this.live[mode]; }
    restore(mode) {
        if (!(mode in this.live)) return;
        if (!(mode in this.cache)) throw new Error(`Mode ${mode} restored with nothing saved.`);
        this.live[mode] = this.cache[mode];
    }
}

/** The bytes of one operation over a list of modes, and its effect on the model. */
const put = (value, ...modes) => ({ bytes: csi(`?${modes.join(';')}`, value ? 'h' : 'l'), apply: model => modes.forEach(mode => model.put(mode, value)) });
const save = (...modes) => ({ bytes: csi(`?${modes.join(';')}`, 's'), apply: model => modes.forEach(mode => model.save(mode)) });
const restore = (...modes) => ({ bytes: csi(`?${modes.join(';')}`, 'r'), apply: model => modes.forEach(mode => model.restore(mode)) });
/** Bytes with no effect on the model (cursor movement, the SCO controls, values the text does not list). */
const raw = bytes => ({ bytes, apply: () => {} });
const cup = (row, col) => raw(csi(`${row};${col}`, 'H'));
/** The probe character, written in the last column of the probe row. */
const lastColumn = [cup(xsvProbeRow + 1, 1), raw(csi('999', 'C'))];
const probeChar = raw(xsvProbeChar);

const baselineBytes = `${esc}[?6l${esc}[?69l${esc}[?7h${esc}[?25h`;
/** After every group: the baseline modes, the probe row erased, the cursor home (uncaptured, after the group's read). */
export const xsvRestoreBaseline = `${baselineBytes}${esc}[${xsvProbeRow + 1};1H${esc}[2K${esc}[1;1H`;
/** The measured reset the bind begins with: protection, SGR and the baseline modes, margins cleared, screen cleared, cursor home. */
export const xsvReset = '\x1b[0"q' + sgrAttrsReset + baselineBytes + '\x1b[r\x1b[2J\x1b[H';
/** The plan's static first feed re-establishes the attributed seed, erases the probe row and homes the cursor, without clearing the nonce row. */
export const xsvReseed = `${baselineBytes}\x1b[2;1H\x1b[1mNtilde\x1b[31mXT\x1b[0m\x1b[4;1H\x1b[4:3mU\x1b[0m\x1b[${xsvProbeRow + 1};1H\x1b[2K\x1b[2;6r\x1b[1;1H`;
/** The attributed seed the bind writes, then the nonce at home. */
export const xsvSeed = nonce => xsvReseed + '\x1b[1;1H' + nonce;
const seedCells = Object.freeze([
    ...[...'Ntilde'].map((char, col) => Object.freeze({ row: 1, col, char, bold: true })),
    Object.freeze({ row: 1, col: 6, char: 'X', bold: true, fgIndex: 1 }),
    Object.freeze({ row: 1, col: 7, char: 'T', bold: true, fgIndex: 1 }),
    Object.freeze({ row: 3, col: 0, char: 'U', underline: 'curly' }),
]);
/** The run's baseline title, written by the finish so the pane never keeps a test label. */
export const xsvBaseTitle = runId => `XSV-base-${runId.slice(0, 8)}`;

// ---------- the exchange groups ----------

/** One exchange group. `kind` is protocol (decides), control (setup proof) or policy (recorded only). `setup` is a list of uncaptured feeds written one by one before the request (each at most 64 bytes),
 *  `request` the captured exchange, `after` the uncaptured feed after the read, `expect` the modeled modes, `cursor` the expected cursor (`[row, col]`, `edge` for the last column of the probe row, or null
 *  when the contract does not read it), `probe` whether the probe character is expected on the probe row and a pending wrap iff auto-wrap holds. */
const ask = (role, kind, note, { request, setup = [], after, expect, cursor, probe }) => {
    const oversized = [request, ...setup].some(text => text.length > 64);
    if (oversized) throw new Error(`The ${role} request or setup exceeds the 64-byte exchange limit.`);
    return Object.freeze({ role, kind, note, request, after, probe, expect: Object.freeze({ ...expect }), setup: Object.freeze([...setup]), cursor: cursor === null || cursor === 'edge' ? cursor : Object.freeze([...cursor]) });
};

/** Build one case's groups by applying every step to the model in order, so each demanded state is the model's at the moment the group's read is taken. */
function builder() {
    const model = new XtermSavedModesModel();
    const feed = ops => ops.map(op => { op.apply(model); return op.bytes; }).join('');
    return {
        /** A group: each `pre` item is one uncaptured feed of ops, `main` ops are the captured request; the model is rebased afterwards, as the after feed rebases the App. */
        group(role, kind, note, pre, main, { cursor = [0, 0], probe = false, after = xsvRestoreBaseline } = {}) {
            const setup = pre.map(item => feed(item));
            const request = feed(main);
            const expect = { ...model.live };
            model.rebase();
            return ask(role, kind, note, { request, setup, after, expect, cursor, probe });
        },
    };
}

const baselineGroup = b => b.group('baseline', 'control', 'the baseline modes (auto-wrap on, origin mode off, DECLRMM off, cursor visible) are the stored state before any mode is changed', [], [cup(1, 1)]);
const flipAll = b => b.group('modes-change', 'control', 'the four read-back modes can be changed and read back: auto-wrap off, origin mode on, DECLRMM on and the cursor hidden',
    [], [put(false, 7), put(true, 6), put(true, 69), put(false, 25)], { cursor: null });
/** The 8 groups that save a mode, change it and restore it in each polarity, in the shape of the case. */
function polarityGroups(b, shape) {
    return readBackModes.flatMap(mode => {
        const name = modeNames[mode], base = xsvBaseline[mode];
        const cursor = mode === 6 ? null : [0, 0];
        return [
            b.group(`${name}-restores-default`, 'protocol', `${name} (?${mode}) saved at its default, changed and restored is back at its default: the stored state is the saved value`, ...shape.toDefault(mode, base), { cursor }),
            b.group(`${name}-restores-changed`, 'protocol', `${name} (?${mode}) saved changed, set back to its default and restored is changed again: the restore applies the saved value, not the default`, ...shape.toChanged(mode, base), { cursor }),
        ];
    });
}
const pinnedSaveOp = raw(pinnedSave), pinnedRestoreOp = raw(pinnedRestore);
const scoSave = [cup(3, 3), raw(csi('', 's')), cup(5, 5), save(7)];

/** The groups of modes.xtsave: each request holds the save and what shows it took effect. */
function saveGroups() {
    const b = builder();
    const shape = {
        toDefault: (mode, base) => [[], [save(mode), put(!base, mode), restore(mode)]],
        toChanged: (mode, base) => [[[put(!base, mode)]], [save(mode), put(base, mode), restore(mode)]],
    };
    return [
        baselineGroup(b), flipAll(b),
        b.group('pinned', 'protocol', 'the exact pinned CSI ? 7 s is accepted silently (its fenced exchange holds no reply) and the pinned CSI ? 7 r that follows leaves the screen unchanged', [], [pinnedSaveOp],
            { after: pinnedRestore + xsvRestoreBaseline }),
        b.group('after-pinned', 'protocol', 'after the pinned save/restore pair the modes are as they were', [], [cup(1, 1)]),
        ...polarityGroups(b, shape),
        b.group('only-listed-modes', 'protocol', 'only the listed mode is restored: ?7 saved, ?7 and ?25 changed, ?7 restored leaves ?25 as it was set', [], [save(7), put(false, 7, 25), restore(7)]),
        b.group('several-modes', 'protocol', 'a list of modes is saved and restored together: ?7;25 saved changed, set back and restored by one pair of sequences are changed again',
            [[put(false, 7, 25)]], [save(7, 25), put(true, 7, 25), restore(7, 25)]),
        b.group('second-save-replaces', 'protocol', 'a second save replaces the first (a one-level cache): saved off, set, saved on, reset and restored is on',
            [[put(false, 7)]], [save(7), put(true, 7), save(7), put(false, 7), restore(7)]),
        b.group('not-scosc', 'protocol', 'CSI ? 7 s is not SCOSC: it does not replace the cursor saved by CSI s, which CSI u restores', [], [...scoSave, cup(1, 1), raw(csi('', 'u'))], { cursor: [2, 2] }),
        b.group('wrap-effect-on', 'protocol', 'a restored auto-wrap on leaves a pending wrap after a character is written in the last column', [], [...lastColumn, save(7), put(false, 7), restore(7), probeChar],
            { cursor: 'edge', probe: true }),
        b.group('wrap-effect-off', 'protocol', 'a restored auto-wrap off leaves no pending wrap after a character is written in the last column', [[put(false, 7)]], [...lastColumn, save(7), put(true, 7), restore(7), probeChar],
            { cursor: 'edge', probe: true }),
        b.group('unlisted-values', 'policy', 'a value that names no saved mode (9999) and a list with none are silent and change nothing in Ntilde', [], [raw(csi('?9999', 's')), raw(csi('?9999', 'r')), raw(csi('?', 's')), raw(csi('?', 'r'))]),
        b.group('restore-twice', 'policy', 'a restore leaves the cache filled in Ntilde: restored, changed and restored again is the saved value', [], [save(7), put(false, 7), restore(7), put(false, 7), restore(7)]),
        b.group('cursor-1048', 'policy', 'xterm\'s ?1048 is the cursor save: saved at 3;3, moved, restored is back at 3;3', [], [cup(3, 3), raw(csi('?1048', 's')), cup(1, 1), raw(csi('?1048', 'r'))], { cursor: [2, 2] }),
        b.group('origin-restore-homes', 'policy', 'xterm\'s restore of ?6 moves the cursor to the home position: saved at 3;3 it is home after the restore', [], [cup(3, 3), save(6), restore(6)], { cursor: [0, 0] }),
    ];
}

/** The groups of modes.xtrestore: the saves are setup, the request is the restore. */
function restoreGroups() {
    const b = builder();
    const shape = {
        toDefault: (mode, base) => [[[save(mode)], [put(!base, mode)]], [restore(mode)]],
        toChanged: (mode, base) => [[[put(!base, mode)], [save(mode)], [put(base, mode)]], [restore(mode)]],
    };
    return [
        baselineGroup(b), flipAll(b),
        b.group('pinned', 'protocol', 'the exact pinned CSI ? 7 r after the pinned CSI ? 7 s is accepted silently (its fenced exchange holds no reply) and leaves the screen unchanged', [[pinnedSaveOp]], [pinnedRestoreOp]),
        b.group('after-pinned', 'protocol', 'after the pinned save/restore pair the modes are as they were', [], [cup(1, 1)]),
        ...polarityGroups(b, shape),
        b.group('only-listed-modes', 'protocol', 'only the listed mode is restored: ?7 saved, ?7 and ?25 changed, ?7 restored leaves ?25 as it was set', [[save(7)], [put(false, 7, 25)]], [restore(7)]),
        b.group('several-modes', 'protocol', 'a list of modes is restored together: ?7;25 saved changed, set back and restored by one sequence are changed again',
            [[put(false, 7, 25)], [save(7, 25)], [put(true, 7, 25)]], [restore(7, 25)]),
        b.group('not-decstbm-or-scorc', 'protocol', 'CSI ? 7 r is neither DECSTBM nor SCORC: the scroll region and the cursor (saved by CSI s at 3;3, moved to 5;5) stay',
            [scoSave], [restore(7)], { cursor: [4, 4] }),
        b.group('wrap-effect-on', 'protocol', 'a restored auto-wrap on leaves a pending wrap after a character is written in the last column',
            [[...lastColumn, save(7), put(false, 7)]], [restore(7), probeChar], { cursor: 'edge', probe: true }),
        b.group('wrap-effect-off', 'protocol', 'a restored auto-wrap off leaves no pending wrap after a character is written in the last column',
            [[put(false, 7)], [...lastColumn, save(7), put(true, 7)]], [restore(7), probeChar], { cursor: 'edge', probe: true }),
        b.group('unlisted-values', 'policy', 'a value that names no saved mode (9999) and a list with none are silent and change nothing in Ntilde', [], [raw(csi('?9999', 'r')), raw(csi('?', 'r'))]),
        b.group('restore-twice', 'policy', 'a restore leaves the cache filled in Ntilde: restored, changed and restored again is the saved value', [[save(7), put(false, 7)]], [restore(7), put(false, 7), restore(7)]),
        b.group('cursor-1048', 'policy', 'xterm\'s ?1048 is the cursor restore: saved at 3;3, moved, restored is back at 3;3', [[cup(3, 3), raw(csi('?1048', 's')), cup(1, 1)]], [raw(csi('?1048', 'r'))], { cursor: [2, 2] }),
        b.group('origin-restore-homes', 'policy', 'xterm\'s restore of ?6 moves the cursor to the home position: saved at 3;3 it is home after the restore', [[cup(3, 3), save(6)]], [restore(6)], { cursor: [0, 0] }),
    ];
}

/** The plan of one case: the reseed, an initial cells read, then for each group its uncaptured setup feeds, the captured request, its report read and the after feed. */
function planOf(groups) {
    const plan = [planFeed(xsvReseed), planRead('cells')];
    for (const item of groups) {
        for (const feed of item.setup) plan.push(planFeed(feed));
        plan.push({ feed: item.request, capture: true }, planRead('report'), planFeed(item.after));
    }
    return plan;
}

const appearanceScopes = Object.freeze({
    'modes.xtsave': 'xterm XTSAVE (CSI ? Pm s) only: silence, an unchanged screen, and the stored DECAWM (?7, also as a pending wrap after a character in the last column), DECOM (?6), DECLRMM (?69) and DECTCEM '
        + '(?25) state read back after restores: each mode returns to its saved value in both polarities, only the listed modes are restored, a list restores together, a second save replaces the first, '
        + 'and the sequence is not SCOSC; values the text does not list, a repeated restore, xterm\'s ?1048 cursor save and the cursor move of a restored ?6 are policy groups; stored state, no rendering '
        + 'claim, no claim for the input-side, mouse, alternate-screen or reverse-wrap modes, and the pinned probe measures only DA1 and cursor responsiveness',
    'modes.xtrestore': 'xterm XTRESTORE (CSI ? Pm r) only: silence, an unchanged screen, and the stored DECAWM (?7, also as a pending wrap after a character in the last column), DECOM (?6), DECLRMM (?69) and '
        + 'DECTCEM (?25) state read back after restores: each mode returns to its saved value in both polarities, only the listed modes are restored, a list restores together, and the sequence is neither '
        + 'DECSTBM nor SCORC; values the text does not list, a repeated restore, xterm\'s ?1048 cursor restore and the cursor move of a restored ?6 are policy groups; stored state, no rendering claim, no claim '
        + 'for the input-side, mouse, alternate-screen or reverse-wrap modes, and the pinned probe measures only DA1 and cursor responsiveness',
});

/** The group state: everything an exchange must leave unchanged apart from the probe row, and the modes it may change. */
const groupOf = (screen, replies) => ({ screen, ink: selectiveEraseInk(screen), cursor: [screen.cursorRow, screen.cursorCol], cursorVisible: screen.cursorVisible,
    cursorMode: screen.cursorMode, rows: screen.rows, cols: screen.cols, buffer: screen.bufferState, horizontalMargins: screen.horizontalMargins, replies });
const outsideProbe = state => state.ink.filter(cell => cell.row !== xsvProbeRow);
const probeInk = state => state.ink.filter(cell => cell.row === xsvProbeRow);
/** The stored modes the contract reads back. */
const modesOf = state => ({ 7: state.buffer.autoWrap, 6: state.buffer.originMode, 69: state.horizontalMargins.leftRightMarginMode, 25: state.cursorVisible });
/** The state key: everything but the cells of the probe row, the cursor and the modeled modes (compared separately). */
const stateKey = state => JSON.stringify([outsideProbe(state), state.cursorMode, { ...state.buffer, autoWrap: null, originMode: null, pendingWrap: null, leftRightMarginMode: null },
    [state.horizontalMargins.leftMargin, state.horizontalMargins.rightMargin]]);
const printable = value => (value === null ? null : value.replaceAll(esc, String.raw`\e`).replaceAll('\x07', String.raw`\a`));

/** Whether the initial read holds the contracted seed: geometry, nonce at home, the attributed cells, margins and baseline modes, and an empty probe row. */
function seedOk(state, nonce) {
    const count = row => state.ink.filter(cell => cell.row === row).length;
    const planted = row => seedCells.filter(cell => cell.row === row).length;
    const roomy = state.rows >= xsvMinimum.rows && state.cols >= xsvMinimum.cols;
    const homed = state.screen.lines[0] === nonce && state.cursor.every(at => at === 0);
    const framed = same([state.buffer.scrollTop, state.buffer.scrollBottom], seedMargins) && state.buffer.alternateScreen === true;
    const modes = same(modesOf(state), xsvBaseline) && state.buffer.pendingWrap === false;
    return roomy && homed && framed && modes && count(1) === planted(1) && count(3) === planted(3) && count(xsvProbeRow) === 0 && seedCells.every(spec => seedCellOk(state.ink, spec));
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

/** The cells the probe row must hold: the probe character in the last column for a probe group, nothing otherwise. */
function probeRowMismatch(group, state) {
    const cells = probeInk(state);
    if (!group.probe) return cells.length === 0 ? null : 'the probe row holds a cell no request wrote';
    return cells.length === 1 && cells[0].col === state.cols - 1 && cells[0].char === xsvProbeChar ? null : 'the probe character is not stored alone in the last column of the probe row';
}

/** The way one exchange group differs from its demand, or null: silence and unchanged state first, then the modes, the cursor and the probe row against the model. */
function groupMismatch(group, state, initial) {
    if (stateKey(state) !== stateKey(initial)) {
        return `The ${group.role} request changed the screen outside the probe row, the cursor style, the scroll or horizontal margins, the insert mode or the active screen: saving and restoring modes must change nothing but the listed modes`;
    }
    if (state.replies.length > 0) return `The ${group.role} request was answered with ${state.replies.length} reply(ies): a mode save or restore must be silent`;
    const modes = modesOf(state);
    const wrong = Object.keys(modeNames).filter(mode => modes[mode] !== group.expect[mode]);
    if (wrong.length > 0) return `The ${group.role} request left ${wrong.map(mode => `${modeNames[mode]} (?${mode}) ${modes[mode] ? 'set' : 'reset'}`).join(', ')} instead of ${wrong.map(mode => (group.expect[mode] ? 'set' : 'reset')).join(', ')}`;
    const edge = [xsvProbeRow, state.cols - 1];
    const cursor = group.cursor === 'edge' ? edge : group.cursor;
    if (cursor !== null && (state.cursor[0] !== cursor[0] || state.cursor[1] !== cursor[1])) return `The ${group.role} request left the cursor at ${state.cursor.join(',')} instead of ${cursor.join(',')}`;
    if (state.buffer.pendingWrap !== (group.probe && group.expect[7])) return `The ${group.role} request left a pending wrap of ${state.buffer.pendingWrap} after the probe character: auto-wrap is ${group.expect[7] ? 'set' : 'reset'}`;
    const rowProblem = probeRowMismatch(group, state);
    return rowProblem === null ? null : `The ${group.role} request: ${rowProblem}`;
}

/** The case's grade, evaluated in execution order so an earlier failure is never overridden by a later group it damaged: the seed first, then each group as the plan ran it. The first control that fails
 *  makes the case inconclusive (the setup did not take, so nothing after it can be judged); the first protocol group that mismatches makes it unsupported; policy groups never decide. */
function gradeCase(item, observation) {
    if (!seedOk(observation.initial, observation.nonce)) return inconclusive('The attributed seed (nonce, cells, margins and baseline modes) was not measured before any request');
    for (const group of item.groups) {
        const mismatch = group.kind === 'policy' ? null : groupMismatch(group, observation[group.role], observation.initial);
        if (mismatch !== null) return group.kind === 'control' ? inconclusive(`Setup control failed before the effect could be judged: ${mismatch}`) : unsupported(mismatch);
    }
    return { outcome: 'supported' };
}

/** The modes as published: each read-back mode set or reset. */
const modeText = modes => Object.fromEntries(Object.entries(modeNames).map(([mode, name]) => [name, modes[mode] ? 'set' : 'reset']));

/** The observed record of one case: every group with its demanded and received modes and whether the state was unchanged. */
function briefCase(item, observation) {
    const groups = Object.fromEntries(item.groups.map(group => {
        const state = observation[group.role];
        return [group.role, { kind: group.kind, request: printable(group.request), ...(group.setup.length === 0 ? {} : { setup: group.setup.map(printable) }), after: printable(group.after),
            demand: group.note, demanded: { modes: modeText(group.expect), cursor: group.cursor === 'edge' ? 'last column of the probe row' : group.cursor, probeCharacter: group.probe },
            received: { modes: modeText(modesOf(state)), cursor: state.cursor, pendingWrap: state.buffer.pendingWrap, probeRow: probeInk(state).map(cell => ({ col: cell.col, char: cell.char })) },
            replies: state.replies.map(printable), matches: groupMismatch(group, state, observation.initial) === null, unchanged: stateKey(state) === stateKey(observation.initial) }];
    }));
    const { rows, cols } = observation.initial;
    return { geometry: { rows, cols }, effectContract: xsvEffectContractId, pinnedScript: printableScript(item.pinnedScript),
        seed: { nonce: observation.nonce, margins: [observation.initial.buffer.scrollTop, observation.initial.buffer.scrollBottom], modes: modeText(modesOf(observation.initial)),
            alternate: observation.initial.buffer.alternateScreen, cells: seedOk(observation.initial, observation.nonce) }, groups };
}

/** The pinned script with its bytes written printably. */
const printableScript = script => ({ termless: script.termless.map(call => call.map(part => printable(part))), terminal: script.terminal.map(call => call.map(part => printable(part))) });

const groupsByCase = Object.freeze({ 'modes.xtsave': saveGroups(), 'modes.xtrestore': restoreGroups() });

/** The contracted cases, in suite order. */
function makeContract(caseId) {
    const groups = groupsByCase[caseId], script = xsvPinnedScripts[caseId];
    const roles = Object.freeze(['initial', ...groups.map(group => group.role)]);
    const contract = {
        caseId, probeId: caseId, id: caseId.replace('modes.', ''), roles, groups: Object.freeze(groups), plan: Object.freeze(planOf(groups)),
        pinnedPath: 'termless', pinnedRead: roles.indexOf('pinned'), pinnedExpected: script.expected, pinnedCriterion: pinnedCriteria[caseId], pinnedScript: script,
        terminalFixture: { writes: script.terminal.filter(([kind]) => kind === 'write').map(([, text]) => text), demand: 'no output from the save and restore sequences and a cursor-position reply',
            minimum: { rows: 1, cols: 1 }, note: 'the stock collector\'s write plus queryCursorPosition path; replayed over the same journaled cursor beside the termless verdict' },
        effectContractId: xsvEffectContractId, appearanceScope: appearanceScopes[caseId], evidence: 'owned-raw-replies-and-stored-modes',
        grade: observation => gradeCase(contract, observation), observed: observation => briefCase(contract, observation),
    };
    return Object.freeze(contract);
}

export const xsvContracts = Object.freeze(xsvCaseIds.map(makeContract));
/** Contract case ids in suite order. */
export const xsvContractCaseIds = Object.freeze(xsvContracts.map(item => item.caseId));
export { planFeeds as xsvFeeds, planReads as xsvReads } from './graded-plan.mjs';

/** The seed bytes of the plan (the reset followed by the attributed seed and nonce). */
export function xsvPlanSeed(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid xterm saved-mode fixture run identity.');
    return xsvReset + xsvSeed(xsvNoncePrefix + runId);
}
/** The finish: the screen reset, the baseline title written, the alternate screen left. */
export const xsvFinish = runId => xsvReset + `${esc}]0;${xsvBaseTitle(runId)}${st}` + '\x1b[?1049l';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateXsvFeeds(item, feeds) { validateExactFeeds(item, feeds, 'contract'); }

/** Group the read screens and replies of the case by role. */
export function xsvObservation(item, screens, replies, nonce) {
    const reads = item.plan.filter(step => step.read);
    if (!Array.isArray(screens) || screens.length !== reads.length || !Array.isArray(replies) || replies.length !== reads.length || typeof nonce !== 'string') {
        throw new Error('Missing xterm saved-mode read captures.');
    }
    const observation = Object.fromEntries(item.roles.map((role, index) => [role, groupOf(screens[index], replies[index])]));
    observation.nonce = nonce;
    return observation;
}

/** Independent contract grader entry. */
export function gradeXsvCase(item, observation, revision = xsvContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown xterm saved-mode contracts revision ${revision}.`);
    if (!observation || item.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) throw new Error(`Missing ${item.caseId} read captures.`);
    return item.grade(observation);
}

/** The fixed fixture facts other modules and tests read. */
export const xsvFixture = Object.freeze({ minimum: xsvMinimum, seedMargins, seedCells, groupsByCase, baseline: xsvBaseline, modeNames });
