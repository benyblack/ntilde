// Reads only the endpoint in the disposable app-data root. Never uses discovery
// from the user's normal app, and never exposes an acting method.
import { copyBufferState, copyHistory } from './editing-state.mjs';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { createConnection } from 'node:net';
import { join } from 'node:path';
import { copyAttributeRuns, readAttributeRuns } from './attributes.mjs';
import { copyCellDetails } from './cell-details.mjs';
import { copyColorBinding, copyResolvedColors } from './resolved-colors.mjs';

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

/** Read one correlated response from the private read-only endpoint; preserve typed refusals. */
export function agentRequest(endpoint, method, params, timeoutMs) {
    if (!['listSessions', 'readScreen', 'readScrollback', 'readPaneMetadata', 'getSessionStatus'].includes(method)) throw new Error('Observer method is not read-only.');
    return privateRequest(endpoint, method, params, timeoutMs, 512 * 1024);
}

/** Fixed resize action for the parent-proved disposable pane; read-only observers never call it. */
export function ownedResizeRequest(endpoint, paneId, rows, cols, timeoutMs) {
    if (!/^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(paneId ?? '')
        || !Number.isSafeInteger(rows) || rows < 1 || rows > 128
        || !Number.isSafeInteger(cols) || cols < 1 || cols > 256) {
        throw new Error('Invalid owned resize request.');
    }
    // Preserve partial actual results for retention and parent validation; never replace them with the target.
    return privateRequest(endpoint, 'resizePane', { paneId, rows, cols }, timeoutMs, 16384);
}

/** Fixed render-only capture for the disposable, nonce-bound pixel fixture. */
export function captureRequest(endpoint, paneId, presentationTimeMs, timeoutMs) {
    return renderRequest(endpoint, paneId, presentationTimeMs, timeoutMs);
}

export function imageCaptureRequest(endpoint, paneId, presentationTimeMs, timeoutMs) {
    return renderRequest(endpoint, paneId, presentationTimeMs, timeoutMs, null, true);
}

export function colorCaptureRequest(endpoint, paneId, syntheticSelection, timeoutMs) {
    if (syntheticSelection !== null) {
        const keys = ['startRow', 'startCol', 'endRow', 'endCol'];
        if (!syntheticSelection || Object.keys(syntheticSelection).length !== keys.length
            || !keys.every(key => Number.isSafeInteger(syntheticSelection[key]) && syntheticSelection[key] >= 0)
            || syntheticSelection.endRow >= 128 || syntheticSelection.startCol >= 256 || syntheticSelection.endCol >= 256
            || syntheticSelection.endRow < syntheticSelection.startRow
            || (syntheticSelection.endRow === syntheticSelection.startRow && syntheticSelection.endCol < syntheticSelection.startCol)) {
            throw new Error('Invalid owned synthetic selection.');
        }
    }
    return renderRequest(endpoint, paneId, 0, timeoutMs, syntheticSelection);
}

function renderRequest(endpoint, paneId, presentationTimeMs, timeoutMs, syntheticSelection = null, includeImageDetails = false) {
    if (!/^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(paneId ?? '')
        || !Number.isSafeInteger(presentationTimeMs) || presentationTimeMs < 0 || presentationTimeMs > 60000) {
        throw new Error('Invalid owned capture request.');
    }
    return privateRequest(endpoint, 'captureScreen', { paneId, mode: 'render', inline: true, maxWidth: 0, scale: 1,
        includeRenderDetails: true, presentationTimeMs, ...(syntheticSelection ? { syntheticSelection } : {}), ...(includeImageDetails ? { includeImageDetails: true } : {}) }, timeoutMs, 5 * 1024 * 1024);
}

function privateRequest(endpoint, method, params, timeoutMs, byteLimit) {
    return new Promise((resolve, reject) => {
        const socket = createConnection(endpoint);
        let buffer = '', received = 0, settled = false;
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
            received += Buffer.byteLength(chunk);
            if (received > byteLimit) return finish(new Error('Oversized observer response.'));
            buffer += chunk;
            if (!buffer.includes('\n')) return;
            try {
                const frame = JSON.parse(buffer.slice(0, buffer.indexOf('\n')));
                if (frame.v !== 1 || frame.id !== 1) throw new Error('Uncorrelated observer response.');
                if (frame.error) {
                    const error = new Error('Observer refused the read.');
                    if (typeof frame.error.code === 'string' && /^[a-zA-Z]{1,64}$/.test(frame.error.code)) error.code = frame.error.code;
                    throw error;
                }
                if (!frame.result) throw new Error('Observer returned no result.');
                finish(null, frame.result);
            } catch (error) { finish(error); }
        });
        socket.on('end', () => finish(new Error('Incomplete observer response.')));
    });
}

/** Copy and bound only requested state; absent or malformed evidence never becomes a successful snapshot. */
export function screenSnapshot(value, includeAttributes = false, includeCellDetails = false, includeCursorMode = false, includeOverline = false, includeBufferState = false, includeResolvedColors = false) {
    if (!Number.isSafeInteger(value?.rows) || value.rows < 3 || value.rows > 128
        || !Number.isSafeInteger(value.cols) || value.cols < 48 || value.cols > 256
        || !Array.isArray(value.lines) || value.lines.length !== value.rows
        || !value.lines.every(line => typeof line === 'string' && line.length <= value.cols * 4)
        || !Number.isSafeInteger(value.cursorRow) || value.cursorRow < 0 || value.cursorRow >= value.rows
        || !Number.isSafeInteger(value.cursorCol) || value.cursorCol < 0 || value.cursorCol >= value.cols
        || typeof value.cursorVisible !== 'boolean') throw new Error('Invalid or unsupported observer geometry/state.');
    const result = { rows: value.rows, cols: value.cols, lines: [...value.lines],
        cursorRow: value.cursorRow, cursorCol: value.cursorCol, cursorVisible: value.cursorVisible };
    if (includeAttributes) result.attributeRuns = copyAttributeRuns(value.attributeRuns, value.rows * value.cols);
    if (includeCellDetails) result.cellDetails = copyCellDetails(value.cellDetails, value.rows * value.cols, includeOverline);
    if (includeResolvedColors) {
        // The binding validates first; every resolved run must cohere with it.
        result.colorBinding = copyColorBinding(value.colorBinding);
        result.resolvedColors = copyResolvedColors(value.resolvedColors, value.rows * value.cols, result.colorBinding);
    }
    if (includeCursorMode) {
        const mode = value.cursorMode;
        if (!mode || !Number.isSafeInteger(mode.style) || mode.style < 0 || mode.style > 2
            || typeof mode.blink !== 'boolean' || typeof mode.isDefault !== 'boolean') throw new Error('Invalid stored cursor mode.');
        result.cursorMode = { style: mode.style, blink: mode.blink, isDefault: mode.isDefault };
    }
    if (includeBufferState) {
        result.bufferState = copyBufferState(value.bufferState, value.rows);
        result.history = copyHistory(value.history, result.bufferState.scrollbackLines, value.cols);
    }
    return result;
}

export class OwnedCellObserver {
    constructor({ appData, ownerPid, profileName, nonce, includeAttributes = false, includeCellDetails = false, includeCursorMode = false, includeOverline = false, includeBufferState = false, includeResolvedColors = false, prefix = 'cell', expectedPaneId, rpc = agentRequest, endpoint = observerEndpoint }) {
        Object.assign(this, { appData, ownerPid, profileName, nonce, includeAttributes, includeCellDetails, includeCursorMode, includeOverline, includeBufferState, includeResolvedColors, prefix, rpc, endpoint });
        this.paneId = expectedPaneId;
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

    /** Capture one coherent singleton pane under the shared deadline, including requested complete details. */
    async capture(budget) {
        if (++this.snapshots > 64) throw new Error('Cell snapshot budget exhausted.');
        const before = await this.session(budget);
        const raw = await this.request('readScreen', { paneId: before.paneId, includeAttributes: this.includeAttributes,
            ...(this.includeCellDetails ? { includeCellDetails: true } : {}),
            ...(this.includeCursorMode ? { includeCursorMode: true } : {}),
            ...(this.includeOverline ? { includeOverline: true } : {}),
            ...(this.includeBufferState ? { includeBufferState: true } : {}),
            ...(this.includeResolvedColors ? { includeResolvedColors: true } : {}) }, budget);
        const projection = screenSnapshot(raw);
        if (this.includeAttributes) projection.attributeRuns = readAttributeRuns(raw.attributeLines, projection.rows, projection.cols);
        if (this.includeCellDetails) projection.cellDetails = raw.cellDetails;
        if (this.includeResolvedColors) {
            projection.colorBinding = copyColorBinding(raw.colorBinding);
            projection.resolvedColors = copyResolvedColors(raw.resolvedColors, projection.rows * projection.cols, projection.colorBinding);
        }
        if (this.includeCursorMode) projection.cursorMode = raw.cursorMode;
        if (this.includeBufferState) {
            projection.bufferState = copyBufferState(raw.bufferState, projection.rows);
            projection.history = copyHistory(await this.request('readScrollback', { paneId: before.paneId, startLine: 0, maxLines: 64 }, budget),
                projection.bufferState.scrollbackLines, projection.cols);
        }
        const screen = screenSnapshot(projection, this.includeAttributes, this.includeCellDetails, this.includeCursorMode, this.includeOverline, this.includeBufferState, this.includeResolvedColors);
        const after = await this.session(budget);
        if (before.paneId !== after.paneId || before.rows !== screen.rows || before.cols !== screen.cols
            || after.rows !== screen.rows || after.cols !== screen.cols
            || (this.baseline && (screen.rows !== this.baseline.rows || screen.cols !== this.baseline.cols))) {
            throw new Error('Owned fixture changed size or pane during observation.');
        }
        this.paneId ??= before.paneId;
        return screen;
    }

    /** Journal a fenced operation only after successful observation, retaining immutable snapshot hashes. */
    async handle(kind, budget, barrier = null) {
        if (this.busy || this.restored) throw new Error('Cell observation is busy or already complete.');
        const operation = kind.startsWith(this.prefix + '-') ? kind.slice(this.prefix.length + 1) : '';
        if (!['start', 'bind', 'read', 'finish'].includes(operation)) throw new Error('Unknown cell operation.');
        if (operation !== 'start' && (typeof barrier !== 'string' || barrier.length > 128 || !/^\x1b\[\?[\d;]+c$/.test(barrier))) {
            throw new Error('Missing cell ordering barrier.');
        }
        this.busy = true;
        try {
            const result = await this.observe('cell-' + operation, budget);
            this.frames.push({ index: this.frames.length, kind, paneId: result.paneId,
                barrier, screen: result.screen, sha256: snapshotHash(result.screen) });
            return result;
        } finally { this.busy = false; }
    }

    /** Enforce start, nonce binding and restoration before exposing fixture observations. */
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
