// The verbatim pinned colorResetProbe and its helpers (the extension's seams, which the derivation checks against the pinned bundle), loaded as a module so
// tests can run the real pinned code over recorded replies without evaluating strings.
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { colorResetSeams } from '../color-reset-grading-collector.mjs';

/** The pinned `colorResetProbe(setCode, resetCode[, index])` factory, as the pinned bundle defines it. */
export async function pinnedColorResetProbe() {
    const directory = mkdtempSync(join(tmpdir(), 'nt-pinned-color-reset-'));
    try {
        const file = join(directory, 'pinned.mjs');
        // Seams 4 to 8: colorResetProbe, sameRgb2, probeForeground, oscReplyResult and unmeasuredStateEffect.
        writeFileSync(file, colorResetSeams.slice(4, 9).join('\n') + '\nexport { colorResetProbe };\n');
        return (await import(pathToFileURL(file).href)).colorResetProbe;
    } finally { rmSync(directory, { recursive: true, force: true }); }
}
