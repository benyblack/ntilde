// These predicates assess the selected frozen semantic criterion. Existing
// independent receipt graders validate the underlying observations first.
import { digest, requireModern, same } from './modern-inputs.mjs';

const pass = observed => ({ conclusion: 'pass', observed, blocker: null });
const pending = blocker => ({ conclusion: 'inconclusive', observed: null, blocker });
const fail = observed => ({ conclusion: 'fail', observed, blocker: null });
const verdict = item => item?.contractResult?.outcome ?? item?.effectContract?.outcome ?? item?.effect?.outcome;
const rgb = color => color && [color.r, color.g, color.b];
const ESC = '\x1b';
const da1 = /\x1b\[\?[0-9;]+c/;

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
        const barrier = query.raw.search(da1);
        requireModern(barrier < 0 || query.raw.indexOf(query.match[0]) <= barrier, 'reply follows the DA barrier ' + id);
    }
    return trace;
}

const queryPatterns = {
    'device.status-report': ['\x1b[5n\x1b[c', /^\x1b\[0n$/],
    'device.decrpm': ['\x1b[?7$p\x1b[c', /^\x1b\[\?7;([0-4])\$y$/],
    'device.xtwinops-18': ['\x1b[18t\x1b[c', /^\x1b\[8;([1-9]\d*);([1-9]\d*)t$/],
    'extensions.osc10-fg-color': ['\x1b]10;?\x07\x1b[c', /^\x1b\]10;rgb:[a-f\d]{1,4}\/[a-f\d]{1,4}\/[a-f\d]{1,4}\x1b\\$/i],
    'extensions.osc11-bg-color': ['\x1b]11;?\x07\x1b[c', /^\x1b\]11;rgb:[a-f\d]{1,4}\/[a-f\d]{1,4}\/[a-f\d]{1,4}\x1b\\$/i],
    'extensions.osc4-palette': ['\x1b]4;0;?\x07\x1b[c', /^\x1b\]4;0;rgb:[a-f\d]{1,4}\/[a-f\d]{1,4}\/[a-f\d]{1,4}\x1b\\$/i],
    'extensions.osc12-cursor-color': ['\x1b]12;?\x07\x1b[c', /^\x1b\]12;rgb:[a-f\d]{1,4}\/[a-f\d]{1,4}\/[a-f\d]{1,4}\x1b\\$/i],
};

function plainQuery(rule, raw) {
    if (['device.primary-da', 'device.secondary-da'].includes(rule.id)) {
        const value = raw.rawReplies[rule.id];
        requireModern(typeof value === 'string', 'missing DA bytes');
        const pattern = rule.id === 'device.primary-da' ? /^\x1b\[\?[0-9;]+c$/ : /^\x1b\[>[0-9]+;[0-9]+;[0-9]+c\x1b\[\?[0-9;]+c$/;
        return pattern.test(value) ? pass({ raw: value }) : pending('The retained DA reply does not meet the frozen frame criterion.');
    }
    const trace = checkedTrace(raw, rule.id), spec = queryPatterns[rule.id];
    requireModern(spec && trace.queries.length === 1 && trace.queries[0].sequence === spec[0], 'wrong query fixture ' + rule.id);
    const reply = trace.queries[0].match[0], matched = spec[1].exec(reply);
    if (!matched) return pending('No frame matching the exact frozen query criterion.');
    return rule.id === 'device.decrpm' && matched[1] === '0' ? fail({ reply }) : pass({ reply });
}

function modeQueryGrade(rule, trace) {
    requireModern(trace.queries.length === 1 && trace.queries[0].sequence === `${ESC}[?${rule.mode}$p${ESC}[c`, 'wrong mode request');
    const reply = trace.queries[0].match[0], match = new RegExp(`^${ESC}\\[\\?${rule.mode};([0-4])\\$y$`).exec(reply);
    if (!match || match[1] === '0') return pending('Mode recognition is not established by the returned status.');
    if (rule.mode === 6 && !['1', '2'].includes(match[1])) return pending('The origin-mode criterion requires a set/reset reply.');
    return match[1] === '4' ? fail({ reply }) : pass({ reply, scope: 'mode recognition only; event delivery and addressing effects not claimed' });
}

function keyboardQueryGrade(_rule, trace) {
    requireModern(same(trace.writes, [`${ESC}[>1u`, `${ESC}[<u`]), 'keyboard push/pop fixture differs');
    const query = trace.queries.at(-1), match = /^\x1b\[\?(\d+)u$/.exec(query.match[0]);
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

function cellGrade(rule, item) {
    const observed = item.observed;
    if (rule.kind === 'underline') return observed.target?.char === 'X' && observed.target.underlineStyle === rule.style
        && (observed.target.flags & 8) === 8 ? pass({ target: observed.target }) : pending('The exact stored underline style is not observed.');
    if (rule.kind === 'overline') return observed.target?.char === 'X' && observed.target.overline === true
        ? pass({ target: observed.target }) : pending('The overline property is not observed.');
    const cells = observed.fixture, channel = rule.channel;
    if (rule.kind === 'bright') {
        const [neutral, first, second] = cells;
        const baseline = neutral[channel] ?? observed.defaults[channel];
        const matches = neutral.char === 'C' && first.char === 'X' && second.char === 'Y' && baseline && first[channel] && second[channel]
            && !same(rgb(baseline), rgb(first[channel])) && !same(rgb(first[channel]), rgb(second[channel]));
        return matches ? pass({ channel, baseline, first: first[channel], second: second[channel] }) : pending('Distinct baseline and named bright colors were not observed.');
    }
    const byChar = text => cells.find(cell => cell.char === text);
    const first = byChar('X'), second = byChar('Y');
    if (rule.kind === 'indexed') {
        const a = byChar('A'), b = byChar('B');
        const matches = same(rgb(a?.[channel]), [95, 135, 175]) && same(rgb(b?.[channel]), [215, 135, 95])
            && same(rgb(first?.[channel]), rgb(a[channel])) && same(rgb(second?.[channel]), rgb(b[channel]));
        return matches ? pass({ channel, first: first[channel], second: second[channel], referenceA: a[channel], referenceB: b[channel] }) : pending('Both canonical indexed-color references were not observed.');
    }
    requireModern(rule.kind === 'truecolor', 'unknown stored-color rule');
    const expected = channel === 'fg' ? [255, 128, 0] : [0, 255, 128];
    return same(rgb(first?.[channel]), expected) && same(rgb(second?.[channel]), [17, 97, 201])
        ? pass({ channel, first: first[channel], second: second[channel] }) : pending('Both exact truecolor specimens were not observed.');
}

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
    return cellGrade(rule, item);
}

export function modernEvidenceReference(rule, evidence) {
    if (rule.kind === 'blocked') return null;
    const sourceId = rule.sourceId ?? rule.id;
    const value = rule.file ? evidence.read(rule.file) : evidence.raw.rawReplies[sourceId];
    return { file: rule.file ?? 'raw.json', sourceId, recordSha256: digest(JSON.stringify(value)) };
}
