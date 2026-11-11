import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { rawInputCapture } from './input-bytes.mjs';
import { inputReset } from './input-cases.mjs';
import { inputPlanV1 } from './input-plan.mjs';
import { defaultInputModes, inputFinalRestore, inputModeQueries, inputModeReplyHex } from './input-state.mjs';
const write = (output, value) => new Promise((resolve, reject) => {
    let settled = false;
    const finish = error => {
        if (settled) return;
        settled = true; clearTimeout(timer);
        if (error) reject(error); else resolve();
    };
    const timer = setTimeout(() => finish(new Error('Input fixture output write timed out.')), 1000);
    try { output.write(value, finish); } catch (error) { finish(error); }
});

/** Last owned suite: one exclusive raw reader, no retries, original fence restores PTY modes in outer finally. */
export async function collectInput({ config, fence, owner, input = process.stdin, output = process.stdout, plan = inputPlanV1 }) {
    let initial;
    try { initial = await owner('input-start'); }
    catch (error) {
        if (['screenDetailsUnavailable', 'inputGeometryUnavailable'].includes(error.code)) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const cases = plan.make(initial.screen.rows, initial.screen.cols), controls = {};
    fence.detachInput();
    const capture = rawInputCapture(input, output);
    let attempts = 0;
    const take = async action => { attempts++; return capture.capture(action); };
    const tell = (kind, sample, extra = {}) => owner(kind, null, { ...extra, capture: sample });
    try {
        await tell('input-bind', await take(() => write(output, '\x1b[?1049h\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[HNTILDE_IN_' + config.runId)));
        await cases.reduce((previous, item) => previous.then(async () => {
            const control = await take(() => write(output, item.setup + inputModeQueries));
            if (control.hex !== inputModeReplyHex(item.modes)) throw new Error('Independent input-mode control failed before dispatch.');
            controls[item.id] = control.hex;
            await tell('input-control', control, { caseId: item.id });
            const sample = await take(() => owner('input-apply', null, { caseId: item.id }));
            await tell('input-read', sample, { caseId: item.id });
        }), Promise.resolve());
        const restored = await take(() => write(output, inputReset + inputFinalRestore + inputModeQueries));
        if (restored.hex !== inputModeReplyHex(defaultInputModes)) throw new Error('Input modes were not restored.');
        await tell('input-finish', restored);
        capture.dispose();
        await owner('input-seal', null, { captureStatus: { failed: capture.failed, disposed: true, attempts, lastAttempt: capture.lastAttempt } });
        // Child-received DECRQM replies of every mode control and of the restored state, for a layer that replays pinned code over them.
        return { status: 'complete', controls, restored: restored.hex, geometry: { rows: initial.screen.rows, cols: initial.screen.cols } };
    } catch (error) {
        writeFileSync(join(config.output, 'input-raw-failure.json'), JSON.stringify({ attempts, failed: capture.failed, lastAttempt: capture.lastAttempt,
            error: error instanceof Error ? error.message : String(error) }) + '\n', { flag: 'wx', mode: 0o600 });
        try { await write(output, inputReset + inputFinalRestore); }
        catch (error_) { throw new AggregateError([error, error_], 'Input collection and output cleanup failed.'); }
        throw error;
    } finally { capture.dispose(); }
}
