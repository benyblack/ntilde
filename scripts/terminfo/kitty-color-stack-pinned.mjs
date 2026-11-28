import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { da } from './sixel-reports-contracts.mjs';
import { caseIds, da1 } from './kitty-color-stack-contracts.mjs';
import { digest, pinnedBundle } from './unicode-publication-inputs.mjs';

function span(source, start, end) {
    const from = source.indexOf(start), to = source.indexOf(end, from + start.length);
    assert.ok(from >= 0 && to > from && !source.includes(start, from + start.length), 'pinned code span differs');
    return source.slice(from, to);
}

export function readPinnedProbes() {
    const archive = new URL('../../docs/qa/terminfo/2026-11-16-static-images/terminfo-dev-4.0.0.bundle.mjs.gz', import.meta.url);
    const bytes = gunzipSync(readFileSync(archive), { maxOutputLength: 1_000_000 });
    assert.equal(digest(bytes), pinnedBundle);
    const source = bytes.toString('utf8');
    const code = [
        span(source, 'function probe(id, termless, term, termObservationEvidence) {', '\nfunction readHyperlinkMetadata('),
        span(source, 'function foregroundValue(', '\nfunction sameRgbCells('),
        span(source, 'function probeForeground(', '\nfunction promptConsumptionResult('),
        span(source, 'function colorStackError(', '\nfunction osc720ScrollProbe('),
        span(source, 'function unansweredQuery(', '\nfunction keyboardFlagsResult('),
        span(source, 'var sixelDa1Reply, foregroundQuery = ', '\nvar init_extensions'),
        span(source, '  foregroundReply = /', '\n'),
        '[' + span(source, '    probe("' + caseIds[0] + '",', '    probe("extensions.osc176-app-id",') + ']',
    ].join('\n');
    const probes = runInNewContext(code, Object.create(null), { timeout: 1000 }); // NOSONAR: execute only SHA-256-pinned upstream code.
    assert.deepEqual(Array.from(probes, probe => probe.id), caseIds);
    assert.equal(probes[0].term, probes[1].term);
    assert.equal(probes[0].termless.toString(), probes[1].termless.toString());
    assert.ok(probes.every(probe => probe.termObservationEvidence === 'behavior'));
    return probes;
}

function contextFor(exchanges) {
    let calls = 0;
    const consume = (type, request) => {
        const exchange = exchanges[calls++];
        assert.equal(exchange?.type, type, 'pinned exchange kind or count changed');
        assert.equal(exchange.request, type === 'query' ? request + da : request, 'pinned request bytes changed');
        return exchange;
    };
    const write = sequence => { consume('write', sequence); };
    return {
        get calls() { return calls; },
        terminal: {
            write,
            queryWithSentinelOutcome(sequence, pattern) {
                const { raw, reply } = consume('query', sequence);
                assert.equal(raw, reply + da1);
                const match = pattern.exec(reply);
                return { raw, match, reason: match ? 'reply' : 'sentinel' };
            },
        },
        termless: { feed: write, feedCapture: sequence => consume('query', sequence).reply },
    };
}

export async function replay(probe, { id, exchanges }) {
    assert.equal(probe.id, id);
    const terminalContext = contextFor(exchanges), termlessContext = contextFor(exchanges);
    const terminal = await probe.term(terminalContext.terminal);
    const termless = probe.termless(termlessContext.termless);
    // The upstream termless callback catches some context errors, so require complete consumption and a real grade.
    assert.equal(terminalContext.calls, exchanges.length, 'pinned terminal replay was incomplete');
    assert.equal(termlessContext.calls, exchanges.length, 'pinned termless replay was incomplete');
    assert.notEqual(terminal.observation.outcome, 'error', terminal.note);
    assert.notEqual(termless.observation.outcome, 'error', termless.note);
    return { id, terminal: structuredClone(terminal), termlessReplay: structuredClone(termless) };
}
