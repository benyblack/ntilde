// Finite contracts for #771. These are plans, not runtime measurements or grades.
const esc = '\x1b', st = esc + '\\';
const silent = [
    ['device.xtgetxres', `${esc}P+Q7465726d4e616d65${st}`],
    ['device.xtreportcolors', `${esc}[#R`],
    ['device.xtwinops-20', `${esc}[20t`],
    ['device.xtwinops-21', `${esc}[21t`],
    ['editing.decrqcra', `${esc}[1;1;1;1;1;1*y`],
    ['extensions.osc1337-capabilities', `${esc}]1337;RequestCapabilities\x07`],
    ['extensions.osc1337-cellsize', `${esc}]1337;ReportCellSize\x07`],
    ['extensions.osc21-kitty-color', `${esc}]21;foreground=?${st}`],
    ['extensions.osc5-special-color', `${esc}]5;0;?\x07`],
    ['extensions.osc701-locale', `${esc}]701;?\x07`],
    ['extensions.osc702-version', `${esc}]702\x07`],
    ['extensions.osc776-cell-size', `${esc}]776\x07`],
    ['extensions.osc7770-font-size', `${esc}]7770;?\x07`],
    ['extensions.osc7777-font-window-size', `${esc}]7777;?\x07`],
    ['extensions.osc99-kitty-notify', `${esc}]99;i=ntilde-query-audit:p=?;${st}`],
    ['extensions.sixel-geometry-report', `${esc}[?2;1;0S`],
];
const decrqss = [['sgr', 'm'], ['horizontal-margins', 's'], ['cursor-style', ' q'],
    ['conformance', '"p'], ['protection', '"q'], ['lines-per-page', 't'],
    ['columns-per-page', '$|'], ['number-lines', '*|'], ['change-extent', '*x'],
    ['unknown', 'unknown'], ['empty', '']];
const tcap = [['terminal-name', '544e'], ['color-count', '436f'], ['colors', '636f6c6f7273'],
    ['rgb', '524742'], ['cursor-up-key', '6b63757531'], ['list', '544e;436f'],
    ['unknown', '6e6f742d612d6361706162696c697479'], ['odd-hex', '5'],
    ['invalid-hex', 'zz'], ['empty', '']];

/** The reply the kitty OSC 99 capability query owes under query policy v3: o=always (no other occasion honored), p=title,body, and no a/c/u/w/s key
 * because actions, close reports, urgency, expiry and sounds are not implemented. The id is the witness request's own, already within the id alphabet. */
const noticeReply = `${esc}]99;i=ntilde-query-audit:p=?;o=always:p=title,body${st}`;

/** The replies query policy v4 owes: XTGETXRES termName answers the TERM the App gives a direct pane's child (xterm-256color, uppercase hex), and XTREPORTCOLORS the
 * xterm palette stack with the ? marker. This suite begins with RIS, which clears the stack, so the depth and the deepest depth are both 0 when it asks. CSI 20 t and
 * CSI 21 t keep the silence expectation: the title and icon are the device-query suite's run baseline label, which this witness model cannot name, so a run with the title
 * reports on records a mismatch whose retained actual reply is that label. */
const xtgetxresReply = `${esc}P1+R7465726d4e616d65=787465726D2D323536636F6C6F72${st}`;
const xtreportcolorsReply = `${esc}[?0;0#Q`;

/** The shape query policy v6 owes the rxvt special-color request: one complete reply `OSC 5 ; 0 ; rgb:rrrr/gggg/bbbb ST` with one to four lowercase hex digits per channel. The value is the
 * host theme's own foreground (this suite begins with RIS, which clears every override), which the witness model cannot name, so the witness decides the exact framing and shape; the special-color layer's
 * effect contract measures exact values against colors it sets itself. */
const osc5SpecialColorShape = new RegExp(String.raw`^<ESC>\]5;0;rgb:[0-9a-f]{1,4}/[0-9a-f]{1,4}/[0-9a-f]{1,4}(?:<BEL>|<ESC>\\)$`.replaceAll('<ESC>', String.fromCodePoint(27)).replaceAll('<BEL>', String.fromCodePoint(7)));

/** The reply query policy v7 owes the iTerm2 feature-reporting request: the exact declaration a direct pane makes — every implemented code in the specification's table order, including `Cw`
 * because the pane's host accepts OSC 52 writes by default — always ST-terminated. The literal is transcribed from the specification's code table (iterm-feature-reporting.md), never read
 * from the parser's constants. */
const capabilitiesReply = `${esc}]1337;Capabilities=T3CwLrMSc7UTs3BFGsGoSyHNoP${st}`;

/** The shape query policy v5 owes the kitty OSC 21 foreground request: one complete reply `OSC 21 ; foreground=rgb:rr/gg/bb ST` with 8-bit lowercase channels. The value is the
 * host theme's own foreground (this suite begins with RIS, which clears every override), which the witness model cannot name, so the witness decides the exact framing and shape;
 * the kitty color-control layer's effect contract measures exact values against colors it sets itself. */
const kittyForegroundShape = new RegExp(String.raw`^<ESC>\]21;foreground=rgb:[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{2}<ESC>\\$`.replaceAll('<ESC>', String.fromCodePoint(27)));

/** The reply DECRQCRA owes the 1x1 witness request while the run turns AllowRectangleChecksumReport on: the bound first line
 * always starts with the N of NTILDE_QRY_ (0x4E, plain), so the negated 16-bit checksum is FFB2. */
const checksumReply = `${esc}P1!~FFB2${st}`;

/** The optional context flags, each exactly `true` or absent: `checksumReport` (a run whose initial settings turn the off-by-default
 * DECRQCRA report on) and `marginsReport` (a run of the query policy that expects the DECRQSS s reply, which is unconditional), `noticeReport` (query policy v3, which also expects the OSC 99 capability reply),
 * `deviceReport` (query policy v4) and `colorReport` (query policy v5) and `specialColorReport` (query policy v6, which also expects the rxvt special-color reply) and `capabilitiesReport` (query policy v7, which also
 * expects the iTerm2 feature-reporting reply). */
const contextFlags = ['checksumReport', 'marginsReport', 'noticeReport', 'deviceReport', 'colorReport', 'specialColorReport', 'capabilitiesReport'];
/** Observed geometry and absolute one-based margins plus the optional flags above. Without them (every run before they existed) the
 * context is exactly the four numbers. */
export function queryWitnessContext(value) {
    const keys = ['rows', 'cols', 'top', 'bottom'];
    const flags = value ? contextFlags.filter(flag => Object.hasOwn(value, flag)) : [];
    if (!value || Object.keys(value).length !== keys.length + flags.length || Object.keys(value).some(k => !keys.includes(k) && !contextFlags.includes(k))
        || !keys.every(k => Object.hasOwn(value, k)) || flags.some(flag => value[flag] !== true)) {
        throw new Error('Expected bounded observed geometry and absolute one-based margins.');
    }
    const snapshot = Object.freeze(Object.fromEntries([...keys, ...flags].map(k => [k, value[k]])));
    if (!keys.every(k => Number.isSafeInteger(snapshot[k]))
        || snapshot.rows < 1 || snapshot.rows > 4096 || snapshot.cols < 1 || snapshot.cols > 4096
        || snapshot.top < 1 || snapshot.bottom < snapshot.top || snapshot.bottom > snapshot.rows) {
        throw new Error('Expected bounded observed geometry and absolute one-based margins.');
    }
    return snapshot;
}

export function queryWitnessPlan(context) {
    const c = queryWitnessContext(context);
    const entry = (id, featureId, request, expectedEvidence, expectedReply, expectedPattern = undefined) => Object.freeze({
        id, featureId, request, expectedEvidence, expectedReply, ...(expectedPattern === undefined ? {} : { expectedPattern }),
    });
    return Object.freeze([
        entry('q07.device.tertiary-da', 'device.tertiary-da', `${esc}[=c`, 'positive-reply', `${esc}P!|00000000${st}`),
        entry('q07.device.xtwinops-18', 'device.xtwinops-18', `${esc}[18t`, 'positive-reply', `${esc}[8;${c.rows};${c.cols}t`),
        ...silent.map(([id, request]) => {
            if (id === 'editing.decrqcra' && c.checksumReport) return entry(`q07.${id}`, id, request, 'positive-reply', checksumReply);
            if (id === 'extensions.osc99-kitty-notify' && c.noticeReport) return entry(`q07.${id}`, id, request, 'positive-reply', noticeReply);
            if (id === 'extensions.osc21-kitty-color' && c.colorReport) return entry(`q07.${id}`, id, request, 'positive-reply', '', kittyForegroundShape);
            if (id === 'extensions.osc5-special-color' && c.specialColorReport) return entry(`q07.${id}`, id, request, 'positive-reply', '', osc5SpecialColorShape);
            if (id === 'extensions.osc1337-capabilities' && c.capabilitiesReport) return entry(`q07.${id}`, id, request, 'positive-reply', capabilitiesReply);
            if (id === 'device.xtgetxres' && c.deviceReport) return entry(`q07.${id}`, id, request, 'positive-reply', xtgetxresReply);
            if (id === 'device.xtreportcolors' && c.deviceReport) return entry(`q07.${id}`, id, request, 'positive-reply', xtreportcolorsReply);
            return entry(`q07.${id}`, id, request, 'silence', '');
        }),
        entry('q12.decrqss.vertical-margins', 'device.decrqss', `${esc}P$qr${st}`, 'positive-reply', `${esc}P1$r${c.top};${c.bottom}r${st}`),
        // DECRQSS s is unconditional. The query suite runs on a reset full-width screen, so the left/right margins are 1 and cols.
        ...decrqss.map(([id, setting]) => (id === 'horizontal-margins' && c.marginsReport
            ? entry(`q12.decrqss.${id}`, 'device.decrqss', `${esc}P$q${setting}${st}`, 'positive-reply', `${esc}P1$r1;${c.cols}s${st}`)
            : entry(`q12.decrqss.${id}`, 'device.decrqss', `${esc}P$q${setting}${st}`, 'negative-reply', `${esc}P0$r${st}`))),
        ...tcap.map(([id, name]) => entry(`q12.xtgettcap.${id}`, 'device.xtgettcap', `${esc}P+q${name}${st}`, 'negative-reply', `${esc}P0+r${st}`)),
    ]);
}
