// The owned runner is a trusted launcher script with side effects at import, so it cannot be imported. This test reads its
// source and holds the request-kind dispatch expressions (SonarCloud S5843, "regular expression complexity") to the property
// that matters: they must stay anchored literal alternations that cannot backtrack. They run on `request.kind`, a string the
// parent-owned worker chooses from its compiled-in suite prefixes and sends over the token-authenticated owner socket.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync(new URL('../terminfo-owned-runner.mjs', import.meta.url), 'utf8');
const literals = [...source.matchAll(/(\/\^[^\n/]*\$\/)\.test\(request\.kind\)/g)].map(match => match[1]);
const expressions = literals.map(literal => new RegExp(literal.slice(1, -1)));
const alternatives = literal => [...literal.matchAll(/\(\?:([^()]*)\)/g)].map(match => match[1].split('|'));

test('the runner dispatches request kinds through four anchored expressions', () => {
    assert.equal(literals.length, 4, 'images, untranslated cell suites, resize and input');
    assert.ok(literals.every(literal => literal.startsWith('/^') && literal.endsWith('$/')));
});

test('no dispatch expression can backtrack: literal alternations only, no quantifier, no nested group', () => {
    for (const literal of literals) {
        const body = literal.slice(2, -2);
        assert.doesNotMatch(body.replaceAll('(?:', ''), /[*+?{}[\]\\.]/, `${literal} has a quantifier, class or wildcard`);
        assert.doesNotMatch(body, /\(\?:[^)]*\(/, `${literal} nests a group`);
        for (const group of alternatives(literal)) assert.ok(group.every(item => /^[a-z]+$/.test(item)), `${literal} alternates a non-literal`);
    }
});

test('the dispatch accepts exactly prefix-suffix pairs and rejects near misses', () => {
    const [images, cells, resize, input] = expressions;
    const [cellPrefixes, cellSuffixes] = alternatives(literals[1]);
    assert.ok(cellPrefixes.length >= 25 && cellPrefixes.includes('scrl') && cellPrefixes.includes('svrs'));
    for (const prefix of cellPrefixes) for (const suffix of cellSuffixes) assert.ok(cells.test(`${prefix}-${suffix}`), `${prefix}-${suffix}`);
    for (const miss of ['cell-apply', 'cell-start\n', 'cell-startx', 'xcell-start', 'cell-', '-start', 'cell-start-start', 'scrl', 'CELL-START', ' cell-start', 'cell-start ', '']) assert.equal(cells.test(miss), false, JSON.stringify(miss));
    assert.ok(images.test('images-seal') && !images.test('images-control') && !images.test('image-start'));
    assert.ok(resize.test('resize-abort') && !resize.test('resize-seal') && !resize.test('resize-start\n'));
    assert.ok(input.test('input-control') && !input.test('input-finish2') && !input.test('inputs-start'));
});

test('maximum and far larger inputs match in linear time and never hang', () => {
    for (const expression of expressions) {
        for (const kind of ['cell-'.repeat(2_000_000), `${'a'.repeat(4_000_000)}-start`, `cell-${'start'.repeat(1_000_000)}`, `${'-'.repeat(4_000_000)}`]) {
            const started = performance.now();
            assert.equal(expression.test(kind), false);
            assert.ok(performance.now() - started < 2000, 'anchored literal alternation of an overlong kind');
        }
    }
});
