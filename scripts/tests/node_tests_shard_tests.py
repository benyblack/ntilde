#!/usr/bin/env python3
"""Checks that scripts/ci/node-tests-shard.py partitions the workflow's script self-test files.

Reads the real workflow: the matrix must name exactly the shards the script lists, the sharded
step must pass every file through the script (and record each file's seconds), and the shards
must cover every file exactly once. Checks the durations table against that list, that the cut
is balanced and deterministic, that each shard lists its longest files first, and that a file the table does not know still runs. Then proves
the refusals - an unknown shard, a repeated file, too few files, an empty shard - and the
`--update` path that refreshes the table from a CI run's timings.
"""
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / '.github/workflows/ci.yml'
spec = importlib.util.spec_from_file_location('node_tests_shard', ROOT / 'scripts/ci/node-tests-shard.py')
shard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shard)


def job(text, name):
    """One complete top-level job block of the workflow."""
    matches = list(re.finditer(r'^  ([a-zA-Z_][a-zA-Z0-9_]*):\s*$', text, re.M))
    for index, match in enumerate(matches):
        if match[1] == name:
            return text[match.start():matches[index + 1].start() if index + 1 < len(matches) else len(text)]
    raise AssertionError(f'missing job {name}')


def workflow_files(block):
    """The test files the sharded step passes to the script."""
    step = re.search(r'node-tests-shard\.py "\$\{\{ matrix\.shard \}\}" ([^\n)]+)\)', block)
    assert step, 'the sharded step must pass the file list through node-tests-shard.py'
    files = step[1].split()
    assert files and all(path.startswith('scripts/terminfo/') and path.endswith('.test.mjs') for path in files), files
    return files


def refused(call):
    """Whether `call` raises the script's ValueError."""
    try:
        call()
    except ValueError:
        return True
    return False


def node_major():
    """The major version of the `node` on PATH, or 0 when there is none."""
    node = shutil.which('node')
    if not node:
        return 0
    version = subprocess.run([node, '--version'], capture_output=True, text=True).stdout.strip()
    match = re.match(r'v(\d+)\.', version)
    return int(match[1]) if match else 0


def check_launcher():
    """scripts/ci/node-tests-run.mjs starts files in the order given (the CLI would sort them),
    writes each file's seconds and fails when a test fails. Needs node 22 or later (the workflow
    uses 24). Locally an older or missing node skips the check; CI sets
    NODE_TESTS_RUN_CHECK_REQUIRED, which makes that a failure instead."""
    if node_major() < 22:
        assert not os.environ.get('NODE_TESTS_RUN_CHECK_REQUIRED'), 'the launcher check needs node 22+ on PATH'
        return 'not checked (needs node 22+ on PATH)'
    launcher = ROOT / 'scripts/ci/node-tests-run.mjs'
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        log = tmp / 'starts.log'
        for name in ('a', 'b', 'c'):
            (tmp / f'{name}.test.mjs').write_text(
                "import { appendFileSync } from 'node:fs'; import { test } from 'node:test';\n"
                f"appendFileSync({json.dumps(str(log))}, '{name}');\n"
                f"test('{name}', () => {{}});\n", encoding='utf-8')
        (tmp / 'fails.test.mjs').write_text("import { test } from 'node:test'; test('fails', () => { throw new Error('x'); });\n", encoding='utf-8')
        env = {**os.environ, 'NODE_TESTS_RUN_CONCURRENCY': '1'}
        timing = tmp / 'timing.json'
        order = ['c', 'a', 'b']
        ran = subprocess.run(['node', str(launcher), str(timing), *(f'{n}.test.mjs' for n in order)], cwd=tmp, env=env, capture_output=True)
        assert ran.returncode == 0, ran.stdout.decode(errors='replace')[-2000:]
        assert log.read_text(encoding='utf-8') == ''.join(order), f'started {log.read_text(encoding="utf-8")}, not in the order given'
        recorded = sorted(Path(path).name for path in json.loads(timing.read_text(encoding='utf-8')))
        assert recorded == ['a.test.mjs', 'b.test.mjs', 'c.test.mjs'], recorded
        failed = subprocess.run(['node', str(launcher), str(tmp / 'timing2.json'), 'a.test.mjs', 'fails.test.mjs'], cwd=tmp, env=env, capture_output=True)
        assert failed.returncode == 1, 'a failing test fails the launcher'
        recorded = sorted(Path(path).name for path in json.loads((tmp / 'timing2.json').read_text(encoding='utf-8')))
        assert recorded == ['a.test.mjs', 'fails.test.mjs'], f'a failing file\'s seconds are recorded too: {recorded}'
    return 'checked'


def main():
    text = WORKFLOW.read_text(encoding='utf-8')
    block = job(text, 'script_self_tests')
    matrix = re.search(r'^        shard: \[([^\]]+)\]', block, re.M)
    assert matrix, 'the job must have a shard matrix'
    assert [value.strip() for value in matrix[1].split(',')] == shard.names(), 'the matrix must list exactly the script\'s shards, in order'
    assert shard.names()[-1] == shard.REST, 'REST, which also runs the quick checks, is last'
    # The launcher, not `node --test $files`, which sorts the files by name and so would undo the
    # longest-first order; it also records each file's seconds.
    assert re.search(r'^\s*node scripts/ci/node-tests-run\.mjs "\$RUNNER_TEMP/node-test-seconds\.json" \$files$', block, re.M), \
        'the step runs the selected files through scripts/ci/node-tests-run.mjs'
    commands = '\n'.join(line for line in block.splitlines() if not line.lstrip().startswith('#'))
    assert not re.search(r'node --test[^\n]*\$files', commands) and '--test-shard' not in commands, 'the step runs the selected files in order, not through the CLI'
    # This test's own step runs after setup-node and requires the launcher check, so CI never skips it.
    own = block.index('name: Self-test the script self-test shards')
    assert block.index('uses: actions/setup-node@') < own, 'setup-node runs before the shard self-test'
    assert "NODE_TESTS_RUN_CHECK_REQUIRED: '1'" in block[own:block.index('- name:', own + 1)], 'the shard self-test step requires the launcher check'
    files = workflow_files(block)
    assert len(set(files)) == len(files), 'the workflow lists each test file once'
    assert all((ROOT / path).is_file() for path in files), 'every listed test file exists'

    # The table: plain positive numbers, and only files the workflow runs (a stale entry would keep
    # a renamed or deleted file's weight in the cut).
    table = shard.load_table()
    assert isinstance(table.get('rest_head_start_seconds'), (int, float)) and table['rest_head_start_seconds'] >= 0
    seconds = table['seconds']
    assert seconds and all(isinstance(v, (int, float)) and v > 0 for v in seconds.values()), 'every measured duration is a positive number'
    stale = sorted(set(seconds) - set(files))
    assert not stale, f'the durations table lists files the workflow does not run: {stale[:5]}'

    # The partition, with the real table.
    owners = {}
    for name in shard.names():
        for path in shard.select(name, files):
            assert path not in owners, f'{path} runs in {owners[path]} and {name}'
            owners[path] = name
    assert sorted(owners) == sorted(files), 'every file runs in exactly one shard'
    # Longest first: the launcher starts files in the order given, so a long file listed late would end the shard late.
    weight = shard.weights(files, table)  # a file the table does not know yet counts at the median
    for name in shard.names():
        chosen = [weight[path] for path in shard.select(name, files)]
        assert chosen == sorted(chosen, reverse=True), f'shard {name} does not print its files longest first'

    # Balanced: dealing longest-first to the least-loaded shard ends with no shard more than one
    # file's weight above the lightest.
    owner, totals = shard.plan(files)
    assert max(totals.values()) - min(totals.values()) <= max(weight.values()) + 1e-6, f'unbalanced cut: {totals}'
    # Deterministic: the order the workflow lists the files in does not change the cut.
    assert shard.plan(list(reversed(files)))[0] == owner, 'the cut depends on file order'

    # A file the table does not know runs, counted at the median - and does not simply fall to REST
    # (the hand-made claims this replaced did that, which put every new file on one shard).
    new = 'scripts/terminfo/brand-new.test.mjs'
    with_new = files + [new]
    assert sum(new in shard.select(name, with_new) for name in shard.names()) == 1, 'a new file runs in exactly one shard'
    median = sorted(seconds.values())[len(seconds) // 2]
    assert shard.weights([new], table)[new] == median, 'an unknown file counts as the median'
    heavy = [f'scripts/terminfo/new-{i}.test.mjs' for i in range(6)]
    spread = {shard.plan(files + heavy)[0][path] for path in heavy}
    assert len(spread) > 1, f'several new files spread over the shards, not one ({spread})'

    tiny = {'rest_head_start_seconds': 100, 'seconds': {}}
    refusals = {
        'unknown shard': lambda: shard.select('other', files),
        'repeated file': lambda: shard.select(shard.REST, files + files[:1]),
        'too few files': lambda: shard.select(shard.REST, files[:len(shard.names()) - 1]),
        'empty shard': lambda: shard.select(shard.REST, files[:len(shard.names())], tiny),
    }
    accepted = [name for name, call in refusals.items() if not refused(call)]
    assert not accepted, f'accepted: {accepted}'

    # --update folds a reporter's output (absolute paths, either slash, seconds) into the table.
    with tempfile.TemporaryDirectory() as tmp:
        table_path = Path(tmp) / 'durations.json'
        table_path.write_text(json.dumps({'rest_head_start_seconds': 5, 'seconds': {'scripts/terminfo/a.test.mjs': 1.0}}), encoding='utf-8')
        timing = Path(tmp) / 'timing.json'
        timing.write_text(json.dumps({'D:\\a\\ntilde\\ntilde\\scripts\\terminfo\\b.test.mjs': 12.345,
                                      '/home/runner/work/ntilde/ntilde/scripts/terminfo/a.test.mjs': 3.04,
                                      '/home/runner/work/ntilde/ntilde/scripts/terminfo/c.test.mjs': 0.004}), encoding='utf-8')
        assert shard.update([timing], table_path) == 3
        updated = json.loads(table_path.read_text(encoding='utf-8'))
        assert updated == {'rest_head_start_seconds': 5,
                           'seconds': {'scripts/terminfo/a.test.mjs': 3.0, 'scripts/terminfo/b.test.mjs': 12.3,
                                       'scripts/terminfo/c.test.mjs': 0.1}}, updated

    # The workflow reads the output through bash command substitution: it must be LF-only on every OS.
    output = subprocess.run([sys.executable, str(ROOT / 'scripts/ci/node-tests-shard.py'), shard.REST, *files], capture_output=True, check=True).stdout
    assert b'\r' not in output and output.decode().split() == shard.select(shard.REST, files), 'LF-only, one file per line'
    launcher = check_launcher()
    print(f'{len(files)} files cut across {len(shard.names())} shards '
          f'({", ".join(f"{n} {totals[n]:.0f} s" for n in shard.names())}); {len(refusals)} refusals passed; launcher {launcher}')


if __name__ == '__main__':
    main()
