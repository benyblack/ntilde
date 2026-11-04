import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createLinuxOutputResources } from './linux-owned-output-resources.mjs';
import { linuxOutputBudget, linuxOutputDeadline } from './linux-owned-output-core.mjs';
import { linuxDeviceIdentity } from './linux-process-core.mjs';

const stat = (kind, values = {}) => ({ dev: 3n, ino: 9n, rdev: kind === 'pty' ? 34817n : 0n,
    uid: kind === 'pty' ? 1000n : 0n, mode: kind === 'pty' ? 0o20620n : 0o100755n,
    size: 8n, mtimeNs: 100n, ctimeNs: 101n, ...values,
    isFile: () => kind === 'file', isCharacterDevice: () => kind === 'pty', isDirectory: () => kind === 'directory' });
function fixture() {
    const state = { time: 0n, offset: 0, chunk: 65536, log: [], hook: null, tool: stat('file'),
        pty: stat('pty'), parent: stat('directory', { mode: 0o40755n }), canonical: '/usr/bin/stty',
        ptyCanonical: '/dev/pts', filesystemType: 0x1cd1n, bytes: Buffer.from([0x7f, 69, 76, 70, 1, 2, 3, 4]),
        constants: { O_RDONLY: 0, O_RDWR: 2, O_NOFOLLOW: 0x20000, O_NOCTTY: 0x100, O_NONBLOCK: 0x800 } };
    const note = (...args) => { state.log.push(args); state.hook?.(...args); };
    const fs = { constants: state.constants,
        realpathSync(path) { note('realpath', path); return path === '/dev/pts' ? state.ptyCanonical : state.canonical; },
        lstatSync(path) { note('lstat', path); return path === '/dev/pts' ? state.parent : path.startsWith('/dev/pts/') ? state.pty : state.tool; },
        fstatSync(fd) { note('fstat', fd); return fd === 7 ? state.tool : state.pty; },
        statfsSync(path) { note('statfs', path); return { type: state.filesystemType }; },
        openSync(path, flags) { note('open', path, flags); return path.startsWith('/dev/pts/') ? 8 : 7; },
        closeSync(fd) { note('close', fd); },
        readSync(fd, buffer) {
            note('read', fd);
            const size = Math.min(state.chunk, buffer.length, state.bytes.length - state.offset);
            state.bytes.copy(buffer, 0, state.offset, state.offset + size); state.offset += size; return size;
        },
    };
    state.resources = createLinuxOutputResources(fs);
    state.budget = () => linuxOutputBudget({ deadlineNs: linuxOutputDeadline(4000, () => state.time) }, () => state.time);
    state.toolInput = () => ({ path: '/usr/bin/stty', sha256: createHash('sha256').update(state.bytes).digest('hex') });
    state.ptyInput = () => ({ path: '/dev/pts/1', device: linuxDeviceIdentity(state.pty) });
    return state;
}

test('verified ELF stays held; short reads, exact flags and complete hash are handled', () => {
    const s = fixture(); s.chunk = 1;
    const held = s.resources.openTool(s.toolInput(), s.budget());
    assert.equal(held.fd, 7); assert.equal(held.observation.sha256, s.toolInput().sha256);
    assert.deepEqual(s.log.find(([kind]) => kind === 'open'), ['open', '/usr/bin/stty', s.constants.O_NOFOLLOW]);
    assert.equal(s.log.filter(([kind]) => kind === 'close').length, 0);
    s.resources.checkTool(held); s.resources.close(held);
    assert.deepEqual(s.log.at(-1), ['close', 7]);
});

for (const [name, mutate] of [
    ['missing no-follow', s => { s.constants.O_NOFOLLOW = 0; }],
    ['non-system canonical path', s => { s.canonical = '/usr/lib/cargo/bin/coreutils/stty'; }],
    ['not root-owned', s => { s.tool.uid = 1000n; }],
    ['group writable', s => { s.tool.mode |= 0o020n; }],
    ['world writable', s => { s.tool.mode |= 0o002n; }],
    ['not executable', s => { s.tool.mode = 0o100644n; }],
    ['not regular', s => { s.tool = stat('pty'); }],
    ['oversized ELF', s => { s.tool.size = 4194305n; }],
    ['script', s => { s.bytes[0] = 35; }],
    ['incomplete bytes', s => { s.tool.size++; }],
]) test('tool admission refuses ' + name + ' and closes every opened fd once', () => {
    const s = fixture(); mutate(s);
    assert.throws(() => s.resources.openTool(s.toolInput(), s.budget()));
    assert.equal(s.log.filter(([kind]) => kind === 'close').length, s.log.filter(([kind]) => kind === 'open').length);
});

test('fixed separately installed GNU path is accepted, substitution and digest mismatch are refused', () => {
    const s = fixture(); s.canonical = '/usr/bin/gnustty';
    assert.equal(s.resources.openTool({ ...s.toolInput(), path: '/usr/bin/gnustty' }, s.budget()).observation.path, '/usr/bin/gnustty');
    const bad = fixture();
    assert.throws(() => bad.resources.openTool({ ...bad.toolInput(), sha256: '0'.repeat(64) }, bad.budget()));
    assert.deepEqual(bad.log.at(-1), ['close', 7]);
    const changed = fixture(); let reads = 0;
    changed.hook = kind => { if (kind === 'read' && ++reads === 2) changed.tool.ctimeNs++; };
    assert.throws(() => changed.resources.openTool(changed.toolInput(), changed.budget()), /admission failed/);
});

test('late open is retained for cleanup and primary plus ambiguous close survive', () => {
    const s = fixture();
    s.hook = kind => { if (kind === 'open') s.time = 4000000000n; if (kind === 'close') throw new Error('close unconfirmed'); };
    let failure;
    try { s.resources.openTool(s.toolInput(), s.budget()); } catch (value) { failure = value; }
    assert.equal(failure.errors.length, 2); assert.equal(failure.linuxOutputOpenResource.closeConfirmed, false);
    assert.equal(s.log.filter(([kind]) => kind === 'close').length, 1);
});

test('PTY holds original0620 device with no-ctty/no-follow/nonblocking flags; traffic timestamps are excluded', () => {
    const s = fixture(), held = s.resources.openPty(s.ptyInput(), s.budget());
    assert.deepEqual(s.log.find(([kind]) => kind === 'open'), ['open', '/dev/pts/1',
        s.constants.O_RDWR | s.constants.O_NOCTTY | s.constants.O_NOFOLLOW | s.constants.O_NONBLOCK]);
    s.pty.mtimeNs++; s.pty.ctimeNs++;
    s.resources.checkPty(held);
    s.pty.ino++; assert.throws(() => s.resources.checkPty(held));
    assert.equal(s.log.filter(([kind]) => kind === 'open').length, 1);
});

for (const [name, mutate] of [
    ['missing O_NOCTTY', s => { s.constants.O_NOCTTY = 0; }],
    ['unowned prefix', s => { s.parent.uid = 1000n; }],
    ['writable prefix', s => { s.parent.mode |= 2n; }],
    ['symlink prefix', s => { s.ptyCanonical = '/somewhere/pts'; }],
    ['wrong filesystem', s => { s.filesystemType = 0n; }],
    ['non-character final path', s => { s.pty = stat('file'); }],
]) test('PTY admission refuses ' + name + ' before opening the terminal', () => {
    const s = fixture(), expected = s.ptyInput(); mutate(s);
    assert.throws(() => s.resources.openPty(expected, s.budget()));
    assert.equal(s.log.some(([kind]) => kind === 'open'), false);
});

test('named or held PTY substitution during open refuses and closes original held descriptor', () => {
    for (const changedAt of ['open', 'after-fstat']) {
        const s = fixture(), expected = s.ptyInput();
        s.hook = kind => { if (kind === (changedAt === 'open' ? 'open' : 'fstat')) s.pty = stat('pty', { ino: 99n }); };
        assert.throws(() => s.resources.openPty(expected, s.budget()));
        assert.deepEqual(s.log.at(-1), ['close', 8]);
    }
});

test('held executable timestamps remain part of identity even though PTY timestamps do not', () => {
    const s = fixture(), held = s.resources.openTool(s.toolInput(), s.budget());
    s.tool.mtimeNs++; assert.throws(() => s.resources.checkTool(held));
});
