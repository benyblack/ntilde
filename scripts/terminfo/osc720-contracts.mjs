// Versioned contract for the rxvt-unicode OSC 720 qualification: extensions.osc720-scroll-up. This is not a publication or a measurement.
//
// PROTOCOL OWNER (primary source, rxvt-unicode rxvt(7) and src/command.C, case URxvt_view_up / URxvt_view_down):
//   OSC 720 ; Pt ST  Move viewing window up by Pt lines, or clear scrollback buffer if Pt = 0 (compile frills).   OSC 721 is the same toward the live line.
//   The reference implementation (process_osc_seq) dispatches an OSC only when the character after its digits is `;`. It then reads `lines = atoi(str)`; a non-zero count calls
//   scr_page(UP, lines) (the view moves, nothing is erased) and a zero count calls scr_erase_savelines() (the saved lines are erased, the screen and view are left alone); an
//   empty count after the `;` is zero too. The BARE form `ESC ] 720 BEL` has no `;`, so urxvt does not dispatch it at all and it has no effect there.
//   OSC 720 therefore controls a viewport for a counted `;Pt` and MUTATES the retained history for `;0` or `;`.
//
// THE PINNED PROBE (terminfo.dev@4.0.0, read from the bundle) sends the BARE form `ESC ] 720 BEL`, with no `;` and no count:
//   termless  feeds 30 lines `scroll-<i>\r\n`, reads ctx.getScrollback() (it needs totalLines > screenLines and viewportOffset > 0, else parserStateResult(null)), feeds the bare
//             form, reads again and passes iff after.viewportOffset < before.viewportOffset. viewportOffset is the absolute buffer row where the viewport starts: at the live view
//             it is totalLines - screenLines, scrolled to the top it is 0.
//   terminal  writes `ESC[1;1H ESC[2K ESC]720 BEL`, asks the cursor position and returns unverifiedEffect: always inconclusive, evidence "consumed". It never measures the viewport.
// The pinned bare form is therefore not a form the protocol owner defines: urxvt itself ignores it, so the pinned criterion cannot pass on its own reference terminal. A terminal that
// erased its history on `;0` would also drop viewportOffset to 0, so the criterion also cannot tell a viewport move from a history erase. This contract keeps those operations apart,
// expects no effect for the bare form (the reference behavior) and never promotes either operation to the other.
//
// The effect contract below uses its own oracle, written from the rxvt text above, and Ntilde's observation API (the view's own scroll position beside the stored history, cursor and screen).
// CSI SU, scrollback growth, keyboard/wheel viewport scrolling and ED 3 are DIFFERENT operations: the wheel and ED 3 appear only as observer controls that prove the measured quantities can move.
export const osc720ContractsVersion = 1;
export const osc720SuiteId = 'ntilde-owned-osc720-graded-v1';
export const osc720Prefix = 'ovu';
export const osc720NoncePrefix = 'NTILDE_OV_';
export const osc720CaseId = 'extensions.osc720-scroll-up';
export const osc720CaseIds = Object.freeze([osc720CaseId]);
export const osc720EffectContractId = 'ntilde-osc720-view-up-and-erase-saved-effect-v1';
export const osc720Minimum = Object.freeze({ rows: 12, cols: 48 });
export const osc720NonceRow = 9;
/** The wheel action's direction per parent action: one notch is three rows, away from (+1) or back toward (-1) the live line. */
export const osc720ActionDeltas = Object.freeze({ 'scroll-up': 1, 'scroll-down': -1 });
/** The wheel action's settle time: the App's smooth-scroll animation must finish before the paired read. */
export const osc720WheelSettleMs = 1500;
/** Every request the pinned and the contract bytes use, in one place. */
export const osc720Bare = '\x1b]720\x07';
const withCount = (count, terminator = '\x07') => `\x1b]720;${count}${terminator}`;

/** The pinned termless feed, exactly: thirty lines, fed one call each. */
export const osc720PinnedLines = Object.freeze(Array.from({ length: 30 }, (_, index) => `scroll-${index}\r\n`));
/** The pinned terminal path's writes, exactly. */
export const osc720PinnedTerminalWrites = Object.freeze(['\x1b[1;1H\x1b[2K', osc720Bare]);
/** What each pinned path can return; retained verbatim in the report. */
export const osc720Pinned = Object.freeze({
    termless: Object.freeze({ feeds: Object.freeze([...osc720PinnedLines, osc720Bare]), scrollbackReads: 2, criterion: 'after.viewportOffset < before.viewportOffset',
        guard: 'needs totalLines > screenLines and viewportOffset > 0, else parserStateResult(null)' }),
    terminal: Object.freeze({ writes: osc720PinnedTerminalWrites, note: 'Cursor answered; scrollback viewport was not measured', alwaysInconclusive: true }),
});

/** The prepared baseline: main screen, soft state reset, blank screen and no history; the bind adds the run nonce. */
export function osc720Fixture(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid OSC 720 run identity.');
    const nonce = osc720NoncePrefix + runId;
    const reset = '\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[H\x1b[2J\x1b[3J\x1b[H';
    return { nonce, reset, bind: reset + `\x1b[${osc720NonceRow + 1};1H` + nonce, finish: reset,
        // The cursor goes to the last row (CUP clamps), so the pinned thirty lines scroll exactly thirty rows into the history whatever the grid: the observation API bounds retained history at 64 rows.
        filler: '\x1b[H\x1b[2J\x1b[999;1H', pinnedFeed: osc720PinnedLines.join('') };
}

const live = Object.freeze({ scrolled: false });
const scrolled = Object.freeze({ scrolled: true });
const none = Object.freeze({ kind: 'none' });
const viewUp = count => Object.freeze({ kind: 'view-up', count });
const eraseSaved = Object.freeze({ kind: 'erase-saved' });
const wheel = Object.freeze({ kind: 'view-up-any' });

// role: protocol = graded against the rxvt text; control = observer/validity control that gates the verdict; policy = none in this version.
/** One experiment: its name (a string), role, setup, probe bytes and rxvt expectation. */
const spec = (/** @type {string} */ name, role, setup, probe, expected) => ({ name, role, setup, probe, expected });
const experimentSpecs = [
    // The pinned bare form has no `;`, so the reference implementation does not dispatch it: it must change nothing.
    spec('pinned-bare', 'protocol', live, osc720Bare, none),
    spec('count-zero', 'protocol', live, withCount(0), eraseSaved),
    // An empty count after the `;` is zero too (atoi of the empty string).
    spec('count-empty', 'protocol', live, '\x1b]720;\x07', eraseSaved),
    spec('count-3', 'protocol', live, withCount(3), viewUp(3)),
    spec('count-3-st', 'protocol', live, withCount(3, '\x1b\\'), viewUp(3)),
    spec('count-clamped', 'protocol', live, withCount(100000), viewUp(100000)),
    spec('count-2-from-scrolled', 'protocol', scrolled, withCount(2), viewUp(2)),
    spec('unmarked', 'control', live, '\x1b[0m', none),
    spec('ed3-control', 'control', live, '\x1b[3J', eraseSaved),
];

/** Two notches back down: from any scrolled offset this experiment can reach (3, or 5 if a view move were implemented) the view returns to the live line before the next reset, so no experiment inherits a scrolled view. */
const restoreView = Object.freeze([{ action: 'scroll-down' }, { action: 'scroll-down' }, { wait: osc720WheelSettleMs }]);

/** The plan of one experiment: reset, the fixed history, an optional wheel scroll, the setup read, the probe, the effect read and, after a scroll, the restoration of the live view. */
function planOf(fixture, { setup, probe }) {
    return [{ feed: fixture.reset }, { feed: fixture.filler }, { feed: fixture.pinnedFeed },
        ...(setup.scrolled ? [{ action: 'scroll-up' }, { wait: osc720WheelSettleMs }] : []),
        { read: 'before', kind: 'setup' }, { feed: probe }, { read: 'after', kind: 'effect' }, ...(setup.scrolled ? restoreView : [])];
}

/** Every observation follows a distinct, observed setup; the wheel control is the only experiment that moves the view without a probe. */
export function osc720Experiments(runId) {
    const fixture = osc720Fixture(runId);
    const probes = experimentSpecs.map(item => ({ caseId: `${osc720CaseId}/${item.name}`, owner: osc720CaseId, name: item.name, role: item.role, setup: item.setup, probe: item.probe,
        expected: item.expected, plan: planOf(fixture, item) }));
    const wheelControl = { caseId: `${osc720CaseId}/wheel-control`, owner: osc720CaseId, name: 'wheel-control', role: 'control', setup: live, probe: null, expected: wheel,
        plan: [{ feed: fixture.reset }, { feed: fixture.filler }, { feed: fixture.pinnedFeed }, { read: 'before', kind: 'setup' },
            { action: 'scroll-up' }, { wait: osc720WheelSettleMs }, { read: 'after', kind: 'effect' }, ...restoreView] };
    return [...probes, wheelControl];
}

/** The pinned replay contexts' wait/action vocabulary, checked by the shared driver. */
export const osc720StepKinds = Object.freeze(['feed', 'read', 'wait', 'action']);

/** The state the pinned termless probe reads, built from the App's own observation: the absolute row where the view starts, the total buffer rows and the visible rows. */
export function osc720ScrollbackState(rows, facts) {
    return { viewportOffset: facts.scrollbackLines - facts.viewport.scrollOffset, totalLines: facts.scrollbackLines + rows, screenLines: rows };
}
