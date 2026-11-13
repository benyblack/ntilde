// Versioned contract for the OSC 9 family: extensions.osc9-progress and extensions.notifications. This is not a publication or a measurement.
//
// The PINNED probes (terminfo.dev@4.0.0, read from the bundle) can only ever return inconclusive and send nothing:
//   extensions.osc9-progress  termless: none (the registration has no termless function); terminal: unmeasuredStateEffect("OSC 9;4 progress display"),
//                             an "insufficient-evidence" result with evidence "none" that writes and reads nothing.
//   extensions.notifications  termless: a declared-capability report {declared: ctx.capabilities.extensions.has("osc9")}, evidence "legacy";
//                             terminal: "No desktop notification delivery readback for OSC 9", evidence "none"; neither writes or reads a byte.
// The effect contracts below therefore use their own bytes. Their oracle is written from the OSC 9 protocol (ConEmu / Windows Terminal
// `OSC 9 ; 4 ; <state> [; <progress>]`: 0 remove, 1 normal, 2 error, 3 indeterminate, 4 paused, progress 0-100) and from the stated Ntilde presentation
// (tab-title suffix). Deliberate implementation choices (an absent percentage, an unknown state, a malformed payload, text flattening and capping,
// focus-gated toasts) are recorded as POLICY observations that never decide a verdict.
const osc = (body, terminator = '\x07') => `\x1b]9;${body}${terminator}`;
const other = (code, value, terminator = '\x07') => `\x1b]${code};${value}${terminator}`;

export const osc9ContractsVersion = 1;
export const osc9SuiteId = 'ntilde-owned-osc9-graded-v1';
export const osc9Prefix = 'osn';
export const osc9NoncePrefix = 'NTILDE_ON_';
export const osc9Minimum = Object.freeze({ rows: 12, cols: 48 });
export const osc9NonceRow = 9;
export const osc9ProgressCaseId = 'extensions.osc9-progress';
export const osc9NotificationCaseId = 'extensions.notifications';
export const osc9CaseIds = Object.freeze([osc9ProgressCaseId, osc9NotificationCaseId]);
export const osc9ProgressContractId = 'ntilde-osc9-progress-state-effect-v1';
export const osc9NotificationContractId = 'ntilde-osc9-notification-toast-effect-v1';
/** Adapter-side declaration the pinned notifications termless probe is served. It names what the observation API can report; it never decides a verdict. */
export const osc9DeclaredCapabilities = Object.freeze(['osc9']);
/** The host waits this long (longer than the App's toast auto-hide) before a toast is expected to be gone. */
export const osc9ToastHideWaitMs = 8000;
/** Documented policy limits (src/Ntilde.VT/AnsiParser.Osc.cs): a notification text is capped at this many characters including its ellipsis. */
export const osc9NotificationCap = 200;

/** What each pinned probe sends and can return, for both its paths; retained verbatim in the report. */
export const osc9Pinned = Object.freeze({
    [osc9ProgressCaseId]: Object.freeze({ termlessFunction: false, termlessFeeds: Object.freeze([]), terminalWrites: Object.freeze([]), fixtureBytes: false,
        reachableOutcomes: Object.freeze({ termless: 'none', terminal: 'inconclusive' }),
        terminalNote: 'OSC 9;4 progress display was not attempted; this probe has no effect readback or prior-state restoration' }),
    [osc9NotificationCaseId]: Object.freeze({ termlessFunction: true, termlessFeeds: Object.freeze([]), terminalWrites: Object.freeze([]), fixtureBytes: false,
        reachableOutcomes: Object.freeze({ termless: 'inconclusive', terminal: 'inconclusive' }),
        termlessNote: 'Declared OSC 9 capability does not establish desktop notification delivery', terminalNote: 'No desktop notification delivery readback for OSC 9' }),
});

/** The measured baseline every experiment starts from: progress withdrawn, lifecycle reset, links closed, title, cwd and the run nonce. */
export function osc9Fixture(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid OSC 9 run identity.');
    const title = `ON-base-${runId.slice(0, 8)}`, cwd = '/ntilde-qa/osc9-base', nonce = osc9NoncePrefix + runId;
    const reset = other(8, ';') + osc('4;0') + other(133, 'D;0') + other(133, 'A')
        + '\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[H\x1b[128M\x1b[2J\x1b[H' + other(0, title) + other(7, `file://localhost${cwd}`);
    const stage = reset + `\x1b[${osc9NonceRow + 1};1H` + nonce;
    return { title, cwd, nonce, reset, stage, bind: stage + other(4, '7;rgb:12/34/56'), finish: reset + other(104, '7') };
}

/** The tab-title suffix the App documents for a progress report (TabProgressPresentation.FormatMarkerSuffix): a glyph, a percentage, or a filler. */
export function progressSuffix(progress) {
    if (progress === null) return '';
    const glyph = { Error: ' ✖', Paused: ' ⏸' }[progress.kind] ?? '';
    if (progress.percent !== null) return `${glyph} ${progress.percent}%`;
    return glyph === '' ? ' ⋯' : glyph;
}
const state = (kind, percent) => Object.freeze({ kind, percent });
const setups = Object.freeze({ none: Object.freeze({ feed: '', progress: null }), normal17: Object.freeze({ feed: osc('4;1;17'), progress: state('Normal', 17) }) });

// role: protocol = graded against the OSC 9;4 protocol; control = unmarked/withdrawal control that gates validity; policy = recorded only.
const progressSpecs = [
    ['normal', 'protocol', 'none', osc('4;1;42'), state('Normal', 42)],
    ['error', 'protocol', 'none', osc('4;2;37'), state('Error', 37)],
    ['indeterminate', 'protocol', 'none', osc('4;3'), state('Indeterminate', null)],
    ['paused', 'protocol', 'none', osc('4;4;61'), state('Paused', 61)],
    ['replace-normal-with-error', 'protocol', 'normal17', osc('4;2;80'), state('Error', 80)],
    ['withdraw', 'protocol', 'normal17', osc('4;0'), null],
    ['clamp-high', 'protocol', 'none', osc('4;1;150'), state('Normal', 100)],
    ['percent-zero', 'protocol', 'none', osc('4;1;0'), state('Normal', 0)],
    ['indeterminate-ignores-percent', 'protocol', 'none', osc('4;3;55'), state('Indeterminate', null)],
    ['st-terminated', 'protocol', 'none', osc('4;1;29', '\x1b\\'), state('Normal', 29)],
    ['withdraw-when-none', 'control', 'none', osc('4;0'), null],
    ['unmarked', 'control', 'normal17', '\x1b[0m', state('Normal', 17)],
    ['policy-absent-percent-normal', 'policy', 'none', osc('4;1'), state('Normal', null)],
    ['policy-absent-percent-error', 'policy', 'normal17', osc('4;2'), state('Error', null)],
    ['policy-absent-percent-paused', 'policy', 'normal17', osc('4;4'), state('Paused', null)],
    ['policy-unknown-state', 'policy', 'normal17', osc('4;7;10'), state('Normal', 17)],
    ['policy-bare-4', 'policy', 'normal17', osc('4'), state('Normal', 17)],
    ['policy-missing-state', 'policy', 'normal17', osc('4;'), state('Normal', 17)],
];

/** Notification experiments. `focus` names the window condition under which the effect is graded. `toast` is the expected message or null for no toast. */
function notificationSpecs(runId) {
    const marker = runId.slice(0, 8), long = 'L'.repeat(250);
    return [
        ['focused-suppressed', 'policy', 'active', osc(`focused-${marker}`), null],
        ['delivered', 'protocol', 'inactive', osc(`NT-notify-${marker}`), `NT-notify-${marker}`],
        ['st-terminated', 'protocol', 'inactive', osc(`NT-notify-st-${marker}`, '\x1b\\'), `NT-notify-st-${marker}`],
        ['unmarked', 'control', 'inactive', '\x1b[0m', null],
        ['progress-is-not-text', 'protocol', 'inactive', osc('4;1;50'), null],
        ['policy-flatten-control', 'policy', 'inactive', osc(`one\ttwo-${marker}`), `one two-${marker}`],
        ['policy-cap', 'policy', 'inactive', osc(long), `${long.slice(0, osc9NotificationCap - 1)}…`],
        ['policy-whitespace-only', 'policy', 'inactive', osc('   '), null],
        ['policy-bare-4', 'policy', 'inactive', osc('4'), '4'],
    ];
}

/** Every observation follows a distinct, observed setup. */
export function osc9Experiments(runId) {
    const fixture = osc9Fixture(runId);
    const progress = progressSpecs.map(([name, role, setup, feed, expected]) => ({ caseId: `${osc9ProgressCaseId}/${name}`, owner: osc9ProgressCaseId, name, role,
        kind: 'progress', setup: setups[setup].progress, expected, plan: [{ feed: fixture.stage }, ...(setups[setup].feed ? [{ feed: setups[setup].feed }] : []),
            { read: 'before', kind: 'setup' }, { feed }, { read: 'after', kind: 'effect' },
            // A bare "4" is notification text by documented policy, so an inactive window may show its toast: wait it out before the next experiment.
            ...(name === 'policy-bare-4' ? [{ wait: osc9ToastHideWaitMs }] : [])] }));
    const specs = notificationSpecs(runId);
    const notifications = specs.map(([name, role, focus, feed, toast], index) => {
        const first = specs.findIndex(item => item[2] === focus) === index, last = index === specs.length - 1;
        const delivered = name === 'delivered';
        return { caseId: `${osc9NotificationCaseId}/${name}`, owner: osc9NotificationCaseId, name, role, kind: 'notification', focus,
            title: fixture.title, expected: toast,
            plan: [{ feed: fixture.stage }, ...(first ? [{ action: focus === 'inactive' ? 'defocus' : 'refocus' }] : []), { read: 'before', kind: 'setup' }, { feed },
                { read: 'after', kind: 'effect' }, ...(delivered ? [{ wait: osc9ToastHideWaitMs }, { read: 'hidden', kind: 'hidden' }] : toast !== null ? [{ wait: osc9ToastHideWaitMs }] : []),
                ...(last ? [{ action: 'refocus' }] : [])] };
    });
    return [...progress, ...notifications];
}

/** The pinned replay contexts' wait/action vocabulary, checked by the shared driver. */
export const osc9StepKinds = Object.freeze(['feed', 'read', 'wait', 'action']);
