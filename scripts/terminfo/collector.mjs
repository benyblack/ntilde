// Local adapter for the pinned MIT-licensed CLI bundle. Probe definitions and
// upstream evidence validation are unchanged; this adds a distinct owner arm.
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';

export const upstreamBundleSha256 = 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6';
export const adapterId = 'ntilde-owned-macos-v1';

export function adaptCollector(source, ownerModulePath) {
    const digest = createHash('sha256').update(source).digest('hex');
    if (digest !== upstreamBundleSha256) throw new Error('Unsupported collector bundle: expected terminfo.dev 4.0.0 pinned digest.');
    // A shebang is only valid on the first line; the adapter prepends an import.
    source = source.replace(/^#![^\n]*/, '// Upstream CLI shebang removed for module import.');
    function replaceOnce(before, after) {
        if (source.split(before).length !== 2) throw new Error('Pinned collector adapter seam changed.');
        source = source.replace(before, after);
    }
    replaceOnce('  let ownedTerminal;\n  if (clipboardReceipt && options.terminalAppOwner)',
        '  let ownedTerminal;\n  if (options.ntildeOwner) {\n    if (clipboardReceipt || options.terminalAppOwner) throw new Error("Conflicting Ntilde ownership");\n    ownedTerminal = await createNtildeOwnedTerminal(options.ntildeOwner, captureRunId, out);\n  }\n  if (clipboardReceipt && options.terminalAppOwner)');
    replaceOnce('program.parse();', '// CLI dispatch is disabled in the local adapter module.');
    return `import { verifyNtildeOutput as ntildeVerifyOutput } from ${JSON.stringify(pathToFileURL(ownerModulePath).href)};\n${source}

// This capability is registered only after the local launcher and PTY proof.
// Existing upstream owners, policy checks and callback grading remain intact.
async function createNtildeOwnedTerminal(assertion, captureRunId, out) {
  init_owned_terminal();
  const binding = await ntildeVerifyOutput(assertion, out);
  const geometryAtGrant = await readBoundGeometry(binding);
  if (geometryAtGrant.status !== "measured") throw new Error(geometryAtGrant.diagnostic);
  const owner = {
    geometryAtGrant,
    geometrySource: geometrySource(binding),
    summary: JSON.stringify({ adapter: ${JSON.stringify(adapterId)}, binding, geometryAtGrant }),
    async readGeometry() {
      if (!verifiedTerminalOwners.has(owner)) throw new Error("Ntilde owner grant has expired");
      const current = await ntildeVerifyOutput(assertion, out);
      if (!sameDevice(current.outputDevice, binding.outputDevice)) throw new Error("Ntilde output device changed");
      return readBoundGeometry(current);
    },
    async dispose() { verifiedTerminalOwners.delete(owner); }
  };
  verifiedTerminalOwners.set(owner, { captureRunId, out });
  return owner;
}
export async function collectNtildeRun(assertion) {
  init_serve();
  return collectProbeRun({ ntildeOwner: assertion, out: process.stdout });
}
`;
}
