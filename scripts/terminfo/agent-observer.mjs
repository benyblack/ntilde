// Reads only the endpoint in the disposable app-data root. Never uses discovery
// from the user's normal app, and never exposes an acting method.
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { createConnection } from 'node:net';
import { join } from 'node:path';

export const snapshotHash = value => createHash('sha256').update(JSON.stringify(value)).digest('hex');

function privateEntry(path, kind, privateMode = true) {
    const entry = lstatSync(path);
    if (entry.isSymbolicLink() || entry.uid !== process.getuid() || !entry[kind]()
        || (privateMode && (entry.mode & 0o077) !== 0)) throw new Error('Invalid private observer endpoint.');
}

export function observerEndpoint(appData, ownerPid) {
    privateEntry(appData, 'isDirectory');
    const path = join(appData, 'agent-endpoint.json');
    // The descriptor can be 0644 inside its exclusively owned 0700 directory.
    privateEntry(path, 'isFile', false);
    const descriptor = JSON.parse(readFileSync(path, 'utf8'));
    const endpoint = join(appData, 'agent.sock');
    if (descriptor.version !== 1 || descriptor.pid !== ownerPid || descriptor.endpoint !== endpoint) {
        throw new Error('Observer descriptor does not identify the launched app.');
    }
    privateEntry(endpoint, 'isSocket');
    return endpoint;
}

export function agentRequest(endpoint, method, params, timeoutMs) {
    if (!['listSessions', 'readScreen'].includes(method)) throw new Error('Observer method is not read-only.');
    return new Promise((resolve, reject) => {
        const socket = createConnection(endpoint);
        let buffer = '', settled = false;
        const finish = (error, value) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer); socket.destroy();
            if (error) reject(error); else resolve(value);
        };
        const timer = setTimeout(() => finish(new Error('Observer response timed out.')), timeoutMs);
        socket.setEncoding('utf8');
        socket.on('error', error => finish(error));
        socket.on('connect', () => socket.write(JSON.stringify({ v: 1, id: 1, method, params }) + '\n'));
        socket.on('data', chunk => {
            buffer += chunk;
            if (Buffer.byteLength(buffer) > 512 * 1024) return finish(new Error('Oversized observer response.'));
            if (!buffer.includes('\n')) return;
            try {
                const frame = JSON.parse(buffer.slice(0, buffer.indexOf('\n')));
                if (frame.v !== 1 || frame.id !== 1 || frame.error || !frame.result) throw new Error('Observer refused the read.');
                finish(null, frame.result);
            } catch (error) { finish(error); }
        });
        socket.on('end', () => finish(new Error('Incomplete observer response.')));
    });
}

export function screenSnapshot(value) {
    if (!Number.isSafeInteger(value?.rows) || value.rows < 3 || value.rows > 128
        || !Number.isSafeInteger(value.cols) || value.cols < 48 || value.cols > 256
        || !Array.isArray(value.lines) || value.lines.length !== value.rows
        || !value.lines.every(line => typeof line === 'string' && line.length <= value.cols * 4)
        || !Number.isSafeInteger(value.cursorRow) || value.cursorRow < 0 || value.cursorRow >= value.rows
        || !Number.isSafeInteger(value.cursorCol) || value.cursorCol < 0 || value.cursorCol >= value.cols
        || typeof value.cursorVisible !== 'boolean') throw new Error('Invalid or unsupported observer geometry/state.');
    return { rows: value.rows, cols: value.cols, lines: [...value.lines],
        cursorRow: value.cursorRow, cursorCol: value.cursorCol, cursorVisible: value.cursorVisible };
}

export class OwnedCellObserver {
    constructor({ appData, ownerPid, profileName, nonce, rpc = agentRequest, endpoint = observerEndpoint }) {
        Object.assign(this, { appData, ownerPid, profileName, nonce, rpc, endpoint });
        this.snapshots = 0;
        this.frames = [];
    }

    async request(method, params, budget) {
        return this.rpc(this.endpoint(this.appData, this.ownerPid), method, params, budget());
    }

    async session(budget) {
        const { sessions } = await this.request('listSessions', {}, budget);
        if (!Array.isArray(sessions) || sessions.length !== 1) throw new Error('Owned fixture must have exactly one pane.');
        const pane = sessions[0];
        if (pane.kind !== 'local' || pane.windowless || pane.profileName !== this.profileName
            || !/^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(pane.paneId ?? '')
            || (this.paneId && pane.paneId !== this.paneId)) throw new Error('Owned fixture pane changed.');
        return pane;
    }

    async capture(budget) {
        if (++this.snapshots > 64) throw new Error('Cell snapshot budget exhausted.');
        const before = await this.session(budget);
        const screen = screenSnapshot(await this.request('readScreen', { paneId: before.paneId, includeAttributes: false }, budget));
        const after = await this.session(budget);
        if (before.paneId !== after.paneId || before.rows !== screen.rows || before.cols !== screen.cols
            || after.rows !== screen.rows || after.cols !== screen.cols
            || (this.baseline && (screen.rows !== this.baseline.rows || screen.cols !== this.baseline.cols))) {
            throw new Error('Owned fixture changed size or pane during observation.');
        }
        this.paneId ??= before.paneId;
        return screen;
    }

    async handle(kind, budget, barrier = null) {
        if (this.busy || this.restored) throw new Error('Cell observation is busy or already complete.');
        if (!['cell-start', 'cell-bind', 'cell-read', 'cell-finish'].includes(kind)) throw new Error('Unknown cell operation.');
        if (kind !== 'cell-start' && (typeof barrier !== 'string' || barrier.length > 128 || !/^\x1b\[\?[\d;]+c$/.test(barrier))) {
            throw new Error('Missing cell ordering barrier.');
        }
        this.busy = true;
        try {
            const result = await this.observe(kind, budget);
            this.frames.push({ index: this.frames.length, kind, paneId: result.paneId,
                barrier, screen: result.screen, sha256: snapshotHash(result.screen) });
            return result;
        } finally { this.busy = false; }
    }

    async observe(kind, budget) {
        if (kind === 'cell-start') {
            if (this.baseline) throw new Error('Cell fixture already started.');
            this.baseline = await this.capture(budget);
            return { screen: this.baseline, paneId: this.paneId };
        }
        if (!this.baseline) throw new Error('Cell fixture was not started.');
        if (kind !== 'cell-bind' && !this.bound) throw new Error('Cell fixture was not bound to worker output.');
        const screen = await this.capture(budget);
        if (kind === 'cell-bind') {
            this.bind(screen);
        }
        if (kind === 'cell-finish') {
            this.finish(screen);
        }
        return { screen, paneId: this.paneId };
    }

    bind(screen) {
        if (this.bound || screen.lines[0] !== this.nonce || screen.cursorRow !== 0 || screen.cursorCol !== this.nonce.length) {
            throw new Error('Worker output did not bind the observed pane.');
        }
        this.bound = true;
    }

    finish(screen) {
        if (snapshotHash(screen) !== snapshotHash(this.baseline)) throw new Error('Cell fixture main-screen restoration failed.');
        this.restored = true;
    }
}
