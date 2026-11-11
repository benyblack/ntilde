// Offline child: initialize verified definitions, never execute a feature callback or launch an app.
import { readFileSync, mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';

/** Match upstream code-unit sorting without relying on host collation. */
function compareCodeUnits(a, b) {
    if (a < b) return -1;
    if (a > b) return 1;
    return 0;
}

const input = JSON.parse(readFileSync(0, 'utf8'));
const directory = mkdtempSync(join(tmpdir(), 'common-core-runtime-'));
try {
    for (const [name, encoded] of Object.entries(input.files)) {
        const definition = /^packages\/probe-defs\/src\/[a-zA-Z0-9._-]+\.ts$/.test(name);
        if (!definition && name !== 'packages/terminfo.dev/src/identity-guard.ts') continue;
        const path = join(directory, name);
        mkdirSync(dirname(path), { recursive: true });
        writeFileSync(path, Buffer.from(encoded, 'base64'), { flag: 'wx', mode: 0o600 });
    }
    writeFileSync(join(directory, 'package.json'), '{"type":"module"}\n', { flag: 'wx', mode: 0o600 });
    const definitions = await import(pathToFileURL(join(directory, 'packages/probe-defs/src/index.ts')).href);
    const checker = await import(pathToFileURL(join(directory, 'packages/terminfo.dev/src/identity-guard.ts')).href);
    const all = definitions.ALL_PROBES;
    if (new Set(all.map((probe) => probe.id)).size !== all.length) throw new Error('Duplicate executable probe ID.');
    const term = all
        .filter((probe) => probe.term !== null)
        .map((probe) => probe.id)
        .sort(compareCodeUnits);
    const lib = all
        .filter((probe) => probe.lib !== null)
        .map((probe) => probe.id)
        .sort(compareCodeUnits);
    const textClaims = all
        .filter((probe) => probe.term !== null && probe.textClaim === 'characters')
        .map((probe) => probe.id)
        .sort(compareCodeUnits);
    const identities = input.subjects.map((subject) => ({
        id: subject.id,
        version: checker.resolveMeasuredAppVersion(subject.id, subject.version, subject.replies),
        check: checker.verifyTerminalIdentity(subject.id, subject.replies),
    }));
    process.stdout.write(
        JSON.stringify({
            probes: { term, lib, mux: [...term] },
            textClaims,
            textQualification: definitions.TEXT_QUALIFICATION_SPEC,
            identities,
        }),
    );
} finally {
    rmSync(directory, { recursive: true, force: true });
}
