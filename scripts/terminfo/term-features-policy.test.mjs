// Policy audit only: execute the unchanged upstream device.term-features probe, without adding a graded suite.
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, rmSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptCollector } from './collector.mjs';

testWithPinnedCollector('device.term-features stays inconclusive without touching the terminal, whatever is declared', async bundleUrl => {
    const source = readFileSync(bundleUrl, 'utf8');
    const derived = adaptCollector(source, fileURLToPath(new URL('./ownership.mjs', import.meta.url)))
        + '\ninit_src();\nexport const auditedProbe = ALL_PROBES.find(item => item.id === "device.term-features");\n'
        + 'export const auditedCount = ALL_PROBES.filter(item => item.id === "device.term-features").length;\n';
    const file = new URL(`./ntilde-term-features-policy-${process.pid}.mjs`, bundleUrl);
    const saved = process.env.TERM_FEATURES;
    try {
        writeFileSync(file, derived);
        const { auditedProbe, auditedCount } = await import(file.href);
        assert.equal(auditedCount, 1);
        assert.equal(auditedProbe.id, 'device.term-features');
        // The pinned registration carries no termless path: the terminal-mode function is the only executable code.
        assert.equal(auditedProbe.termless, null);
        assert.equal(auditedProbe.termObservationEvidence, 'legacy');
        assert.equal(typeof auditedProbe.term, 'function');
        // The probe reads this collector process's own environment and must never touch its terminal context:
        // the negative control (absent) and the declaration control (an encoded feature string) differ only in the note.
        for (const declared of [undefined, 'TSGoUwTsBFSyHNoSxP']) {
            if (declared === undefined) delete process.env.TERM_FEATURES;
            else process.env.TERM_FEATURES = declared;
            const terminal = new Proxy({}, { get() { assert.fail('the pinned probe must not touch its terminal context'); } });
            assert.deepEqual(await auditedProbe.term(terminal), {
                pass: false,
                note: declared === undefined ? 'TERM_FEATURES not advertised' : 'TERM_FEATURES advertised; source unverified',
                observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy' },
            });
        }
    } finally {
        if (saved === undefined) delete process.env.TERM_FEATURES;
        else process.env.TERM_FEATURES = saved;
        rmSync(file, { force: true });
    }
});
