import assert from 'node:assert/strict';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { id, sentinel } from './modern-x11-osc5522-clipboard.mjs';
import { digest, pinnedBundle } from './unicode-publication-inputs.mjs';

function span(source, start, end) {
    const from = source.indexOf(start);
    const to = source.indexOf(end, from + start.length);
    assert.ok(from >= 0 && to > from && !source.includes(start, from + start.length), 'pinned OSC 5522 source span differs');
    return source.slice(from, to);
}

export function pinnedClipboardProbe(gzip) {
    const bundle = gunzipSync(gzip, { maxOutputLength: 1_000_000 });
    assert.equal(digest(bundle), pinnedBundle);
    const source = bundle.toString('utf8');
    const probe = span(source, 'function probe(id, termless, term, termObservationEvidence) {', '\nfunction readHyperlinkMetadata(');
    const queryOnly = span(source, 'function queryOnly(definition) {', '\nfunction oscReplyResult(');
    const unanswered = span(source, 'function unansweredQuery(reply, note) {', '\nfunction keyboardFlagsResult(');
    const grade = span(source, 'function clipboardProtocolResult(response) {', '\nfunction headlessClipboardRoundtrip(');
    const registration = span(source, '    {\n      ...queryOnly(probe("' + id + '",', '    {\n      ...probe("extensions.osc1-icon"').trim().slice(0, -1);
    const definition = runInNewContext(probe + queryOnly + unanswered + grade + '\n(' + registration + ')', { btoa }, { timeout: 1000 }); // NOSONAR: executes only the verified upstream callback and its verified helpers.
    assert.equal(definition.id, id);
    return definition;
}

export async function replayPinned(probe, sequence, raw) {
    let calls = 0;
    const terminal = await probe.term({ async queryWithSentinelOutcome(request, pattern) {
        calls++;
        assert.equal(request + sentinel, sequence);
        const match = pattern.exec(raw);
        return { match, raw, rawBase64: btoa(raw), reason: match ? 'reply' : 'sentinel' };
    } });
    assert.equal(calls, 1);
    // Feed only the observed query reply into the pinned parser-context callback.
    // The DA1 barrier came from the terminal-context transport, not the probe itself.
    const decrpm = raw.replace(/\x1b\[\?62;4;22c$/, '');
    const termless = probe.termless({ feedCapture(request) {
        assert.equal(request + sentinel, sequence);
        return decrpm;
    } });
    return { terminal: structuredClone(terminal), termless: structuredClone(termless) };
}
