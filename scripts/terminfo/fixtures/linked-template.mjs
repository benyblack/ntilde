// A template directory handed to each test as a private directory of hard links instead of a copy.
//
// The receipt fixture builds one template per process (some 650 files, 50 to 80 MB) and every test gets its own
// directory of it. As a recursive copy that was the receipt tests' largest cost on windows: the 33 test files that use
// the fixture took 3.0 times their ubuntu time (3650 s against 1234 s, run 44507620334) while every other file took 1.1
// times, and of 1587 fixture uses 1583 change at most three of the files they were given.
//
// A hard link costs no data, but a test that writes a file in place writes the template's bytes too. So the template
// is kept honest, not trusted: a second, never-linked copy holds the pristine bytes, and after every use each template
// file whose size, modification time or mode moved is replaced from it. The test that wrote saw exactly its own write, and
// the next test starts from the pristine bytes again. Removing, replacing or adding files in a private directory never
// reaches the template at all.
//
// Three cases use the plain copy, from the pristine directory: a second use that starts while one is still open (it
// could otherwise read the first one's write), a file system whose modification times are whole seconds (a same-size
// write inside the template's second would go unseen), and macOS. There the copy is already a copy-on-write clone
// (APFS), and making the links was slower than it: 13.4 s against 8.6 s for one test file's 97 uses on an arm64 Mac.
import { copyFileSync, cpSync, linkSync, lstatSync, mkdirSync, mkdtempSync, readdirSync, readlinkSync, rmSync, symlinkSync } from 'node:fs';
import { dirname, join } from 'node:path';

/** Every entry under `root` as a relative path, a directory before its contents. */
function entriesOf(root, base = '') {
    return readdirSync(join(root, base), { withFileTypes: true }).flatMap(entry => {
        const path = base + entry.name;
        if (entry.isDirectory()) return [{ path, kind: 'directory' }, ...entriesOf(root, path + '/')];
        return [{ path, kind: entry.isSymbolicLink() ? 'symlink' : 'file' }];
    });
}

const stat = path => lstatSync(path, { bigint: true });
/** What a write or a mode change through a link cannot leave unchanged on a file system with sub-second times. Not the
 * change time: making a link moves it, so every use would look like a write. */
const signature = path => { const value = stat(path); return `${value.size}:${value.mtimeNs}:${value.mode}`; };

/**
 * Wrap the finished template directory `root`. `materialize(output)` fills the existing empty directory `output` with
 * the template and returns the function that must be called when its user is done (before or after `output` is removed).
 * `remove()` deletes the pristine copy; the caller owns `root`. `link: false` always copies; the default links
 * everywhere but macOS. `linking` says whether this template links at all.
 */
export function linkedTemplate(root, { link = process.platform !== 'darwin' } = {}) {
    const pristine = mkdtempSync(join(dirname(root), 'nt-template-pristine-'));
    try { cpSync(root, pristine, { recursive: true, verbatimSymlinks: true }); }
    catch (error) { rmSync(pristine, { recursive: true, force: true }); throw error; }
    const entries = entriesOf(root);
    const files = entries.filter(entry => entry.kind === 'file');
    const signatures = new Map(files.map(({ path }) => [path, signature(join(root, path))]));
    const subSecond = files.some(({ path }) => stat(join(root, path)).mtimeNs % 1_000_000_000n !== 0n);
    let open = false;

    /** Put back every template file a link was written through. */
    function repair() {
        for (const { path } of files) {
            const target = join(root, path);
            let current;
            try { current = signature(target); } catch { current = null; }
            if (current === signatures.get(path)) continue;
            rmSync(target, { force: true, recursive: true });
            copyFileSync(join(pristine, path), target);
            signatures.set(path, signature(target));
        }
    }

    function materialize(output) {
        if (!link || open || !subSecond) {
            cpSync(pristine, output, { recursive: true, verbatimSymlinks: true });
            return () => {};
        }
        open = true;
        try {
            for (const { path, kind } of entries) {
                const target = join(output, path);
                if (kind === 'directory') mkdirSync(target);
                else if (kind === 'symlink') symlinkSync(readlinkSync(join(root, path)), target);
                else {
                    // A file system or volume that refuses the link (or has run out of them) still gets the file.
                    try { linkSync(join(root, path), target); } catch { copyFileSync(join(pristine, path), target); }
                }
            }
        } catch (error) {
            open = false;
            throw error;
        }
        // `open` is cleared only by a repair that finished: after one that threw, some template files may still hold a
        // test's bytes, so every later use copies from the pristine directory instead of linking them.
        return () => { repair(); open = false; };
    }

    return { materialize, remove: () => rmSync(pristine, { recursive: true, force: true }),
        /** Whether a use that finds no other open is linked rather than copied. */
        linking: link && subSecond };
}
