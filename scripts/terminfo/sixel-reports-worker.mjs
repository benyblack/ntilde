import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { assertPrivateFile, requestOwner, verifyNtildeOutput } from './ownership.mjs';
import { withOwnedOutput } from './terminal-output.mjs';
import { rawInputCapture } from './input-bytes.mjs';
import { write } from './static-images-collector.mjs';
import { leave, planFor, prefix, queryWrites, requireSixel } from './sixel-reports-contracts.mjs';

async function measure(step, capture, send) {
    if (step.sample) {
        const samples = [];
        for (const requestBytes of step.queries ?? queryWrites) {
            const sample = await capture.capture(() => write(process.stdout, requestBytes)); // NOSONAR: serial PTY exchanges.
            samples.push({ requestBytes, sample });
        }
        await send(step.stage, { samples });
    } else if (step.write) {
        const sample = await capture.capture(() => write(process.stdout, step.write));
        await send(step.stage, { requestBytes: step.write, sample });
    } else {
        await send(step.stage);
    }
}

async function collect(runId, request, plan, stagePrefix) {
    const send = (stage, value) => request(stagePrefix + stage, value);
    await send('start');
    const capture = rawInputCapture(process.stdin, process.stdout);
    try {
        for (const step of plan(runId).slice(1, -1)) {
            await measure(step, capture, send); // NOSONAR: owner verifies each stage before the next.
        }
        requireSixel(!capture.failed, 'raw reply capture failed after the final fence');
        capture.dispose();
        await send('seal', { attempts: capture.lastAttempt });
    } catch (error) {
        capture.dispose();
        const failures = [error];
        await send('abort').catch(error_ => failures.push(error_));
        await write(process.stdout, leave).catch(error_ => failures.push(error_));
        if (failures.length > 1) throw new AggregateError(failures, 'Sixel report measurement and restoration failed.');
        throw error;
    } finally { capture.dispose(); }
}

async function run(config, plan, stagePrefix) {
    const { tabTty: tty, originalTerminalState: original } = await verifyNtildeOutput(config, process.stdout);
    const request = (kind, details) => requestOwner(config, kind, { ...details, tty });
    try {
        await withOwnedOutput({ tty, original, restoreInput: () => {} }, async () => {
            await request('raw-output');
            await collect(config.runId, request, plan, stagePrefix);
        });
        await request('done');
    } catch (error) {
        const message = error instanceof AggregateError ? error.errors.map(item => item.message).join('; ') : error.message;
        await request('failed', { error: message }).catch(() => {});
        throw error;
    }
}

export async function runReportWorker(plan, stagePrefix) {
    try {
        assertPrivateFile(process.argv[2]);
        await run(JSON.parse(readFileSync(process.argv[2], 'utf8')), plan, stagePrefix);
    } catch (error) {
        console.error(error.message);
        process.exitCode = 1;
    }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) await runReportWorker(planFor, prefix);
