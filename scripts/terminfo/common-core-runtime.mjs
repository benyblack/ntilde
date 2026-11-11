import { execFileSync } from 'node:child_process';
import { isDeepStrictEqual } from 'node:util';
import { fileURLToPath } from 'node:url';

/** Execute only Git-verified upstream definitions and identity code over retained replies. */
export function derivePinnedRuntime(proof, subjects) {
    const input = {
        files: proof.files,
        subjects: subjects.map((subject) => {
            const raw = JSON.parse(subject.rawBytes);
            return { id: raw.subject.id, version: raw.subject.version, replies: raw.rawReplies };
        }),
    };
    const path = fileURLToPath(new URL('./replay-common-core-source.mjs', import.meta.url));
    return JSON.parse(
        execFileSync(process.execPath, [path], {
            input: JSON.stringify(input),
            encoding: 'utf8',
            timeout: 30000,
            maxBuffer: 4 * 1024 * 1024,
            stdio: ['pipe', 'pipe', 'pipe'],
        }),
    );
}

/** Require declared membership and successful identity receipts to agree with upstream replay. */
export function verifyRuntimeClaims(runtime, subjects) {
    if (runtime.identities.length !== subjects.length) throw new Error('Identity replay cardinality differs.');
    for (const [index, subject] of subjects.entries()) {
        const suite = JSON.parse(subject.sourceBytes).suite;
        if (!isDeepStrictEqual(suite.probes, runtime.probes)) {
            throw new Error('Recorded probe membership differs from retained executable definitions.');
        }
        if (!isDeepStrictEqual(suite.textClaims ?? [], runtime.textClaims)) {
            throw new Error('Recorded text-claim membership differs from retained definitions.');
        }
        if (runtime.textClaims.length && !isDeepStrictEqual(suite.textQualification, runtime.textQualification)) {
            throw new Error('Recorded text qualification differs from retained definitions.');
        }
        verifyRuntimeIdentity(runtime.identities[index], subject);
    }
}

/** Compare the exact upstream identity verdict and measured version against the receipt. */
function verifyRuntimeIdentity(observed, subject) {
    const raw = JSON.parse(subject.rawBytes),
        identity = JSON.parse(subject.identityBytes);
    if (
        observed.id !== raw.subject.id ||
        observed.version !== raw.subject.version ||
        observed.check?.ok !== true ||
        observed.check?.checked !== true ||
        !isDeepStrictEqual(observed.check, identity.check)
    ) {
        throw new Error('Retained terminal identity verdict differs from upstream reply replay.');
    }
}
