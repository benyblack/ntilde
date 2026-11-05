// Local adapter for the pinned MIT-licensed CLI bundle. Probe definitions and
// upstream evidence validation are unchanged; this adds a distinct owner arm.
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { preparePrivateClipboardSource } from './private-clipboard-source.mjs';

export const upstreamBundleSha256 = 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6';
export const adapterId = 'ntilde-owned-macos-v1';
export const upstreamReceipt = Object.freeze({
    schemaVersion: 1,
    probeHash: '16a690f34c0e',
    collectorRevision: 'cf7d8759c7e637aa8dba1e429f3dddd1c0124158',
    manifestSha256: 'f3e3273478446ac50c942b7b4a7dc95a5da2252a8c23b2bada0acf329588f1d3',
    bundleSha256: upstreamBundleSha256,
});

export function validateCollectorReceipt(source, receipt) {
    const digest = createHash('sha256').update(source).digest('hex');
    if (digest !== upstreamBundleSha256
        || Object.entries(upstreamReceipt).some(([key, value]) => receipt?.[key] !== value)) {
        throw new Error('Collector receipt does not match the pinned suite and bundle digest.');
    }
}

export function adaptCollector(source, ownerModulePath, options) {
    const digest = createHash('sha256').update(source).digest('hex');
    if (digest !== upstreamBundleSha256) throw new Error('Unsupported collector bundle: expected terminfo.dev 4.0.0 pinned digest.');
    const privateMode = options !== undefined;
    if (privateMode && (!options || Array.isArray(options) || !Object.hasOwn(options, 'privateClipboardModulePath') || Object.keys(options).length !== 1
        || typeof options.privateClipboardModulePath !== 'string' || !options.privateClipboardModulePath))
        throw new Error('Invalid private collector options.');
    if (privateMode) source = preparePrivateClipboardSource(source).source;
    // A shebang is only valid on the first line; the adapter prepends an import.
    source = source.replace(/^#![^\n]*/, '// Upstream CLI shebang removed for module import.');
    function replaceOnce(before, after) {
        if (source.split(before).length !== 2) throw new Error('Pinned collector adapter seam changed.');
        source = source.replace(before, after);
    }
    replaceOnce('  let ownedTerminal;\n  if (clipboardReceipt && options.terminalAppOwner)',
        '  let ownedTerminal;\n  if (options.ntildeOwner) {\n    if (clipboardReceipt || options.terminalAppOwner) throw new Error("Conflicting Ntilde ownership");\n    ownedTerminal = await createNtildeOwnedTerminal(options.ntildeOwner, captureRunId, out);\n  }\n  if (clipboardReceipt && options.terminalAppOwner)');
    replaceOnce('program.parse();', '// CLI dispatch is disabled in the local adapter module.');
    let adapted = `import { verifyNtildeOutput as ntildeVerifyOutput } from ${JSON.stringify(pathToFileURL(ownerModulePath).href)};\n${source}

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
    if (!privateMode) return adapted;
    const replacePrivate = (before, after) => {
        if (adapted.split(before).length !== 2) throw new Error('Pinned private collector seam changed.');
        adapted = adapted.replace(before, after);
    };
    replacePrivate('import { verifyNtildeOutput as ntildeVerifyOutput }',
        'import { verifyNtildeOutput as ntildeVerifyOutput, requestOwner as ntildePrivateOwnerRequest }');
    adapted = `import { createPrivateClipboardWorker } from ${JSON.stringify(pathToFileURL(options.privateClipboardModulePath).href)};\n` + adapted;
    replacePrivate('  const owner = {\n    geometryAtGrant,',
        '  const clipboard = createPrivateClipboardWorker(frame => ntildePrivateOwnerRequest(assertion, "private-clipboard", { tty: binding.tabTty, frame }).then(reply => { if (reply.ownerPid !== binding.ownerPid) throw new Error("Private clipboard owner changed"); return reply.result; }), assertion.runId);\n'
        + '  const owner = {\n    clipboard,\n    geometryAtGrant,');
    replacePrivate('    async dispose() { verifiedTerminalOwners.delete(owner); }',
        '    async dispose() { try { await clipboard.finish(); } finally { verifiedTerminalOwners.delete(owner); } }');
    replacePrivate('    if (options.ownedTerminal?.clipboard && options.ownedTerminal.clipboard.profile !== "default") {',
        '    if (options.ownedTerminal?.clipboard && options.ownedTerminal.clipboard.profile !== "default"\n'
        + '        && ["extensions.osc52-clipboard", "extensions.osc52-write", "extensions.osc52-read"].includes(probe.id)) {');
    replacePrivate('context.withClipboardFixture = (work) => clipboard.withClipboardFixture(work, (event) => clipboardEvents.push(event));',
        'context.withClipboardFixture = (work) => clipboard.withClipboardFixture(probe.id, work, (event) => clipboardEvents.push(event));');
    replacePrivate('    batch.rawReplies["collector.terminalOwnership"] = ownedTerminal.summary;',
        '    batch.rawReplies["collector.terminalOwnership"] = ownedTerminal.summary;\n'
        + '  if (ownedTerminal?.clipboard) batch.rawReplies["collector.ntildePrivateClipboard"] = ownedTerminal.clipboard.summary;');
    return adapted;
}
