import assert from 'node:assert/strict';
import { appendFileSync, chmodSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, truncateSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { linkedTemplate } from './fixtures/linked-template.mjs';
import { privateClipboardReceiptFixture } from './fixtures/private-clipboard-receipt.mjs';

const content = { 'a.json': '{"a":1}\n', 'b.jsonl': 'one\ntwo\n', 'nested/c.bin': 'cccc', 'nested/deep/d.txt': 'dddd' };
/** Every file under a directory with its bytes, by relative path. */
function read(root, base = '') {
    return Object.fromEntries(readdirSync(join(root, base), { withFileTypes: true }).flatMap(entry => entry.isDirectory()
        ? Object.entries(read(root, `${base}${entry.name}/`)) : [[base + entry.name, readFileSync(join(root, base, entry.name), 'utf8')]]));
}
/** A template of `content`, and `use(action)` giving each action its own directory of it as the receipt fixture does.
 * Linked on every platform by default here, so the link path is tested where the fixture itself would copy. A file system
 * with whole-second modification times is copied to all the same (`links.linking` is false), and the tests then expect copies. */
function harness(action, options = { link: true }) {
    const root = mkdtempSync(join(tmpdir(), 'nt-linked-template-test-'));
    mkdirSync(join(root, 'nested/deep'), { recursive: true });
    for (const [path, text] of Object.entries(content)) writeFileSync(join(root, path), text);
    const links = linkedTemplate(root, options);
    const use = inner => {
        const output = mkdtempSync(join(tmpdir(), 'nt-linked-template-use-'));
        let release = () => {};
        try { release = links.materialize(output); return inner(output); }
        finally { try { release(); } finally { rmSync(output, { recursive: true, force: true }); } }
    };
    try { return action(use, root, links); } finally { links.remove(); rmSync(root, { recursive: true, force: true }); }
}

test('a use receives the whole template, as links wherever the file system can tell a write from its times', () => harness((use, root, links) => use(output => {
    assert.deepEqual(read(output), content);
    for (const path of Object.keys(content)) assert.equal(lstatSync(join(output, path)).nlink > 1, links.linking, `${path} is ${links.linking ? 'a link' : 'a copy'}`);
})));

for (const [name, write, expected] of [
    ['overwritten with longer bytes', path => writeFileSync(path, '{"a":1,"edited":true}\n'), '{"a":1,"edited":true}\n'],
    ['overwritten with bytes of the same length', path => writeFileSync(path, '{"a":2}\n'), '{"a":2}\n'],
    ['appended to', path => appendFileSync(path, 'more'), '{"a":1}\nmore'],
    ['truncated', path => truncateSync(path, 3), '{"a'],
]) {
    test(`a file ${name} in place is that use's own and is pristine again for the next use and in the template`, () => harness((use, root, links) => {
        use(output => {
            write(join(output, 'a.json'));
            assert.equal(readFileSync(join(output, 'a.json'), 'utf8'), expected, 'the use reads its own write');
        });
        assert.deepEqual(read(root), content, 'the template is repaired when the use ends');
        use(output => {
            assert.deepEqual(read(output), content);
            assert.equal(lstatSync(join(output, 'a.json')).nlink > 1, links.linking, 'the repaired file is linked again');
        });
    }));
}

test('a mode changed through a use is the template\'s original mode again for the next use', () => harness((use, root) => {
    const original = lstatSync(join(root, 'a.json')).mode;
    use(output => {
        chmodSync(join(output, 'a.json'), 0o444);
        assert.notEqual(lstatSync(join(output, 'a.json')).mode, original);
    });
    assert.equal(lstatSync(join(root, 'a.json')).mode, original, 'the template is repaired when the use ends');
    use(output => {
        assert.equal(lstatSync(join(output, 'a.json')).mode, original);
        writeFileSync(join(output, 'a.json'), 'still writable');
    });
    assert.deepEqual(read(root), content);
}));

test('after a repair that failed, no later use is handed the unrepaired template', t => harness((use, root, links) => {
    // A copy has nothing to repair, so there is no failure to provoke: that is the whole template on a file system it
    // copies to, and this one file where the link itself was refused (the first test reports that case).
    let linked = false;
    use(output => { linked = lstatSync(join(output, 'a.json')).nlink > 1; });
    if (!links.linking || !linked) return t.skip('this file was copied, not linked');
    // Removing the pristine copy mid-use makes the repair of the written file throw.
    assert.throws(() => use(output => { writeFileSync(join(output, 'a.json'), 'never repaired'); links.remove(); }));
    // The template may still hold that write, so the next use must not link it. With the pristine copy gone its copy fails;
    // what matters is that it is not given 'never repaired' as though it were the template.
    let seen = null;
    assert.throws(() => use(output => { seen = read(output); }));
    assert.equal(seen, null);
}));

test('an in-place write in a nested directory is repaired too', () => harness((use, root) => {
    use(output => writeFileSync(join(output, 'nested/deep/d.txt'), 'changed'));
    assert.deepEqual(read(root), content);
    use(output => assert.deepEqual(read(output), content));
}));

test('removing, replacing and adding files in a use never reaches the template or the next use', () => harness((use, root) => {
    use(output => {
        rmSync(join(output, 'b.jsonl'));
        rmSync(join(output, 'a.json')); writeFileSync(join(output, 'a.json'), 'replaced');
        rmSync(join(output, 'nested'), { recursive: true });
        writeFileSync(join(output, 'added.json'), '{}');
        assert.deepEqual(read(output), { 'a.json': 'replaced', 'added.json': '{}' });
        assert.deepEqual(read(root), content, 'the template is untouched while the use is still open');
    });
    use(output => assert.deepEqual(read(output), content));
}));

test('a use that starts while another is open is a copy and reads none of its writes', () => harness((use, root) => {
    use(first => {
        writeFileSync(join(first, 'a.json'), 'written before the second use starts');
        use(second => {
            assert.deepEqual(read(second), content, 'the second use starts from the pristine bytes, not the template the first wrote through');
            for (const path of Object.keys(content)) assert.equal(lstatSync(join(second, path)).nlink, 1, `${path} is a copy`);
            writeFileSync(join(second, 'b.jsonl'), 'second');
            writeFileSync(join(first, 'nested/c.bin'), 'written while the second use is open');
            assert.equal(readFileSync(join(second, 'nested/c.bin'), 'utf8'), 'cccc');
        });
        assert.equal(readFileSync(join(first, 'b.jsonl'), 'utf8'), 'one\ntwo\n', 'the first use reads none of the second one\'s writes');
        assert.equal(readFileSync(join(first, 'a.json'), 'utf8'), 'written before the second use starts');
    });
    assert.deepEqual(read(root), content);
    use(output => assert.deepEqual(read(output), content));
}));

test('a template told not to link hands out independent copies', () => harness((use, root) => {
    use(output => {
        assert.deepEqual(read(output), content);
        for (const path of Object.keys(content)) assert.equal(lstatSync(join(output, path)).nlink, 1, `${path} is a copy`);
        writeFileSync(join(output, 'a.json'), 'written to a copy');
        assert.deepEqual(read(root), content, 'the template never saw the write');
    });
    use(output => assert.deepEqual(read(output), content));
}, { link: false }));

test('a use that fails still repairs the template', () => harness((use, root) => {
    assert.throws(() => use(output => { writeFileSync(join(output, 'a.json'), 'then it throws'); throw new Error('the test failed'); }), /the test failed/);
    assert.deepEqual(read(root), content);
    use(output => assert.deepEqual(read(output), content));
}));

test('the receipt fixture gives each test pristine files after a test that wrote them in place', async () => {
    let before;
    await privateClipboardReceiptFixture(f => {
        before = { raw: readFileSync(join(f.output, 'raw.json')), proof: readFileSync(join(f.output, 'ownership-proof.json')) };
        writeFileSync(join(f.output, 'raw.json'), '{}');
        appendFileSync(join(f.output, 'ownership-proof.json'), ' ');
    });
    await privateClipboardReceiptFixture(f => {
        assert.ok(readFileSync(join(f.output, 'raw.json')).equals(before.raw));
        assert.ok(readFileSync(join(f.output, 'ownership-proof.json')).equals(before.proof));
        assert.equal(f.finish().fixtureRemoved, true, 'and the untouched fixture still completes its receipt');
    });
});
