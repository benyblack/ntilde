import { randomInt } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { rawInputCapture } from './input-bytes.mjs';
import { queryAction, queryPlan, queryPrior, queryPriorCounts, checkQueries as check } from './queries.mjs';

function write(output, bytes, signal) {
    return new Promise((resolve, reject) => {
        let settled = false;
        const finish = error => {
            if (settled) return;
            settled = true; clearTimeout(timer); signal.removeEventListener('abort', abort);
            if (error) reject(error); else resolve();
        };
        const abort = () => finish(signal.reason ?? new Error('Query write aborted.'));
        const timer = setTimeout(() => finish(new Error('Query write timed out.')), 1000);
        signal.addEventListener('abort', abort, { once: true });
        if (signal.aborted) return abort();
        try { output.write(bytes, finish); } catch (error) { finish(error); }
    });
}
/** Exclusive reader after the input/image seals. Only parent-admitted fixed actions may write. */
export async function collectQueries({ runId, owner, expectedOwnerPid, outputDirectory, input = process.stdin, output = process.stdout, nextNonces }) {
    check(typeof runId === 'string' && /^[a-f\d]{32}$/.test(runId) && typeof owner === 'function');
    const callOwner = async (...args) => {
        const reply = structuredClone(await owner(...args));
        if (expectedOwnerPid !== undefined) {
            check(reply.ok === true && reply.ownerPid === expectedOwnerPid); delete reply.ok; delete reply.ownerPid;
        }
        return reply;
    };
    const initial = await callOwner('queries-start');
    if (initial.status === 'unavailable') { check(initial.reason === 'queryPrerequisiteUnavailable'); return initial; }
    check(typeof initial.cursorVisible === 'boolean');
    const prior = new Set(queryPrior(initial.priorChallenges));
    check(queryPriorCounts.includes(prior.size));
    const choose = used => {
        for (let attempt = 0; attempt < 8; attempt++) {
            const pair = nextNonces ? structuredClone(nextNonces(new Set(used))) : [randomInt(32768, 65536), randomInt(32768, 65536)];
            if (Array.isArray(pair) && pair.length === 2 && [0, 1].every(i => Object.hasOwn(pair, i)
                && Number.isSafeInteger(pair[i]) && pair[i] >= 32768 && pair[i] <= 65535)
                && pair[0] !== pair[1] && !used.has(pair.join(',')) && !prior.has(pair.join(','))) return pair;
        }
        throw new Error('Global query challenge generation exhausted.');
    };
    const reader = rawInputCapture(input, output, 4500, choose); let attempts = 0;
    try {
        await queryPlan.reduce((previous, entry) => previous.then(async () => {
            const action = structuredClone(await callOwner('queries-admit', null, { index: entry.index }));
            const expected = queryAction(entry, { runId }, initial.cursorVisible);
            check(JSON.stringify(action) === JSON.stringify(expected));
            attempts++;
            const capture = await reader.capture(signal => write(output, Buffer.from(expected.commandHex, 'hex'), signal));
            await callOwner('queries-read', null, { index: entry.index, capture });
        }), Promise.resolve());
        reader.dispose();
        await callOwner('queries-seal', null, { captureStatus: { attempts, failed: reader.failed, disposed: true, lastAttempt: reader.lastAttempt } });
    } catch (error) {
        const failure = { attempts, failed: reader.failed, lastAttempt: reader.lastAttempt, error: String(error.message ?? error).slice(0, 1024) };
        if (outputDirectory) writeFileSync(join(outputDirectory, 'query-raw-failure.json'), JSON.stringify(failure) + '\n', { flag: 'wx', mode: 0o600 });
        throw Object.assign(new Error('Owned query collection failed.', { cause: error }), { queryFailure: structuredClone(failure) });
    } finally { reader.dispose(); }
}
