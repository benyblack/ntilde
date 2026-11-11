// Versioned probe contract for the OSC 7 / OSC 8 metadata qualification batch: extensions.osc7-cwd (working-directory metadata) and extensions.osc8
// (stored hyperlink-bearing cells).
//
// Pinned terminfo.dev@4.0.0 definitions (read from the retained bundle, never inferred from the names):
//
//   extensions.osc7-cwd  has NO fixture bytes at all. Its termless function only reports whether the backend DECLARES the capability
//     (response {"declared": ctx.capabilities.extensions.has("osc7")}, inconclusive, evidence "legacy", note "Declared OSC 7 capability does not
//     establish working-directory metadata delivery"); its terminal function resolves inconclusive with evidence "none" ("No working-directory metadata
//     readback for OSC 7") without writing or reading anything. There is therefore no pinned byte fixture to replay: the pinned verdict is inconclusive
//     by design and is preserved, and the canonical OSC 7 bytes below are a SEPARATELY IDENTIFIED effect contract that is never attributed to the pinned probe.
//
//   extensions.osc8  termless: ctx.feed("ESC[1;1H ESC[2K A ESC]8;;https://example.com/osc8-proof BEL LINK ESC]8;; BEL Z"), reads the stored cells (0,0)..(0,5) and
//     the hyperlink metadata of each, and passes when the characters are ALINKZ and the links are [null, uri, uri, uri, uri, null]
//     (supported / unsupported, evidence "parser-state"; inconclusive when the metadata or the linked text is not observable). Its terminal function writes
//     the same bytes (the erase/home first), then asks for the cursor position and ALWAYS returns inconclusive (evidence "consumed": "Cursor movement cannot
//     verify OSC 8 link metadata or click behavior"). Both paths are preserved; the termless path, executed by the pinned code over the parent-journaled
//     stored cells and link runs, is the pinned verdict and the terminal path is recorded beside it.
//
// The oracle below is written from the OSC 7 / OSC 8 descriptions and the repository's own parser documentation (AnsiParser.Osc.cs), independently of the
// production implementation. OSC 7: the authority of a file:// URI is always dropped, the path is percent-decoded, and the reported value is exactly the
// pane's host working-directory metadata; the expected decoded path of every transmitted URI is a literal below, never produced by a parser. OSC 8: a link
// is opened by a non-empty URI (optional id= parameter), closed by an empty one, switching straight to another link is legal, and every cell written while a
// link is open carries it; the API reports maximal runs with snapshot-local identities that are never compared across reads or to the OSC id.

/** Contracts v1 is the first measured semantics of this batch. */
export const osc78ContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const osc78SuiteId = 'ntilde-owned-osc7-osc8-graded-v1';
/** Identities of the two separately identified effect contracts. */
export const osc7EffectContractId = 'ntilde-osc7-cwd-metadata-effect-v1';
export const osc8EffectContractId = 'ntilde-osc8-hyperlink-cells-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const osc78NoncePrefix = 'NTILDE_LK_';
/** Owner-protocol prefix of this suite's observation requests. */
export const osc78Prefix = 'lnk';
/** The two pinned cases, in report order. */
export const osc78CaseIds = Object.freeze(['extensions.osc7-cwd', 'extensions.osc8']);
export const osc7CaseId = osc78CaseIds[0];
export const osc8CaseId = osc78CaseIds[1];
/** The geometry the fixtures need. The nonce row sits below every fixture row, so the pinned ESC[1;1H ESC[2K never erases it. */
export const osc78Minimum = Object.freeze({ rows: 12, cols: 48 });
export const osc78NonceRow = 9;

const osc = body => `\x1b]${body}\x07`;
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const mark = (letter, tail = '') => osc(`133;${letter}${tail}`);

/** The pinned OSC 8 URI, byte for byte. */
export const osc8PinnedUri = 'https://example.com/osc8-proof';
const osc8TermlessFeed = `\x1b[1;1H\x1b[2KA\x1b]8;;${osc8PinnedUri}\x07LINK\x1b]8;;\x07Z`;

/** The pinned fixtures. OSC 7 has none (termless/terminal code reads no bytes); OSC 8 is the exact pinned feed, written as the terminal path writes it. */
export const osc78Pinned = Object.freeze({
    [osc7CaseId]: Object.freeze({ termlessFeeds: Object.freeze([]), terminalWrites: Object.freeze([]), fixtureBytes: false,
        note: 'No pinned fixture: the probe reports only a declared capability (termless) or the absence of readback (terminal)' }),
    [osc8CaseId]: Object.freeze({ termlessFeeds: Object.freeze([osc8TermlessFeed]),
        terminalWrites: Object.freeze(['\x1b[1;1H\x1b[2K', `A\x1b]8;;${osc8PinnedUri}\x07LINK\x1b]8;;\x07Z`]), fixtureBytes: true,
        expectedChars: 'ALINKZ', linkedColumns: Object.freeze([1, 2, 3, 4]), geometryMinimum: Object.freeze({ rows: 1, cols: 7 }),
        note: 'Cursor movement cannot verify OSC 8 link metadata or click behavior' }),
});

/** The capability declarations the replay contexts make. These are adapter-side constants naming what the observation API can report (link runs,
 * working-directory metadata); they are not observed facts and never decide a verdict. */
export const osc78DeclaredCapabilities = Object.freeze(['hyperlinks', 'osc7']);

/** The measured baseline every experiment starts from: lifecycle state reset by a completed D then A, links closed, title, cwd and the run nonce. */
export function osc78Fixture(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid OSC 7/8 fixture run identity.');
    const short = runId.slice(0, 8), title = `LK-base-${short}`, cwd = '/ntilde-qa/osc78-base', nonce = osc78NoncePrefix + runId;
    const reset = osc('8;;') + osc('9;4;0') + mark('D', ';0') + mark('A')
        + '\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[H\x1b[128M\x1b[2J\x1b[H' + osc(`2;${title}`) + osc(`7;file://localhost${cwd}`);
    const stage = reset + at(osc78NonceRow, 0) + nonce;
    // A runtime palette override proves the handling preserves runtime colors; it is withdrawn at the end.
    return { short, title, cwd, nonce, reset, stage, bind: stage + osc('4;7;rgb:12/34/56'), finish: reset + osc('104;7') };
}

/** The OSC 7 URIs and their literal expected decoded paths, independent of any parser. The authority is dropped by documented design. */
export function osc7Forms(runId) {
    const short = osc78Fixture(runId).short;
    const form = (name, uri, path) => ({ name, uri, path });
    return {
        base: form('base', 'file://localhost/ntilde-qa/osc78-base', '/ntilde-qa/osc78-base'),
        first: form('first', `file://localhost/ntilde-qa/osc7-${short}/first`, `/ntilde-qa/osc7-${short}/first`),
        second: form('second', `file://localhost/ntilde-qa/osc7-${short}/second`, `/ntilde-qa/osc7-${short}/second`),
        space: form('space', `file://localhost/ntilde-qa/osc7%20space/${short}`, `/ntilde-qa/osc7 space/${short}`),
        authority: form('authority', `file://remote-host.example/ntilde-qa/osc7-authority/${short}`, `/ntilde-qa/osc7-authority/${short}`),
    };
}
const osc7Bytes = (form, terminator = '\x07') => `\x1b]7;${form.uri}${terminator}`;

/** The OSC 8 bytes the experiments use, with their literal expected cells. */
export function osc8Forms(runId) {
    const short = osc78Fixture(runId).short;
    return { idUri: `https://example.invalid/osc8-id-${short}`, idValue: `osc8-${short}`, oneUri: `https://example.invalid/osc8-one-${short}`,
        twoUri: `https://example.invalid/osc8-two-${short}`, secondFixtureUri: `https://example.invalid/osc8-second-${short}` };
}

// ---------- the independent oracle ----------

const pad = (text, column) => text.padEnd(column, ' ');
const refusal = () => new Error('OSC 7/8 oracle does not understand a contracted feed.');

/** A fresh measured baseline after the stage bytes. */
function baselineState(fixture) {
    return { rows: { [osc78NonceRow]: fixture.nonce }, cells: new Map(), cursor: [osc78NonceRow, fixture.nonce.length], link: null, title: fixture.title, cwd: fixture.cwd };
}

const ESC = '\x1b';
const controlStarts = [ESC, '\r', '\n'];

/** The cursor-position or erase-line-2 sequence starting at an ESC [ pair, and where it ends. */
function parseCsi(feed, start) {
    let end = start + 2;
    while ('0123456789;'.includes(feed[end] ?? '#')) end++;
    const params = feed.slice(start + 2, end), numbers = params.split(';').map(Number), final = feed[end];
    if (final === 'H' && numbers.length === 2 && numbers.every(Number.isInteger)) return [{ type: 'cup', row: numbers[0] - 1, column: numbers[1] - 1 }, end + 1];
    if (final === 'K' && params === '2') return [{ type: 'el2' }, end + 1];
    throw refusal();
}

/** The OSC 2, 7 or 8 sequence starting at an ESC ] pair (BEL or the two-byte string terminator ends it), and where it ends. */
function parseOsc(feed, start) {
    const terminators = [[feed.indexOf('\x07', start + 2), 1], [feed.indexOf(`${ESC}\\`, start + 2), 2]].filter(([at]) => at >= 0).sort((a, b) => a[0] - b[0]);
    if (terminators.length === 0) throw refusal();
    const [at, length] = terminators[0], body = feed.slice(start + 2, at), separator = body.indexOf(';');
    const code = body.slice(0, separator);
    if (separator < 0 || !['2', '7', '8'].includes(code)) throw refusal();
    return [{ type: 'osc', code, data: body.slice(separator + 1) }, at + length];
}

/** The next token of a feed and where it ends; any other control byte is refused. */
function nextToken(feed, index) {
    const pair = feed.slice(index, index + 2);
    if (pair === `${ESC}[`) return parseCsi(feed, index);
    if (pair === `${ESC}]`) return parseOsc(feed, index);
    if (pair === '\r\n') return [{ type: 'newline' }, index + 2];
    if (controlStarts.includes(feed[index])) throw refusal();
    let end = index;
    while (end < feed.length && !controlStarts.includes(feed[end])) end++;
    return [{ type: 'text', text: feed.slice(index, end) }, end];
}

/** Split one feed into the few kinds of token the contracts emit. */
function tokensOf(feed) {
    const tokens = [];
    for (let index = 0; index < feed.length;) {
        const [token, next] = nextToken(feed, index);
        tokens.push(token);
        index = next;
    }
    return tokens;
}

/** The link an OSC 8 payload opens, or null when it closes (an empty URI). Only the `id` parameter is understood. */
function osc8Link(data) {
    const separator = data.indexOf(';');
    if (separator < 0) throw refusal();
    const parameters = data.slice(0, separator), uri = data.slice(separator + 1);
    let id = null;
    if (parameters !== '') {
        if (!/^id=[A-Za-z0-9_-]+$/.test(parameters)) throw refusal();
        id = parameters.slice(3);
    }
    return uri === '' ? null : { uri, id };
}

function write(state, text) {
    const [row, column] = state.cursor, current = state.rows[row] ?? '';
    state.rows[row] = pad(current, column).slice(0, column) + text + current.slice(column + text.length);
    for (let offset = 0; offset < text.length; offset++) {
        const key = `${row}:${column + offset}`;
        if (state.link === null) state.cells.delete(key); else state.cells.set(key, state.link);
    }
    state.cursor = [row, column + text.length];
}

/** The expected cwd of an OSC 7 payload: only the contracted URIs are understood, each with its literal expected path. */
function osc7Path(data, forms) {
    const form = Object.values(forms).find(value => value.uri === data);
    if (!form) throw refusal();
    return form.path;
}

/** Cells written under a link, grouped into the maximal same-link runs of one row, with identities in order of first appearance (snapshot-local). */
function runsOf(state) {
    const keys = [...state.cells.keys()].map(key => key.split(':').map(Number)).sort((a, b) => a[0] - b[0] || a[1] - b[1]);
    const runs = [], distinct = [];
    for (const [row, col] of keys) {
        const link = state.cells.get(`${row}:${col}`), last = runs.at(-1);
        if (last && last.row === row && last.col + last.length === col && last.uri === link.uri && last.id === link.id) { last.length++; continue; }
        let identity = distinct.findIndex(value => value.uri === link.uri && value.id === link.id);
        if (identity < 0) { identity = distinct.length; distinct.push(link); }
        runs.push({ row, col, length: 1, identity, uri: link.uri, id: link.id });
    }
    return runs;
}

/** Erase the whole current row: its text and every link its cells carried. */
function eraseRow(state) {
    state.rows[state.cursor[0]] = '';
    for (const key of state.cells.keys()) {
        if (key.startsWith(`${state.cursor[0]}:`)) state.cells.delete(key);
    }
}

/** The effect of one OSC token: a title, a working directory or the open link. */
function applyOsc(state, token, forms) {
    if (token.code === '2') state.title = token.data;
    else if (token.code === '7') state.cwd = osc7Path(token.data, forms);
    else state.link = osc8Link(token.data);
}

/** The effect of one token on the oracle state. */
function applyToken(state, token, forms) {
    if (token.type === 'cup') state.cursor = [token.row, token.column];
    else if (token.type === 'el2') eraseRow(state);
    else if (token.type === 'osc') applyOsc(state, token, forms);
    else if (token.type === 'newline') state.cursor = [state.cursor[0] + 1, 0];
    else write(state, token.text);
}

/** Interpret feeds over the oracle state. Only the bytes the contracts emit are understood; anything else is refused. */
export function simulateOsc78(feeds, fixture, runId) {
    const forms = osc7Forms(runId);
    let state = baselineState(fixture);
    for (const feed of feeds) {
        if (feed === fixture.stage) state = baselineState(fixture);
        else for (const token of tokensOf(feed)) applyToken(state, token, forms);
    }
    return { rows: state.rows, cursor: state.cursor, title: state.title, cwd: state.cwd, runs: runsOf(state) };
}

/** The expected whole screen: the bound baseline with the oracle's rows and cursor; every other stored fact is the baseline's. */
export function expectedOsc78Screen(boundScreen, state) {
    const screen = structuredClone(boundScreen);
    screen.lines = Array.from({ length: boundScreen.rows }, (_, row) => (state.rows[row] ?? '').trimEnd());
    [screen.cursorRow, screen.cursorCol] = state.cursor;
    return screen;
}

// ---------- the plan ----------

const read = label => ({ read: label });
const writes = [...osc78Pinned[osc8CaseId].terminalWrites];
/** The pinned OSC 8 fixture with the string terminator (ESC \\) in place of BEL for both the open and the close. */
const stWrites = writes.map((value, index) => index === 1 ? value.replaceAll('\x07', '\x1b\\') : value);
/** The pinned text and cursor movement with the link bytes removed: the same characters, no OSC 8. */
const ordinaryWrites = ['\x1b[1;1H\x1b[2K', 'ALINKZ'];
/** The pinned bytes with only the close sequences (no link is ever opened). */
const closeOnlyWrites = ['\x1b[1;1H\x1b[2K', `A${osc('8;;')}LINK${osc('8;;')}Z`];

function experiment(fixture, caseId, name, role, steps) {
    const plan = [{ feed: fixture.stage }];
    for (const step of steps) plan.push(typeof step === 'string' ? { feed: step } : step);
    return { caseId: `${caseId}/${name}`, owner: caseId, name, role, plan };
}

/** The fixed experiment plan, in execution order. Roles: stock (the pinned or measured baseline read), effect (a measured prior state, an unmarked control,
 * then the effect), required (a canonical sequence graded in full), gate (other bytes must not produce the target transition) and observed (recorded,
 * never graded). */
export function makeOsc78Experiments(runId) {
    const fixture = osc78Fixture(runId), forms = osc7Forms(runId), links = osc8Forms(runId);
    const close = osc('8;;'), open = (uri, id = null) => osc('8;' + (id === null ? '' : 'id=' + id) + ';' + uri);
    const idLink = at(1, 0) + 'B' + open(links.idUri, links.idValue) + 'IDLN' + close + 'Y';
    const twoLinks = at(2, 0) + 'A' + open(links.oneUri) + 'one' + open(links.twoUri) + 'two' + close + 'Z';
    const sameAnchor = at(3, 0) + open(links.idUri, links.idValue) + 'ab' + close + '-' + open(links.idUri, links.idValue) + 'cd' + close;
    const secondFixture = [writes[0], 'A' + open(links.secondFixtureUri) + 'LINK' + close + 'Z'];
    return [
        experiment(fixture, osc7CaseId, 'stock', 'stock', [read('baseline')]),
        experiment(fixture, osc7CaseId, 'effect', 'effect', [read('prior'), at(1, 0) + 'osc7-ordinary', read('ordinary'), osc7Bytes(forms.first), read('first'),
            osc7Bytes(forms.second), read('second'), osc7Bytes(forms.base), read('restored')]),
        experiment(fixture, osc7CaseId, 'space', 'required', [osc7Bytes(forms.space), read('encoded')]),
        experiment(fixture, osc7CaseId, 'gate-title', 'gate', [osc(`2;LK-title-${fixture.short}`), read('post')]),
        experiment(fixture, osc7CaseId, 'authority', 'observed', [osc7Bytes(forms.authority), read('post')]),
        experiment(fixture, osc7CaseId, 'st-terminator', 'observed', [osc7Bytes(forms.first, '\x1b\\'), read('post')]),

        experiment(fixture, osc8CaseId, 'stock', 'stock', [...writes, read('pinned')]),
        experiment(fixture, osc8CaseId, 'effect', 'effect', [read('prior'), ...ordinaryWrites, read('ordinary'), ...writes, read('pinned'), at(4, 0) + 'later', read('later')]),
        experiment(fixture, osc8CaseId, 'gate-close', 'gate', [...closeOnlyWrites, read('post')]),
        experiment(fixture, osc8CaseId, 'id', 'required', [idLink, read('linked')]),
        experiment(fixture, osc8CaseId, 'two-links', 'required', [twoLinks, read('linked')]),
        experiment(fixture, osc8CaseId, 'stale', 'required', [...writes, read('first'), ...ordinaryWrites, read('cleared'), ...secondFixture, read('second')]),
        experiment(fixture, osc8CaseId, 'same-anchor', 'observed', [sameAnchor, read('post')]),
        experiment(fixture, osc8CaseId, 'st-terminator', 'observed', [...stWrites, read('post')]),
    ];
}

/** Every experiment is a graded-plan contract: its plan names feeds and reads. */
export const osc78ExperimentCount = 14;
export const osc78ReadCount = 23;

// ---------- pinned replay ----------

const osc7TermlessNote = 'Declared OSC 7 capability does not establish working-directory metadata delivery';
const osc7TerminalNote = 'No working-directory metadata readback for OSC 7';

/** The pinned OSC 7 results: independent of any observation. */
export function replayOsc7Pinned() {
    return {
        termless: { pass: false, response: JSON.stringify({ declared: osc78DeclaredCapabilities.includes('osc7') }), note: osc7TermlessNote,
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note: osc7TermlessNote } },
        terminal: { pass: false, note: osc7TerminalNote,
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none', note: osc7TerminalNote } },
    };
}

/** The stored cells (0,0)..(0,5) and per-cell link of the stock read: characters and the URI of the run covering each cell (null when none). */
export function osc8StoredCells(screen, links) {
    return Array.from({ length: 6 }, (_, col) => ({ char: screen.lines[0]?.[col] ?? '',
        uri: links.find(run => run.row === 0 && col >= run.col && col < run.col + run.length)?.uri ?? null }));
}

/** The pinned OSC 8 termless result over stored cells, reproduced independently (key order included). */
export function replayOsc8Termless(cells) {
    const pinned = osc78Pinned[osc8CaseId], uri = osc8PinnedUri;
    const response = JSON.stringify({ chars: cells.map(cell => cell.char), links: cells.map(cell => ({ reported: true, uri: cell.uri })) });
    const charsMatch = cells.every((cell, index) => cell.char === pinned.expectedChars[index]);
    if (!charsMatch || cells.slice(1, 5).some(cell => cell.uri === null)) {
        return { pass: false, response, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note: 'Linked text was not observable' } };
    }
    const expected = JSON.stringify({ chars: [...pinned.expectedChars], links: [null, uri, uri, uri, uri, null].map(link => ({ reported: true, uri: link })) });
    const pass = response === expected;
    return { pass, response, observation: { outcome: pass ? 'supported' : 'unsupported', evidence: 'parser-state',
        ...(!pass && { note: 'OSC 8 cell URI differs from the exact requested link or leaked past close' }) },
    assertions: [{ kind: pass ? 'positive' : 'negative', expected, observed: response }] };
}

/** The pinned OSC 8 terminal result after the cursor-position reply (always inconclusive: cursor movement cannot verify link metadata). */
export function replayOsc8Terminal(geometry, position) {
    if (!Number.isSafeInteger(geometry.rows) || !Number.isSafeInteger(geometry.cols) || geometry.rows < 1 || geometry.cols < 7) {
        return { pass: false, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none',
            note: `OSC 8 fixture needs at least 1x7, measured ${geometry.rows}x${geometry.cols}` } };
    }
    if (!position) return { pass: false, note: 'No cursor response', observation: { outcome: 'inconclusive', reason: 'no-response', evidence: 'consumed' } };
    return { pass: false, response: `${position.row};${position.col}`,
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed', note: osc78Pinned[osc8CaseId].note } };
}

/** The replay of one case over its stock-context read (a frame's screen and metadata links). */
export function replayOsc78Pinned(caseId, screen, links) {
    if (caseId === osc7CaseId) return replayOsc7Pinned();
    return { termless: replayOsc8Termless(osc8StoredCells(screen, links)),
        terminal: replayOsc8Terminal({ rows: screen.rows, cols: screen.cols }, { row: screen.cursorRow + 1, col: screen.cursorCol + 1 }) };
}

/** The contexts the pinned code is replayed in. Each refuses any write, read or feed outside the pinned fixture. */
export function osc78TermlessContext(caseId, screen, links) {
    const pinned = osc78Pinned[caseId], declared = new Set(osc78DeclaredCapabilities);
    let fed = 0;
    const refuse = what => { throw new Error(`Pinned ${caseId} ${what} differs from the contract.`); };
    const stored = (row, col) => {
        const readable = pinned.termlessFeeds.length > 0 && fed === pinned.termlessFeeds.length && row === 0 && col >= 0 && col <= 5;
        if (!readable) refuse('read');
    };
    const feed = text => {
        if (fed >= pinned.termlessFeeds.length || text !== pinned.termlessFeeds[fed]) refuse('feed');
        fed++;
    };
    return { capabilities: { extensions: declared }, get fedCount() { return fed; }, feed,
        getCell: (row, col) => { stored(row, col); return { char: screen.lines[0]?.[col] ?? '' }; },
        getHyperlinkAt: (row, col) => { stored(row, col); return osc8StoredCells(screen, links)[col].uri; } };
}
export function osc78TerminalContext(caseId, screen) {
    const pinned = osc78Pinned[caseId];
    let written = 0;
    const write = text => {
        if (written >= pinned.terminalWrites.length || text !== pinned.terminalWrites[written]) throw new Error(`Pinned ${caseId} write differs from the contract.`);
        written++;
    };
    const queryCursorPosition = () => {
        if (written !== pinned.terminalWrites.length) throw new Error(`Pinned ${caseId} queried the cursor early.`);
        return Promise.resolve({ row: screen.cursorRow + 1, col: screen.cursorCol + 1 });
    };
    return { rows: screen.rows, cols: screen.cols, get writtenCount() { return written; }, write, queryCursorPosition };
}
