import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { requireIdentity } from './device-identity-contracts.mjs';

const reader = fileURLToPath(new URL('./device-identity-version.cs', import.meta.url));
export const assemblyFile = 'Ntilde.VT.dll';
export const metadataFile = 'vt-metadata.json';
export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

export function readAssembly(file) {
    const result = execFileSync('dotnet', ['run', '--disable-build-servers', '--file', reader, '--', file], {
        encoding: 'utf8', env: { ...process.env, DOTNET_CLI_USE_MSBUILD_SERVER: '0' }, timeout: 30000, maxBuffer: 16384,
    });
    const [hash, offset, blob] = result.trimEnd().split('\n');
    const evidence = { sha256: hash, offset: Number(offset), attributeBlob: blob };
    return { evidence, version: deriveVersion(readFileSync(file), evidence) };
}

// Decode the ECMA-335 custom attribute blob selected by the metadata reader.
export function deriveVersion(bytes, evidence) {
    const { sha256: hash, offset, attributeBlob } = evidence;
    const blob = Buffer.from(attributeBlob, 'base64');
    requireIdentity(bytes.length <= 16 * 1024 * 1024 && sha256(bytes) === hash
        && Number.isSafeInteger(offset) && offset > 0 && blob.toString('base64') === attributeBlob
        && blob.length > 5 && bytes.subarray(offset, offset + blob.length).equals(blob),
    'VT binary digest or retained metadata bytes differ');
    // The constrained build-version string fits the one-byte length form.
    requireIdentity(blob.readUInt16LE() === 1 && blob[2] === blob.length - 5 && blob[2] < 128
        && blob.readUInt16LE(blob.length - 2) === 0, 'unexpected informational version attribute encoding');
    const informationalVersion = new TextDecoder('utf-8', { fatal: true }).decode(blob.subarray(3, -2));
    requireIdentity(/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(informationalVersion),
        'unexpected VT informational version');
    return { assembly: assemblyFile, attribute: 'System.Reflection.AssemblyInformationalVersionAttribute',
        sha256: hash, informationalVersion, version: informationalVersion.split('+', 1)[0] };
}
