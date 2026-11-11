// Versioned probe contract for the OSC 133 shell-integration qualification batch: extensions.osc133-a (prompt start), osc133-b (command-input
// start), osc133-c (command execution start) and osc133-d (command completion).
//
// Pinned terminfo.dev@4.0.0 definitions (read from the retained bundle, never inferred from the names). Each case registers
//   termless:  ctx.feed("\x1B]133;<M>\x07X"); cell = ctx.getCell(0, 0); promptConsumptionResult(cell.char === "X")
//   terminal:  ctx.write("\x1B[1;1H\x1B[2K"); ctx.write("\x1B]133;<M>\x07X"); pos = await ctx.queryCursorPosition();
//              promptConsumptionResult(pos === null ? null : pos.col === 2)
// with <M> = A, B, C and D;0 respectively. promptConsumptionResult ALWAYS returns pass:false with outcome "inconclusive" ("Marker was consumed;
// shell prompt integration was not observed", evidence "consumed"). So the pinned verdict of every case is inconclusive by design: the acceptance
// criterion is marker consumption only. That verdict is preserved; the roles named by the task (prompt start, command-input start, command
// execution start, command completion) are qualified by a separately identified effect contract graded from the owned pane-metadata and
// session-status observations (docs/qa/terminfo/osc133.md), never attributed to the pinned probes.
//
// The oracle below is written from the OSC 133 description (FinalTerm / Contour semantic prompts) and the repository's own documented lifecycle,
// independently of the production implementation. Every field it predicts is tagged: a field that a marker's documented semantics DETERMINE is
// "certain" and is graded; a field a marker leaves UNSPECIFIED (for example what A does to the anchors of an unterminated command, or where C places
// the output anchor when the cursor jumped above the input line) stays "uncertain": the oracle carries the prior value forward as an
// implementation-policy prediction that is recorded beside the observation but never decides a verdict.
import { Buffer } from 'node:buffer';

/** Contracts v1 is the first measured semantics of this batch. */
export const osc133ContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const osc133SuiteId = 'ntilde-owned-osc133-graded-v1';
/** Identity of the separately identified effect contract. */
export const osc133EffectContractId = 'ntilde-osc133-shell-lifecycle-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const osc133NoncePrefix = 'NTILDE_SH_';
/** Owner-protocol prefix of this suite's observation requests. */
export const osc133Prefix = 'shi';
/** The four pinned cases, in report order. */
export const osc133CaseIds = Object.freeze(['extensions.osc133-a', 'extensions.osc133-b', 'extensions.osc133-c', 'extensions.osc133-d']);
/** Marker letter and pinned role of each case. */
export const osc133Roles = Object.freeze({
    'extensions.osc133-a': { marker: 'A', payload: '', role: 'prompt start' },
    'extensions.osc133-b': { marker: 'B', payload: '', role: 'command-input start' },
    'extensions.osc133-c': { marker: 'C', payload: '', role: 'command execution start' },
    'extensions.osc133-d': { marker: 'D', payload: ';0', role: 'command completion' },
});
/** The geometry the fixtures need. The nonce row sits below every fixture row, so the pinned ESC[1;1H ESC[2K never erases it. */
export const osc133Minimum = Object.freeze({ rows: 12, cols: 48 });
export const osc133NonceRow = 9;
export const osc133Prompt = 'ntilde$ ';

const osc = body => `\x1b]${body}\x07`;
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const mark = (letter, tail = '') => osc(`133;${letter}${tail}`);
const base64 = text => Buffer.from(text, 'utf8').toString('base64');

/** The pinned fixture bytes, byte for byte. The terminal path writes them in two parts; the termless path feeds only the second. */
export const osc133Pinned = Object.freeze(Object.fromEntries(osc133CaseIds.map(caseId => {
    const { marker, payload } = osc133Roles[caseId];
    const feed = `\x1b]133;${marker}${payload}\x07X`;
    return [caseId, Object.freeze({ termlessFeeds: [feed], terminalWrites: ['\x1b[1;1H\x1b[2K', feed], expectedChar: 'X', expectedCursorColumn: 2,
        note: 'Marker was consumed; shell prompt integration was not observed' })];
})));

/** The measured baseline every experiment starts from: all shell lifecycle state reset by a completed D then A, title, cwd and the run nonce. */
export function osc133Fixture(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid OSC 133 fixture run identity.');
    const title = `SH-base-${runId.slice(0, 8)}`, cwd = '/ntilde-qa/osc133-base', nonce = osc133NoncePrefix + runId;
    const reset = osc('8;;') + osc('9;4;0') + mark('D', ';0') + mark('A')
        + '\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[H\x1b[128M\x1b[2J\x1b[H' + osc(`2;${title}`) + osc(`7;file://localhost${cwd}`);
    const stage = reset + at(osc133NonceRow, 0) + nonce;
    // A runtime palette override proves marker handling preserves runtime colors; it is withdrawn at the end.
    return { title, cwd, nonce, reset, stage, bind: stage + osc('4;7;rgb:12/34/56'), finish: reset + osc('104;7') };
}

// ---------- the independent oracle ----------

const fields = ['status', 'command', 'accepting', 'commandStart', 'outputStart'];
const pad = (text, column) => text.padEnd(column, ' ');

/** A fresh measured baseline after the stage bytes. */
function baselineState(fixture) {
    return { rows: { [osc133NonceRow]: fixture.nonce }, cursor: [osc133NonceRow, fixture.nonce.length], status: 'prompt', command: null, accepting: false,
        commandStart: null, outputStart: null, certain: Object.fromEntries(fields.map(field => [field, true])) };
}

const isBase64 = text => text.length > 0 && text.length % 4 === 0 && [...text].every(char => /[A-Za-z0-9+/=]/.test(char)) && !/=[^=]/.test(text);
const printable = text => text.length > 0 && [...text].every(char => char === '\t' || (char >= ' ' && char !== '\x7f'));

/** The command text a C payload names, and whether the documented contract determines it. Base64 of UTF-8 text is the shape every Ntilde bootstrap emits
 * and is graded; a FinalTerm key=value attribute names no command and plain text is passed through, but both are implementation-shaped readings and are
 * recorded, not graded. */
function payloadCommand(tail) {
    if (tail === '' || tail === ';') return { value: null, certain: true };
    const body = tail.slice(1), decoded = isBase64(body) ? Buffer.from(body, 'base64') : null;
    if (decoded !== null && decoded.toString('base64') === body && printable(decoded.toString('utf8'))) return { value: decoded.toString('utf8'), certain: true };
    return { value: /^[A-Za-z_][\w-]*=\S*$/.test(body) ? null : body, certain: false };
}

const refusal = () => new Error('OSC 133 oracle does not understand a contracted feed.');
const isDigitOrSemicolon = char => (char >= '0' && char <= '9') || char === ';';

/** One CSI token starting at `index` (cursor position or erase-line 2); returns the token and where the sequence ends. */
function csiToken(feed, index) {
    let end = index + 2;
    while (end < feed.length && isDigitOrSemicolon(feed[end])) end++;
    const params = feed.slice(index + 2, end), final = feed[end];
    if (final === 'H') {
        const [row, column] = params.split(';').map(Number);
        return { token: { type: 'cup', row: row - 1, column: column - 1 }, next: end + 1 };
    }
    if (final === 'K' && params === '2') return { token: { type: 'el2' }, next: end + 1 };
    throw refusal();
}

/** The earliest OSC terminator (BEL, or the two-byte string terminator) at or after an index: where it starts and how long it is, or null. */
function oscTerminator(feed, from) {
    const bell = feed.indexOf('\x07', from), st = feed.indexOf('\x1b\\', from);
    if (bell < 0 && st < 0) return null;
    if (bell < 0 || (st >= 0 && st < bell)) return { at: st, length: 2 };
    return { at: bell, length: 1 };
}

/** One OSC 133 marker token starting at `index`; any other OSC is refused. */
function oscToken(feed, index) {
    const terminator = oscTerminator(feed, index + 2);
    if (terminator === null) throw refusal();
    const body = feed.slice(index + 2, terminator.at), letter = body.slice(4, 5), tail = body.slice(5);
    if (!body.startsWith('133;') || letter === '' || !'ABCD'.includes(letter) || (tail !== '' && !tail.startsWith(';'))) throw refusal();
    return { token: { type: 'marker', letter, tail }, next: terminator.at + terminator.length };
}

const isControlStart = char => char === '\x1b' || char === '\r' || char === '\n';

/** Split one feed into the few kinds of token the contracts emit; any other byte is refused. */
function tokensOf(feed) {
    const result = [];
    let index = 0;
    while (index < feed.length) {
        const char = feed[index], following = feed[index + 1];
        let step;
        if (char === '\x1b' && following === '[') step = csiToken(feed, index);
        else if (char === '\x1b' && following === ']') step = oscToken(feed, index);
        else if (char === '\r' && following === '\n') step = { token: { type: 'newline' }, next: index + 2 };
        else if (isControlStart(char)) throw refusal();
        else {
            let end = index;
            while (end < feed.length && !isControlStart(feed[end])) end++;
            step = { token: { type: 'text', text: feed.slice(index, end) }, next: end };
        }
        result.push(step.token);
        index = step.next;
    }
    return result;
}

function write(state, text) {
    const [row, column] = state.cursor, current = state.rows[row] ?? '';
    state.rows[row] = pad(current, column).slice(0, column) + text + current.slice(column + text.length);
    state.cursor = [row, column + text.length];
}

/** The documented transition of each marker; fields a marker leaves unspecified become uncertain. */
function applyMarker(state, letter, tail) {
    const [row] = state.cursor;
    const out = { ...state, certain: { ...state.certain } };
    if (letter === 'A') {
        // Prompt start: the shell is at its prompt and no command text is current. After a completed (or no) command nothing else is pending, so
        // the anchors stay exactly as D left them; what A does to the anchors and window of an unterminated command is not specified.
        const clean = state.status === 'prompt' && !state.accepting && state.commandStart === null && state.outputStart === null
            && fields.every(field => state.certain[field]);
        Object.assign(out, { status: 'prompt', command: null });
        if (!clean) for (const field of ['accepting', 'commandStart', 'outputStart']) out.certain[field] = false;
    } else if (letter === 'B') {
        // Prompt end: the command line begins at the cursor and the input window opens. B says nothing about a running command.
        Object.assign(out, { accepting: true, commandStart: [...state.cursor] });
        out.certain.accepting = true; out.certain.commandStart = true;
        if (state.status === 'running') { out.certain.status = false; out.certain.command = false; }
        if (state.outputStart !== null) out.certain.outputStart = false;
    } else if (letter === 'C') {
        // Command accepted: the input window closes, the command runs, and its payload (when present) names it. The mark survives C. The output
        // region begins at the row after the input line; that is only specified when the cursor is still on the line the B mark began.
        const named = payloadCommand(tail);
        const canonical = state.certain.commandStart && state.commandStart !== null && state.commandStart[0] === row;
        const duplicate = state.status === 'running';
        Object.assign(out, { accepting: false, status: 'running', command: named.value, outputStart: [row + 1, 0] });
        // A second C while a command is already running (no D between) is a duplicate marker: nothing about it is specified.
        out.certain.accepting = !duplicate; out.certain.status = !duplicate; out.certain.command = !duplicate && named.certain;
        out.certain.outputStart = canonical && !duplicate;
    } else {
        // Command finished: the window closes, both anchors are dropped, and no command is current. The exit payload is not interpreted here.
        Object.assign(out, { accepting: false, status: 'prompt', command: null, commandStart: null, outputStart: null });
        for (const field of fields) out.certain[field] = true;
    }
    return out;
}

/** Interpret feeds over the oracle state. Only the bytes the contracts emit are understood; anything else is refused. */
export function simulateOsc133(feeds, fixture) {
    let state = baselineState(fixture);
    for (const feed of feeds) {
        if (feed === fixture.stage) { state = baselineState(fixture); continue; }
        for (const token of tokensOf(feed)) {
            if (token.type === 'cup') state.cursor = [token.row, token.column];
            else if (token.type === 'el2') state.rows[state.cursor[0]] = '';
            else if (token.type === 'marker') state = applyMarker(state, token.letter, token.tail);
            else if (token.type === 'newline') state.cursor = [state.cursor[0] + 1, 0];
            else write(state, token.text);
        }
    }
    return state;
}

/** The expected whole screen: the bound baseline with the oracle's rows and cursor; every other stored fact is the baseline's. */
export function expectedOsc133Screen(boundScreen, state) {
    const screen = structuredClone(boundScreen);
    screen.lines = Array.from({ length: boundScreen.rows }, (_, row) => (state.rows[row] ?? '').trimEnd());
    [screen.cursorRow, screen.cursorCol] = state.cursor;
    return screen;
}

// ---------- the plan ----------

const writes = letter => [...osc133Pinned[osc133CaseIds.find(caseId => osc133Roles[caseId].marker === letter)].terminalWrites];
const ordinaryWrites = ['\x1b[1;1H\x1b[2K', 'X'];

/** One canonical lifecycle segment starting at `row`: A, the prompt, B, the typed text and, in turn, C with its payload and an output line, then D. */
function cycle(row, typed, { through = 'D', payload = typed, output = 'out' } = {}) {
    const steps = [mark('A'), at(row, 0) + osc133Prompt, mark('B') + typed];
    if (through === 'B') return steps;
    steps.push(mark('C', `;${base64(payload)}`), `\r\n${output}`);
    if (through === 'D') steps.push(mark('D', ';0'));
    return steps;
}

/** `markerFeeds` is how many trailing feeds emit the marker under test (the pinned fixture's two writes, or one bare marker). */
function experiment(fixture, caseId, name, role, steps, extra = {}) {
    const plan = [{ feed: fixture.stage }];
    for (const step of steps) plan.push(typeof step === 'string' ? { feed: step } : step);
    return { caseId: `${caseId}/${name}`, owner: caseId, name, role, plan, ...extra };
}
const read = label => ({ read: label });
const marked = letter => writes(letter);
/** The pinned fixture with the string terminator (ESC \\) in place of BEL: the same marker, a supported terminator variant. */
const stMarked = letter => writes(letter).map((write, index) => index === 1 ? write.replace('\x07', '\x1b\\') : write);

/** The fixed experiment plan, in execution order. Roles: stock (the pinned fixture on the prepared baseline), effect (a measured prior state, an
 * unmarked control, then the pinned fixture), required (a canonical sequence graded in full), gate (another marker must not produce the target
 * transition) and disclosure (a marker that legitimately shares an observable; recorded, never required). */
export function makeOsc133Experiments(runId) {
    const fixture = osc133Fixture(runId), a = 'extensions.osc133-a', b = 'extensions.osc133-b', c = 'extensions.osc133-c', d = 'extensions.osc133-d';
    const running = (row, text, output) => cycle(row, text, { through: 'C', output });
    const input = (row, text) => cycle(row, text, { through: 'B' });
    return [
        experiment(fixture, a, 'stock', 'stock', [...marked('A'), read('pinned')]),
        experiment(fixture, a, 'effect', 'effect', [...running(2, 'echo a-run', 'a-out'), read('prior'), ...ordinaryWrites, read('ordinary'), ...marked('A'), read('pinned')]),
        experiment(fixture, a, 'gate-b', 'gate', [...running(2, 'echo a-run', 'a-out'), ...marked('B'), read('post')]),
        experiment(fixture, a, 'gate-c', 'gate', [...running(2, 'echo a-run', 'a-out'), ...marked('C'), read('post')]),
        experiment(fixture, a, 'disclose-d', 'disclosure', [...running(2, 'echo a-run', 'a-out'), ...marked('D'), read('post')]),
        experiment(fixture, a, 'st-terminator', 'observed', [...running(2, 'echo a-run', 'a-out'), ...stMarked('A'), read('post')]),

        experiment(fixture, b, 'stock', 'stock', [...marked('B'), read('pinned')]),
        experiment(fixture, b, 'effect', 'effect', [mark('A'), at(3, 0) + osc133Prompt, read('prior'), ...ordinaryWrites, read('ordinary'), ...marked('B'), read('pinned')]),
        experiment(fixture, b, 'boundary', 'required', [...input(3, 'echo b-input'), read('input')]),
        experiment(fixture, b, 'gate-a', 'gate', [mark('A'), at(3, 0) + osc133Prompt, ...marked('A'), read('post')]),
        experiment(fixture, b, 'gate-c', 'gate', [mark('A'), at(3, 0) + osc133Prompt, ...marked('C'), read('post')]),
        experiment(fixture, b, 'gate-d', 'gate', [mark('A'), at(3, 0) + osc133Prompt, ...marked('D'), read('post')]),
        experiment(fixture, b, 'st-terminator', 'observed', [mark('A'), at(3, 0) + osc133Prompt, ...stMarked('B'), read('post')]),
        experiment(fixture, b, 'cycles', 'required', [...cycle(3, 'echo b-one', { output: 'b-one-out' }), read('after-d'), ...input(6, 'echo b-two'), read('second')]),

        experiment(fixture, c, 'stock', 'stock', [...marked('C'), read('pinned')]),
        experiment(fixture, c, 'effect', 'effect', [...input(2, 'echo c-typed'), read('prior'), ...ordinaryWrites, read('ordinary'), ...marked('C'), read('pinned')]),
        experiment(fixture, c, 'payload', 'required', [...input(2, 'echo c-typed'), read('input'), mark('C', `;${base64('echo c-payload')}`), read('accepted'), '\r\nc-output', read('output')]),
        experiment(fixture, c, 'gate-a', 'gate', [...input(2, 'echo c-typed'), ...marked('A'), read('post')]),
        experiment(fixture, c, 'gate-b', 'gate', [...input(2, 'echo c-typed'), ...marked('B'), read('post')]),
        experiment(fixture, c, 'gate-d', 'gate', [...input(2, 'echo c-typed'), ...marked('D'), read('post')]),
        experiment(fixture, c, 'st-terminator', 'observed', [...input(2, 'echo c-typed'), ...stMarked('C'), read('post')]),
        experiment(fixture, c, 'payload-attribute', 'observed', [...input(2, 'echo c-typed'), mark('C', ';aid=7'), read('accepted')]),
        experiment(fixture, c, 'payload-plain', 'observed', [...input(2, 'echo c-typed'), mark('C', ';echo c-plain'), read('accepted')]),
        experiment(fixture, c, 'cycles', 'required', [...cycle(3, 'echo c-one', { output: 'c-one-out' }), read('after-d'), ...running(6, 'echo c-two', 'c-two-out'), read('second')]),

        experiment(fixture, d, 'stock', 'stock', [...marked('D'), read('pinned')]),
        experiment(fixture, d, 'effect', 'effect', [...running(2, 'echo d-run', 'd-out'), read('prior'), ...ordinaryWrites, read('ordinary'), ...marked('D'), read('pinned')]),
        experiment(fixture, d, 'exit-payload', 'observed', [...running(2, 'echo d-run', 'd-out'), read('prior'), mark('D', ';7'), read('post')]),
        experiment(fixture, d, 'gate-a', 'gate', [...running(2, 'echo d-run', 'd-out'), ...marked('A'), read('post')]),
        experiment(fixture, d, 'gate-b', 'gate', [...running(2, 'echo d-run', 'd-out'), ...marked('B'), read('post')]),
        experiment(fixture, d, 'gate-c', 'gate', [...running(2, 'echo d-run', 'd-out'), ...marked('C'), read('post')]),
        experiment(fixture, d, 'st-terminator', 'observed', [...running(2, 'echo d-run', 'd-out'), ...stMarked('D'), read('post')]),
        experiment(fixture, d, 'cycles', 'required', [...cycle(3, 'echo d-one', { output: 'd-one-out' }), read('after-d1'), ...cycle(6, 'echo d-two', { output: 'd-two-out' }), read('after-d2')]),
    ];
}

/** Every experiment is a graded-plan contract: its plan names feeds and reads. */
export const osc133ExperimentCount = 32;
export const osc133ReadCount = 46;

// ---------- pinned replay ----------

/** The pinned promptConsumptionResult(consumed), key order included. */
export function consumptionResult(consumed) {
    let note = 'Marker consumption was not confirmed; shell prompt integration was not observed';
    if (consumed === null) note = 'No cursor reply after semantic prompt marker';
    else if (consumed) note = 'Marker was consumed; shell prompt integration was not observed';
    return { pass: false, note, observation: { outcome: 'inconclusive', evidence: consumed === null ? 'query' : 'consumed', reason: consumed === null ? 'no-response' : 'insufficient-evidence', note } };
}

/** The contexts the pinned code is replayed in. Both refuse any write, read or feed outside the pinned fixture. */
export function osc133TermlessContext(caseId, screen) {
    const pinned = osc133Pinned[caseId];
    let fed = 0;
    return { get fedCount() { return fed; },
        feed: text => { if (fed >= pinned.termlessFeeds.length || text !== pinned.termlessFeeds[fed]) throw new Error(`Pinned ${caseId} feed differs from the contract.`); fed++; },
        getCell: (row, col) => { if (fed !== 1 || row !== 0 || col !== 0) throw new Error(`Pinned ${caseId} reads an uncontracted cell.`); return { char: screen.lines[0]?.[0] ?? '' }; } };
}
export function osc133TerminalContext(caseId, screen) {
    const pinned = osc133Pinned[caseId];
    let written = 0;
    return { get writtenCount() { return written; },
        write: text => { if (written >= pinned.terminalWrites.length || text !== pinned.terminalWrites[written]) throw new Error(`Pinned ${caseId} write differs from the contract.`); written++; },
        queryCursorPosition: () => { if (written !== pinned.terminalWrites.length) throw new Error(`Pinned ${caseId} queried the cursor early.`); return Promise.resolve({ row: screen.cursorRow + 1, col: screen.cursorCol + 1 }); } };
}
/** The result the pinned code returns over the journaled stock read. */
export function replayOsc133Pinned(caseId, screen) {
    const termless = consumptionResult((screen.lines[0]?.[0] ?? '') === osc133Pinned[caseId].expectedChar);
    const terminal = consumptionResult(screen.cursorCol + 1 === osc133Pinned[caseId].expectedCursorColumn);
    return { termless, terminal };
}

