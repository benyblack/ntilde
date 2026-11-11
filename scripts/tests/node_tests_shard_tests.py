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
import re
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


def main():
    text = WORKFLOW.read_text(encoding='utf-8')
    block = job(text, 'script_self_tests')
    matrix = re.search(r'^        shard: \[([^\]]+)\]', block, re.M)
    assert matrix, 'the job must have a shard matrix'
    assert [value.strip() for value in matrix[1].split(',')] == shard.names(), 'the matrix must list exactly the script\'s shards, in order'
    assert shard.names()[-1] == shard.REST, 'REST, which also runs the quick checks, is last'
    assert re.search(r'node --test .*\$files', block, re.S) and '--test-shard' not in block, 'the step runs the selected files, not a positional shard'
    assert '--test-reporter=./scripts/ci/node-tests-timing.mjs' in block and (ROOT / 'scripts/ci/node-tests-timing.mjs').is_file(), \
        'the step records each file\'s seconds with the timing reporter'
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
    # Longest first: node starts files in the order given, so a long file listed late ends the shard late.
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
    print(f'{len(files)} files cut across {len(shard.names())} shards '
          f'({", ".join(f"{n} {totals[n]:.0f} s" for n in shard.names())}); {len(refusals)} refusals passed')


if __name__ == '__main__':
    main()
