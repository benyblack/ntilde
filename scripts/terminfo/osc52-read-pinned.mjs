import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { digest, pinnedBundle } from './unicode-publication-inputs.mjs';
import { id, uuidPattern } from './modern-x11-osc52-read.mjs';

const path = new URL('../../docs/qa/terminfo/2026-11-16-shell-integration-aggregates/terminfo-dev-4.0.0.bundle.mjs.gz', import.meta.url);

function span(source, start, end) {
    const from = source.indexOf(start);
    const to = source.indexOf(end, from + start.length);
    assert.ok(from >= 0 && to > from && !source.includes(start, from + start.length), 'pinned OSC 52 read source span differs');
    return source.slice(from, to);
}

export async function replayPinned(uuid, { queryHex, replyHex }, bundle = readFileSync(path)) {
    assert.match(uuid, uuidPattern);
    assert.match(replyHex, /^(?:[a-f\d]{2}){0,1024}$/);
    const bytes = gunzipSync(bundle, { maxOutputLength: 1_000_000 });
    assert.equal(digest(bytes), pinnedBundle, 'wrong pinned collector bundle');
    const source = bytes.toString('utf8');
    const probe = span(source, 'function probe(id, termless, term, termObservationEvidence) {', '\nfunction readHyperlinkMetadata(');
    const helpers = span(source, 'function headlessClipboardRoundtrip(', '\nvar sixelDa1Reply,');
    const replyPattern = span(source, '  osc52Reply = ', '\n  extensionsProbes =');
    const registration = span(source, '    {\n      ...probe("' + id + '",', '\n    oscColorQueryProbe(').trim().slice(0, -1);
    const definition = runInNewContext(probe + helpers + '\nlet osc52Reply;\n' + replyPattern + '\n(' + registration + ')',
        { randomUUID: () => uuid, btoa, atob }, { timeout: 1000 }); // NOSONAR: the source and all helper spans come from the digest-verified bundle.
    assert.equal(definition.id, id);

    const steps = [];
    const result = await definition.term({
        withClipboardFixture: callback => callback({
            async writeText(text) {
                steps.push('seed');
                assert.equal(text, 'terminfo-osc52-' + uuid);
            },
        }),
        async queryOutcome(request, pattern) {
            steps.push('query');
            assert.equal(Buffer.from(request, 'latin1').toString('hex'), queryHex);
            const raw = Buffer.from(replyHex, 'hex').toString('latin1');
            const match = pattern.exec(raw);
            return { raw, match, rawBase64: btoa(raw), reason: match ? 'reply' : 'timeout' };
        },
    });
    assert.deepEqual(steps, ['seed', 'query']);
    return structuredClone(result);
}
