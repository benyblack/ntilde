// Versioned contract for the OSC 3008 hierarchical-context qualification (extensions.osc3008-context). This is a plan and an independent oracle, not a measurement.
//
// Pinned terminfo.dev@4.0.0 definition (read from the retained bundle, never inferred from the name). The case registers NO termless function and one terminal path:
//   terminal:  ctx.write("\x1B[1;1H\x1B[2K"); ctx.write("\x1B]3008;type=test\x07"); pos = await ctx.queryCursorPosition();
//              return unmeasuredCellResult(pos, "OSC 3008 systemd context")
// unmeasuredCellResult ALWAYS returns pass:false with outcome "inconclusive" ("Cursor response does not measure OSC 3008 systemd context"). So the pinned verdict is inconclusive by
// design, and the pinned payload is not even a UAPI.15 sequence (it has neither `start=` nor `end=`), so a conforming terminal must ignore it. That verdict is preserved; the protocol
// itself is qualified by a separately identified effect contract (docs/qa/terminfo/osc3008.md), never attributed to the pinned probe.
//
// The oracle below is written from UAPI.15 v1.0 (https://uapi-group.org/specifications/specs/osc_context/): its semantics, ABNF and "Processing, Limits, Security" section,
// independently of the production implementation. Where the text leaves a choice, the choice is an implementation POLICY: it is recorded and compared, but an experiment
// whose role is `policy` never decides the verdict.
import { osc133Fixture } from './osc133-contracts.mjs';

/** Contracts v1 is the first measured semantics of this suite. */
export const osc3008ContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const osc3008SuiteId = 'ntilde-owned-osc3008-graded-v1';
/** Identity of the separately identified effect contract. */
export const osc3008EffectContractId = 'ntilde-osc3008-context-effect-v1';
/** Owner-protocol prefix of this suite's observation requests. */
export const osc3008Prefix = 'ocx';
/** The one pinned case. */
export const osc3008CaseId = 'extensions.osc3008-context';
export const osc3008CaseIds = Object.freeze([osc3008CaseId]);
/** What the pinned terminal path writes, in order, before asking for the cursor position. */
export const osc3008PinnedWrites = Object.freeze(['\x1b[1;1H\x1b[2K', '\x1b]3008;type=test\x07']);
export const osc3008PinnedNote = 'Cursor response does not measure OSC 3008 systemd context';
/** The geometry the fixture needs (the OSC 133 fixture's nonce row sits below every row this suite writes). */
export const osc3008Minimum = Object.freeze({ rows: 12, cols: 48 });

/** The bounds the oracle applies. The first three come from the specification's ABNF; depth and ring size are implementation policy. */
export const osc3008Limits = Object.freeze({ idChars: 64, fieldChars: 255, depth: 16, ended: 8, sequenceChars: 16384, fields: 32 });

const OSC = '\x1b]3008;', ST = '\x1b\\', BEL = '\x07';
const typeNames = new Set(['service', 'session', 'shell', 'command', 'vm', 'container', 'elevate', 'chpriv', 'subcontext', 'remote', 'boot', 'app']);
const exitNames = new Set(['success', 'failure', 'crash', 'interrupt']);
/** Start-field names in wire (lower-case) form and the JSON name the App reports for each. */
const startFields = Object.freeze({ type: 'type', user: 'user', hostname: 'hostname', machineid: 'machineId', bootid: 'bootId', pid: 'pid', pidfdid: 'pidFdId', comm: 'comm',
    cwd: 'cwd', cmdline: 'cmdline', vm: 'vm', container: 'container', targetuser: 'targetUser', targethost: 'targetHost', sessionid: 'sessionId' });

// ---------- the independent oracle ----------

const isHex = char => (char >= '0' && char <= '9') || (char >= 'a' && char <= 'f') || (char >= 'A' && char <= 'F');
const isDigit = char => char >= '0' && char <= '9';
const check = (value, message) => { if (!value) throw new Error(message); };

/** `\x3b` is a semicolon and `\x5c` a backslash; any other backslash is invalid because the text says every backslash must be replaced. */
function unescape(text) {
    let result = '';
    for (let index = 0; index < text.length; index++) {
        if (text[index] !== '\\') { result += text[index]; continue; }
        const code = text.slice(index + 1, index + 4).toLowerCase();
        if (code === 'x3b') result += ';';
        else if (code === 'x5c') result += '\\';
        else return null;
        index += 3;
    }
    return result;
}

const isControl = code => code < 0x20 || (code >= 0x7f && code <= 0x9f) || code === 0xfffd;
function isText(value, allowEmpty = false) {
    if (value.length === 0) return allowEmpty;
    if (value.length > osc3008Limits.fieldChars) return false;
    for (let index = 0; index < value.length; index++) {
        const code = value.charCodeAt(index);
        if (isControl(code)) return false;
        if (code >= 0xd800 && code <= 0xdbff) {
            const next = value.charCodeAt(index + 1);
            if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
            index++;
        } else if (code >= 0xdc00 && code <= 0xdfff) return false;
    }
    return true;
}
const isId = id => id.length >= 1 && id.length <= osc3008Limits.idChars && [...id].every(char => char.charCodeAt(0) >= 0x20 && char.charCodeAt(0) <= 0x7e);
const isId128 = value => value.length >= 32 && value.length <= 36 && [...value].every(char => isHex(char) || char === '-');
const isUint64 = value => value.length >= 1 && value.length <= 20 && [...value].every(isDigit) && BigInt(value) <= 18446744073709551615n;
const isSignal = value => value.length >= 4 && value.length <= 19 && value.startsWith('SIG') && [...value.slice(3)].every(char => (char >= 'A' && char <= 'Z') || isDigit(char) || char === '+');

/** The validity rule of each start field that is not plain bounded text. */
const startRules = Object.freeze({ type: value => typeNames.has(value), machineid: isId128, bootid: isId128, pid: isUint64, pidfdid: isUint64, cmdline: value => isText(value, true) });

/** One start field: the JSON name and value to keep, or null when the field is invalid or unknown. */
function startField(key, value) {
    const name = startFields[key];
    if (name === undefined) return null;
    return (startRules[key] ?? isText)(value) ? [name, value] : null;
}

/** One end field. */
function endField(key, value) {
    if (key === 'exit' && exitNames.has(value)) return ['exit', value];
    if (key === 'status' && isUint64(value) && BigInt(value) <= 255n) return ['status', value];
    if (key === 'signal' && isSignal(value)) return ['signal', value];
    return null;
}

/** Interpret the payload after `OSC 3008 ;`: an operation with its id and valid fields, or null when the sequence is ignored. */
function parseSequence(data) {
    if (data.length > osc3008Limits.sequenceChars) return null;
    const parts = data.split(';').slice(0, osc3008Limits.fields);
    const operation = ['start', 'end'].find(name => parts[0].startsWith(`${name}=`));
    if (operation === undefined) return null;
    const id = unescape(parts[0].slice(operation.length + 1));
    if (id === null || !isId(id)) return null;
    const fields = {};
    for (const part of parts.slice(1)) {
        const at = part.indexOf('=');
        if (at <= 0) continue;
        const value = unescape(part.slice(at + 1));
        if (value === null) continue;
        const kept = (operation === 'start' ? startField : endField)(part.slice(0, at), value);
        if (kept !== null) fields[kept[0]] = kept[1]; // a repeated field keeps its last valid value
    }
    return { operation, id, fields };
}

/** Every complete OSC 3008 payload of one feed, in order. A terminator is BEL or ESC \\. */
function payloadsOf(feed) {
    const payloads = [];
    for (let from = feed.indexOf(OSC); from >= 0; from = feed.indexOf(OSC, from + 1)) {
        const bell = feed.indexOf(BEL, from + OSC.length), st = feed.indexOf(ST, from + OSC.length);
        const stop = [bell, st].filter(value => value >= 0).sort((a, b) => a - b)[0];
        if (stop !== undefined) payloads.push(feed.slice(from + OSC.length, stop));
    }
    return payloads;
}

/** The contexts the specification (plus the recorded policies) predicts after every feed so far. Terminal reset sequences never touch them. */
export function simulateContexts(feeds) {
    const stack = [], ended = [];
    for (const feed of feeds) {
        for (const payload of payloadsOf(feed)) {
            const parsed = parseSequence(payload);
            if (parsed === null) continue;
            const index = stack.findIndex(item => item.id === parsed.id);
            if (parsed.operation === 'start') {
                if (index < 0 && stack.length >= osc3008Limits.depth) continue; // the earlier contexts are kept, the later one is discarded
                if (index >= 0) stack.splice(index); // an update flushes the old metadata and ends every sub-context
                stack.push({ id: parsed.id, ...parsed.fields });
            } else if (index >= 0) {
                const { type } = stack[index];
                stack.splice(index);
                ended.push({ id: parsed.id, ...(type === undefined ? {} : { type }), ...parsed.fields });
                if (ended.length > osc3008Limits.ended) ended.shift();
            }
        }
    }
    return { stack, ended };
}

/** The OSC bodies the shell-lifecycle model never needs: the context sequences, and the foreign spellings the negative controls send. None changes stored text, the cursor or shell state. */
const inertOscPrefixes = Object.freeze(['3008', '3007;', '30080;', '133;start', '633;start']);

/** One feed with every inert OSC sequence and every soft terminal reset (which leaves the tracked text, cursor and shell state unchanged) removed. The fixture's own stage bytes are never passed through this. */
export function withoutContextSequences(feed) {
    let remaining = feed, result = '';
    for (let from = remaining.indexOf('\x1b]'); from >= 0; from = remaining.indexOf('\x1b]')) {
        const bell = remaining.indexOf(BEL, from + 2), st = remaining.indexOf(ST, from + 2);
        const stop = [bell, st].filter(value => value >= 0).sort((a, b) => a - b)[0];
        check(stop !== undefined, 'Unterminated OSC sequence.');
        const inert = inertOscPrefixes.some(prefix => remaining.slice(from + 2, stop).startsWith(prefix));
        result += remaining.slice(0, inert ? from : stop + (stop === st ? ST.length : BEL.length));
        remaining = remaining.slice(stop + (stop === st ? ST.length : BEL.length));
    }
    return (result + remaining).replaceAll('\x1b[!p', '');
}

// ---------- the plan ----------

const sequence = (operation, id, fields, terminator) => `${OSC}${operation}=${id}${fields.map(field => `;${field}`).join('')}${terminator}`;
const start = (id, fields = [], terminator = ST) => sequence('start', id, fields, terminator);
const end = (id, fields = [], terminator = ST) => sequence('end', id, fields, terminator);
const read = label => ({ read: label });
const rawOsc = (body, terminator = ST) => `${OSC}${body}${terminator}`;
const longText = (char, length) => char.repeat(length);

/** The distinguishable ids of one run: every experiment's contexts derive from the run identity so a stale stack cannot satisfy a read. */
const idsFor = runId => {
    const mark = runId.slice(0, 8);
    return name => `ctx-${mark}-${name}`;
};

/** The ids and field values the specification's own example uses, byte for byte. */
export const specExample = Object.freeze({ id: 'bed86fab93af4328bbed0a1224af6d40', fields: Object.freeze([
    'type=container', 'user=lennart', 'hostname=zeta', 'machineid=3deb5353d3ba43d08201c136a47ead7b', 'bootid=d4a3d0fdf2e24fdea6d971ce73f4fbf2', 'pid=1062862', 'pidfdid=1063162',
    'comm=systemd-nspawn', 'container=foobar']) });

/** `role` says what an experiment may decide: stock (the pinned fixture, expected to change nothing), required (determined by the specification text), control (a negative that must change
 * nothing) and policy (a documented implementation choice: compared and recorded, never deciding). `compareEnded: 'last'` also compares the most recent ended record. */
function experiment(fixture, name, role, steps, extra = {}) {
    return { caseId: `${osc3008CaseId}/${name}`, owner: osc3008CaseId, name, role, plan: [{ feed: fixture.stage }, ...steps.map(step => (typeof step === 'string' ? { feed: step } : step))], ...extra };
}

/** The fixed experiment plan, in execution order. Contexts cannot be cleared by a reset, so every experiment closes what it opened; the oracle replays the whole history. */
export function makeOsc3008Experiments(runId) {
    const fixture = osc133Fixture(runId), id = idsFor(runId);
    const [a, b, c, d] = ['a', 'b', 'c', 'd'].map(id);
    const unknownFields = ['nonsense=1', 'pid=not-a-number', 'type=hacker', 'user=', 'pidfdid=99999999999999999999999', 'bootid=zz', 'machineid=short', '=novalue', 'noequals'];
    return [
        experiment(fixture, 'stock', 'stock', [...osc3008PinnedWrites, read('pinned')]),
        experiment(fixture, 'minimal', 'required', [start(a), read('opened'), end(a), read('closed')]),
        experiment(fixture, 'spec-example', 'required', [start(specExample.id, specExample.fields), read('opened'), end(specExample.id), read('closed')]),
        experiment(fixture, 'nesting', 'required', [start(a, ['type=session', 'user=u-a']), start(b, ['type=shell', 'cwd=/ctx/shell']), start(c, ['type=command', 'cmdline=echo hi']), read('nested'),
            start(a, ['type=app', 'comm=updated']), read('updated'), end(a), read('closed')]),
        experiment(fixture, 'terminators', 'required', [start(a, ['type=shell'], BEL), start(b, ['type=command'], ST), read('opened'), end(b, [], BEL), read('inner-closed'), end(a, [], ST), read('closed')]),
        experiment(fixture, 'id-bounds', 'required', [start(a), start('k'.repeat(64)), start('k'.repeat(65)), start(''), start('café'), read('bounded'), end('k'.repeat(64)), end(a), read('closed')]),
        experiment(fixture, 'field-validity', 'required', [start(a, ['type=shell', 'user=lennart', ...unknownFields, `cwd=${longText('d', 255)}`, `comm=${longText('c', 256)}`,
            'cmdline=', `hostname=${longText('h', 255)}`, 'pid=18446744073709551615', 'pidfdid=18446744073709551616']), read('fields'),
            start(b, ['type=vm', 'vm=guest', 'machineid=3deb5353-d3ba-43d0-8201-c136a47ead7b', 'bootid=ABCDEFabcdef01234567890123456789', 'targetuser=root', 'targethost=h.example', 'sessionid=7']), read('typed'),
            end(b), end(a), read('closed')]),
        experiment(fixture, 'escapes', 'required', [start(a, ['cwd=/a\\x3bb\\x5cc', 'cmdline=echo \\x3b done', 'user=zoë', 'comm=日本語']), read('escaped'), end(a), read('closed')]),
        experiment(fixture, 'end-fields', 'required', [start(a, ['type=command']), end(a, ['exit=crash', 'status=139', 'signal=SIGSEGV', 'user=ignored', 'pid=9']), read('crash'),
            start(b, ['type=command']), end(b, ['exit=exploded', 'status=256', 'signal=segv']), read('invalid-fields'),
            start(c, ['type=command']), end(c, ['exit=success', 'status=0']), read('success')], { compareEnded: 'last' }),
        experiment(fixture, 'foreign-negatives', 'control', [start(a), ...osc3008PinnedWrites, rawOsc('start'), rawOsc('begin=x'), rawOsc('START=x'), rawOsc(''), rawOsc('end=never-started'),
            `\x1b]3007;start=x${ST}`, `\x1b]30080;start=x${ST}`, `\x1b]133;start=x${ST}`, `\x1b]633;start=x${ST}`, read('ignored'), end(a), read('closed')]),
        experiment(fixture, 'separate-from-shell-state', 'control', [`\x1b]133;A${BEL}`, `\x1b]133;B${BEL}`, start(a, ['type=shell', 'cwd=/ctx/not-the-cwd', 'comm=title-bait']), read('input-window'),
            `\x1b]133;C${BEL}`, `\x1b]133;D;0${BEL}`, read('finished'), end(a), read('closed')]),
        experiment(fixture, 'reset-resilience', 'required', [start(a, ['type=session']), start(b, ['type=shell']), '\x1b[!p', read('after-soft-reset'), end(b), end(a), read('closed')]),
        experiment(fixture, 'end-ends-subcontexts', 'policy', [start(a), start(b), start(c), end(b), read('b-ended'), end(c), end(b), end(a), read('closed')]),
        experiment(fixture, 'depth', 'policy', [...Array.from({ length: 17 }, (_, index) => start(id(`d${index}`))), read('full'), start(id('d15'), ['pid=999']), read('updated-at-depth'),
            end(id('d0')), read('closed'), ...Array.from({ length: 17 }, (_, index) => end(id(`d${16 - index}`))), read('cleaned')]),
        experiment(fixture, 'repeated-field', 'policy', [start(d, ['user=first', 'user=', 'user=last', 'pid=1', 'pid=oops']), read('repeated'), end(d), read('closed')]),
        experiment(fixture, 'bare-backslash', 'policy', [start(d, ['comm=bad\\q', 'user=kept']), start('bad\\id'), read('invalid-escape'), end(d), read('closed')]),
    ];
}

// ---------- pinned replay ----------

/** The pinned unmeasuredCellResult(position, feature) over the one-based cursor position a CPR reports, key order included. */
export function osc3008PinnedTerminalResult(cursor) {
    const [row, col] = cursor;
    return { pass: false, response: `${row};${col}`, note: osc3008PinnedNote, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query' } };
}

/** What the pinned code returns over the journaled stock read: it depends on the cursor alone. */
export function replayOsc3008PinnedStatic(cursor) {
    const position = [cursor[0] + 1, cursor[1] + 1];
    return { caseId: osc3008CaseId, termlessFunction: false, termless: null, terminal: osc3008PinnedTerminalResult(position), writes: [...osc3008PinnedWrites], cursor: position };
}
