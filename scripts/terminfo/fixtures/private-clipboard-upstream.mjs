// Immutable archive bytes are data; exact independent upstream digest is authority.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { upstreamBundleSha256 } from '../collector.mjs';
import { historicalArchive } from './captured-sources.mjs';
export const archived = historicalArchive('2026-11-05-query-observations').get('collector-adapter.mjs').toString();
/** Recover data-only original bytes, reversing only known adapter seams; the independent AA5f digest is authority. */
function recoverOriginal(archived) {
let original = archived.slice(archived.indexOf('\n') + 1, archived.lastIndexOf('\n// This capability is registered'));
original = original.replace('// Upstream CLI shebang removed for module import.', '#!/usr/bin/env bun')
    .replace('// CLI dispatch is disabled in the local adapter module.', 'program.parse();')
    .replace('  let ownedTerminal;\n  if (options.ntildeOwner) {\n    if (clipboardReceipt || options.terminalAppOwner) throw new Error("Conflicting Ntilde ownership");\n    ownedTerminal = await createNtildeOwnedTerminal(options.ntildeOwner, captureRunId, out);\n  }\n  if (clipboardReceipt && options.terminalAppOwner)',
        '  let ownedTerminal;\n  if (clipboardReceipt && options.terminalAppOwner)');
original = original.slice(0, -1); // The adapter adds one separator newline before its owner suffix.
assert.equal(createHash('sha256').update(original).digest('hex'), upstreamBundleSha256);
return original;
}
export const original = recoverOriginal(archived);
