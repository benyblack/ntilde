// These predicates assess the selected frozen semantic criterion. Existing
// independent receipt graders validate the underlying observations first.
import { digest, requireModern, same } from './modern-inputs.mjs';

const pass = observed => ({ conclusion: 'pass', observed, blocker: null });
const pending = blocker => ({ conclusion: 'inconclusive', observed: null, blocker });
const fail = observed => ({ conclusion: 'fail', observed, blocker: null });
const verdict = item => item?.contractResult?.outcome ?? item?.effectContract?.outcome ?? item?.effect?.outcome;
const rgb = color => color && [color.r, color.g, color.b];
const ESC = '\x1b';
function da1Index(raw) {
    for (const match of raw.matchAll(/\[\?[\d;]+c/g)) if (match.index > 0 && raw[match.index - 1] === ESC) return match.index - 1;
    return -1;
}
const csiPayload = reply => reply.startsWith(ESC + '[') ? reply.slice(2) : null;
const oscPayload = reply => reply.startsWith(ESC + ']') && reply.endsWith(ESC + String.fromCodePoint(92)) ? reply.slice(2, -2) : null;

/** A callback's complete events, query bytes and base64 copy must agree. */
export function checkedTrace(raw, id) {
    const value = raw.rawReplies[id];
    requireModern(typeof value === 'string', 'missing stock trace ' + id);
    const trace = JSON.parse(value);
    requireModern(Array.isArray(trace.queries) && trace.queries.length > 0 && Array.isArray(trace.writes) && Array.isArray(trace.events), 'incomplete trace ' + id);
    requireModern(same(trace.events.filter(event => event.kind === 'write').map(event => event.sequence), trace.writes), 'write order differs ' + id);
    requireModern(same(trace.events.filter(event => event.kind === 'query').map(({ kind, ...query }) => query), trace.queries), 'query order differs ' + id);
    for (const query of trace.queries) {
        requireModern(typeof query.raw === 'string' && Buffer.from(query.rawBase64, 'base64').toString('utf8') === query.raw, 'reply bytes differ ' + id);
        requireModern(query.reason === 'reply' && typeof query.match?.[0] === 'string' && query.raw.includes(query.match[0]), 'unqualified query ' + id);
        const barrier = da1Index(query.raw);
        requireModern(barrier < 0 || query.raw.indexOf(query.match[0]) <= barrier, 'reply follows the DA barrier ' + id);
    }
    return trace;
}

const rgbReply = code => new RegExp(String.raw`^${code};rgb:[a-f\d]{1,4}\/[a-f\d]{1,4}\/[a-f\d]{1,4}$`, 'i');
const queryPatterns = {
    'device.status-report': ['\x1b[5n\x1b[c', /^0n$/],
    'device.decrpm': ['\x1b[?7$p\x1b[c', /^\?7;([0-4])\$y$/],
    'device.xtwinops-18': ['\x1b[18t\x1b[c', /^8;([1-9]\d*);([1-9]\d*)t$/],
    'extensions.osc10-fg-color': ['\x1b]10;?\x07\x1b[c', rgbReply('10'), 'osc'],
    'extensions.osc11-bg-color': ['\x1b]11;?\x07\x1b[c', rgbReply('11'), 'osc'],
    'extensions.osc4-palette': ['\x1b]4;0;?\x07\x1b[c', rgbReply('4;0'), 'osc'],
    'extensions.osc12-cursor-color': ['\x1b]12;?\x07\x1b[c', rgbReply('12'), 'osc'],
};

function daGrade(rule, raw) {
    const value = raw.rawReplies[rule.id];
    requireModern(typeof value === 'string', 'missing DA bytes');
    let matched;
    if (rule.id === 'device.primary-da') matched = /^\?[\d;]+c$/.test(csiPayload(value) ?? '');
    else {
        const barrier = da1Index(value);
        const first = barrier > 0 ? value.slice(0, barrier) : '';
        const last = barrier > 0 ? value.slice(barrier) : '';
        matched = /^>\d+;\d+;\d+c$/.test(csiPayload(first) ?? '') && /^\?[\d;]+c$/.test(csiPayload(last) ?? '');
    }
    return matched ? pass({ raw: value }) : pending('The retained DA reply does not meet the frozen frame criterion.');
}

function plainQuery(rule, raw) {
    if (['device.primary-da', 'device.secondary-da'].includes(rule.id)) return daGrade(rule, raw);
    const trace = checkedTrace(raw, rule.id), spec = queryPatterns[rule.id];
    requireModern(spec && trace.queries.length === 1 && trace.queries[0].sequence === spec[0], 'wrong query fixture ' + rule.id);
    const reply = trace.queries[0].match[0], payload = spec[2] === 'osc' ? oscPayload(reply) : csiPayload(reply);
    const matched = payload === null ? null : spec[1].exec(payload);
    if (!matched) return pending('No frame matching the exact frozen query criterion.');
    return rule.id === 'device.decrpm' && matched[1] === '0' ? fail({ reply }) : pass({ reply });
}

function modeQueryGrade(rule, trace) {
    requireModern(trace.queries.length === 1 && trace.queries[0].sequence === `${ESC}[?${rule.mode}$p${ESC}[c`, 'wrong mode request');
    const reply = trace.queries[0].match[0], payload = csiPayload(reply);
    const match = payload === null ? null : new RegExp(String.raw`^\?${rule.mode};([0-4])\$y$`).exec(payload);
    if (!match || match[1] === '0') return pending('Mode recognition is not established by the returned status.');
    if (rule.mode === 6 && !['1', '2'].includes(match[1])) return pending('The origin-mode criterion requires a set/reset reply.');
    return match[1] === '4' ? fail({ reply }) : pass({ reply, scope: 'mode recognition only; event delivery and addressing effects not claimed' });
}

function keyboardQueryGrade(_rule, trace) {
    requireModern(same(trace.writes, [`${ESC}[>1u`, `${ESC}[<u`]), 'keyboard push/pop fixture differs');
    const query = trace.queries.at(-1), match = /^\?(\d+)u$/.exec(csiPayload(query.match[0]) ?? '');
    return match && (Number(match[1]) & 1) === 1 ? pass({ flags: Number(match[1]), scope: 'acknowledged flag 1, not key-event encoding' }) : pending('The retained acknowledgement does not include flag 1.');
}

function cursorReportGrade(rule, trace) {
    const position = trace.events.findIndex(event => event.kind === 'write' && event.sequence === `${ESC}[3;5H`);
    const query = position < 0 ? null : trace.events[position + 1];
    return query?.kind === 'query' && query.sequence === `${ESC}[6n` && query.match[0] === `${ESC}[3;5R`
        ? pass({ sourceId: rule.sourceId, request: `${ESC}[3;5H`, reply: query.match[0] }) : pending('The exact positioned DSR specimen is missing.');
}

function cursorRoundtripGrade(_rule, trace) {
    requireModern(same(trace.writes, [`${ESC}[5;10H`, `${ESC}[?1048h`, `${ESC}[15;20H`, `${ESC}[?1048l`]), '1048 fixture differs');
    const replies = trace.queries.map(query => query.match[0]);
    return same(replies, [`${ESC}[5;10R`, `${ESC}[15;20R`, `${ESC}[5;10R`]) ? pass({ replies }) : pending('Saved cursor restoration is not observed.');
}

const protocolGraders = { 'mode-query': modeQueryGrade, 'keyboard-query': keyboardQueryGrade,
    'cursor-report': cursorReportGrade, 'cursor-roundtrip': cursorRoundtripGrade };

function protocolGrade(rule, raw) {
    if (rule.kind === 'query') return plainQuery(rule, raw);
    requireModern(Object.hasOwn(protocolGraders, rule.kind), 'unknown protocol rule');
    return protocolGraders[rule.kind](rule, checkedTrace(raw, rule.sourceId ?? rule.id));
}

function cursorShapeGrade(_rule, item) {
    requireModern(same(item.pinnedFeeds, [`${ESC}[6 q`]), 'wrong stored cursor fixture');
    const group = item.observed.groups.find(value => value.id === 'termless');
    const target = group?.reads.find(value => value.role === 'measured')?.cursorMode;
    return target?.style === 1 && target.blink === false && target.isDefault === false
        ? pass({ target, scope: 'stored steady beam after CSI 6 SP q; native focus/capture criterion not claimed' }) : pending('The steady beam state was not observed.');
}

function underlineColorGrade(_rule, item) {
    requireModern(item.feeds.some(feed => feed.includes(`${ESC}[58:2::255:0:128m`)), 'missing empty-colorspace colon fixture');
    const target = item.observed.control.find(cell => cell.char === 'C'), sibling = item.observed.control.find(cell => cell.char === 'P');
    const reset = item.observed.fixture.find(cell => cell.char === 'B');
    const matches = same(rgb(target?.underlineColor), [255, 0, 128]) && target.underlineSource === 'literal'
        && target.underlineStyle === 1 && reset?.underlineSource === 'default' && reset.underlineColor === null
        && same(rgb(sibling?.underlineColor), [95, 135, 175]);
    return matches ? pass({ target, reset, sibling, scope: 'exact exposed colon-form underline RGB and default/sibling controls; painted color not claimed' }) : pending('The exact colon-form underline RGB and controls were not observed.');
}

function underlineGrade(rule, item) {
    const target = item.observed.target;
    return target?.char === 'X' && target.underlineStyle === rule.style && (target.flags & 8) === 8
        ? pass({ target }) : pending('The exact stored underline style is not observed.');
}

function overlineGrade(_rule, item) {
    const target = item.observed.target;
    return target?.char === 'X' && target.overline === true ? pass({ target }) : pending('The overline property is not observed.');
}

function brightGrade(rule, item) {
    const observed = item.observed, channel = rule.channel;
    const [neutral, first, second] = observed.fixture;
    const baseline = neutral[channel] ?? observed.defaults[channel];
    const matches = neutral.char === 'C' && first.char === 'X' && second.char === 'Y' && baseline && first[channel] && second[channel]
        && !same(rgb(baseline), rgb(first[channel])) && !same(rgb(first[channel]), rgb(second[channel]));
    return matches ? pass({ channel, baseline, first: first[channel], second: second[channel] }) : pending('Distinct baseline and named bright colors were not observed.');
}

function indexedGrade(rule, item) {
    const cells = item.observed.fixture, channel = rule.channel;
    const [a, b, first, second] = ['A', 'B', 'X', 'Y'].map(char => cells.find(cell => cell.char === char));
    const matches = same(rgb(a?.[channel]), [95, 135, 175]) && same(rgb(b?.[channel]), [215, 135, 95])
        && same(rgb(first?.[channel]), rgb(a[channel])) && same(rgb(second?.[channel]), rgb(b[channel]));
    return matches ? pass({ channel, first: first[channel], second: second[channel], referenceA: a[channel], referenceB: b[channel] }) : pending('Both canonical indexed-color references were not observed.');
}

function truecolorGrade(rule, item) {
    const channel = rule.channel, cells = item.observed.fixture;
    const first = cells.find(cell => cell.char === 'X'), second = cells.find(cell => cell.char === 'Y');
    const expected = channel === 'fg' ? [255, 128, 0] : [0, 255, 128];
    return same(rgb(first?.[channel]), expected) && same(rgb(second?.[channel]), [17, 97, 201])
        ? pass({ channel, first: first[channel], second: second[channel] }) : pending('Both exact truecolor specimens were not observed.');
}

const cellGraders = { 'cursor-shape': cursorShapeGrade, 'underline-color': underlineColorGrade, underline: underlineGrade,
    overline: overlineGrade, bright: brightGrade, indexed: indexedGrade, truecolor: truecolorGrade };

/** Evidence readers are supplied only after complete receipt rederivation. */
export function evaluateModern(rule, evidence) {
    if (rule.kind === 'blocked') return pending(rule.blocker);
    if (!rule.file) return protocolGrade(rule, evidence.raw);
    const report = evidence.read(rule.file), sourceId = rule.sourceId ?? rule.id;
    const item = report.cases?.find(value => value.caseId === sourceId) ?? (report.pinned?.probeId === sourceId ? report : null);
    requireModern(report.runId === evidence.runId && item, 'missing/cross-run source ' + sourceId);
    if (verdict(item) !== 'supported') return pending('The retained source contract does not support the selected semantic predicate.');
    if (rule.kind === 'contract') return pass({ sourceId, contractOutcome: verdict(item), pinnedFeeds: item.pinnedFeeds });
    if (rule.kind === 'insert-replace') return item.observed.seed.text.startsWith('ABC') && item.observed.inserted.text.startsWith('XABC')
        ? pass({ before: item.observed.seed, after: item.observed.inserted }) : pending('The ABC to XABC displacement is missing.');
    requireModern(Object.hasOwn(cellGraders, rule.kind), 'unknown stored-cell rule');
    return cellGraders[rule.kind](rule, item);
}

export function modernEvidenceReference(rule, evidence) {
    if (rule.kind === 'blocked') return null;
    const sourceId = rule.sourceId ?? rule.id;
    const value = rule.file ? evidence.read(rule.file) : evidence.raw.rawReplies[sourceId];
    return { file: rule.file ?? 'raw.json', sourceId, recordSha256: digest(JSON.stringify(value)) };
}
