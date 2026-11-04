// Fixed synthetic PTY bytes only. This is not an App, shell or terminal qualification worker.
const marker = process.argv[2], runId = marker?.slice('--ntilde-terminfo-worker='.length);
if (process.platform !== 'linux' || process.argv.length !== 3 || marker !== '--ntilde-terminfo-worker=' + runId
    || !/^[a-f0-9]{32}$/.test(runId) || !process.stdin.isTTY || !process.stdout.isTTY) process.exit(2);
// Do not setRawMode: the private parent owns the exact termios control, including OPOST.
process.stdout.write('READY ' + runId + '\n');
let buffered = '', calls = 0;
process.stdin.on('data', chunk => {
    if (!Buffer.isBuffer(chunk) || chunk.length > 64 - buffered.length) process.exit(2);
    buffered += chunk.toString('ascii');
    while (buffered.includes('\n')) {
        const end = buffered.indexOf('\n'), command = buffered.slice(0, end); buffered = buffered.slice(end + 1);
        if (++calls > 12) process.exit(2);
        if (command === 'probe') process.stdout.write('QA-' + runId + '\nEND-' + runId + '\n');
        else if (command === 'mutate-argv') {
            process.title = 'ntilde-output-mutated'; // A comparison fact deliberately changes, not an executable attestation.
            process.stdout.write('MUTATED\n');
        } else if (command === 'exit') process.exit(0);
        else process.exit(2);
    }
});
