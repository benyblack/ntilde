import assert from 'node:assert/strict';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { caseIds, maximumPinnedNumber } from './kitty-graphics-owned.mjs';
import { digest, pinnedBundle } from './unicode-publication-inputs.mjs';

function span(source, start, end) {
    const from = source.indexOf(start), to = source.indexOf(end, from + start.length);
    assert.ok(from >= 0 && to > from && !source.includes(start, from + start.length));
    return source.slice(from, to);
}

function loadDefinition(source, id, number) {
    const helper = span(source, 'function probe(id, termless, term, termObservationEvidence) {', '\nfunction readHyperlinkMetadata(');
    const unanswered = span(source, 'function unansweredQuery(reply, note) {', '\nfunction keyboardFlagsResult(');
    const graphics = span(source, 'function graphicsQueryResult(response, imageId, acceptedNote', '\nfunction kittyForegroundResult(');
    const prefix = '    kittyImageTransferProbe("' + id + '", ';
    const registration = span(source, prefix, '),').trim() + ')';
    // Reproduce the observed image number through the upstream randomUUID seam.
    const uuid = (number - 1).toString(16).padStart(8, '0') + '-0000-4000-8000-000000000000';
    const sandbox = { crypto: { randomUUID: () => uuid }, btoa };
    return runInNewContext(helper + unanswered + graphics + '\n' + registration, sandbox, { timeout: 1000 }); // NOSONAR: digest-pinned upstream functions with no external input.
}

async function replayPath(definition, rows, path) {
    let index = 0;
    function read(bytes) {
        const row = rows[index++];
        assert.equal(Buffer.from(bytes).toString('hex'), row?.replyEvidence.requestHex);
        return Buffer.from(row.replyEvidence.replyHex, 'hex').toString('ascii');
    }
    function cleanup(bytes) {
        assert.equal(rows[index]?.stage, 'delete');
        assert.equal(read(bytes), '');
    }
    const termless = { feedCapture: read, feed: cleanup };
    const terminal = { async queryWithSentinelOutcome(bytes, pattern) {
        const raw = read(bytes), match = pattern.exec(raw);
        return { raw, rawBase64: btoa(raw), match, reason: match ? 'reply' : 'sentinel' };
    }, write: cleanup };
    const result = await definition[path](path === 'termless' ? termless : terminal);
    // A missing upload ACK leads to no placement and no delete in the pinned
    // callbacks. The measured plan records these skipped stages as empty writes.
    assert.ok(rows.slice(index).every(row => row.replyEvidence.requestHex === '' && row.replyEvidence.replyHex === ''));
    return structuredClone(result);
}

async function replayCase(source, id, observations) {
    const rows = observations.filter(row => row.caseId === id);
    const upload = Buffer.from(rows[0]?.replyEvidence.requestHex ?? '', 'hex').toString('ascii');
    const number = Number(/,I=([1-9]\d*);/.exec(upload)?.[1]);
    assert.ok(Number.isSafeInteger(number) && number >= 1 && number <= maximumPinnedNumber);
    const definition = loadDefinition(source, id, number);
    assert.equal(definition.id, id);
    const [termless, terminal] = await Promise.all([replayPath(definition, rows, 'termless'), replayPath(definition, rows, 'term')]);
    return [id, { termless, terminal }];
}

export async function replayPinnedGraphics(archive, observations) {
    const bundle = gunzipSync(archive, { maxOutputLength: 1_000_000 });
    assert.equal(digest(bundle), pinnedBundle);
    const source = bundle.toString('utf8');
    return Object.fromEntries(await Promise.all(caseIds.map(id => replayCase(source, id, observations))));
}
