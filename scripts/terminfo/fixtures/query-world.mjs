// Entirely synthetic App/process/PTY simulation. Never runtime or ownership evidence.
import { EventEmitter } from 'node:events';
import { initialInputScreen } from './input-journal.mjs';
import { queryWitnessPlan } from '../query-witness-cases.mjs';
import { OwnedQueryObserver, rejectQueryOwnerRequest } from '../query-capture.mjs';
import { collectQueries } from '../query-collector.mjs';

export class QueryInput extends EventEmitter {
    isRaw = false; readableEncoding = null; paused = true;
    isPaused() { return this.paused; }
    setRawMode(value) { this.isRaw = value; }
    resume() { this.paused = false; }
    pause() { this.paused = true; }
}
export function queryWorld({ priorChallenges = Array.from({ length: 110 }, (_, index) => [32768 + index * 2, 32769 + index * 2].join(',')), mutateReply, mutateState, mutateOwnership, mutateRpc, mutateAction, nextNonces, cursorVisible = true, binding: suppliedBinding, initialScreen, processFacts, transport = false, marginsReport = false, beforeDispatch, atStart = Date.UTC(2026, 10, 4, 12) } = {}) {
    const binding = suppliedBinding ?? { runId: 'a'.repeat(32), ownerPid: 42, workerPid: 43, uid: 1000, appExecutable: '/owned/app',
        workerExecutable: '/node', tty: '/dev/ttys100', profileName: 'fixture', paneId: '11111111-1111-1111-1111-111111111111' };
    const initial = initialScreen ? structuredClone(initialScreen) : initialInputScreen(10, 60); initial.cursorVisible = cursorVisible;
    const rows = initial.rows, cols = initial.cols;
    initial.attributeRuns = [{ count: rows * cols, flags: 768, fg: -1, bg: -1 }];
    let screen = structuredClone(initial), tick = 0, challenge = 0;
    const input = new QueryInput(), packets = [], ownerships = []; let ownerFailure;
    const specs = queryWitnessPlan({ rows, cols, top: 1, bottom: rows });
    const commands = new Map(specs.map(item => [item.request, item.id]));
    const nonce = 'NTILDE_QRY_' + binding.runId;
    const replyFor = id => {
        if (id === 'q07.device.tertiary-da') return '\x1bP!|00000000\x1b\\';
        if (id === 'q07.device.xtwinops-18') return '\x1b[8;' + screen.rows + ';' + screen.cols + 't';
        if (id === 'q12.decrqss.vertical-margins') return '\x1bP1$r' + (screen.bufferState.scrollTop + 1) + ';' + (screen.bufferState.scrollBottom + 1) + 'r\x1b\\';
        if (marginsReport && id === 'q12.decrqss.horizontal-margins') return '\x1bP1$r1;' + screen.cols + 's\x1b\\';
        if (id.startsWith('q12.decrqss.')) return '\x1bP0$r\x1b\\';
        if (id.startsWith('q12.xtgettcap.')) return '\x1bP0+r\x1b\\';
        return '';
    };
    const applyControl = command => {
        if (command === '\x1bc\x1b[?1049h' + nonce) {
            screen = structuredClone(initial); screen.cursorVisible = true; screen.lines[0] = nonce; screen.cursorCol = nonce.length; screen.bufferState.alternateScreen = true;
        } else if (command === '\x1b[2;6r\x1b[?6h') {
            Object.assign(screen.bufferState, { scrollTop: 1, scrollBottom: 5, originMode: true }); screen.cursorRow = 1; screen.cursorCol = 0;
        } else if (command === '\x1b[?6l\x1b[r\x1b[1;44H') {
            Object.assign(screen.bufferState, { scrollTop: 0, scrollBottom: rows - 1, originMode: false }); screen.cursorRow = 0; screen.cursorCol = 43;
        } else if (command === '\x1bc\x1b[?25' + (cursorVisible ? 'h' : 'l')) screen = structuredClone(initial);
        else return false;
        return true;
    };
    const output = { write(bytes, callback) {
        const command = Buffer.isBuffer(bytes) ? bytes.toString('ascii') : bytes;
        queueMicrotask(() => {
            try {
                const challengeFields = [...command.matchAll(/\x1b\[\?(\d+)\$p/g)];
                let reply = '';
                if (challengeFields.length === 2 && challengeFields.map(match => match[0]).join('') === command)
                    reply = challengeFields.map(match => '\x1b[?' + match[1] + ';0$y').join('');
                else {
                    packets.push(command);
                    if (!applyControl(command)) {
                        const id = commands.get(command); if (!id) throw new Error('Synthetic writer received an undeclared packet.');
                        reply = mutateReply?.(id, replyFor(id)) ?? replyFor(id); mutateState?.(id, screen);
                    }
                }
                if (reply.length) input.emit('data', Buffer.isBuffer(reply) ? reply : Buffer.from(reply));
                callback?.();
            } catch (error) { callback?.(error); }
        });
        return true;
    } };
    const attest = async () => {
        const fact = { at: new Date(atStart + tick++).toISOString(), tty: binding.tty,
            app: structuredClone(processFacts?.app ?? { pid: 42, ppid: 41, uid: 1000, tty: '??', executable: '/owned/app' }),
            worker: structuredClone(processFacts?.worker ?? { pid: 43, ppid: 42, uid: 1000, tty: 'ttys100', executable: '/node' }),
            appCommand: binding.appExecutable + ' --ntilde-terminfo-owner=' + binding.runId,
            workerCommand: binding.workerExecutable + ' --ntilde-terminfo-worker=' + binding.runId,
            terminalOutput: { tool: '/bin/stty', outputProcessing: false, description: rows + ' rows; ' + cols + ' columns;\n-opost' } };
        mutateOwnership?.(fact, ownerships.length); ownerships.push(structuredClone(fact)); return fact;
    };
    const rpc = async (_, method, params) => {
        let result;
        if (method === 'listSessions') result = { sessions: [{ paneId: binding.paneId, profileName: binding.profileName, kind: 'local', windowless: false, rows, cols }] };
        else if (method === 'readScrollback') result = structuredClone(screen.history);
        else if (method === 'readScreen') result = { ...structuredClone(screen), attributeLines: new Array(rows).fill('0300:-1/-1|'.repeat(cols)) };
        else throw new Error('Synthetic observer received an acting method.');
        return mutateRpc?.(method, result, params) ?? result;
    };
    const observer = new OwnedQueryObserver({ binding, attest, marginsReport, appData: '/not-a-real-app', rpc,
        endpoint: () => 'synthetic-only', priorChallenges });
    const owner = async (kind, ignored, extra = {}) => {
        const request = { kind, ...extra };
        try {
            beforeDispatch?.(request);
            const result = await observer.handle(request, () => 1000);
            const value = mutateAction?.(kind, result) ?? result;
            return transport ? { ok: true, ownerPid: binding.ownerPid, ...value } : value;
        } catch (error) {
            rejectQueryOwnerRequest({ authenticated: true, observer, error,
                request: { ...request, token: 'synthetic-capability-must-not-be-retained' }, reject: value => ownerFailure = value });
            throw error;
        }
    };
    return { observer, input, output, binding, packets, ownerships, ownerFailure: () => ownerFailure, screen: () => screen,
        collect: async () => {
            try { await collectQueries({ runId: binding.runId, owner, expectedOwnerPid: transport ? binding.ownerPid : undefined, input, output,
                nextNonces: nextNonces ?? (() => [42000 + challenge * 2, 42001 + challenge++ * 2]) }); }
            catch (error) { observer.abort(error); throw error; } // Synthetic coordinator, not a runtime hook.
        } };
}
