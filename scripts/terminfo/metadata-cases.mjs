// Source-known private inputs. Expected effects are independent of captured metadata.
const osc = data => '\x1b]' + data + '\x07';
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
export const metadataSuiteId = 'ntilde-owned-pane-metadata-v1';
export const ownedMetadataPolicy = 'paired private main-screen cells, bounded host metadata and actual UI controls; prepared baseline restoration plus verified disposable teardown';
export function metadataFixture(runId) {
    if (!/^[a-f\d]{32}$/.test(runId ?? '')) throw new Error('Invalid metadata run identity.');
    const title = 'Owned metadata ' + runId, cwd = '/tmp/ntilde-owned-metadata-base';
    const nonce = 'NTILDE_MD_' + runId;
    const reset = osc('8;;') + osc('9;4;0') + osc('133;D;0') + osc('133;A')
        + '\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H'
        + osc('2;' + title) + osc('7;file://localhost' + cwd);
    return { title, cwd, nonce, reset, setup: reset + nonce, finish: reset };
}
/** Explicit control, assertions and restoration witnesses; exact P probes never imply cwd support. */
export function makeMetadataCases(rows, cols, runId) {
    if (!Number.isSafeInteger(rows) || rows < 10 || rows > 128 || !Number.isSafeInteger(cols)
        || cols < 48 || cols > 256 || rows * cols > 8192) throw Object.assign(new Error('Unsupported metadata fixture grid.'), { code: 'metadataGeometryUnavailable' });
    const fixture = metadataFixture(runId);
    const change = (id, sequence, expected, extra = {}) => ({ id: 'metadata.' + id, setup: fixture.setup, ...extra, stages: [
        { id: 'control', sequence: '', expected: { type: 'baseline' } },
        { id: 'observed', sequence, expected },
        { id: 'restored', sequence: fixture.setup, expected: { type: 'baseline' } },
    ] });
    const cases = [
        change('title.osc2', osc('2;Metadata OSC2 ' + runId), { type: 'title', title: 'Metadata OSC2 ' + runId }),
        change('title.osc0', osc('0;Metadata OSC0 ' + runId), { type: 'title', title: 'Metadata OSC0 ' + runId },
            { limitation: 'Title component only; no independent icon-name observation.' }),
        change('cwd.osc7', osc('7;file://localhost/tmp/ntilde%20owned/' + runId), { type: 'cwd', cwd: '/tmp/ntilde owned/' + runId }),
        change('links.osc8', at(1, 3) + osc('8;id=owned-' + runId + ';https://example.invalid/' + runId) + 'LINK' + osc('8;;') + 'x',
            { type: 'link', row: 1, col: 3, text: 'LINKx', uri: 'https://example.invalid/' + runId, id: 'owned-' + runId }),
        change('shell.lifecycle', '', { type: 'shell-a' }),
        change('shell.P.stock', osc('133;P;Cwd=/tmp'), { type: 'unqualified-p' },
            { qualification: 'inconclusive', limitation: 'Pinned consumption-only P;Cwd probe has no independently observed standard prompt-part effect; OSC133P is not OSC633P cwd.' }),
        change('shell.P.prompt-part', osc('133;P;k=r'), { type: 'unqualified-p' },
            { qualification: 'inconclusive', limitation: 'No independent prompt-part boundary readback. Unchanged cells/metadata are not semantic support.' }),
        ...[['normal', 1, 'Normal', 42], ['error', 2, 'Error', 42], ['paused', 4, 'Paused', 42], ['indeterminate', 3, 'Indeterminate', 0]]
            .map(([id, state, kind, percent]) => change('progress.' + id, osc(`9;4;${state};${percent}`), { type: 'progress', kind, percent: kind === 'Indeterminate' ? null : percent })),
        change('title.empty', osc('2;'), { type: 'title', title: '' }, { limitation: 'Explicit empty-title request; retained old title is a negative result.' }),
        change('notification.osc9', osc('9;Owned notification ' + runId), { type: 'notification', message: 'Owned notification ' + runId },
            { limitation: 'Actual private in-window toast only; focused suppression and lack of inactive positive control are reported explicitly.' }),
    ];
    const shell = cases.find(value => value.id === 'metadata.shell.lifecycle');
    shell.stages = [
        { id: 'control', sequence: '', expected: { type: 'baseline' } },
        { id: 'A', sequence: osc('133;A'), expected: { type: 'shell-a' } },
        { id: 'B', sequence: at(2, 4) + osc('133;B') + 'echo owned', expected: { type: 'shell-b', row: 2, col: 4, text: 'echo owned' } },
        { id: 'C', sequence: osc('133;C;ZWNobyBvd25lZA=='), expected: { type: 'shell-c', row: 2, col: 4, text: 'echo owned', command: 'echo owned' } },
        { id: 'D', sequence: osc('133;D;7'), expected: { type: 'shell-d', row: 2, col: 4, text: 'echo owned' } },
        { id: 'restored', sequence: fixture.setup, expected: { type: 'baseline' } },
    ];
    const notify = cases.at(-1);
    notify.stages.splice(2, 0, { id: 'auto-hidden', sequence: '', delayMs: 6500, expected: { type: 'notification-hidden', message: 'Owned notification ' + runId } });
    return cases;
}

/** Only the proved disposable worker executes the bounded fixed plan, serially. */
export async function collectMetadata({ config, fence, owner, sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)) }) {
    let initial;
    try { initial = await owner('metadata-start'); }
    catch (error) {
        if (['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable'].includes(error.code)) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const { pristineEditing } = await import('./editing-capture.mjs');
    if (!pristineEditing(initial.screen)) throw new Error('Metadata fixture requires a pristine private main screen.');
    const cases = makeMetadataCases(initial.screen.rows, initial.screen.cols, config.runId), fixture = metadataFixture(config.runId);
    try {
        await owner('metadata-bind', await fence.write(fixture.setup));
        for (const item of cases) for (const [index, stage] of item.stages.entries()) {
            const barrier = await fence.write((index === 0 ? item.setup : '') + stage.sequence);
            if (stage.delayMs) await sleep(stage.delayMs);
            await owner('metadata-read', barrier, { caseId: item.id, stageId: stage.id });
        }
        await owner('metadata-finish', await fence.write(fixture.finish));
    } catch (error) {
        try {
            if (fence.failed) await fence.writeCleanup(fixture.finish);
            else await fence.write(fixture.finish);
        } catch (cleanupError) { throw new AggregateError([error, cleanupError], 'Metadata collection and cleanup failed.'); }
        throw error;
    }
}
