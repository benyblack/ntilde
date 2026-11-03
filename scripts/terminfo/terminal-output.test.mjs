import assert from 'node:assert/strict';
import { test } from 'node:test';
import { spawnSync } from 'node:child_process';
import { prepareOwnedOutput, readOwnedOutputMode, sttyTool } from './terminal-output.mjs';
const tty = '/dev/ttys123';
function fixture({ flags = 'oflags: -opost onlcr', fail = null } = {}) {
    const calls = [], saved = 'gfmt1:cflag=4b00:iflag=0:oflag=3:lflag=0';
    const run = (tool, args, options) => {
        calls.push({ tool, args, options }); assert.equal(tool, '/bin/stty'); assert.deepEqual(args.slice(0, 2), ['-f', tty]);
        assert.equal(options.encoding, 'utf8'); assert.equal(options.maxBuffer, 4096);
        if (fail && fail(args[2])) throw new Error('tool failed');
        if (args[2] === '-g') return saved + '\n';
        return args[2] === '-a' ? flags : '';
    };
    return { run, calls, saved };
}
test('owned output mode is explicitly applied, verified and restored through one bounded absolute tool', () => {
    const f = fixture(), restore = prepareOwnedOutput(tty, f.run);
    assert.deepEqual(f.calls.map(value => value.args[2]), ['-g', '-opost', '-a']);
    restore(); assert.equal(f.calls.at(-1).args[2], f.saved);
    assert.ok(f.calls.every(value => value.options.timeout === 1000));
    assert.equal(readOwnedOutputMode(tty, 123, f.run).outputProcessing, false); assert.equal(f.calls.at(-1).options.timeout, 123);
});
for (const flags of ['', 'oflags: opost onlcr', 'oflags: -opost opost']) test('missing or contradictory output mode remains fatal: ' + flags, () => {
    const f = fixture({ flags }); assert.throws(() => prepareOwnedOutput(tty, f.run), /not disabled/);
    assert.equal(f.calls.at(-1).args[2], f.saved);
});
for (const operation of ['-opost', '-a']) test('failure during ' + operation + ' attempts original mode restoration', () => {
    const f = fixture({ fail: value => value === operation }); assert.throws(() => prepareOwnedOutput(tty, f.run), /tool failed/);
    assert.equal(f.calls.at(-1).args[2], f.saved);
});
test('failed verification and failed restoration preserve both errors', () => {
    const f = fixture({ flags: '', fail: value => value.startsWith('gfmt1:') });
    assert.throws(() => prepareOwnedOutput(tty, f.run), value => value instanceof AggregateError && value.errors.length === 2);
});
test('an unproved or malformed tty spelling cannot reach stty', () => {
    for (const value of [null, '', '/dev/tty', '/dev/ttys123/else', '/dev/pts/1', 'ttys123']) {
        let called = false; assert.throws(() => prepareOwnedOutput(value, () => { called = true; })); assert.equal(called, false);
    }
});
test('real private PTY proves Node raw input alone translates LF, controlled output does not, and restore is exact', {
    skip: process.platform !== 'darwin',
}, () => {
    const module = new URL('./terminal-output.mjs', import.meta.url).href;
    const program = `import { execFileSync } from 'node:child_process';
        import { prepareOwnedOutput } from ${JSON.stringify(module)};
        process.stdin.setRawMode(true);
        const tty = process.argv[1];
        const saved = execFileSync(${JSON.stringify(sttyTool)}, ['-f', tty, '-g'], {encoding:'utf8'});
        const restore = prepareOwnedOutput(tty);
        process.stdout.write('A\\n', () => {
            restore();
            if (execFileSync(${JSON.stringify(sttyTool)}, ['-f', tty, '-g'], {encoding:'utf8'}) !== saved) process.exitCode = 2;
            process.stdout.write('B\\n');
        });`;
    const script = `import os,pty,subprocess,sys
node,program=sys.argv[1:]
def capture(program,with_tty=False):
    master,slave=pty.openpty()
    args=[node,'--input-type=module','-e',program]
    if with_tty:args.append(os.ttyname(slave))
    p=subprocess.Popen(args,stdin=slave,stdout=slave,stderr=slave)
    os.close(slave);data=b''
    while True:
        try:
            part=os.read(master,1024)
            if not part:break
            data+=part
        except OSError:break
    p.wait();os.close(master)
    assert p.returncode==0,(p.returncode,data)
    return data
assert capture("process.stdin.setRawMode(true);process.stdout.write(String.fromCharCode(10));")==bytes([13,10])
assert capture(program,True)==bytes([65,10,66,13,10])
print('baseline CRLF; owned LF; exact original-mode restoration')`;
    const result = spawnSync('/usr/bin/python3', ['-c', script, process.execPath, program], { encoding: 'utf8', timeout: 5000 });
    assert.equal(result.status, 0, result.stdout + result.stderr); assert.match(result.stdout, /owned LF/);
});
