// Portable synthetic source-loader tests. No terminal/App behavior is graded here.
import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
function fixture(action) {
    const root = mkdtempSync(join(tmpdir(), 'nt-bootstrap-test-'));
    try {
        mkdirSync(join(root, 'scripts/terminfo'), { recursive: true });
        copyFileSync(new URL('../terminfo-owned.mjs', import.meta.url), join(root, 'scripts/terminfo-owned.mjs'));
        const inventory = { runnerSourceSha256: 'terminfo-owned-runner.mjs', workerSourceSha256: 'worker.mjs',
            fixtureSourceSha256: 'fixture.mjs', sourceInventorySha256: 'owned-source-inventory.json', sourceImportsSourceSha256: 'source-imports.mjs' };
        writeFileSync(join(root, 'scripts/terminfo/owned-source-inventory.json'), JSON.stringify(inventory));
        copyFileSync(new URL('./source-imports.mjs', import.meta.url), join(root, 'scripts/terminfo/source-imports.mjs'));
        writeFileSync(join(root, 'scripts/terminfo/fixture.mjs'), "export default 'CAPTURED';");
        writeFileSync(join(root, 'scripts/terminfo/worker.mjs'), "import value from './fixture.mjs'; console.log(value);");
        const parent = `import value from './terminfo/fixture.mjs';
            import {execFileSync} from 'node:child_process'; import {writeFileSync} from 'node:fs';
            import {dirname,resolve} from 'node:path'; import {fileURLToPath} from 'node:url';
            if(process.env.EDIT_BETWEEN==='1') writeFileSync(process.env.ORIGINAL_FIXTURE, "export default 'CHANGED';");
            const worker=execFileSync(process.execPath,[fileURLToPath(new URL('./terminfo/worker.mjs',import.meta.url))],{encoding:'utf8'}).trim();
            console.log(JSON.stringify({parent:value,worker,sourceRoot:resolve(dirname(fileURLToPath(import.meta.url)),'..')}));`;
        writeFileSync(join(root, 'scripts/terminfo-owned-runner.mjs'), parent);
        action({ root, inventory, run: env => spawnSync(process.execPath, [join(root, 'scripts/terminfo-owned.mjs')], {
            encoding: 'utf8', timeout: 5000, env: { ...process.env, ORIGINAL_FIXTURE: join(root, 'scripts/terminfo/fixture.mjs'), ...env } }) });
    } finally { rmSync(root, { recursive: true, force: true }); }
}
test('original edits between parent import and worker import do not split the captured graph', () => fixture(f => {
    const result = f.run({ EDIT_BETWEEN: '1' }); assert.equal(result.status, 0, result.stderr);
    const value = JSON.parse(result.stdout); assert.equal(value.parent, 'CAPTURED'); assert.equal(value.worker, 'CAPTURED');
    assert.ok(readFileSync(join(f.root, 'scripts/terminfo/fixture.mjs'), 'utf8').includes('CHANGED'));
    assert.equal(existsSync(value.sourceRoot), false);
}));
test('original edits before measured parent imports cannot replace the captured bytes', () => fixture(f => {
    const preload = join(f.root, 'edit-before.mjs');
    writeFileSync(preload, `import{registerHooks}from'node:module';import{writeFileSync}from'node:fs';
        registerHooks({load(url,context,next){if(url.endsWith('/scripts/terminfo-owned-runner.mjs')&&url.includes('ntilde-owned-sources-'))writeFileSync(process.env.ORIGINAL_FIXTURE,"export default 'CHANGED';");return next(url,context);}});`);
    const result = f.run({ NODE_OPTIONS: '--import=' + pathToFileURL(preload).href }); assert.equal(result.status, 0, result.stderr);
    const value = JSON.parse(result.stdout); assert.equal(value.parent, 'CAPTURED'); assert.equal(value.worker, 'CAPTURED');
    assert.ok(readFileSync(join(f.root, 'scripts/terminfo/fixture.mjs'), 'utf8').includes('CHANGED'));
    assert.equal(existsSync(value.sourceRoot), false);
}));
test('an unlisted original module has no fallback into the measured snapshot', () => fixture(f => {
    writeFileSync(join(f.root, 'scripts/terminfo-owned-runner.mjs'), "import './terminfo/unlisted.mjs';");
    writeFileSync(join(f.root, 'scripts/terminfo/unlisted.mjs'), "console.log('UNLISTED EXECUTED');");
    const result = f.run({}); assert.equal(result.status, 1); assert.match(result.stderr, /outside captured scope before import/);
    assert.ok(!result.stdout.includes('UNLISTED EXECUTED'));
}));
test('path traversal in the inventory is rejected before source loading', () => fixture(f => {
    f.inventory.fixtureSourceSha256 = '../../outside.mjs';
    writeFileSync(join(f.root, 'scripts/terminfo/owned-source-inventory.json'), JSON.stringify(f.inventory));
    const result = f.run({}); assert.equal(result.status, 1); assert.match(result.stderr, /Invalid source inventory entry/);
}));

test('a side-effect import outside the graph is rejected before any measured module executes', () => fixture(f => {
    const outside = join(f.root, 'outside.mjs'), marker = join(f.root, 'side-effect');
    writeFileSync(outside, "import { writeFileSync } from 'node:fs'; writeFileSync(" + JSON.stringify(marker) + ", 'EXECUTED');");
    writeFileSync(join(f.root, 'scripts/terminfo-owned-runner.mjs'), 'import ' + JSON.stringify(pathToFileURL(outside).href) + ';');
    const result = f.run({}); assert.equal(result.status, 1); assert.match(result.stderr, /outside captured scope before import/);
    assert.equal(existsSync(marker), false);
}));

for (const spelling of ['compact-side-effect', 'literal-dynamic']) test('pre-import closure rejects ' + spelling + ' outside the captured graph', () => fixture(f => {
    const outside = join(f.root, 'outside.mjs'), marker = join(f.root, 'side-effect');
    writeFileSync(outside, "import { writeFileSync } from 'node:fs'; writeFileSync(" + JSON.stringify(marker) + ", 'EXECUTED');");
    const target = JSON.stringify(pathToFileURL(outside).href);
    writeFileSync(join(f.root, 'scripts/terminfo-owned-runner.mjs'), spelling === 'literal-dynamic' ? 'await import(' + target + ');' : 'import' + target + ';');
    const result = f.run({}); assert.equal(result.status, 1); assert.match(result.stderr, /outside captured scope before import/);
    assert.equal(existsSync(marker), false);
}));

for (const spelling of ['side-effect', 'dynamic', 'from', 'line-comment']) test('comment-separated ' + spelling + ' import is rejected before source execution', () => fixture(f => {
    const outside = join(f.root, 'outside.mjs'), marker = join(f.root, 'side-effect');
    writeFileSync(outside, "import { writeFileSync } from 'node:fs'; writeFileSync(" + JSON.stringify(marker) + ", 'EXECUTED'); export default 1;");
    const target = JSON.stringify(pathToFileURL(outside).href);
    const commands = { 'side-effect': 'import/* gap */' + target + ';', dynamic: 'await import/* gap */(/* inner */' + target + ');',
        from: 'import value from/* gap */' + target + ';', 'line-comment': 'import// gap\n' + target + ';' };
    writeFileSync(join(f.root, 'scripts/terminfo-owned-runner.mjs'), commands[spelling]);
    const result = f.run({}); assert.equal(result.status, 1); assert.match(result.stderr, /outside captured scope before import/);
    assert.equal(existsSync(marker), false);
}));

test('unsupported template dynamic import is rejected before an unretained module executes', () => fixture(f => {
    const outside = join(f.root, 'outside.mjs'), marker = join(f.root, 'side-effect');
    writeFileSync(outside, "import { writeFileSync } from 'node:fs'; writeFileSync(" + JSON.stringify(marker) + ", 'EXECUTED');");
    writeFileSync(join(f.root, 'scripts/terminfo-owned-runner.mjs'), 'await import(`' + pathToFileURL(outside).href + '`);');
    const result = f.run({}); assert.equal(result.status, 1); assert.match(result.stderr, /Unsupported template import/);
    assert.equal(existsSync(marker), false);
}));

test('modified scanner bytes never execute before the trusted bootstrap pin check', () => fixture(f => {
    writeFileSync(join(f.root, 'scripts/terminfo/source-imports.mjs'), "console.log('UNREVIEWED SCANNER EXECUTED'); export const literalImports = () => [];");
    const result = f.run({}); assert.equal(result.status, 1); assert.match(result.stderr, /changed trusted source scanner/);
    assert.equal(result.stdout.includes('UNREVIEWED SCANNER EXECUTED'), false);
}));

test('a dependency-looking keyword in a line comment cannot hide a later real import', () => fixture(f => {
    const outside = join(f.root, 'outside.mjs'), marker = join(f.root, 'side-effect');
    writeFileSync(outside, "import { writeFileSync } from 'node:fs'; writeFileSync(" + JSON.stringify(marker) + ", 'EXECUTED');");
    writeFileSync(join(f.root, 'scripts/terminfo-owned-runner.mjs'), '// import /*\nimport ' + JSON.stringify(pathToFileURL(outside).href) + ';\n// */');
    const result = f.run({}); assert.equal(result.status, 1); assert.match(result.stderr, /outside captured scope before import/);
    assert.equal(existsSync(marker), false);
}));

for (const [label, prefix] of [['string', 'const lookalike = "import /*";\n'], ['regexp', 'const lookalike = /[import /*]/;\n']])
    test(label + ' lookalike cannot suppress a later actual import keyword', () => fixture(f => {
        const outside = join(f.root, 'outside.mjs'), marker = join(f.root, 'side-effect');
        writeFileSync(outside, "import { writeFileSync } from 'node:fs'; writeFileSync(" + JSON.stringify(marker) + ", 'EXECUTED');");
        writeFileSync(join(f.root, 'scripts/terminfo-owned-runner.mjs'), prefix + 'import ' + JSON.stringify(pathToFileURL(outside).href) + ';\n// */');
        const result = f.run({}); assert.equal(result.status, 1); assert.match(result.stderr, /outside captured scope before import/);
        assert.equal(existsSync(marker), false);
    }));
