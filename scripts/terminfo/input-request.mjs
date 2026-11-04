import { createConnection } from 'node:net';

/** Private acting channel for the independently proved disposable fixture only. */
export function ownedInputRequest(endpoint, request, timeoutMs) {
    if (!/^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(request?.paneId ?? '')
        || !Number.isSafeInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 4000) throw new Error('Invalid owned input request.');
    return new Promise((resolve, reject) => {
        const socket = createConnection(endpoint);
        let buffer = '', settled = false;
        const finish = (error, value) => {
            if (settled) return;
            settled = true; clearTimeout(timer); socket.destroy();
            if (error) reject(error); else resolve(value);
        };
        const timer = setTimeout(() => finish(new Error('Owned input response timed out.')), timeoutMs);
        socket.setEncoding('utf8');
        socket.on('error', error => finish(error));
        socket.on('connect', () => socket.write(JSON.stringify({ v: 1, id: 1, method: 'generatePaneInput', params: request }) + '\n'));
        socket.on('data', chunk => {
            buffer += chunk;
            if (Buffer.byteLength(buffer) > 16384) return finish(new Error('Oversized owned input response.'));
            if (!buffer.includes('\n')) return;
            try {
                const frame = JSON.parse(buffer.slice(0, buffer.indexOf('\n')));
                if (frame.v !== 1 || frame.id !== 1 || frame.error || !frame.result) throw new Error('Invalid or refused owned input response.');
                finish(null, frame.result);
            } catch (error) { finish(error); }
        });
        socket.on('end', () => finish(new Error('Incomplete owned input response.')));
    });
}
