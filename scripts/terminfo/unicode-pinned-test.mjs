import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { digest, pinnedBundle } from './unicode-publication-inputs.mjs';

/** Expose the original callbacks from the freshly installed, digest-checked test bundle. */
export async function pinnedUnicodeProbes(bundleUrl) {
    const bytes = readFileSync(bundleUrl);
    assert.equal(digest(bytes), pinnedBundle);
    const entry = 'program.parse();', source = bytes.toString('utf8');
    assert.equal(source.split(entry).length, 2);
    const adapted = new URL('./ntilde-unicode-replay.mjs', bundleUrl);
    const exports = '\ninit_src();\nexport { ALL_PROBES as ntildeUnicodeProbes };\n';
    writeFileSync(adapted, source.replace(entry, '// No CLI dispatch during replay.') + exports, { flag: 'wx' });
    return (await import(adapted.href)).ntildeUnicodeProbes;
}
