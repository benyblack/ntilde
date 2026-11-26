import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { tmpdir } from 'node:os';
import { digest, pinnedBundle, readJson } from './unicode-publication-inputs.mjs';

import { caseIds, da } from './sixel-reports-contracts.mjs';
const sources = Object.freeze([
    { file: '2026-11-04-reset-observations-evidence.zip', sha256: 'd3d85d29f5d90c17ef8dfb62739d94b29ce46018b258534c0c038e9fa3a7992d' },
    { file: '2026-11-16-static-images/owned-run.zip', sha256: '7d5ab79451faad63090f8ab29cffee27f6af94a7032a0a3ce0841811d18e3eed' },
]);

function span(source, start, end) {
    const from = source.indexOf(start), to = source.indexOf(end, from + start.length);
    assert.ok(from >= 0 && to > from && !source.includes(start, from + start.length), 'pinned source span differs');
    return source.slice(from, to);
}

export function pinnedSixelReports(gzip) {
    const bytes = gunzipSync(gzip, { maxOutputLength: 1_000_000 });
    assert.equal(digest(bytes), pinnedBundle);
    const source = bytes.toString('utf8');
    const probe = span(source, 'function probe(id, termless, term, termObservationEvidence) {', '\nfunction readHyperlinkMetadata(');
    const queryOnly = span(source, 'function queryOnly(definition) {', '\nfunction oscReplyResult(');
    const results = span(source, 'function sixelDa1Result(raw, frame) {', '\nfunction oscColorQueryProbe(');
    const reply = span(source, '  sixelDa1Reply = /', '\n  foregroundReply').trim();
    const registrations = span(source, '    {\n      ...queryOnly(probe("' + caseIds[0] + '",', '\n  ];\n});\n\n// ../probe-defs/src/input.ts');
    const probes = runInNewContext(probe + queryOnly + results + '\nlet ' + reply + '\n[' + registrations + ']', Object.create(null), { timeout: 1000 }); // NOSONAR: verified upstream source in isolation.
    assert.deepEqual(Array.from(probes, probe => probe.id), caseIds);
    return probes;
}

export async function replaySixelReport(probe, exchange) {
    let count = 0;
    const result = await probe.term({
        queryOutcome(command, pattern) { return query(command, pattern, false); },
        queryWithSentinelOutcome(command, pattern) { return query(command, pattern, true); },
    });
    function query(command, pattern, sentinel) {
        count++;
        assert.equal(command + (sentinel ? da : ''), exchange.sequence);
        assert.equal(Buffer.from(exchange.raw, 'ascii').toString('base64'), exchange.rawBase64);
        const match = exchange.reason === 'reply' ? pattern.exec(exchange.raw) : null;
        assert.deepEqual(match ? Array.from(match) : null, exchange.match);
        return { ...exchange, match };
    }
    assert.equal(count, 1);
    return structuredClone(result);
}

function retainedTrace(raw, probe) {
    const observation = raw.observations.find(row => row.featureId === probe.id);
    assert.equal(observation.rawReplyRef, probe.id);
    const trace = JSON.parse(raw.rawReplies[probe.id]);
    assert.deepEqual(trace.writes, []);
    assert.equal(trace.queries.length, 1);
    const [exchange] = trace.queries;
    assert.deepEqual(trace.events, [{ kind: 'query', ...exchange }]);
    return { exchange, observation };
}

async function rederiveHistory(qa, source, probes) {
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-sixel-history-'));
    try {
        extractOwnedArchive(qa, { archives: { 'owned-run.zip': source.sha256 } }, scratch, join(qa, source.file));
        const raw = readJson(scratch, 'raw.json'), proof = readJson(scratch, 'ownership-proof.json');
        assert.equal(proof.completed, true);
        assert.equal(raw.probeHash, '16a690f34c0e');
        const rows = await Promise.all(Array.from(probes, async probe => {
            const { exchange, observation } = retainedTrace(raw, probe);
            const result = await replaySixelReport(probe, exchange);
            assert.equal(result.observation.outcome, observation.outcome);
            assert.equal(result.observation.reason, observation.reason);
            return { id: probe.id, exchange, outcome: result.observation.outcome };
        }));
        return { ...source, runId: raw.runId, cases: rows };
    } finally { rmSync(scratch, { recursive: true, force: true }); }
}

export function rederiveSixelHistory(qa, probes) {
    return Promise.all(sources.map(source => rederiveHistory(qa, source, probes)));
}

export function readPinnedBundle(qa) {
    return pinnedSixelReports(readFileSync(join(qa, '2026-11-16-static-images/terminfo-dev-4.0.0.bundle.mjs.gz')));
}
