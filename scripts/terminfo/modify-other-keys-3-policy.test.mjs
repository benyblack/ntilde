// Policy audit only: execute both unchanged upstream paths, without adding a graded suite.
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, rmSync } from 'node:fs';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptCollector } from './collector.mjs';

testWithPinnedCollector('level 3 pinned paths remain inconclusive regardless of the declaration', async bundleUrl => {
    const source = readFileSync(bundleUrl, 'utf8');
    const derived = adaptCollector(source, new URL('./ownership.mjs', import.meta.url).pathname)
        + '\ninit_src();\nexport const auditedProbe = ALL_PROBES.find(item => item.id === "input.modify-other-keys-3");\n';
    const file = new URL(`./ntilde-mok3-policy-${process.pid}.mjs`, bundleUrl);
    try {
        writeFileSync(file, derived);
        const { auditedProbe } = await import(file.href);
        assert.equal(auditedProbe.id, 'input.modify-other-keys-3');
        const note = 'No generated input event and encoded report were validated';
        for (const declared of [false, true]) {
            const reads = [];
            const context = new Proxy({ capabilities: { extensions: { has(name) {
                reads.push(name);
                assert.equal(name, 'modifyOtherKeys');
                return declared;
            } } } }, { get(target, name) {
                assert.equal(name, 'capabilities', 'no fixture, mode, event, cell or child-byte access');
                return target[name];
            } });
            assert.deepEqual(auditedProbe.termless(context), {
                pass: false, response: JSON.stringify({ declaredModifyOtherKeys: declared }), note,
                observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note },
            });
            assert.deepEqual(reads, ['modifyOtherKeys']);
        }
        const terminal = new Proxy({}, { get() { assert.fail('the terminal path must not touch its context'); } });
        assert.deepEqual(await auditedProbe.term(terminal), {
            pass: false, note,
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none', note },
        });
    } finally { rmSync(file, { force: true }); }
});
