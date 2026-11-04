// Disposable provider fixture only. No App, clipboard, OS input or terminal qualification.
const marker = process.argv[2];
if (process.platform !== 'linux' || process.argv.length !== 3 || !/^--ntilde-terminfo-worker=[a-f\d]{32}$/.test(marker ?? '')) process.exit(2);
process.stdout.write('READY ' + marker.slice('--ntilde-terminfo-worker='.length) + '\n');
const deadline = setTimeout(() => process.exit(3), 15000);
let text = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', value => {
    text += value;
    if (text.length > 16) process.exit(2);
    if (text === 'exit\n') { clearTimeout(deadline); process.exit(0); }
});
process.stdin.on('end', () => process.exit(3));
