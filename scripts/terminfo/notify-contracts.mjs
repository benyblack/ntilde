// Versioned contract for the notification family OSC 777 and OSC 99: extensions.osc777-notify and extensions.osc99-kitty-notify. This is not a publication or a measurement.
//
// The PINNED probes (terminfo.dev@4.0.0, read from the bundle):
//   extensions.osc777-notify     termless: none (the registration has no termless function). Terminal: ESC[1;1H ESC[2K, then OSC 777;notify;test;body BEL, then a cursor
//                                query, returning unmeasuredCellResult: pass false, an "insufficient-evidence" observation with evidence "query". It can never return
//                                supported or unsupported, whatever the terminal does.
//   extensions.osc99-kitty-notify  queryOnly: sends OSC 99;i=<uuid>:p=?;ST and is supported only when the reply is `OSC 99;i=<uuid>:p=?;<payload>` whose payload carries exactly
//                                one `p=` field listing `title`. Its verdict is decided by the unchanged stock collector in the same owned run and by the exact-query witness
//                                `q07.extensions.osc99-kitty-notify` (query policy v3), never by this layer.
// The effect contracts below use their own bytes. Their oracle is written from the two protocols (kitty's desktop-notification protocol, OSC 99: title is the default payload,
// d=0 holds the notification until done, chunks of one id concatenate, e=1 is base64, unknown keys and payload types are ignored, the query's capability keys; OSC 777 `notify`:
// title then body) and from the stated Ntilde presentation: the opt-in in-window toast, suppressed while the user is looking at the pane, shown as a title and a message, a
// lone title or body being the message under the pane's own tab title. Deliberate implementation choices (flattening, caps, empty payloads, unknown keys, a refused chunk)
// are recorded as POLICY observations that never decide a verdict.
const osc777 = (title, body, terminator = '\x07') => `\x1b]777;notify;${title}${body === undefined ? '' : `;${body}`}${terminator}`;
const osc777Other = (rest, terminator = '\x07') => `\x1b]777;${rest}${terminator}`;
const osc99 = (metadata, payload, terminator = '\x1b\\') => `\x1b]99;${metadata};${payload}${terminator}`;
const other = (code, value, terminator = '\x07') => `\x1b]${code};${value}${terminator}`;

export const notifyContractsVersion = 1;
export const notifySuiteId = 'ntilde-owned-notify-graded-v1';
export const notifyPrefix = 'ntf';
export const notifyNoncePrefix = 'NTILDE_NF_';
export const notifyMinimum = Object.freeze({ rows: 12, cols: 48 });
export const notifyNonceRow = 9;
export const osc777CaseId = 'extensions.osc777-notify';
export const osc99CaseId = 'extensions.osc99-kitty-notify';
export const notifyCaseIds = Object.freeze([osc777CaseId, osc99CaseId]);
export const osc777ContractId = 'ntilde-osc777-notification-toast-effect-v1';
export const osc99ContractId = 'ntilde-osc99-notification-toast-effect-v1';
/** The host waits this long (longer than the App's toast auto-hide) before a toast is expected to be gone. */
export const notifyToastHideWaitMs = 8000;
/** Documented policy limits (src/Ntilde.VT/AnsiParser.Notifications.cs): a title is capped at 80 characters and a body at 200, each including its ellipsis. */
export const notifyTitleCap = 80;
export const notifyBodyCap = 200;
/** The pinned OSC 777 terminal path writes exactly these two strings, then asks for the cursor position. */
export const osc777PinnedWrites = Object.freeze(['\x1b[1;1H\x1b[2K', '\x1b]777;notify;test;body\x07']);
/** The pinned query the stock OSC 99 probe sends, apart from its random identifier. */
export const osc99PinnedQueryPattern = /^\x1b\]99;i=[0-9a-f-]{36}:p=\?;\x1b\\$/;
/** The capability reply the App owes the OSC 99 query (query policy v3): o=always, p=title,body, and no action, close, urgency, expiry or sound key. */
export const osc99CapabilityPayload = 'o=always:p=title,body';

/** What each pinned probe sends and can return; retained verbatim in the report. */
export const notifyPinned = Object.freeze({
    [osc777CaseId]: Object.freeze({ termlessFunction: false, replay: 'terminal-constant', terminalWrites: osc777PinnedWrites,
        reachableOutcomes: Object.freeze({ termless: 'none', terminal: 'inconclusive' }),
        terminalNote: 'Cursor response does not measure OSC 777 notification delivery' }),
    [osc99CaseId]: Object.freeze({ termlessFunction: false, replay: 'stock-collector-and-exact-query-witness', terminalWrites: Object.freeze([]),
        reachableOutcomes: Object.freeze({ termless: 'none', terminal: 'supported-or-inconclusive' }),
        terminalNote: 'Decided by the unchanged stock probe against the App in the same owned run and by the exact-query witness; this layer measures the notification effect only' }),
});

/** The measured baseline every experiment starts from: progress withdrawn, lifecycle reset, links closed, title, cwd and the run nonce. */
export function notifyFixture(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid notification run identity.');
    const title = `NF-base-${runId.slice(0, 8)}`, cwd = '/ntilde-qa/notify-base', nonce = notifyNoncePrefix + runId;
    const reset = other(8, ';') + other(9, '4;0') + other(133, 'D;0') + other(133, 'A')
        + '\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[H\x1b[128M\x1b[2J\x1b[H' + other(0, title) + other(7, `file://localhost${cwd}`);
    const stage = reset + `\x1b[${notifyNonceRow + 1};1H` + nonce;
    return { title, cwd, nonce, reset, stage, bind: stage + other(4, '7;rgb:12/34/56'), finish: reset + other(104, '7') };
}

/** An expected toast: `pane` stands for the pane's own base tab title (the run title). Null means no toast. */
const toast = (title, message) => Object.freeze({ title, message });
const pane = 'pane';
const base64 = text => Buffer.from(text, 'utf8').toString('base64');

/** The experiment table: [case, name, role, focus, feeds, expected, extra]. `held` names a read taken after the first feed (a notification that must not show yet). */
function specTable(runId) {
    const m = runId.slice(0, 8), longTitle = 'T'.repeat(100), longBody = 'L'.repeat(250), tooLong = 'x'.repeat(4097);
    const capped = (text, cap) => `${text.slice(0, cap - 1)}…`;
    const s = osc777CaseId, k = osc99CaseId;
    return [
        [s, 'focused-suppressed', 'policy', 'active', [osc777(`focused-${m}`, `body-${m}`)], null],
        [k, 'focused-suppressed', 'policy', 'active', [osc99('', `focused-${m}`)], null],
        [s, 'delivered', 'protocol', 'inactive', [osc777(`NT-777-${m}`, `body-${m}`)], toast(`NT-777-${m}`, `body-${m}`)],
        [s, 'st-terminated', 'protocol', 'inactive', [osc777(`NT-777-st-${m}`, `body-st-${m}`, '\x1b\\')], toast(`NT-777-st-${m}`, `body-st-${m}`)],
        [s, 'pinned-fixture', 'protocol', 'inactive', [osc777PinnedWrites.join('')], toast('test', 'body'), { setup: '\x1b[1;1H' }],
        [s, 'body-keeps-semicolons', 'protocol', 'inactive', [osc777(`T-${m}`, 'a;b;c')], toast(`T-${m}`, 'a;b;c')],
        [s, 'title-only', 'protocol', 'inactive', [osc777(`NT-777-only-${m}`)], toast(pane, `NT-777-only-${m}`)],
        [s, 'unmarked', 'control', 'inactive', ['\x1b[0m'], null],
        [s, 'other-subcommands', 'control', 'inactive', [osc777Other('precmd') + osc777Other('preexec;ls')], null],
        [s, 'policy-flatten-control', 'policy', 'inactive', [osc777(`one\ttwo-${m}`, `x\ty-${m}`)], toast(`one two-${m}`, `x y-${m}`)],
        [s, 'policy-caps', 'policy', 'inactive', [osc777(longTitle, longBody)], toast(capped(longTitle, notifyTitleCap), capped(longBody, notifyBodyCap))],
        [s, 'policy-empty', 'policy', 'inactive', [osc777('', '') + osc777('\t', '\t')], null],
        [k, 'delivered-title', 'protocol', 'inactive', [osc99('', `NT-99-${m}`)], toast(pane, `NT-99-${m}`)],
        [k, 'title-and-body', 'protocol', 'inactive', [osc99(`i=a${m}:d=0`, `NT-99-t-${m}`), osc99(`i=a${m}:p=body`, `body-${m}`)], toast(`NT-99-t-${m}`, `body-${m}`), { held: true }],
        [k, 'chunks-concatenate', 'protocol', 'inactive', [osc99(`i=c${m}:d=0`, 'NT-99-'), osc99(`i=c${m}`, `cat-${m}`)], toast(pane, `NT-99-cat-${m}`)],
        [k, 'base64', 'protocol', 'inactive', [osc99('e=1', base64(`NT-99-b64-${m}`))], toast(pane, `NT-99-b64-${m}`)],
        [k, 'body-only', 'protocol', 'inactive', [osc99('p=body', `NT-99-body-${m}`)], toast(pane, `NT-99-body-${m}`)],
        [k, 'bel-terminated', 'protocol', 'inactive', [osc99('', `NT-99-bel-${m}`, '\x07')], toast(pane, `NT-99-bel-${m}`)],
        [k, 'unmarked', 'control', 'inactive', ['\x1b[0m'], null],
        [k, 'unsupported-payload-types', 'control', 'inactive', [osc99('i=x:p=close', '') + osc99('i=x:p=alive', '') + osc99('p=icon', 'AAAA') + osc99('p=buttons', 'one')], null],
        [k, 'policy-unknown-keys', 'policy', 'inactive', [osc99('u=2:a=report:z=9', `NT-99-keys-${m}`)], toast(pane, `NT-99-keys-${m}`)],
        [k, 'policy-bad-base64', 'policy', 'inactive', [osc99('e=1', '***not-base64***')], null],
        [k, 'policy-oversize-chunk', 'policy', 'inactive', [osc99('', tooLong)], null],
        [k, 'policy-flatten-control', 'policy', 'inactive', [osc99('', `one\ttwo-${m}`)], toast(pane, `one two-${m}`)],
        [k, 'policy-title-cap', 'policy', 'inactive', [osc99('', longTitle)], toast(pane, capped(longTitle, notifyTitleCap))],
    ];
}

/** What follows a notification's effect read: the delivered toast is waited out and read hidden (auto-hide, restoration); any other toast is waited out. */
function toastTail(delivered, waitsForHide) {
    if (delivered) return [{ wait: notifyToastHideWaitMs }, { read: 'hidden', kind: 'hidden' }];
    return waitsForHide ? [{ wait: notifyToastHideWaitMs }] : [];
}

/** Every observation follows a distinct, observed setup. */
export function notifyExperiments(runId) {
    const fixture = notifyFixture(runId), specs = specTable(runId);
    return specs.map(([owner, name, role, focus, feeds, toastExpected, extra = {}], index) => {
        const first = specs.findIndex(item => item[3] === focus) === index, last = index === specs.length - 1;
        const delivered = name === 'delivered' || name === 'delivered-title';
        const expected = toastExpected === null ? null : { title: toastExpected.title === pane ? fixture.title : toastExpected.title, message: toastExpected.message };
        const tail = toastTail(delivered, expected !== null || focus === 'active');
        const body = extra.held
            ? [{ feed: feeds[0] }, { read: 'held', kind: 'held' }, { feed: feeds[1] }]
            : feeds.map(feed => ({ feed }));
        return { caseId: `${owner}/${name}`, owner, name, role, kind: 'notification', focus, title: fixture.title, expected, held: extra.held === true,
            plan: [{ feed: fixture.stage }, ...(first ? [{ action: focus === 'inactive' ? 'defocus' : 'refocus' }] : []),
                ...(extra.setup ? [{ feed: extra.setup }] : []), { read: 'before', kind: 'setup' }, ...body, { read: 'after', kind: 'effect' }, ...tail,
                ...(last ? [{ action: 'refocus' }] : [])] };
    });
}

/** The pinned replay contexts' wait/action vocabulary, checked by the shared driver. */
export const notifyStepKinds = Object.freeze(['feed', 'read', 'wait', 'action']);
