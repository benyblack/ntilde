// Versioned probe contracts for the saved-state / reset qualification batch:
// modes.alt-screen.exit, modes.altscreen-47, modes.altscreen-1047, reset.sgr and reset.decaln.
//
// Pinned terminfo.dev@4.0.0 definitions (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e):
//   modes.alt-screen.exit   altScreen1049Roundtrip(ctx, "exit"): feeds ESC[2J ESC[1;1H PRIMARY-A, ESC[3;1H PRIMARY-B, ESC[5;7H, reads the cursor
//                           and both seeds, feeds ESC[?1049h and requires both seeded spans blank, feeds ESC[1;1H ALT-MARK and reads it, feeds
//                           ESC[?1049l and reads both spans, the marker position and the cursor, and always feeds ESC[?1049l ESC[0m afterwards.
//                           Supported iff both spans are restored, the marker did not leak and the cursor is back at 6,4. EVERY other outcome is
//                           the pinned inconclusive ("cannot attribute failure to enter or exit"); the pinned probe never returns unsupported.
//                           The pinned terminal path is a cursor-position query that always returns inconclusive.
//   modes.altscreen-47      feeds ESC[?47h, reads getMode("altScreen"), feeds ESC[?47l, reads it again, and ALWAYS returns the pinned
//                           inconclusive ("Mode metadata does not measure the buffer").
//   modes.altscreen-1047    the same with ?1047; ALWAYS the pinned inconclusive. Its pinned terminal path is a DECRPM query, also inconclusive.
//   reset.sgr               feeds ESC[1;3;7m X ESC[0m Y, reads cells (0,0) and (0,1): inconclusive unless X is bold, italic and inverse; supported
//                           iff Y is not bold, not italic and not inverse. A separate registered case from the already-qualified sgr.reset
//                           (ESC[1;3;4m / ESC[0m); its own fixture and criterion are executed here, the sibling's verdict is never reused.
//   reset.decaln            feeds ESC # 8 and reads cell (0,0): supported iff its stored character is E. The pinned terminal path is a
//                           capture-only pixel fixture that never returns a verdict.
//
// PINNED VERDICT AND EFFECT CONTRACT ARE DIFFERENT THINGS. For modes.altscreen-47 and modes.altscreen-1047 the pinned probe is inconclusive by
// design, whatever the terminal does. That verdict is preserved verbatim next to a separately identified effect contract
// (ntilde.saved-reset.effect/v1) that observes the visible buffer: its supported/unsupported verdict is an Ntilde observation graded
// from active-screen reads and controlled writes, never attributed to the pinned probe. The invisible bank is never read directly.
// For the other three cases the effect contract is the finer grader over the pinned fixture (never more permissive; every difference is
// recorded as a divergence), as in the earlier batches.
//
// The harness itself runs inside the alternate screen, where a nested ESC[?1049h is a no-op and would void the pinned exit fixture.
// Every plan therefore begins with a bank-normalizing reset (no RIS) that leaves the MAIN screen active, both banks blank and every
// saved bank at its default, so the pinned bytes run on the main screen exactly as the benchmark intends.
//
// Not claimed: reset.method (needs a backend reset() API, RIS is not substituted), state stacks, private-mode/SGR/color stacks, scrolling and
// history behavior, rendered appearance, and the pinned DECALN pixel path.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsFlagBits, validateSgrAttrsScreen, sgrAttrsPredicates } from './sgr-attrs-contracts.mjs';
import { expandScreenFlags } from './ascii-sgr-contracts.mjs';
import { expandResolvedColors, exposedRgb } from './resolved-colors.mjs';
import { expandCellDetails } from './cell-details.mjs';

const { neutral } = sgrAttrsPredicates;

/** Contracts v1 is the first measured semantics of this batch. */
export const savedResetContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const savedResetSuiteId = 'ntilde-owned-saved-reset-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const savedResetNoncePrefix = 'NTILDE_SR_';
/** Identity of the separately graded effect contract (an Ntilde observation, never a pinned grade). */
export const savedResetEffectContractId = 'ntilde.saved-reset.effect/v1';

/** Bank-normalizing clean: soft reset, DECSCNM/DECOM off, autowrap on, margins, IRM and SGR default, both erase variants, home. */
const clean = '\x1b[!p\x1b[?5l\x1b[?6l\x1b[?7h\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[3J\x1b[H';
/** The measured reset every group starts from: leave the alternate screen, clean the main bank, enter and clean the
 * alternate bank, and return to the MAIN screen. It never relies on RIS, which stays an operation under test elsewhere. */
export const savedResetReset = '\x1b[?1049l' + clean + '\x1b[?1049h' + clean + '\x1b[?47l' + clean;
/** The cell validation the attribute suite uses, so attribute runs, stored details and resolved colors must agree. */
export const validateSavedResetScreen = screen => {
    validateSgrAttrsScreen(screen);
    if (!screen.bufferState || !screen.history) throw new Error('Saved-state evidence needs observed buffer state and history.');
};

/** The pinned note texts, byte for byte. */
export const pinnedSeedNote = 'Primary seed was not measured';
export const pinnedModeNote = 'Mode metadata does not measure the buffer';
export const pinnedStyledNote = 'Styled control or reset cell was not measured';
/** The smallest measured geometry the controls need (the pinned probes need far less). */
const minimum = Object.freeze({ rows: 8, cols: 24 });

// ---- fixture bytes -------------------------------------------------------------------------------------------------------------------
/** CUP bytes for a zero-based row and column. */
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const markA = 'PRIMARY-A', markB = 'PRIMARY-B', altMark = 'ALT-MARK';
const exitPinned = Object.freeze(['\x1b[2J\x1b[1;1HPRIMARY-A', '\x1b[3;1HPRIMARY-B', '\x1b[5;7H', '\x1b[?1049h', '\x1b[1;1HALT-MARK',
    '\x1b[?1049l', '\x1b[?1049l\x1b[0m']);
const sgrPinned = Object.freeze(['\x1b[1;3;7mX\x1b[0mY']);
const decalnPinned = Object.freeze(['\x1b#8']);
const aligned = '\x1b#8';
const gridSeed = at(0, 0) + 'ABABABABABAB' + at(1, 0) + 'BABABABABABA' + at(2, 0) + 'ABABABABABAB' + at(0, 0);
const fullStyle = '\x1b[1;2;3;4;5;7;8;9;53;38;2;1;2;3;48;2;4;5;6;58;2;7;8;9m';

/** Compile a case's groups into one fixed plan. A group is its feeds followed by exactly one read; `pin: n` marks the group whose read
 * is the one the pinned probe sees after its n-th pinned feed. Group 0 is read after nothing but the reset (pinned reads at zero feeds). */
function compile(groups, pinnedFeeds) {
    const plan = [], roles = [], pinnedReads = { 0: 0 }, pinnedSeen = [];
    for (const [index, group] of groups.entries()) {
        for (const feed of group.feeds) plan.push(planFeed(feed));
        plan.push(planRead('cells'));
        roles.push(group.role);
        if (group.pin !== undefined) {
            pinnedReads[group.pin] = index;
            pinnedSeen.push(...group.pinFeeds);
        }
    }
    if (JSON.stringify(pinnedSeen) !== JSON.stringify(pinnedFeeds) || new Set(roles).size !== roles.length) throw new Error('Invalid saved-state plan.');
    return { plan: Object.freeze(plan), roles: Object.freeze(roles), pinnedReads: Object.freeze(pinnedReads) };
}
/** One plan group: its feeds, its read role and, for pinned groups, the pinned feed count the read follows. */
const group = (role, feeds, pin, pinFeeds = feeds) => ({ role, feeds, ...(pin === undefined ? {} : { pin, pinFeeds }) });
const initial = group('initial', [savedResetReset]);

/** The five plans, each the pinned fixture bytes followed by labeled controls and effect refinements. */
const plans = Object.freeze({
    'modes.alt-screen.exit': compile([initial,
        group('seed', exitPinned.slice(0, 3), 3), group('entered', [exitPinned[3]], 4), group('marked', [exitPinned[4]], 5),
        group('exited', [exitPinned[5]], 6), group('final', [exitPinned[6]], 7),
        group('reentry', ['\x1b[?47h']), group('back', ['\x1b[?47l']),
        group('staleSeed', [savedResetReset, at(0, 0) + markA + at(2, 0) + markB + at(1, 1) + '\x1b7' + at(4, 6)]),
        group('staleExited', ['\x1b[?1049h', at(0, 0) + altMark, '\x1b[?1049l']),
        group('styledSeed', [savedResetReset, at(0, 0) + '\x1b[1;31mSTYLED-A\x1b[0m' + at(2, 0) + '\x1b[4;32mSTYLED-B\x1b[0m' + at(4, 6) + '\x1b[1;34m']),
        group('styledExited', ['\x1b[?1049h', '\x1b[0m' + at(0, 0) + altMark, '\x1b[?1049l']), group('styledWitness', ['W'])], exitPinned),
    'modes.altscreen-47': compile([initial, group('pinnedEntered', ['\x1b[?47h'], 1), group('pinnedExited', ['\x1b[?47l'], 2),
        group('seed', [savedResetReset, at(0, 0) + 'MAIN-47' + at(2, 4)]), group('active', ['\x1b[?47h']),
        group('marked', [at(1, 1) + 'ALT-47']), group('exited', ['\x1b[?47l']), group('reentry', ['\x1b[?47h']), group('back', ['\x1b[?47l'])],
        ['\x1b[?47h', '\x1b[?47l']),
    'modes.altscreen-1047': compile([initial, group('pinnedEntered', ['\x1b[?1047h'], 1), group('pinnedExited', ['\x1b[?1047l'], 2),
        group('seed', [savedResetReset, at(0, 0) + 'MAIN-1047' + at(2, 4)]), group('active', ['\x1b[?1047h']),
        group('marked', [at(1, 1) + 'ALT-1047']), group('exited', ['\x1b[?1047l']), group('reentry', ['\x1b[?47h']), group('back', ['\x1b[?47l']),
        group('preSeed', [savedResetReset, '\x1b[?47h' + at(0, 0) + 'PRE-ALT' + '\x1b[?47l']), group('retainedEntry', ['\x1b[?1047h']),
        group('retainedExit', ['\x1b[?1047l'])], ['\x1b[?1047h', '\x1b[?1047l']),
    'reset.sgr': compile([initial, group('pinned', sgrPinned, 1),
        group('persist', [savedResetReset, '\x1b[1;3;7mXY']), group('full', [savedResetReset, fullStyle + 'X\x1b[0mY']),
        group('omitted', [savedResetReset, '\x1b[1;3;7mX\x1b[mY'])], sgrPinned),
    'reset.decaln': compile([initial, group('pinned', decalnPinned, 1),
        group('seed', [savedResetReset, gridSeed]), group('aligned', [aligned]),
        group('margins', [savedResetReset, '\x1b[3;6r' + aligned]), group('pen', [savedResetReset, '\x1b[1;4;44m' + aligned])], decalnPinned),
});

// ---- observation ---------------------------------------------------------------------------------------------------------------------
/** Project every cell of one screen once (the per-cell projector re-expands the whole screen, which is quadratic for a full grid).
 * Each cell is exactly what the pinned getCell sees (sgrAttrsCellAt) plus the stored fields the controls compare. */
export function projectSavedResetCells(screen) {
    validateSavedResetScreen(screen);
    const total = screen.rows * screen.cols;
    const flags = expandScreenFlags(screen);
    const details = expandCellDetails(screen.cellDetails, total, true);
    const resolved = expandResolvedColors(screen.resolvedColors, total, screen.colorBinding);
    const underlineNames = [false, 'single', 'double', 'curly', 'dotted', 'dashed'];
    return (row, col) => {
        const position = row * screen.cols + col;
        const detail = details[position], color = resolved[position], value = flags[position] ?? 0;
        if (!detail || !color) throw new Error(`Missing stored cell evidence at ${row},${col}.`);
        if (detail.flags !== value || Boolean(value & sgrAttrsFlagBits.underline) !== Boolean(detail.underlineStyle)) {
            throw new Error(`Contradictory stored cell projections at ${row},${col}.`);
        }
        return {
            char: (screen.lines[row] ?? ' ')[col] ?? ' ', flags: value,
            bold: (value & sgrAttrsFlagBits.bold) !== 0, dim: (value & sgrAttrsFlagBits.faint) !== 0,
            italic: (value & sgrAttrsFlagBits.italic) !== 0, underline: underlineNames[detail.underlineStyle] ?? false,
            inverse: (value & sgrAttrsFlagBits.inverse) !== 0, hidden: (value & sgrAttrsFlagBits.hidden) !== 0,
            strikethrough: (value & sgrAttrsFlagBits.strikethrough) !== 0, blink: (value & sgrAttrsFlagBits.blink) !== 0,
            overline: detail.overline, fg: exposedRgb(color.fg), bg: exposedRgb(color.bg), fgSource: color.fg.source, bgSource: color.bg.source,
            underlineStyle: detail.underlineStyle, underlineColorIndex: detail.underlineColorIndex, underlineArgb: detail.underlineArgb ?? null,
            fgIndex: detail.fg, bgIndex: detail.bg, foregroundArgb: detail.foregroundArgb ?? null, backgroundArgb: detail.backgroundArgb ?? null,
        };
    };
}
/** The pinned getCell of one cell (kept for the pinned replay and its tests). */
export const savedResetCellAt = (screen, row, col) => projectSavedResetCells(screen)(row, col);

/** Whether one projected cell is the default stored tuple (no attribute, default colors, no underline or overline color). */
const defaultDetail = cell => cell.flags === 0x300 && cell.fgIndex === -1 && cell.bgIndex === -1 && cell.foregroundArgb === null
    && cell.backgroundArgb === null && cell.overline === false && cell.underlineStyle === 0 && cell.underlineColorIndex === -1 && cell.underlineArgb === null;

/** Every cell that carries text or a non-default stored tuple, row-major, with its full stored detail. */
export function savedResetInk(screen) {
    const cellAt = projectSavedResetCells(screen);
    const ink = [];
    for (let row = 0; row < screen.rows; row++) {
        for (let col = 0; col < screen.cols; col++) {
            const cell = cellAt(row, col);
            if (cell.char === ' ' && defaultDetail(cell)) continue;
            ink.push({ row, col, ...cell });
        }
    }
    return ink;
}

/** A group's observation: ink, cursor, geometry, active buffer, margins, mode flags and history. */
const state = screen => {
    const buffer = screen.bufferState;
    return { ink: savedResetInk(screen), cursor: [screen.cursorRow, screen.cursorCol], rows: screen.rows, cols: screen.cols,
        alt: buffer.alternateScreen, top: buffer.scrollTop, bottom: buffer.scrollBottom, origin: buffer.originMode, wrap: buffer.autoWrap,
        insert: buffer.insertMode, pending: buffer.pendingWrap, scrollback: buffer.scrollbackLines, history: [...screen.history.lines] };
};
/** Whether a group's cursor is at a zero-based position. */
const sameCursor = (item, row, col) => item.cursor[0] === row && item.cursor[1] === col;
/** Nothing outside the measured operation moved: default margins, modes, no pending wrap and no history. */
const quiet = item => item.top === 0 && item.bottom === item.rows - 1 && !item.origin && item.wrap && !item.insert && !item.pending
    && item.scrollback === 0 && item.history.length === 0;
/** Characters laid on one row from a column. */
const place = (row, col, text) => [...text].flatMap((char, offset) => (char === ' ' ? [] : [[row, col + offset, char]]));
/** Row-major order of [row, col, char] placements. */
const byPosition = (a, b) => (a[0] - b[0]) || (a[1] - b[1]);
/** Ink equals exactly these [row, col, char] cells, every one with a neutral stored tuple. */
function inkIs(item, placements) {
    const sorted = [...placements].sort(byPosition);
    return item.ink.length === sorted.length && sorted.every(([row, col, char], index) => item.ink[index].row === row
        && item.ink[index].col === col && item.ink[index].char === char && neutral(item.ink[index]));
}
/** The stored identity of one cell (position, text and every stored field) for exact before/after comparisons. */
const storedKey = cell => JSON.stringify([cell.row, cell.col, cell.char, cell.flags, cell.fgIndex, cell.bgIndex, cell.foregroundArgb,
    cell.backgroundArgb, cell.underlineStyle, cell.underlineColorIndex, cell.underlineArgb, cell.overline]);
/** Whether two groups carry exactly the same stored ink (text, position and every stored field). */
const sameStored = (left, right) => JSON.stringify(left.ink.map(storedKey)) === JSON.stringify(right.ink.map(storedKey));
/** A blank, quiet main screen with the cursor home: the state the measured reset must leave. */
const mainBlank = item => !item.alt && item.ink.length === 0 && sameCursor(item, 0, 0) && quiet(item);
const attrNames = ['bold', 'dim', 'italic', 'underline', 'inverse', 'hidden', 'strikethrough', 'blink', 'overline'];
/** Exactly these attributes set, default colors and no underline color: the stored tuple of a styled cell. */
const exactly = (cell, set) => attrNames.every(name => (name === 'underline' ? cell.underline !== false : cell[name]) === set.includes(name))
    && cell.fgSource === 'default' && cell.bgSource === 'default' && cell.fgIndex === -1 && cell.bgIndex === -1 && cell.foregroundArgb === null
    && cell.backgroundArgb === null && cell.underlineColorIndex === -1 && cell.underlineArgb === null;

/** An inconclusive effect result: a control or setup was not measured. */
const inconclusive = note => ({ outcome: 'inconclusive', note });
/** An unsupported effect result: valid controls plus a measured mismatch. */
const unsupported = note => ({ outcome: 'unsupported', note });

/** The shared validity walk: geometry and DECSCNM first, then the blank main-screen control every case starts from. */
function prelude(observation) {
    const { initial: start, reverseVideo } = observation;
    if (start.rows < minimum.rows || start.cols < minimum.cols) return inconclusive('Measured geometry cannot fit the saved-state fixtures');
    if (reverseVideo !== false) return inconclusive('DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it');
    if (!mainBlank(start)) return inconclusive('The measured reset did not leave a blank, quiet main screen (blank neutral control not measured)');
    return null;
}

// ---- modes.alt-screen.exit ----------------------------------------------------------------------------------------------------------------
const exitSeedInk = [...place(0, 0, markA), ...place(2, 0, markB)];
const staleInk = exitSeedInk;
/** Whether a group's ink is exactly the two styled rows (bold red STYLED-A, single-underlined green STYLED-B). */
const styledInk = item => item.ink.length === 16 && item.ink.slice(0, 8).every((cell, col) => cell.row === 0 && cell.col === col && cell.char === 'STYLED-A'[col]
    && cell.bold && !cell.italic && cell.underline === false && cell.fgIndex === 1)
    && item.ink.slice(8).every((cell, col) => cell.row === 2 && cell.col === col && cell.char === 'STYLED-B'[col]
        && !cell.bold && cell.underline === 'single' && cell.fgIndex === 2);

/** Why the main screen did not come back after the roundtrip; the diagnosis only picks the words, never the verdict. */
function diagnoseExit(item) {
    if (item.alt) return 'The alternate screen is still active after ESC[?1049l';
    if (item.ink.filter(cell => cell.row === 0).map(cell => cell.char).join('').startsWith(altMark)) return 'ALT-MARK leaked into the restored main screen';
    if (item.ink.length === 0) return 'The main screen came back blank: its contents were not restored';
    if (inkIs(item, exitSeedInk)) return `The saved cursor was not restored: it is at ${item.cursor[0]},${item.cursor[1]}`;
    return 'The restored main screen differs from its seeded contents';
}

/** Grade the ?1049 roundtrip effect: main contents, saved cursor, bank separation, stale-save and attribute controls. */
function gradeExit(observation) {
    const early = prelude(observation);
    if (early) return early;
    const { seed, entered, marked, exited, final, reentry, back, staleSeed, staleExited, styledSeed, styledExited } = observation;
    if (seed.alt || !inkIs(seed, exitSeedInk) || !sameCursor(seed, 4, 6) || !quiet(seed)) return inconclusive(pinnedSeedNote);
    if (!entered.alt || entered.ink.length !== 0) {
        return inconclusive('Entry did not switch to a blank alternate screen; exit cannot be attributed (no-op transition control failed)');
    }
    if (!marked.alt || !inkIs(marked, place(0, 0, altMark))) return inconclusive('Alternate marker was not measured on the alternate screen');
    if (!reentry.alt) return inconclusive('The ESC[?47h re-entry control did not switch to the alternate screen');
    if (staleSeed.alt || !inkIs(staleSeed, staleInk) || !sameCursor(staleSeed, 4, 6)) {
        return inconclusive('The stale saved-cursor decoy was not measured');
    }
    if (styledSeed.alt || !styledInk(styledSeed) || !sameCursor(styledSeed, 4, 6)) return inconclusive('The styled main-screen seed was not measured');
    if (!inkIs(exited, exitSeedInk) || exited.alt || !sameCursor(exited, 4, 6) || !quiet(exited)) return unsupported(diagnoseExit(exited));
    if (final.alt || !inkIs(final, exitSeedInk) || !sameCursor(final, 4, 6)) {
        return unsupported('The pinned trailing ESC[?1049l ESC[0m changed the restored main screen');
    }
    if (reentry.ink.map(cell => cell.char).join('').includes('PRIMARY')) {
        return unsupported('Both banks show the same contents: the alternate screen is not a separate buffer');
    }
    if (back.alt || !inkIs(back, exitSeedInk)) return unsupported('The main screen was cleared or changed by a second screen switch');
    if (staleExited.alt || !inkIs(staleExited, staleInk) || !sameCursor(staleExited, 4, 6)) {
        return unsupported(`The saved cursor is stale: exit restored ${staleExited.cursor[0]},${staleExited.cursor[1]} instead of the cursor at entry`);
    }
    if (styledExited.alt || !sameStored(styledExited, styledSeed) || !sameCursor(styledExited, 4, 6)) {
        return unsupported('The main screen lost stored text or attributes across the roundtrip (incomplete restoration)');
    }
    return { outcome: 'supported', layers: [{ name: 'main-contents-and-cursor', outcome: 'supported' }] };
}

// ---- modes.altscreen-47 and modes.altscreen-1047 ---------------------------------------------------------------------------------------
/** The two swap semantics: ?47 keeps the alternate buffer across exit, ?1047 clears it on exit (xterm). Both keep the main buffer intact. */
const swaps = Object.freeze({ 47: { mainText: 'MAIN-47', altText: 'ALT-47', retains: true }, 1047: { mainText: 'MAIN-1047', altText: 'ALT-1047', retains: false } });

/** Grade the visible-buffer swap of ?47 (retains the alternate buffer) or ?1047 (clears it on exit). */
function gradeSwap(mode, observation) {
    const early = prelude(observation);
    if (early) return early;
    const { mainText, altText, retains } = swaps[mode];
    const { seed, active, marked, exited, reentry, back } = observation;
    const mainInk = place(0, 0, mainText), altInk = place(1, 1, altText);
    if (seed.alt || !inkIs(seed, mainInk) || !sameCursor(seed, 2, 4) || !quiet(seed)) return inconclusive(`The main-screen seed was not measured for ?${mode}`);
    if (mode === 1047 && (observation.preSeed.alt || observation.preSeed.ink.length !== 0)) return inconclusive('The alternate pre-content control left the main screen unclean');
    if (!active.alt) return unsupported(`ESC[?${mode}h did not switch to the alternate screen (the buffer swap was a no-op)`);
    if (inkIs(active, mainInk) || active.ink.some(cell => cell.char === 'M')) return unsupported('The alternate screen shows the main-screen contents: the two banks share contents');
    if (active.ink.length !== 0) return unsupported(`The alternate screen was not blank on first entry`);
    if (!marked.alt || !inkIs(marked, altInk)) return inconclusive('The alternate marker write was not measured');
    if (exited.alt) return unsupported(`ESC[?${mode}l did not return to the main screen`);
    if (!inkIs(exited, mainInk)) {
        return unsupported(exited.ink.length === 0 ? 'The main screen was cleared by leaving the alternate screen'
            : 'The alternate contents leaked into, or altered, the main screen');
    }
    if (!reentry.alt) return inconclusive('The ESC[?47h re-entry control did not switch to the alternate screen');
    if (retains ? !inkIs(reentry, altInk) : reentry.ink.length !== 0) {
        return unsupported(retains ? 'The alternate buffer was not retained across exit and re-entry (?47 swaps without clearing)'
            : 'The alternate buffer was not cleared on exit (?1047 clears it)');
    }
    if (back.alt || !inkIs(back, mainInk)) return unsupported('The main screen was changed by a second screen switch');
    return { outcome: 'supported', layers: [{ name: `visible-buffer-swap-${retains ? 'retain' : 'clear'}-on-exit`, outcome: 'supported' }] };
}

// ---- reset.sgr -----------------------------------------------------------------------------------------------------------------------
const styledAttrs = ['bold', 'italic', 'inverse'];
/** Grade SGR 0: styles stored on X, every stored styled field cleared on Y, nothing else changed. */
function gradeSgr(observation) {
    const early = prelude(observation);
    if (early) return early;
    const { pinned, persist, full, omitted } = observation;
    const cells = item => ({ x: item.ink.find(cell => cell.row === 0 && cell.col === 0), y: item.ink.find(cell => cell.row === 0 && cell.col === 1) });
    const { x, y } = cells(pinned);
    if (x?.char !== 'X' || y?.char !== 'Y' || !exactly(x, styledAttrs)) return inconclusive(pinnedStyledNote);
    if (pinned.ink.length !== 2 || !sameCursor(pinned, 0, 2) || pinned.alt || !quiet(pinned)) return inconclusive('The styled fixture changed more than the two measured cells');
    /** Only the two measured cells changed, the cursor advanced past them and nothing outside the operation moved. */
    const contained = item => item.ink.length === 2 && sameCursor(item, 0, 2) && !item.alt && quiet(item);
    const held = cells(persist);
    if (!contained(persist)) return inconclusive('The no-reset control changed more than the two measured cells or moved unrelated state');
    if (!held.x || !held.y || !exactly(held.x, styledAttrs) || !exactly(held.y, styledAttrs)) {
        return inconclusive('The no-reset control did not keep the pen active (the styles cannot be attributed to the reset)');
    }
    const wide = cells(full);
    const storesUnderlineColor = wide.x !== undefined && (wide.x.underlineColorIndex !== -1 || wide.x.underlineArgb !== null);
    if (wide.x?.char !== 'X' || neutral(wide.x) || wide.x.underline !== 'single' || !wide.x.overline || !storesUnderlineColor
        || wide.x.foregroundArgb === null || wide.x.backgroundArgb === null) {
        return inconclusive('The full-tuple control did not store every styled field on X');
    }
    if (!contained(full)) return unsupported('SGR 0 in the full-tuple control changed unrelated state (cursor, active buffer, margins, modes, history or extra cells)');
    if (!neutral(y)) return unsupported('SGR 0 did not clear every styled field on Y');
    if (wide.y?.char !== 'Y' || !neutral(wide.y)) return unsupported('SGR 0 left a styled field (underline, overline, strikethrough, color or underline color) on Y');
    if (!exactly(x, styledAttrs)) return unsupported('The reset changed the already-written X cell');
    const spelled = cells(omitted);
    if (!spelled.y || !spelled.x || !contained(omitted)) return inconclusive('The omitted-parameter spelling control was not measured');
    return { outcome: 'supported', layers: [{ name: 'styles-active-on-x-cleared-on-y', outcome: 'supported' }] };
}

// ---- reset.decaln --------------------------------------------------------------------------------------------------------------------
/** Whether every cell of the screen stores E in row-major order. */
const fullGrid = item => item.ink.length === item.rows * item.cols && item.ink.every((cell, index) => cell.row === Math.floor(index / item.cols)
    && cell.col === index % item.cols && cell.char === 'E');
const gridSeedInk = [...place(0, 0, 'ABABABABABAB'), ...place(1, 0, 'BABABABABABA'), ...place(2, 0, 'ABABABABABAB')];
/** Grade DECALN: E stored in cell 0,0 (pinned) and across the full grid with a homed cursor (refinement). */
function gradeDecaln(observation) {
    const early = prelude(observation);
    if (early) return early;
    const { pinned, seed, aligned: grid } = observation;
    if (seed.alt || !inkIs(seed, gridSeedInk) || !sameCursor(seed, 0, 0)) return inconclusive('The known non-E seed grid (cursor-only control) was not measured');
    const cell = pinned.ink.find(entry => entry.row === 0 && entry.col === 0);
    if (!cell || cell.char !== 'E') return unsupported('DECALN did not store E in cell 0,0');
    if (grid.alt || grid.ink.some(entry => entry.char === 'A' || entry.char === 'B')) return unsupported('DECALN left the seeded A/B cells in place (a no-op on the seeded grid)');
    const layers = [{ name: 'pinned-cell-0-0', outcome: 'supported' }];
    if (!fullGrid(pinned) || !fullGrid(grid) || !sameCursor(pinned, 0, 0) || !sameCursor(grid, 0, 0) || pinned.pending || grid.pending) {
        layers.push({ name: 'full-grid-refinement', outcome: 'unsupported' });
        return { outcome: 'unsupported', layers, note: 'Cell 0,0 holds E but the full grid or the homed cursor does not match the DECALN alignment pattern' };
    }
    layers.push({ name: 'full-grid-refinement', outcome: 'supported' });
    return { outcome: 'supported', layers };
}

// ---- observed records ------------------------------------------------------------------------------------------------------------------
/** A compact stored record: adjacent cells of one row with the same stored tuple collapse into `{ row, cols, text, stored }`. */
function briefInk(ink) {
    const tuple = cell => (neutral(cell) ? 'neutral' : { flags: cell.flags, fgIndex: cell.fgIndex, bgIndex: cell.bgIndex, foregroundArgb: cell.foregroundArgb,
        backgroundArgb: cell.backgroundArgb, underlineStyle: cell.underlineStyle, underlineColorIndex: cell.underlineColorIndex,
        underlineArgb: cell.underlineArgb, overline: cell.overline });
    const runs = [];
    for (const cell of ink) {
        const stored = tuple(cell), last = runs.at(-1);
        if (last && last.row === cell.row && last.cols[1] + 1 === cell.col && JSON.stringify(last.stored) === JSON.stringify(stored)) {
            last.cols[1] = cell.col;
            last.text += cell.char;
        } else runs.push({ row: cell.row, cols: [cell.col, cell.col], text: cell.char, stored });
    }
    return runs;
}
/** The compact observed record of one group. */
const brief = item => ({ alt: item.alt, cursor: item.cursor, margins: [item.top, item.bottom], origin: item.origin, autoWrap: item.wrap, insertMode: item.insert,
    pendingWrap: item.pending, scrollbackLines: item.scrollback, ink: briefInk(item.ink) });

/** The observed record of the exit case, with its labeled observations. */
const observedExit = observation => {
    const witness = observation.styledWitness.ink.find(cell => cell.row === 4 && cell.col === 6);
    return { roles: Object.fromEntries(plans['modes.alt-screen.exit'].roles.map(role => [role, brief(observation[role])])),
        observations: { policy: 'observed, not required: whether the alternate bank is retained after ESC[?1049l, and whether the saved pen is restored',
            alternateRetainedAfter1049Exit: inkIs(observation.reentry, place(0, 0, altMark)),
            savedPenWitness: witness === undefined ? null : { char: witness.char, bold: witness.bold, fgIndex: witness.fgIndex,
                restored: witness.char === 'W' && witness.bold && witness.fgIndex === 4 } } };
};
/** The observed record of a swap case, with its labeled observations. */
const observedSwap = mode => observation => ({
    roles: Object.fromEntries(plans[`modes.altscreen-${mode}`].roles.map(role => [role, brief(observation[role])])),
    observations: { policy: 'observed, not required: cursor positions across the swaps' + (mode === 1047 ? ' and whether ?1047h retains previous alternate contents on entry' : ''),
        ...(mode === 1047 ? { retainsAlternateContentsOnEntry: inkIs(observation.retainedEntry, place(0, 0, 'PRE-ALT')) } : {}),
        cursorAfterExit: observation.exited.cursor, cursorAfterBack: observation.back.cursor } });
/** The Y cell of the omitted-spelling control. */
const omittedY = observation => observation.omitted.ink.find(cell => cell.col === 1 && cell.row === 0);
/** The observed record of the reset.sgr case, with its labeled spelling observation. */
const observedSgr = observation => ({
    roles: Object.fromEntries(plans['reset.sgr'].roles.map(role => [role, brief(observation[role])])),
    observations: { policy: 'observed, not graded: the omitted-parameter reset spelling (ESC[m) against the pinned explicit ESC[0m',
        omittedSpellingClearsY: omittedY(observation) !== undefined && neutral(omittedY(observation)) } });
/** The observed record of the reset.decaln case, with its labeled observations. */
const observedDecaln = observation => ({
    roles: Object.fromEntries(plans['reset.decaln'].roles.map(role => [role, brief(observation[role])])),
    observations: { policy: 'observed, not required: stored tuple of the E cells, margins and pen after DECALN',
        gridCoversScreen: fullGrid(observation.aligned), marginsAfterDecalnWithRegion: [observation.margins.top, observation.margins.bottom],
        eCellStoredNeutral: observation.pinned.ink.length > 0 && observation.pinned.ink.every(neutral),
        eCellsUnderPen: observation.pen.ink.length > 0 ? briefInk(observation.pen.ink.slice(0, 1)) : [] } });

// ---- the five contracts --------------------------------------------------------------------------------------------------------------
/** One contract: its pinned fixture and replay map, its plan, its effect grader and its provenance. */
const case_ = fields => {
    const compiled = plans[fields.caseId];
    return Object.freeze({
        probeId: fields.caseId, pinnedPath: 'termless', plan: compiled.plan, roles: compiled.roles, pinnedReads: compiled.pinnedReads,
        effectContract: savedResetEffectContractId, ...fields,
        grade: observation => fields.gradeEffect(observation),
    });
};
const exitCase = case_({ caseId: 'modes.alt-screen.exit', pinnedFeeds: exitPinned, pinnedAlwaysInconclusive: false,
    // The pinned `finally` always feeds this write, also when the roundtrip returns early (an early return skips the later feeds).
    pinnedTrailing: exitPinned[6],
    pinnedExpected: 'CSI ? 1049 l restores the primary buffer and the saved cursor',
    pinnedCriterion: 'PRIMARY-A/B restored, ALT-MARK not leaked and cursor back at 6,4 after a seeded ?1049 roundtrip (supported or inconclusive, never unsupported)',
    terminalFixture: { writes: ['\x1b[?1049h', '\x1b[3;3H', '\x1b[?1049l'], demand: 'cursor responsiveness after exit only (always inconclusive)', minimum: { rows: 3, cols: 3 },
        note: 'upstream terminal path is a cursor-position query that never carries a verdict' },
    appearanceScope: 'stored main-screen cells and attributes, the saved cursor and the active-buffer flag, observed through active-screen reads; the alternate bank is never read directly',
    gradeEffect: gradeExit, observedOf: observedExit });
/** The contract of one swap case (?47 or ?1047): pinned inconclusive by design, effect graded separately. */
const swapCase = mode => case_({ caseId: `modes.altscreen-${mode}`, pinnedFeeds: Object.freeze([`\x1b[?${mode}h`, `\x1b[?${mode}l`]), pinnedAlwaysInconclusive: true,
    pinnedExpected: `?${mode} swaps${mode === 1047 ? ' and clears' : ''} the visible screen buffer`,
    pinnedCriterion: `getMode("altScreen") after ?${mode}h and ?${mode}l; the pinned verdict is always inconclusive ("${pinnedModeNote}")`,
    terminalFixture: { writes: [], demand: mode === 1047 ? 'DECRPM status of mode 1047 (always inconclusive)' : 'none (always inconclusive)', minimum: { rows: 1, cols: 1 },
        note: mode === 1047 ? 'upstream terminal path is a DECRPM query that never carries a verdict' : 'upstream terminal path leaves the alternate buffer unchanged' },
    appearanceScope: `visible active-screen contents across ?${mode} entry, writes, exit and re-entry; separately identified effect contract, not the pinned probe`,
    gradeEffect: observation => gradeSwap(mode, observation), observedOf: observedSwap(mode) });
const sgrCase = case_({ caseId: 'reset.sgr', pinnedFeeds: sgrPinned, pinnedAlwaysInconclusive: false,
    pinnedExpected: 'SGR 0 clears measured bold, italic, and inverse styling on Y',
    pinnedCriterion: 'ESC[1;3;7m X ESC[0m Y: X is bold, italic and inverse, Y is none of them (a separate case from sgr.reset)',
    terminalFixture: { writes: ['\x1b[1;1H\x1b[2K', '\x1b[1m', '\x1b[0m', 'X'], demand: 'cursor advance only (always inconclusive)', minimum: { rows: 1, cols: 2 },
        note: 'upstream terminal path is a consumed-byte cursor query that never carries a verdict' },
    appearanceScope: 'stored attribute flags, color storage and underline color of X and Y, the cursor and unrelated state; no resolved-color or pixel claim',
    gradeEffect: gradeSgr, observedOf: observedSgr });
const decalnCase = case_({ caseId: 'reset.decaln', pinnedFeeds: decalnPinned, pinnedAlwaysInconclusive: false,
    pinnedExpected: 'DECALN fills the measured cell (0,0) with E',
    pinnedCriterion: 'cell 0,0 holds E after ESC # 8 on the reset screen; the full-grid observation is a separately labeled refinement',
    terminalFixture: { writes: ['AB grid', '\x1b#8', 'capture'], demand: 'repeated E across the grid in captured pixels (capture only, never a verdict)', minimum: { rows: 2, cols: 2 },
        note: 'upstream terminal path is a capture-only pixel fixture and is not qualified here' },
    appearanceScope: 'stored cells only: E in cell 0,0 (pinned), the full alignment grid and homed cursor (refinement); the pixel capture path is not claimed',
    gradeEffect: gradeDecaln, observedOf: observedDecaln });

/** The five pinned cases, in suite order. */
export const savedResetContracts = Object.freeze([exitCase, swapCase(47), swapCase(1047), sgrCase, decalnCase]);
/** Contract case ids in suite order. */
export const savedResetCaseIds = Object.freeze(savedResetContracts.map(contract => contract.caseId));

/** Group the read screens of one case by role. */
export function savedResetObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing saved-state read captures.');
    const observation = Object.fromEntries(contract.roles.map((role, index) => [role, state(screens[index])]));
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    observation.reverseVideo = null;
    if (binding.every(value => value === false)) observation.reverseVideo = false;
    else if (binding.includes(true)) observation.reverseVideo = true;
    return observation;
}

/** The observed record of one case. */
export const savedResetObserved = (contract, observation) => contract.observedOf(observation);

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeSavedResetCase(contract, observation, revision = savedResetContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown saved-state contracts revision ${revision}.`);
    if (!observation || contract.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as savedResetFeeds, planReads as savedResetReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateSavedResetFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}
