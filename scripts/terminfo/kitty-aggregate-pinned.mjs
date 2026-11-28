import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { caseIds } from './kitty-aggregate-owned.mjs';
import { digest, pinnedBundle } from './unicode-publication-inputs.mjs';

const [keyboard, graphics] = caseIds;
const da = '\x1b[c';

function span(source, start, end) {
    const from = source.indexOf(start), to = source.indexOf(end, from + start.length);
    assert.ok(from >= 0 && to > from && !source.includes(start, from + start.length));
    return source.slice(from, to);
}

function callbackSource(source, id) {
    const helper = span(source, 'function probe(id, termless, term, termObservationEvidence) {', '\nfunction readHyperlinkMetadata(');
    if (id === keyboard) {
        const constants = span(source, '  DA1_COMPLETE = ', '\n  osc52Reply = ');
        const methods = span(source, 'function kittyDetection(raw) {', '\nfunction graphicsQueryResult(');
        const registration = span(source, '    kittyKeyboardFlagProbe("extensions.kitty-keyboard",', '\n    kittyKeyboardFlagProbe("extensions.kitty-keyboard.disambiguate"').trim();
        return helper + methods + '\nlet DA1_COMPLETE, KITTY_FLAGS, KITTY_FLAGS_SHAPED, KITTY_FLAGS_TRUNCATED;\n'
            + constants + '\n' + registration.slice(0, -1);
    }
    const queryOnly = span(source, 'function queryOnly(definition) {', '\nfunction oscReplyResult(');
    const unanswered = span(source, 'function unansweredQuery(reply, note) {', '\nfunction keyboardFlagsResult(');
    const result = span(source, 'function graphicsQueryResult(response, imageId, acceptedNote', '\nfunction imageTransferRequest(');
    const registration = span(source, '    {\n      ...queryOnly(probe("extensions.kitty-graphics",',
        '\n    kittyImageTransferProbe("extensions.kitty-graphics.transmit"').trim();
    return helper + queryOnly + unanswered + result + '\n(' + registration.slice(0, -1) + ')';
}

const ascii = value => Buffer.from(value, 'hex').toString('ascii');

async function replayPath(source, id, rows, terminal) {
    let index = 0;
    function exchange(request, withSentinel) {
        const row = rows[index++];
        assert.ok(row, 'Pinned callback exhausted the retained exchanges');
        const sentinel = ascii(row.sentinelRequestHex);
        assert.equal(request, ascii(row.requestHex) + (withSentinel ? sentinel : ''));
        if (withSentinel) assert.equal(sentinel, da);
        return ascii(row.replyHex) + (withSentinel ? ascii(row.sentinelReplyHex) : '');
    }
    function write(request) {
        assert.equal(exchange(request, false), '');
    }
    const stdin = new EventEmitter();
    const context = {
        btoa, Buffer, process: { stdin }, setTimeout, clearTimeout,
        queryTraceContext: { getStore() {} },
        withTTYOperation: callback => callback(),
        currentTTYOutput: () => ({ write(request) {
            const raw = exchange(request, true);
            stdin.emit('data', Buffer.from(raw, 'ascii'));
        } }),
    };
    const transport = span(source, 'function matchResponse(pattern, timeoutMs, write, sentinel', '\nasync function queryWithSentinel(sequence,');
    const script = transport + '\n' + callbackSource(source, id);
    const definition = runInNewContext(script, context, { timeout: 1000 }); // NOSONAR: verified immutable upstream bundle, with a bounded fixture-only transport.
    assert.equal(definition.id, id);
    const result = terminal
        ? await definition.term({ queryWithSentinelOutcome: context.queryWithSentinelOutcome, write })
        : definition.termless({ feedCapture: request => exchange(request, request.endsWith(da)), feed: write });
    // On a detection failure the pinned keyboard callback correctly skips mutation. The retained later rows still belong to the independent owned check.
    assert.ok(index === rows.length || (id === keyboard && index === 1 && result.pass === false));
    return structuredClone(result);
}

export async function replayPinnedAggregates(archive, observations) {
    const bundle = gunzipSync(archive, { maxOutputLength: 1_000_000 });
    assert.equal(digest(bundle), pinnedBundle);
    const source = bundle.toString('utf8');
    const cases = await Promise.all(caseIds.map(async id => {
        const stages = id === keyboard ? ['detect', 'push', 'active', 'pop'] : ['query'];
        const rows = observations.filter(row => row.caseId === id && stages.includes(row.stage));
        assert.deepEqual(rows.map(row => row.stage), stages);
        const [terminal, termless] = await Promise.all([replayPath(source, id, rows, true), replayPath(source, id, rows, false)]);
        return [id, { terminal, termless }];
    }));
    return Object.fromEntries(cases);
}
