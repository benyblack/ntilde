import test from 'node:test';
import assert from 'node:assert/strict';
import { parseLinuxStat, parseLinuxStatus, parseLinuxArgv, linuxControllingDevice,
    readLinuxRecords, assertLinuxOwnedChild } from './linux-process-core.mjs';
import { readLinuxProcessFacts } from './linux-process-provider.mjs';

const start = '9007199254740993';
const file = ({ character = false, directory = false, ino = 10n, uid = 1000n, rdev = 0n } = {}) => ({
    dev: 3n, ino, rdev, uid, mode: 0o100755n, size: 100n, mtimeNs: 123n, ctimeNs: 123n,
    isFile: () => !character && !directory, isDirectory: () => directory, isCharacterDevice: () => character,
});
const tty = file({ character: true, ino: 20n, rdev: 34817n });
function stat({ pid = 42, ppid = 40, comm = 'worker (odd) )', ticks = start, state = 'S', ttyNr = '34817' } = {}) {
    const fields = Array(50).fill('0');
    Object.assign(fields, { 0: state, 1: String(ppid), 2: '42', 3: '42', 4: ttyNr, 19: ticks });
    return `${pid} (${comm}) ${fields.join(' ')}\n`;
}
const status = (pid = 42, ppid = 40, uids = '1000\t1000\t1000\t1000') => `Pid:\t${pid}\nTgid:\t${pid}\nPPid:\t${ppid}\nUid:\t${uids}\n`;

function synthetic() {
    const opened = [], closed = [];
    const api = {
        opened, closed, time: 0n, statReads: 0, hook: null,
        now: () => api.time,
        open: pid => { const handle = { pid }; opened.push(handle); api.hook?.('open', handle); return handle; },
        close: handle => { closed.push(handle); },
        directoryStat: handle => { api.hook?.('directoryStat', handle); return file({ directory: true, ino: BigInt(handle.pid) }); },
        read: (handle, name) => {
            api.hook?.('read:' + name, handle);
            if (name === 'stat') { api.statReads++; return Buffer.from(stat({ pid: handle.pid, ppid: handle.pid === 40 ? 1 : 40 })); }
            if (name === 'status') return Buffer.from(status(handle.pid, handle.pid === 40 ? 1 : 40));
            if (name === 'cmdline') return Buffer.from('/opt/worker\0space argument\0--ntilde-terminfo-worker=' + 'a'.repeat(32) + '\0');
            throw new Error('Unexpected read');
        },
        link: (_handle, name) => { api.hook?.('link:' + name); return name === 'exe' ? '/opt/worker' : '/dev/pts/1'; },
        linkStat: (_handle, name) => { api.hook?.('linkStat:' + name); return name === 'exe' ? file() : tty; },
        pathStat: path => { api.hook?.('pathStat:' + path); return path === '/opt/worker' ? file() : tty; },
        canonical: path => path,
    };
    return api;
}
const records = (reader, pty = true) => readLinuxRecords(reader, [{ pid: 42, pty }], 1000);

test('comm spaces, nested/closing parentheses and newline never shift starttime or PID', () => {
    for (const comm of ['a b', 'a)b(c', ') S 1 2 )', 'line\nbreak', '(x)\n) R 1']) {
        const value = parseLinuxStat(stat({ comm }), 42);
        assert.equal(value.startTimeTicks, start); assert.equal(value.ppid, 40); assert.equal(value.controllingDevice, '34817');
    }
    assert.notEqual(BigInt(start), BigInt(Number(start)));
});

for (const [name, text] of [['zombie', stat({ state: 'Z' })], ['dead', stat({ state: 'X' })],
    ['PID changed', stat({ pid: 43 })], ['precision overflow', stat({ ticks: '18446744073709551616' })],
    ['negative start', stat({ ticks: '-1' })], ['bad separator', stat().replace(') S ', ')  S ')],
    ['short fields', '42 (a) S 1 2 3\n'], ['bad tty', stat({ ttyNr: '4294967295' })]]) {
    test('stat refuses ' + name, () => assert.throws(() => parseLinuxStat(text, 42)));
}

test('signed tty_nr converts exactly to Linux kernel new_encode_dev without Number rounding', () => {
    const minor = 524289n, major = 136n;
    const rdev = (minor & 255n) | (major << 8n) | ((minor & ~255n) << 12n);
    const encoded = linuxControllingDevice(rdev);
    assert.equal(encoded, '2147518465');
    const signed = BigInt.asIntN(32, BigInt(encoded));
    assert.ok(signed < 0n);
    assert.equal(parseLinuxStat(stat({ ttyNr: signed.toString() }), 42).controllingDevice, encoded);
    assert.throws(() => linuxControllingDevice(-1n));
    assert.throws(() => linuxControllingDevice(1n << 64n));
});

test('status exact identity/UID and NUL argv preserve empty and spaced arguments', () => {
    const process = parseLinuxStat(stat(), 42);
    assert.deepEqual(parseLinuxStatus(status(), process), [1000, 1000, 1000, 1000]);
    assert.deepEqual(parseLinuxArgv(Buffer.from('/opt/x\0two words\0\0last\0')), ['/opt/x', 'two words', '', 'last']);
    for (const text of [status(43), status(42, 41), status(42, 40, '1000 1001 1000 1000'), status() + 'Uid: 1000 1000 1000 1000\n']) {
        assert.throws(() => parseLinuxStatus(text, process));
    }
    for (const value of [Buffer.alloc(0), Buffer.from('not nul terminated'), Buffer.from([0xff, 0]), Buffer.alloc(32769)]) {
        assert.throws(() => parseLinuxArgv(value));
    }
});

test('held directory snapshots retain exact bigint/string facts and close every handle', () => {
    const reader = synthetic(), [value] = records(reader);
    assert.equal(value.startTimeTicks, start); assert.equal(value.pty.selectedFd, 1);
    assert.deepEqual(value.pty.descriptors.map(x => x.path), ['/dev/pts/1', '/dev/pts/1']);
    assert.equal(value.executableFile.ino, '10');
    assert.equal(reader.statReads, 2); assert.deepEqual(reader.closed, reader.opened);
});

for (const name of ['reuse', 'vanish', 'argv change', 'UID change', 'executable replacement', 'FD replacement', 'descriptor target change']) {
    test(name + ' between reads fails closed and releases the pinned process directory', () => {
        const reader = synthetic(), read = reader.read, link = reader.link, linkStat = reader.linkStat;
        reader.read = (handle, entry) => {
            if (reader.statReads >= 1 && entry === 'stat') {
                if (name === 'reuse') return Buffer.from(stat({ ticks: String(BigInt(start) + 1n) }));
                if (name === 'vanish') throw Object.assign(new Error('gone'), { code: 'ESRCH' });
            }
            if (reader.statReads >= 2 && entry === 'cmdline' && name === 'argv change') return Buffer.from('/opt/worker\0changed\0');
            if (reader.statReads >= 2 && entry === 'status' && name === 'UID change') return Buffer.from(status(42, 40, '1001 1001 1001 1001'));
            return read(handle, entry);
        };
        reader.link = (handle, entry) => reader.statReads >= 2 && name === 'descriptor target change' && entry === 'fd/1' ? '/dev/pts/2' : link(handle, entry);
        reader.linkStat = (handle, entry) => {
            if (reader.statReads >= 2 && name === 'executable replacement' && entry === 'exe') return file({ ino: 11n });
            if (reader.statReads >= 2 && name === 'FD replacement' && entry === 'fd/1') return file({ character: true, ino: 21n, rdev: 34817n });
            return linkStat(handle, entry);
        };
        assert.throws(() => records(reader), /changed|mismatch|gone|no longer|different PTYs/);
        assert.equal(reader.closed.length, 1);
    });
}

for (const name of ['deleted', 'relative', 'traversal', 'control', 'alias', 'pipe', 'non-character', 'wrong UID', 'wrong device', 'different stdin']) {
    test('refuse executable/PTY path or device: ' + name, () => {
        const reader = synthetic(), link = reader.link, statLink = reader.linkStat;
        reader.link = (handle, entry) => {
            if (entry === 'exe') {
                if (name === 'deleted') return '/opt/worker (deleted)';
                if (name === 'relative') return 'opt/worker';
                if (name === 'traversal') return '/opt/../worker';
                if (name === 'control') return '/opt/work\ner';
            }
            if (entry === 'fd/1' && name === 'pipe') return 'pipe:[12]';
            if (entry === 'fd/0' && name === 'different stdin') return '/dev/pts/2';
            return link(handle, entry);
        };
        if (name === 'alias') reader.canonical = () => '/elsewhere/worker';
        reader.linkStat = (handle, entry) => {
            if (entry === 'fd/1') {
                if (name === 'non-character') return file();
                if (name === 'wrong UID') return file({ character: true, ino: 20n, rdev: 34817n, uid: 1001n });
                if (name === 'wrong device') return file({ character: true, ino: 20n, rdev: 34818n });
            }
            return statLink(handle, entry);
        };
        assert.throws(() => records(reader)); assert.equal(reader.closed.length, 1);
    });
}

test('one shared deadline covers both identities and closes handles even when open finishes late', () => {
    const reader = synthetic();
    reader.hook = operation => { if (operation === 'open') reader.time = 1000000000n; };
    assert.throws(() => records(reader), /deadline/); assert.equal(reader.closed.length, 1);
    const before = synthetic(); before.now = () => { before.time += 1000000000n; return before.time; };
    assert.throws(() => records(before), /budget/); assert.equal(before.opened.length, 0);
});

test('parent and child handles stay held across reverse-order rechecks; exact argv is not shell splitting', () => {
    const reader = synthetic();
    const [parent, child] = readLinuxRecords(reader, [{ pid: 40, pty: false }, { pid: 42, pty: true }], 1000);
    const expected = { parentPid: 40, childPid: 42, uid: 1000, executable: '/opt/worker', argv: child.argv, runId: 'a'.repeat(32) };
    assert.equal(assertLinuxOwnedChild(parent, child, expected).qualification, 'provider-foundation-only');
    for (const changed of [{ uid: 1001 }, { parentPid: 41 }, { executable: '/opt/other' },
        { argv: ['/opt/worker space argument', '--ntilde-terminfo-worker=' + 'a'.repeat(32)] }, { runId: 'b'.repeat(32) }]) {
        assert.throws(() => assertLinuxOwnedChild(parent, child, { ...expected, ...changed }));
    }
    assert.equal(reader.closed.length, 2);
});

test('production API refuses another platform or an alternate proc-root parameter', () => {
    assert.throws(() => readLinuxProcessFacts(process.pid, { procRoot: '/tmp/fake' }), process.platform === 'linux' ? /options/ : /requires Linux/);
});


test('directory UID mismatch and symlink/non-file executable targets are refused', () => {
    const owner = synthetic(); owner.directoryStat = () => file({ directory: true, uid: 1001n });
    assert.throws(() => records(owner)); assert.equal(owner.closed.length, 1);
    const alias = synthetic(); alias.pathStat = () => file({ directory: true });
    assert.throws(() => records(alias)); assert.equal(alias.closed.length, 1);
});

test('all pinned directories are released if second open fails or another close fails', () => {
    const reader = synthetic(), open = reader.open;
    reader.open = pid => { if (pid === 42) throw new Error('second open failed'); return open(pid); };
    assert.throws(() => readLinuxRecords(reader, [{ pid: 40, pty: false }, { pid: 42, pty: true }], 1000), /second open/);
    assert.equal(reader.closed.length, 1);
    const closeFailure = synthetic();
    closeFailure.close = handle => { closeFailure.closed.push(handle); if (handle.pid === 42) throw new Error('close failed'); };
    assert.throws(() => readLinuxRecords(closeFailure, [{ pid: 40, pty: false }, { pid: 42, pty: true }], 1000), /close failed/);
    assert.equal(closeFailure.closed.length, 2);
});

test('parent PID/start identity changes across child observation cannot prove a relation', () => {
    const reader = synthetic(), read = reader.read; let parentReads = 0;
    reader.read = (handle, name) => {
        if (handle.pid === 40 && name === 'stat' && ++parentReads === 2) return Buffer.from(stat({ pid: 40, ppid: 1, ticks: String(BigInt(start) + 1n) }));
        return read(handle, name);
    };
    assert.throws(() => readLinuxRecords(reader, [{ pid: 40, pty: false }, { pid: 42, pty: true }], 1000), /identity changed/);
    assert.equal(reader.closed.length, 2);
});

test('aggregate operation budget bounds a reader returning pathological tiny chunks', () => {
    const reader = synthetic(), read = reader.read;
    reader.read = (handle, name, limit, query) => {
        for (let i = 0; i < 200; i++) query(() => 1);
        return read(handle, name);
    };
    assert.throws(() => records(reader), /budget/); assert.equal(reader.closed.length, 1);
});


test('legitimate PTY timestamps may change while stable device identity remains the same', () => {
    const reader = synthetic(), linkStat = reader.linkStat, pathStat = reader.pathStat;
    reader.linkStat = (handle, entry) => {
        const value = linkStat(handle, entry);
        return entry.startsWith('fd/') && reader.statReads >= 2 ? { ...value, mtimeNs: 999n, ctimeNs: 1000n } : value;
    };
    reader.pathStat = path => {
        const value = pathStat(path);
        return path.startsWith('/dev/pts/') && reader.statReads >= 2 ? { ...value, mtimeNs: 999n, ctimeNs: 1000n } : value;
    };
    const [result] = records(reader);
    assert.deepEqual(Object.keys(result.pty.descriptors[0].device), ['dev', 'ino', 'rdev', 'uid', 'mode']);
    assert.equal(result.executableFile.mtimeNs, '123');
});

test('regular executable timestamp changes still invalidate its source identity', () => {
    const reader = synthetic(), linkStat = reader.linkStat, pathStat = reader.pathStat;
    reader.linkStat = (handle, entry) => {
        const value = linkStat(handle, entry);
        return entry === 'exe' && reader.statReads >= 2 ? { ...value, mtimeNs: 999n } : value;
    };
    reader.pathStat = path => {
        const value = pathStat(path);
        return path === '/opt/worker' && reader.statReads >= 2 ? { ...value, mtimeNs: 999n } : value;
    };
    assert.throws(() => records(reader), /identity changed/);
});
