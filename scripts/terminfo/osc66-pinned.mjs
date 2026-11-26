import assert from 'node:assert/strict';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { parseCursorReport, cursorReportQuery } from './cursor-report.mjs';
import { digest, pinnedBundle } from './unicode-publication-inputs.mjs';

export const featureId = 'extensions.osc66-text-sizing';
const comparisonHash = '93aa59de71c37635fbc07caa1bb7787b3c2e96339809812e284e13a75bb90d24';

function span(source, start, end) {
    const from = source.indexOf(start), to = source.indexOf(end, from + start.length);
    assert.ok(from >= 0 && to > from && !source.includes(start, from + start.length), 'pinned code span differs');
    return source.slice(from, to);
}

export function pinnedOsc66(archive) {
    const bytes = gunzipSync(archive, { maxOutputLength: 1_000_000 });
    assert.equal(digest(bytes), pinnedBundle);
    const source = bytes.toString('utf8');
    const probe = span(source, 'function probe(id, termless, term, termObservationEvidence) {', '\nfunction readHyperlinkMetadata(');
    const result = span(source, 'function textSizingResult(', '\nfunction clipboardProtocolResult(');
    const definition = span(source, '    {\n      ...probe("' + featureId + '",', ',\n    {\n      ...queryOnly(probe("extensions.osc5522-clipboard"');
    const upstream = runInNewContext(probe + result + '\n(' + definition + ')', Object.create(null), { timeout: 1000 }); // NOSONAR: digest-pinned upstream code in isolation.
    assert.equal(upstream.id, featureId);
    assert.equal(upstream.termNeedsGeometry, true);
    return upstream;
}

/** Run the terminal callback over exact ordered writes and the replies the child received. */
export async function replayOsc66(upstream, rows, cols, exchanges) {
    let index = 0, awaitingReply = false;
    const verdict = await upstream.term({
        rows, cols,
        write(sequence) {
            assert.equal(awaitingReply, false);
            assert.equal(sequence, exchanges[index]?.write);
            awaitingReply = true;
        },
        queryCursorPosition() {
            assert.equal(awaitingReply, true);
            const exchange = exchanges[index++];
            assert.equal(exchange.query, cursorReportQuery);
            awaitingReply = false;
            if (exchange.raw === null) return null;
            const { row, col } = parseCursorReport(exchange.raw);
            return { row, col };
        },
    });
    assert.equal(awaitingReply, false);
    // The upstream callback stops after an absent or off-home initial reply.
    assert.ok(index === exchanges.length || (index === 1 && verdict.observation.outcome === 'inconclusive'));
    return structuredClone(verdict);
}

function historicalExchanges(trace) {
    assert.equal(trace.writes.length, 3);
    assert.equal(trace.queries.length, 3);
    assert.equal(trace.events.length, 6);
    return trace.queries.map((query, index) => {
        const write = trace.writes[index], { raw, match, sequence, reason, rawBase64 } = query;
        assert.deepEqual(trace.events[index * 2], { kind: 'write', sequence: write });
        assert.deepEqual(trace.events[index * 2 + 1], { kind: 'query', ...query });
        assert.equal(reason, 'reply');
        assert.equal(Buffer.from(raw).toString('base64'), rawBase64);
        const { row, col } = parseCursorReport(raw);
        assert.deepEqual(match, [raw, String(row), String(col)]);
        return { write, query: sequence, raw };
    });
}

export async function rederiveHistoricalOsc66(upstream, bytes) {
    assert.equal(digest(bytes), comparisonHash);
    const comparison = JSON.parse(bytes);
    const [change] = comparison.changes;
    assert.equal(change.featureId, featureId);
    const results = await Promise.all(['before', 'after'].map(async label => {
        const exchanges = historicalExchanges(change[label + 'Trace']);
        // The legacy comparison kept the exact writes and CPR, with 1×5 the
        // independently verified minimum needed for replay of this callback.
        const replay = await replayOsc66(upstream, 1, 5, exchanges);
        assert.equal(replay.observation.outcome, change[label].outcome);
        assert.equal(replay.observation.note, change[label].note);
        return [label, { sourceCommit: comparison[label].sourceCommit, outcome: replay.observation.outcome,
            replies: exchanges.map(exchange => exchange.raw) }];
    }));
    return { comparisonSha256: comparisonHash, ...Object.fromEntries(results) };
}
