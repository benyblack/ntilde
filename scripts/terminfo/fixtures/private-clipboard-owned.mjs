// Synthetic App/process/PTY only. Reads frozen screen DATA, never executes archive code.
import { createHash } from 'node:crypto';
import { historicalArchive } from './captured-sources.mjs';
import { OwnedPrivateClipboardObserver } from '../private-clipboard-capture.mjs';
import { createPrivateClipboardWorker, ownedPrivateClipboardSuiteId } from '../private-clipboard-worker.mjs';
import { PRIVATE_CLIPBOARD_PROBES } from '../private-clipboard-controller.mjs';
import { preparePrivateClipboardCallback } from '../private-clipboard-source.mjs';
import { stockClipboardCallback } from './private-clipboard-callback.mjs';

export const privateRunId = 'a'.repeat(32), privatePaneId = '11111111-1111-1111-1111-111111111111';
const fixtureId = '22222222-2222-2222-2222-222222222222';
const hash = value => createHash('sha256').update(value).digest('hex');
const baseline = JSON.parse(historicalArchive('2026-11-05-query-observations').get('query-initial.json')).before.screen;
const uuids = [1, 2, 3, 4].map(value => '33333333-3333-3333-3333-' + value.toString().padStart(12, '0'));
let fixtureSerial = 0;
export async function privateClipboardFixture(options = {}) {
    const selectedRun = options.binding?.runId ?? privateRunId, selectedSeed = 'terminfo-private-seed-' + selectedRun;
    let value = selectedSeed, count = 0, restored = false, armed, closed = false, ticks = 0;
    const binding = { runId: privateRunId, ownerPid: 4242, workerPid: 31337, uid: 501,
        appExecutable: '/synthetic/Ntilde', workerExecutable: '/synthetic/node', tty: '/dev/ttys123', profileName: 'private test profile', paneId: privatePaneId,
        sourceSnapshotManifestSha256: 'b'.repeat(64), executableSha256: 'c'.repeat(64), assemblySha256: 'd'.repeat(64),
        appBirth: 'Thu Nov  5 09:00:00 2026', workerBirth: 'Thu Nov  5 09:00:01 2026', outputDevice: { dev: '1', rdev: '2', ino: '3' }, ...options.binding };
    const api = { binding, screen: structuredClone(options.initialScreen ?? baseline), requests: [], ownerFrames: [], packets: [], callbackResults: [],
        onRpc: null, onCapture: null, onAttest: null, captures: 0, attestations: 0, queryTraces: [] };
    api.rpc = async request => {
        api.requests.push(structuredClone(request)); await api.onRpc?.(request);
        if (closed) throw new Error('Synthetic fixture closed');
        if (request.operation === 'armWrite') armed = request.value;
        if (request.operation === 'seed') { value = request.value; restored = false; }
        if (request.operation === 'restore') { value = selectedSeed; restored = true; armed = undefined; }
        if (request.operation === 'close') closed = true;
        const result = { fixtureId, state: closed ? 'closed' : 'active', valueSha256: hash(value), acceptedWrites: count,
            restored, complete: closed, ...(request.operation === 'read' ? { value } : {}) };
        return api.changeResult ? api.changeResult(result, request) : result;
    };
    api.observer = new OwnedPrivateClipboardObserver({ binding, appData: '/synthetic/appdata', endpoint: () => '/synthetic/agent.sock',
        rpc: (_endpoint, request) => api.rpc(request),
        capture: async () => { api.captures++; await api.onCapture?.(); return structuredClone(api.screen); },
        attest: async () => {
            api.attestations++;
            const fact = { app: { pid: binding.ownerPid, ppid: options.processFacts?.app.ppid ?? 42, uid: binding.uid, tty: options.processFacts?.app.tty ?? '??', executable: binding.appExecutable, birth: binding.appBirth },
                worker: { pid: binding.workerPid, ppid: binding.ownerPid, uid: binding.uid, tty: binding.tty.slice(5), executable: binding.workerExecutable, birth: binding.workerBirth },
                tty: binding.tty, at: new Date((options.atStart ?? Date.UTC(2026, 10, 5, 9, 0, 2)) + ticks++).toISOString(),
                appCommand: binding.appExecutable + ' --ntilde-terminfo-owner=' + selectedRun + ' --ntilde-terminfo-private-clipboard=' + selectedRun,
                workerCommand: binding.workerExecutable + ' /synthetic/worker.mjs --ntilde-terminfo-worker=' + selectedRun,
                terminalOutput: { tool: '/bin/stty', outputProcessing: false, description: 'speed 9600 baud; 40 rows; 140 columns; oflags: -opost onlcr' },
                sourceSnapshotManifestSha256: binding.sourceSnapshotManifestSha256, executableSha256: binding.executableSha256,
                assemblySha256: binding.assemblySha256, outputDevice: structuredClone(binding.outputDevice) };
            await api.onAttest?.(fact); return fact;
        } });
    api.worker = createPrivateClipboardWorker(async frame => {
        api.ownerFrames.push(structuredClone(frame));
        const result = await api.observer.handle(frame, () => 4000);
        if (frame.kind !== 'finish') return result;
        const bytes = JSON.stringify(api.observer.report, null, 2) + '\n';
        return { runId: selectedRun, suiteId: ownedPrivateClipboardSuiteId, file: 'private-clipboard-observations.json',
            sha256: hash(bytes), counts: { completedProbes: 3, acceptedWrites: 2 }, complete: true };
    }, selectedRun);
    const controls = 'let n=0;const randomUUID=()=>'+JSON.stringify(uuids)+'[n++];\n'
        + String.raw`const osc52Reply = /\x1b\]52;c;([A-Za-z0-9+/=]*)(?:\x07|\x1b\\)/;` + '\n';
    const { liveClipboardProbe } = await import('data:text/javascript;base64,'
        + Buffer.from('// fixture '+(++fixtureSerial)+'\n'+controls + preparePrivateClipboardCallback(stockClipboardCallback) + '\nexport { liveClipboardProbe };').toString('base64'));
    const runProbe = async (index, kind) => {
            const result = await liveClipboardProbe({ withClipboardFixture: work => api.worker.withClipboardFixture(PRIVATE_CLIPBOARD_PROBES[index], work),
                write: packet => {
                    api.packets.push(packet);
                    const encoded = '\x1b]52;c;' + btoa(armed) + '\x07';
                    if (!armed || packet !== encoded) throw new Error('Synthetic terminal write was not armed');
                    value = armed; armed = undefined; count++; restored = false;
                },
                queryCursorPosition: async () => ({ row: api.screen.cursorRow + 1, col: api.screen.cursorCol + 1 }),
                queryOutcome: async packet => {
                    api.queryTraces.push(packet);
                    const raw = '\x1b]52;c;\x1b\\'; return { raw, reason: 'reply', match: [raw, ''] }; // The pinned callback independently parses this exact synthetic denial.
                },
            }, kind);
            api.callbackResults.push(result);
    };
    api.run = async () => {
        await runProbe(0, 'roundtrip');
        await runProbe(1, 'write');
        await runProbe(2, 'read');
        await api.worker.finish();
        return api.observer.report;
    };
    return api;
}
