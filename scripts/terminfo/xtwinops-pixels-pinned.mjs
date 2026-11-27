import assert from 'node:assert/strict';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { da } from './sixel-reports-contracts.mjs';
import { digest, pinnedBundle } from './unicode-publication-inputs.mjs';
import { caseIds, requests } from './xtwinops-pixels-contracts.mjs';

function span(source, start, end) {
    const from = source.indexOf(start), to = source.indexOf(end, from + start.length);
    assert.ok(from >= 0 && to > from && !source.includes(start, from + start.length), 'pinned code span differs');
    return source.slice(from, to);
}

export function pinnedXtwinopsPixels(gzip) {
    const bytes = gunzipSync(gzip, { maxOutputLength: 1_000_000 });
    assert.equal(digest(bytes), pinnedBundle);
    const source = bytes.toString('utf8');
    const probe = span(source, 'function probe(id, termless, term, termObservationEvidence) {', '\nfunction readHyperlinkMetadata(');
    const helper = span(source, 'function deviceReplyResult(', '\nvar iconLabelReply,');
    const start = id => '    deviceQuery({\n      id: "' + id + '",';
    const registrations = span(source, start(caseIds[0]), start('device.xtwinops-18'));
    const probes = runInNewContext(probe + helper + '\n[' + registrations + ']', Object.create(null), { timeout: 1000 }); // NOSONAR: digest-pinned upstream code in isolation.
    assert.deepEqual(Array.from(probes, item => item.id), caseIds);
    return probes;
}

function pinnedExchangeIndex(probe, exchange) {
    const index = caseIds.indexOf(probe.id);
    assert.ok(index >= 0 && exchange.id === probe.id);
    return index;
}

export function replayXtwinopsPixelsTermless(probe, exchange) {
    const index = pinnedExchangeIndex(probe, exchange);
    let calls = 0;
    const termless = probe.termless({
        feedCapture(sequence) {
            calls++;
            assert.equal(sequence, requests[index]);
            assert.equal(sequence + da, exchange.request);
            const sentinel = /\x1b\[\?[0-9;]+c$/.exec(exchange.raw);
            assert.ok(sentinel);
            assert.equal(exchange.raw, exchange.reply + sentinel[0]);
            return exchange.reply;
        },
    });
    assert.equal(calls, 1);
    return structuredClone(termless);
}

export async function replayXtwinopsPixels(probe, exchange) {
    const index = pinnedExchangeIndex(probe, exchange);
    let calls = 0;
    const terminal = await probe.term({
        queryWithSentinelOutcome(sequence, pattern) {
            calls++;
            assert.equal(sequence, requests[index]);
            assert.equal(sequence + da, exchange.request);
            // The retained DA1 terminated the exchange. A match after it
            // cannot turn silence into a pinned success.
            const sentinel = /\x1b\[\?[0-9;]+c/.exec(exchange.raw);
            assert.ok(sentinel);
            const match = pattern.exec(exchange.raw.slice(0, sentinel.index));
            return { raw: exchange.raw, match, reason: match ? 'reply' : 'sentinel' };
        },
    });
    assert.equal(calls, 1);
    return structuredClone(terminal);
}
