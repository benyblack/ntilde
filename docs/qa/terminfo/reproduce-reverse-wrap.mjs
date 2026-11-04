import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import vm from 'node:vm';
import assert from 'node:assert/strict';

const bundlePath = process.argv[2];
const bytes = await readFile(bundlePath);
const sha256 = value => createHash('sha256').update(value).digest('hex');
assert.equal(sha256(bytes), 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6');
const source = bytes.toString('utf8');
function between(start, end) {
    assert.equal(source.split(start).length, 2);
    const offset = source.indexOf(start);
    const limit = source.indexOf(end, offset);
    assert.ok(limit > offset);
    return source.slice(offset, limit);
}
const probeDefinition = between('function probe(id, termless, term, termObservationEvidence) {', '\nfunction readHyperlinkMetadata');
const callback = between('probe("cursor.reverse-wrap", (ctx) => {', ',\n    {\n      ...probe("cursor.cup-boundaries"');
const contextDefinition = between('function createTermContext({', '\nfunction selectAppProbes');
const sandbox = vm.createContext({
    query() { throw new Error('Unexpected real query'); },
    queryOutcome() { throw new Error('Unexpected real query'); },
    queryWithSentinel() { throw new Error('Unexpected real query'); },
    queryWithSentinelOutcome() { throw new Error('Unexpected real query'); },
    queryMode() { throw new Error('Unexpected real query'); },
});
const { selectedProbe, createContext } = vm.runInContext(`${probeDefinition}\n${contextDefinition}\n({selectedProbe: ${callback}, createContext: createTermContext})`, sandbox, { timeout: 1000 });
assert.equal(selectedProbe.id, 'cursor.reverse-wrap');
assert.equal(Object.hasOwn(selectedProbe, 'termNeedsGeometry'), false);
const cases = [];
for (const [label, item, geometry] of [
    ['original probe, supplied geometry', selectedProbe, { rows: 24, cols: 80 }],
    ['declared control, missing geometry', { ...selectedProbe, termNeedsGeometry: true }, undefined],
    ['declared control, insufficient geometry', { ...selectedProbe, termNeedsGeometry: true }, { rows: 1, cols: 80 }],
]) {
    const writes = [];
    const context = createContext({ probe: item, geometry, out: { write(value) { writes.push(value); } }, writes: [], events: [] });
    let result;
    try {
        result = { returned: JSON.parse(JSON.stringify(await item.term(context))) };
    } catch (error) {
        result = { error: { name: error.name, message: error.message } };
    }
    assert.equal(writes.length, 0);
    cases.push({ label, writes, ...result });
}
assert.equal(cases[0].error.name, 'UndeclaredTerminalGeometry');
assert.equal(cases[1].error.name, 'UndeclaredTerminalGeometry');
assert.equal(cases[2].returned.observation.outcome, 'inconclusive');
assert.equal(cases[2].returned.observation.reason, 'insufficient-evidence');
const declared = createContext({ probe: { ...selectedProbe, termNeedsGeometry: true }, geometry: { rows: 24, cols: 80 }, out: { write() { throw new Error('No writes allowed'); } } });
assert.equal(declared.rows, 24);
assert.equal(declared.cols, 80);
const result = {
    schemaVersion: 1,
    scope: 'Offline pinned-source reproduction; no terminal, App, subprocess, or GUI',
    bundleSha256: sha256(bytes),
    probeId: selectedProbe.id,
    termNeedsGeometryPresent: false,
    extractedSha256: { probeDefinition: sha256(probeDefinition), callback: sha256(callback), contextDefinition: sha256(contextDefinition) },
    cases,
    declaredGeometryGetterControl: { rows: declared.rows, cols: declared.cols },
    limitation: 'The declared control is a synthetic copy, not a modified upstream suite, owned collection, reverse-wrap support result, or completeness repair.',
};
console.log(JSON.stringify(result, null, 2));
