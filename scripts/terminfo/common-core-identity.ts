// Apply the upstream app launcher's identity guard without rewriting its immutable capture.
import { readFileSync, writeFileSync, realpathSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
const [upstreamArg, runArg] = process.argv.slice(2);
if (!upstreamArg || !runArg) throw new Error('Usage: bun common-core-identity.ts <upstream> <completed-run>');
const upstream = realpathSync(upstreamArg), directory = resolve(runArg);
const rawBytes = readFileSync(join(directory, 'raw.json'));
const raw = JSON.parse(rawBytes.toString()), source = JSON.parse(readFileSync(join(directory, 'source.json'), 'utf8'));
const revision = String(execFileSync('git', ['rev-parse', 'HEAD'], { cwd: upstream })).trim();
if (revision !== source.upstreamRevision || revision !== raw.sourceRevision) throw new Error('Identity guard source differs from capture source.');
const checker = join(upstream, 'packages/terminfo.dev/src/identity-guard.ts');
const { verifyTerminalIdentity } = await import(pathToFileURL(checker).href);
const check = verifyTerminalIdentity(raw.subject.id, raw.rawReplies);
if (check.ok !== true || check.checked !== true) throw new Error('Upstream identity guard did not verify the intended application: ' + JSON.stringify(check));
writeFileSync(join(directory, 'identity-check.json'), JSON.stringify({ runId: raw.runId, subject: raw.subject,
    rawSha256: createHash('sha256').update(rawBytes).digest('hex'), upstreamRevision: revision,
    checkerSha256: createHash('sha256').update(readFileSync(checker)).digest('hex'), check,
    verifiedAt: new Date().toISOString() }, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
console.log('Upstream identity guard passed for ' + raw.subject.id);
