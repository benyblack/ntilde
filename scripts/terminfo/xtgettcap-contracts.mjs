// Versioned probe contract for the XTGETTCAP qualification batch: device.xtgettcap (DCS + q hex-name list; capability reports).
//
// Pinned terminfo.dev@4.0.0 definition (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6,
// probeHash 16a690f34c0e), read from the bundle, never inferred from the name:
//   deviceQuery({ id: "device.xtgettcap", query: DCS + q 544e ST, valid: /DCS 1 + r 544e = <even hex> ST/i,
//                refusal: /DCS 0 + r ST/, malformed: /DCS [01] + r/,
//                expected: "complete XTGETTCAP status 1 for TN with even-length hex value and ST" })
// The pinned termless path feeds the exact query and grades the captured reply through deviceReplyResult: a 1+r544e=<even hex>
// value reply is supported; the bare 0+r refusal is inconclusive (insufficient-evidence, "Requested setting or name refused;
// other settings or names unmeasured"); silence or a malformed frame stays inconclusive. The pinned terminal path writes the same
// query with a sentinel DA and can only ever reach the same refusal branch on this terminal.
//
// Ntilde deliberately refuses TN: its honest value is the TERM the session uses, which VT does not know (#807/#861). The pinned
// verdict therefore stays inconclusive and is never upgraded by this suite, and no TN value is invented or inferred from another
// capability, the application name or any child-TERM evidence.
//
// The effect contract (ntilde-xtgettcap-capability-report-effect-v1) is separately identified and grades XTGETTCAP capability-report
// replies only, from an independent reply oracle written from the documented #844 contract (it shares no code with the parser):
// one reply per name in request order — DCS 1 + r name=value ST for numbers (value uppercase hex of the decimal text), DCS 1 + r name ST
// for flags, and xterm's bare DCS 0 + r ST for a name this terminal does not advertise — with the name echoed exactly as the request
// spelled it; a request that is not entirely valid (empty, odd-length or non-hex name, mixed valid/invalid, a name decoding outside
// printable ASCII, an over-long name or more than 16 names) gets exactly one DCS 0 + r ST and nothing else. Controls: the exact pinned
// TN request; the numeric Co and colors replies; the RGB and Tc flag replies; one string reply (Smulx) so string/number replies are
// distinguished from flag replies; two multi-name lists with exact reply order and per-name outcomes (including the Q12 TN;Co witness);
// the documented unavailable names (TN, kcuu1, the case-sensitive co) and a controlled unknown; the uppercase-echo boundary; four
// malformed requests; and a verified recovery after each one (the pinned request again after the empty list). Every exchange must
// leave the complete screen, cursor, rendition, margins, modes and active screen unchanged.
//
// Ms is deliberately not advertised by the oracle and never requested: it is settings- and session-dependent (the owned fixture
// disables clipboard writes) and is not this contract's claim. An advertised RGB flag or color count proves the reply only — never
// rendering, color accuracy, or execution of the advertised control strings (the owning VT tests cover those). No X resource
// (xtgetxres is a different case), clipboard permission, remote-delivery or other-platform claim.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset } from './sgr-attrs-contracts.mjs';
import { selectiveEraseInk } from './selective-erase-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const xtgettcapContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const xtgettcapSuiteId = 'ntilde-owned-xtgettcap-graded-v1';
/** The separately identified effect contract. */
export const xtgettcapEffectContractId = 'ntilde-xtgettcap-capability-report-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const xtgettcapNoncePrefix = 'NTILDE_XG_';
/** Owner-protocol prefix of this suite's observation requests. */
export const xtgettcapPrefix = 'xgtc';
/** The single pinned case. */
export const xtgettcapCaseIds = Object.freeze(['device.xtgettcap']);

/** XTGETTCAP screens carry stored attributes, per-cell details (with overline), resolved colors and the App's stored buffer state. */
export { validateSelectiveEraseScreen as validateXtgettcapScreen } from './selective-erase-contracts.mjs';

/** The measured geometry the controls need (the seed uses margins 2..6, a nonce row and a coloured/bold/underlined seed row). */
const minimum = Object.freeze({ rows: 8, cols: 48 });
/** The margins the seed sets (one-based 2..6; zero-based 1..5). */
const seedMargins = Object.freeze([1, 5]);

// ---------- the pinned fixture and its criterion ----------

const st = '\x1b\\';
/** The pinned request bytes: DCS + q 544e ST (the hex spelling of the name "TN"). */
export const pinned = Object.freeze({
    query: '\x1bP+q544e' + st,
    expected: 'complete XTGETTCAP status 1 for TN with even-length hex value and ST',
    refusalNote: 'Requested setting or name refused; other settings or names unmeasured',
});

/** The pinned valid pattern: DCS 1 + r 544e = one-or-more hex pairs then ST, case-insensitive (the pin's /i flag). Written without a
 *  regex literal so no control character sits in a pattern literal; the semantics are the pinned spec's. */
function pinnedValidAt(raw) {
    const folded = raw.toLowerCase(), head = '\x1bp1+r544e=';
    for (let at = folded.indexOf(head); at >= 0; at = folded.indexOf(head, at + 1)) {
        let end = at + head.length, digits = 0;
        while (end < raw.length && /[0-9A-Fa-f]/.test(raw[end])) { end++; digits++; }
        if (digits > 0 && digits % 2 === 0 && raw.startsWith(st, end)) return { at, end: end + 2 };
    }
    return null;
}

/** The refusal frame, byte for byte. */
const refusalFrame = '\x1bP0+r' + st;

/** The first frame the pinned (valid|refusal) pattern would match, or null: the earliest of the two, valid winning ties. */
function pinnedAnswerFrame(raw) {
    const valid = pinnedValidAt(raw), refusalAt = raw.indexOf(refusalFrame);
    if (valid === null && refusalAt < 0) return null;
    if (refusalAt < 0 || (valid !== null && valid.at <= refusalAt)) return { at: valid.at, frame: raw.slice(valid.at, valid.end) };
    return { at: refusalAt, frame: refusalFrame };
}

/** The pinned malformed pattern: any DCS [01] + r framing that is neither valid nor the refusal. */
function pinnedMalformedPresent(raw) {
    for (let at = raw.indexOf('\x1bP'); at >= 0; at = raw.indexOf('\x1bP', at + 1)) {
        if ((raw[at + 2] === '0' || raw[at + 2] === '1') && raw[at + 3] === '+' && raw[at + 4] === 'r') return true;
    }
    return false;
}

/** The pinned deviceReplyResult for this spec, key order included. */
export function replayXtgettcapPinned(raw = '') {
    const response = raw;
    // The earliest (valid|refusal) frame, valid winning ties; a frame that only follows a primary-DA reply is not a reply to this request.
    const answer = pinnedAnswerFrame(response);
    const da1At = response.search(/\x1b\[\?[0-9;]*c/);
    const usable = answer !== null && (da1At === -1 || answer.at < da1At) ? answer : null;
    if (usable !== null && usable.frame !== refusalFrame) {
        return { pass: true, response, observation: { outcome: 'supported', evidence: 'query' },
            assertions: [{ kind: 'positive', expected: pinned.expected, observed: usable.frame }] };
    }
    if (usable !== null) {
        return { pass: false, response,
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query', note: pinned.refusalNote } };
    }
    // The pinned reason rule: a complete unmatched frame anywhere keeps this a plain no-response; only a truly absent frame
    // with leftover DCS [01] + r framing is invalid-reply.
    const anyCompleteFrame = pinnedAnswerFrame(response) !== null;
    const reason = !anyCompleteFrame && pinnedMalformedPresent(response) ? 'invalid-reply' : 'no-response';
    return { pass: false, response, observation: { outcome: 'inconclusive', reason, evidence: 'query' } };
}

// ---------- the independent reply oracle ----------

/** Uppercase hex of every character, the way the documented reply encodes values. */
const hexOf = text => [...text].map(char => char.codePointAt(0).toString(16).toUpperCase().padStart(2, '0')).join('');

/** The advertised capabilities the oracle models, written from the documented #844 table. `Ms` is deliberately absent: it is
 *  settings- and session-dependent and outside this contract. */
const advertised = new Map(Object.entries({
    Co: { kind: 'number', value: '256' },
    colors: { kind: 'number', value: '256' },
    RGB: { kind: 'flag' },
    Tc: { kind: 'flag' },
    Su: { kind: 'flag' },
    hs: { kind: 'flag' },
    setrgbf: { kind: 'string', value: '\x1b[38;2;%p1%d;%p2%d;%p3%dm' },
    setrgbb: { kind: 'string', value: '\x1b[48;2;%p1%d;%p2%d;%p3%dm' },
    Smulx: { kind: 'string', value: '\x1b[4:%p1%dm' },
    Setulc: { kind: 'string', value: '\x1b[58:2::%p1%{65536}%/%d:%p1%{256}%/%{255}%&%d:%p1%{255}%&%dm' },
    Sync: { kind: 'string', value: '\x1b[?2026%?%p1%{1}%-%tl%eh%;' },
    Ss: { kind: 'string', value: '\x1b[%p1%d q' },
    Se: { kind: 'string', value: '\x1b[0 q' },
    Enmg: { kind: 'string', value: '\x1b[?69h' },
    Dsmg: { kind: 'string', value: '\x1b[?69l' },
    Clmg: { kind: 'string', value: '\x1b[s' },
    Cmg: { kind: 'string', value: '\x1b[%i%p1%d;%p2%ds' },
    tsl: { kind: 'string', value: '\x1b]2;' },
    fsl: { kind: 'string', value: '\x07' },
}));

/** The most names one request may carry, the longest decoded name and its encoded length (the documented bounds). */
export const xtgettcapMaxNames = 16;
export const xtgettcapMaxNameChars = 32;

/** Decode one hex-encoded name, or null when it is not a bounded printable-ASCII name. */
function decodeName(encoded) {
    if (encoded.length === 0 || encoded.length % 2 !== 0 || encoded.length > xtgettcapMaxNameChars * 2) return null;
    if (!/^[0-9A-Fa-f]+$/.test(encoded)) return null;
    let name = '';
    for (let index = 0; index < encoded.length; index += 2) {
        const char = Number.parseInt(encoded.slice(index, index + 2), 16);
        if (char < 0x21 || char > 0x7e) return null;
        name += String.fromCodePoint(char);
    }
    return name;
}

/** The refusal reply byte for byte. */
export const xtgettcapRefusal = '\x1bP0+r' + st;

/** The exact replies the oracle demands for one request's name list: one per name, or exactly one refusal when the list is not
 *  entirely valid. Only a non-string list is refused outright (it is not a request this contract can reason about). */
export function oracleReplies(nameList) {
    if (typeof nameList !== 'string') return null;
    const encodedNames = nameList === '' ? [] : nameList.split(';');
    if (encodedNames.length === 0 || encodedNames.length > xtgettcapMaxNames) return [xtgettcapRefusal];
    const decoded = encodedNames.map(decodeName);
    if (decoded.includes(null)) return [xtgettcapRefusal];
    return decoded.map((name, index) => {
        const entry = advertised.get(name);
        if (entry === undefined) return xtgettcapRefusal;
        const echo = encodedNames[index];
        return entry.kind === 'flag' ? `\x1bP1+r${echo}${st}` : `\x1bP1+r${echo}=${hexOf(entry.value)}${st}`;
    });
}

/** The request bytes for one name list. */
export const xtgettcapRequest = nameList => '\x1bP+q' + nameList + st;

// ---------- the measured seed and plan ----------

/** The measured reset the bind begins with: protection, SGR and origin off, margins cleared, screen cleared, cursor home. */
export const xtgettcapReset = '\x1b[0"q' + sgrAttrsReset + '\x1b[?6l\x1b[r\x1b[2J\x1b[H';
/** The attributed seed the bind writes: bold and indexed-red text on row 2, a curly-underlined cell on row 4, margins 2..6, then the
 *  nonce at home. Queries must leave every one of these facts untouched. */
export function xtgettcapSeed(nonce) {
    return xtgettcapReseed + '\x1b[1;1H' + nonce;
}
/** The plan's static first feed re-establishes the attributed seed and homes the cursor without clearing the nonce row. */
export const xtgettcapReseed = '\x1b[2;1H\x1b[1mNtilde\x1b[31mXT\x1b[0m\x1b[4;1H\x1b[4:3mU\x1b[0m\x1b[2;6r\x1b[1;1H';
/** The seed cells (row, col, char) the initial read must find. */
const seedCells = Object.freeze([
    Object.freeze({ row: 1, col: 0, char: 'N', bold: true }),
    Object.freeze({ row: 1, col: 1, char: 't', bold: true }),
    Object.freeze({ row: 1, col: 2, char: 'i', bold: true }),
    Object.freeze({ row: 1, col: 3, char: 'l', bold: true }),
    Object.freeze({ row: 1, col: 4, char: 'd', bold: true }),
    Object.freeze({ row: 1, col: 5, char: 'e', bold: true }),
    Object.freeze({ row: 1, col: 6, char: 'X', bold: true, fgIndex: 1 }),
    Object.freeze({ row: 1, col: 7, char: 'T', bold: true, fgIndex: 1 }),
    Object.freeze({ row: 3, col: 0, char: 'U', underline: 'curly' }),
]);

/** One report role: the request's name list, the bytes it is sent as and a one-line role description. */
const ask = (role, nameList, demand) => Object.freeze({ role, nameList, bytes: xtgettcapRequest(nameList), demand });

/** Every read group, in plan order. */
const groups = Object.freeze([
    { role: 'initial' },
    { role: 'pinned', request: ask('pinned', '544e', 'the exact pinned request is refused: DCS 0 + r ST') },
    { role: 'numeric-co', request: ask('numeric-co', '436f', 'Co answers 256 as uppercase hex') },
    { role: 'numeric-colors', request: ask('numeric-colors', '636f6c6f7273', 'colors answers 256 as uppercase hex') },
    { role: 'flag-rgb', request: ask('flag-rgb', '524742', 'RGB is a flag reply with no value') },
    { role: 'flag-tc', request: ask('flag-tc', '5463', 'Tc is a flag reply with no value') },
    { role: 'string-smulx', request: ask('string-smulx', '536d756c78', 'Smulx answers its control string as uppercase hex') },
    { role: 'list-order', request: ask('list-order', '436f;544e;5463;436f', 'four replies in request order, one per name') },
    { role: 'list-witness', request: ask('list-witness', '544e;436f', 'the Q12 witness list: refusal then value') },
    { role: 'unavailable-kcuu1', request: ask('unavailable-kcuu1', '6b63757531', 'kcuu1 is inherited from xterm-256color and refused') },
    { role: 'unknown', request: ask('unknown', '6e6f742d612d6361706162696c697479', 'a controlled unknown capability is refused') },
    { role: 'case-co', request: ask('case-co', '636f', 'names are case-sensitive: co is refused') },
    { role: 'echo-upper', request: ask('echo-upper', '436F', 'the name is echoed as spelled, uppercase included') },
    { role: 'malformed-odd', request: ask('malformed-odd', '5', 'an odd-length name is refused once') },
    { role: 'recovery-tc', request: ask('recovery-tc', '5463', 'the parser answers normally after odd-length input') },
    { role: 'malformed-hex', request: ask('malformed-hex', 'zz', 'a non-hex name is refused once') },
    { role: 'recovery-co', request: ask('recovery-co', '436f', 'the parser answers normally after non-hex input') },
    { role: 'malformed-empty', request: ask('malformed-empty', '', 'an empty name list is refused once') },
    { role: 'recovery-pinned', request: ask('recovery-pinned', '544e', 'the exact pinned request is still refused after the empty list') },
    { role: 'malformed-mixed', request: ask('malformed-mixed', '436f;5', 'a mixed valid/invalid list is refused as a whole') },
    { role: 'recovery-string', request: ask('recovery-string', '536d756c78', 'the parser answers normally after a mixed list') },
].map(Object.freeze));

/** The single contracted case: the pinned request plus its controls, in a fixed plan. */
function makeContract() {
    const plan = [planFeed(xtgettcapReseed)];
    for (const item of groups) {
        if (item.request) plan.push({ feed: item.request.bytes, capture: true }, planRead('report'));
        else plan.push(planRead('cells'));
    }
    const roles = Object.freeze(groups.map(item => item.role));
    return Object.freeze({
        caseId: 'device.xtgettcap', probeId: 'device.xtgettcap', id: 'XTGETTCAP', roles, plan: Object.freeze(plan),
        requests: Object.freeze(groups.filter(item => item.request).map(item => item.request)),
        pinnedPath: 'termless', pinnedRead: roles.indexOf('pinned'), pinnedExpected: pinned.expected,
        pinnedCriterion: 'XTGETTCAP (DCS + q 544e ST) answers a framed DCS 1 + r 544e = <even-length hex> ST for TN; the deliberate '
            + 'DCS 0 + r ST refusal is inconclusive (insufficient-evidence) and is preserved, never upgraded',
        pinnedFeeds: Object.freeze([pinned.query]),
        terminalFixture: { writes: [pinned.query], demand: 'a framed DCS 1 + r 544e = <even-length hex> ST reply within 2000 ms of the sentinel write',
            minimum: { rows: 1, cols: 1 }, note: 'the stock collector\'s queryWithSentinelOutcome path; replayed beside the termless verdict and equally inconclusive on the refusal' },
        effectContractId: xtgettcapEffectContractId,
        appearanceScope: 'exact capability-report replies only: the pinned TN request, the numeric Co/colors replies, the RGB/Tc flag '
            + 'replies, one string reply, multi-name reply order with per-name outcomes, the documented unavailable and unknown names, '
            + 'case-sensitivity, the uppercase echo, malformed lists (odd-length, non-hex, empty, mixed) with exactly one refusal, and '
            + 'verified recovery after each malformed request, all leaving the complete screen, cursor, rendition, margins, modes and '
            + 'active screen unchanged; an advertised flag or count proves the reply only — no rendering, color accuracy, control-string '
            + 'execution, Ms clipboard, X resource, remote-delivery or other-platform claim',
        evidence: 'owned-raw-replies-and-unchanged-state',
        grade: observation => gradeXtgettcap(observation),
        observed: observation => briefXtgettcap(observation),
    });
}

const contract = makeContract();
/** The contracted cases, in suite order. */
export const xtgettcapContracts = Object.freeze([contract]);
/** Contract case ids in suite order. */
export const xtgettcapContractCaseIds = Object.freeze(xtgettcapContracts.map(item => item.caseId));
export { planFeeds as xtgettcapFeeds, planReads as xtgettcapReads } from './graded-plan.mjs';

/** The seed bytes of the plan (the reset followed by the attributed seed and nonce). */
export function xtgettcapPlanSeed(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid XTGETTCAP fixture run identity.');
    return xtgettcapReset + xtgettcapSeed(xtgettcapNoncePrefix + runId);
}

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateXtgettcapFeeds(item, feeds) {
    validateExactFeeds(item, feeds, 'contract');
}

// ---------- grading ----------

/** A group's observation: its ink, cursor, geometry, stored buffer state and, for a report group, the raw replies or an empty list. */
const groupOf = (screen, replies) => ({ screen, ink: selectiveEraseInk(screen), cursor: [screen.cursorRow, screen.cursorCol],
    rows: screen.rows, cols: screen.cols, margins: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom],
    origin: screen.bufferState.originMode, alternate: screen.bufferState.alternateScreen, replies });
/** Everything an exchange must leave unchanged: ink (cells, stored attributes, protection), cursor, margins, origin and screen. */
const stateKey = state => JSON.stringify([state.ink, state.cursor, state.margins, state.origin, state.alternate]);
/** The roles of report groups. */
const reportRoles = contract.roles.filter((role, index) => groups[index].request !== undefined);

/** Whether the initial read holds the contracted seed: geometry, nonce at home, the attributed cells, margins and modes. The
 *  cursor sits at home: the plan's static reseed ends there and no exchange may move it. */
function seedOk(state, nonce) {
    const boldCells = state.ink.filter(cell => cell.row === 1), underlineCells = state.ink.filter(cell => cell.row === 3);
    return state.rows >= minimum.rows && state.cols >= minimum.cols
        && state.screen.lines[0] === nonce && state.cursor[0] === 0 && state.cursor[1] === 0
        && state.margins[0] === seedMargins[0] && state.margins[1] === seedMargins[1] && state.origin === false && state.alternate === true
        && boldCells.length === seedCells.filter(cell => cell.row === 1).length
        && underlineCells.length === seedCells.filter(cell => cell.row === 3).length
        && seedCells.every(spec => {
            const cell = state.ink.find(item => item.row === spec.row && item.col === spec.col);
            return cell !== undefined && cell.char === spec.char && cell.bold === (spec.bold ?? false)
                && (spec.fgIndex === undefined || cell.fgIndex === spec.fgIndex)
                && (spec.underline === undefined || cell.underline === spec.underline);
        });
}

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });

/** The reads every case measures before grading anything, in order, each with the note naming what it failed to measure. */
function seedGap(observation, nonce) {
    if (!seedOk(observation.initial, nonce)) return 'The attributed seed (nonce, cells, margins and modes) was not measured before any request';
    return null;
}

/** The first way a measured exchange differs from the contract, or null. */
function replyMismatch(observation) {
    for (const role of reportRoles) {
        const state = observation[role];
        if (stateKey(state) !== stateKey(observation.initial)) {
            return `The ${role} request changed the screen, the cursor, the rendition, the margins or the modes: a capability report must change nothing`;
        }
        const request = contract.requests.find(item => item.role === role);
        const demanded = oracleReplies(request.nameList);
        if (demanded === null) return `The ${role} request is not a valid oracle request`;
        if (JSON.stringify(state.replies) !== JSON.stringify(demanded)) {
            if (state.replies.length === 0) return `The ${role} request was not answered`;
            if (state.replies.length !== demanded.length) {
                return `The ${role} reply count differs from the contract: ${state.replies.length} replies where ${demanded.length} were demanded (count, order and framing are part of the reply)`;
            }
            return `The ${role} reply differs from the reply oracle: a name, value, status, echo spelling or reply order was wrong`;
        }
    }
    return null;
}

/** The shared control walk, then the exact reply verdict. */
function gradeXtgettcap(observation) {
    const gap = seedGap(observation, observation.nonce);
    if (gap !== null) return inconclusive(gap);
    const mismatch = replyMismatch(observation);
    return mismatch === null ? { outcome: 'supported' } : unsupported(mismatch);
}

/** A printable spelling of raw bytes: ESC as \e. */
const printable = raw => (raw === null ? null : raw.replaceAll('\x1b', String.raw`\e`));

/** The observed record: the pinned exchange, every request with its demanded and received replies, and the seed. */
function briefXtgettcap(observation) {
    const requests = Object.fromEntries(reportRoles.map(role => {
        const request = contract.requests.find(item => item.role === role);
        const demanded = oracleReplies(request.nameList);
        return [role, { request: printable(request.bytes), demand: request.demand,
            demanded: demanded.map(printable), received: observation[role].replies.map(printable),
            matches: JSON.stringify(observation[role].replies) === JSON.stringify(demanded),
            unchanged: stateKey(observation[role]) === stateKey(observation.initial) }];
    }));
    const { rows, cols } = observation.initial;
    return { geometry: { rows, cols }, effectContract: xtgettcapEffectContractId,
        pinnedFeeds: contract.pinnedFeeds, pinnedReply: printable(observation.pinned.replies[0] ?? null),
        pinnedNote: 'TN is deliberately refused (its value is the session TERM, which VT does not know); the pinned verdict stays inconclusive',
        seed: { nonce: observation.nonce, margins: observation.initial.margins, origin: observation.initial.origin,
            alternate: observation.initial.alternate, cells: seedOk(observation.initial, observation.nonce) },
        requests };
}

/** Group the read screens and replies of the case by role. */
export function xtgettcapObservation(item, screens, replies, nonce) {
    if (!Array.isArray(screens) || screens.length !== item.roles.length || !Array.isArray(replies) || replies.length !== item.roles.length
        || typeof nonce !== 'string') {
        throw new Error('Missing XTGETTCAP read captures.');
    }
    const observation = Object.fromEntries(item.roles.map((role, index) => [role, groupOf(screens[index], replies[index])]));
    observation.nonce = nonce;
    return observation;
}

/** Independent contract grader entry: mirrors the pinned criterion without the bundle. */
export function gradeXtgettcapCase(item, observation, revision = xtgettcapContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown XTGETTCAP contracts revision ${revision}.`);
    if (!observation || item.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) {
        throw new Error(`Missing ${item.caseId} read captures.`);
    }
    return item.grade(observation);
}

/** The fixed fixture facts other modules and tests read. */
export const xtgettcapFixture = Object.freeze({ pinned, minimum, seedMargins, seedCells, groups, reportRoles, advertisedNames: Object.freeze([...advertised.keys()]) });
