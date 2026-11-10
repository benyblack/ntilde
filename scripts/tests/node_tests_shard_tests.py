#!/usr/bin/env python3
"""Checks that scripts/ci/node-tests-shard.py partitions the workflow's script self-test files.

Reads the real workflow: the matrix must name exactly the shards the script lists, the sharded
step must pass every file through the script, and the shards must cover every file exactly once.
Then proves the refusals: an unknown shard, a stale claim, a repeated file and an empty shard.
"""
import importlib.util
import re
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


def main():
    text = WORKFLOW.read_text()
    block = job(text, 'script_self_tests')
    matrix = re.search(r'^        shard: \[([^\]]+)\]', block, re.M)
    assert matrix, 'the job must have a shard matrix'
    assert [value.strip() for value in matrix[1].split(',')] == shard.names(), 'the matrix must list exactly the script\'s shards, in order'
    assert 'node --test $files' in block and '--test-shard' not in block, 'the step runs the selected files, not a positional shard'
    files = workflow_files(block)
    assert all((ROOT / path).is_file() for path in files), 'every listed test file exists'
    owners = {}
    for name in shard.names():
        for path in shard.select(name, files):
            assert path not in owners, f'{path} runs in {owners[path]} and {name}'
            owners[path] = name
    assert sorted(owners) == sorted(files), 'every file runs in exactly one shard'
    assert shard.names()[-1] == shard.REST and shard.SHARDS[-1][1] == [], 'REST is last and claims nothing'
    # A new file lands in REST; the claimed shards keep their own files.
    assert shard.select(shard.REST, files + ['scripts/terminfo/new.test.mjs'])[-1] == 'scripts/terminfo/new.test.mjs'
    refusals = {
        'unknown shard': lambda: shard.select('other', files),
        'stale claim': lambda: shard.select(shard.REST, [path for path in files if not path.endswith('/query-receipt.test.mjs')]),
        'repeated file': lambda: shard.select(shard.REST, files + files[:1]),
        'empty shard': lambda: shard.select(shard.REST, [path for path in files if owners[path] != shard.REST]),
    }
    for name, call in refusals.items():
        try:
            call()
        except ValueError:
            continue
        raise AssertionError('accepted: ' + name)
    # The workflow reads the output through bash command substitution: it must be LF-only on every OS.
    import subprocess, sys
    output = subprocess.run([sys.executable, str(ROOT / 'scripts/ci/node-tests-shard.py'), shard.REST, *files], capture_output=True, check=True).stdout
    assert b'\r' not in output and output.decode().split() == shard.select(shard.REST, files), 'LF-only, one file per line'
    print(f'{len(files)} files partitioned across {len(shard.names())} shards; {len(refusals)} refusals passed')


if __name__ == '__main__':
    main()
