#!/usr/bin/env python3
"""Checks ci.yml's .NET gate: scripts/ci/dotnet-gate-paths.sh, the dotnet_detect job and what
depends on it.

The gate skips Build, the Unit Tests shards, Analyzers, Format, Coverage and the Rust FFI tests
on a PR that cannot change their result. Its two ways to go wrong are a skip that should have
been a run (a .NET change going untested, with every check green) and a .NET job nobody gated
(the slots it was meant to free stay taken). So this proves:
  - the path rule, case by case, including that an unlisted path and an empty list both run;
  - that the five jobs which start the .NET and Rust work carry the gate, and that every other
    job that builds or tests .NET or Rust waits for one of them or sits behind another gate;
  - the Unit Tests roll-up's verdict, by running its own shell script with every combination
    that matters: a skip passes only after dotnet_detect said run=false.
Then that each of those checks refuses a mutated workflow.
"""
import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/ci/dotnet-gate-paths.sh'
WORKFLOW = ROOT / '.github/workflows/ci.yml'
GATE = "needs.dotnet_detect.outputs.run == 'true'"
# The jobs that start the .NET and Rust work; everything else that builds waits for `build`.
GATED = ['rust-ffi-tests', 'build', 'analyzers', 'format', 'coverage']
# Gates of their own, which run on any change that matters to their jobs (src/, build inputs,
# workflows) - a superset of what matters to these, so a PR this gate skips never needs them.
OTHER_GATES = ['aot_gate_detect', 'linux_packaging_detect']
BUILDS = re.compile(r'\./\.github/actions/dotnet-sdk|actions/setup-dotnet@|scripts/build\.(sh|ps1)|\bcargo (build|test)\b|\bdotnet (build|test|publish|restore|format)\b')

CASES = [
    # (changed paths, expected)
    (['scripts/terminfo/routed-input-receipt.mjs'], 'false'),
    (['scripts/terminfo/new.test.mjs', 'docs/qa/terminfo/2026-11-11-x/summary.json'], 'false'),
    (['scripts/terminfo-owned.mjs', 'scripts/terminfo-core-report.py'], 'false'),
    (['scripts/ci/node-tests-durations.json', 'scripts/ci/node-tests-shard.py'], 'false'),
    (['.github/workflows/terminfo-linux-launch.yml'], 'false'),
    (['docs/qa/terminfo/2026-11-11-x/README.md', 'scripts/terminfo/x.mjs'], 'false'),
    (['docs/QA_TERMINFO.md', 'scripts/terminfo/x.mjs'], 'true'),
    (['docs/vt-conformance.md'], 'true'),
    (['scripts/terminfo/x.mjs', 'src/Ntilde.VT/AnsiParser.cs'], 'true'),
    (['tests/Ntilde.VT.Tests/x.cs'], 'true'),
    (['.github/workflows/ci.yml'], 'true'),
    (['.github/actions/dotnet-sdk/action.yml'], 'true'),
    (['scripts/build.sh'], 'true'),
    (['scripts/ci/native-libs-present.sh'], 'true'),
    (['scripts/tests/node_tests_shard_tests.py'], 'true'),
    (['.gitattributes', 'scripts/terminfo/x.mjs'], 'true'),
    (['docs/assets/shots/x.png'], 'true'),
    (['scripts/terminfo-new/x.mjs'], 'true'),
    (['global.json'], 'true'),
    ([], 'true'),
    ([''], 'true'),
]


def find_bash():
    for candidate in (r'C:\Program Files\Git\bin\bash.exe', r'C:\Program Files\Git\usr\bin\bash.exe'):
        if Path(candidate).exists():
            return candidate
    return shutil.which('bash')


def jobs(text):
    """{job id: its text}, for every top-level job."""
    matches = list(re.finditer(r'^  ([a-zA-Z_][\w-]*):\s*$', text[text.index('\njobs:\n'):], re.M))
    body = text[text.index('\njobs:\n'):]
    return {m[1]: body[m.start():matches[i + 1].start() if i + 1 < len(matches) else len(body)] for i, m in enumerate(matches)}


def needs(block):
    """The job ids a job's `needs:` names."""
    match = re.search(r'^    needs: (\[[^\]]*\]|\S+)', block, re.M)
    return [] if not match else [n.strip() for n in match[1].strip('[]').split(',') if n.strip()]


def wiring(text):
    """What is wrong with the gate's wiring in this workflow text, as a list of messages."""
    found = []
    all_jobs = jobs(text)
    detect = all_jobs.get('dotnet_detect')
    if not detect:
        return ['no dotnet_detect job']
    if 'bash scripts/ci/dotnet-gate-paths.sh' not in detect or 'git diff --no-renames --name-only HEAD^1 HEAD' not in detect:
        found.append('dotnet_detect does not decide with dotnet-gate-paths.sh on the merge commit against its base')
    if 'fetch-depth: 2' not in detect:
        found.append('dotnet_detect checks out too little history for HEAD^1')
    for name in GATED:
        block = all_jobs.get(name, '')
        if 'dotnet_detect' not in needs(block) or f'    if: {GATE}\n' not in block:
            found.append(f'{name} does not wait for dotnet_detect and skip on run=false')

    def behind_gate(name, seen=()):
        if name in GATED or name in OTHER_GATES:
            return True
        parents = needs(all_jobs.get(name, ''))
        return bool(parents) and any(behind_gate(p, seen + (name,)) for p in parents if p not in seen)

    for name, block in all_jobs.items():
        commands = '\n'.join(line for line in block.splitlines() if not line.lstrip().startswith('#'))
        if BUILDS.search(commands) and not behind_gate(name):
            found.append(f'{name} builds or tests .NET or Rust but no gate decides whether it runs')
    return found


def rollup_script(text):
    """The Unit Tests roll-up's shell script, as bash would get it."""
    block = jobs(text)['unit_tests']
    match = re.search(r'^        run: \|\n((?:          .*\n|\n)+)', block, re.M)
    assert match, 'the roll-up has a run script'
    assert 'dotnet: ${{ needs.dotnet_detect.outputs.run }}' in block and 'dotnet_detect' in needs(block), \
        'the roll-up reads dotnet_detect\'s decision'
    return '\n'.join(line[10:] for line in match[1].splitlines()) + '\n'


ROLLUP = [
    # (dotnet, shards, analyzers, scripts, passes)
    ('true', 'success', 'success', 'success', True),
    ('false', 'skipped', 'skipped', 'success', True),
    ('true', 'skipped', 'skipped', 'success', False),   # run=true and Build did not run
    ('', 'skipped', 'skipped', 'success', False),       # no decision (dotnet_detect failed)
    ('true', 'failure', 'success', 'success', False),   # a failed shard, nothing else wrong
    ('false', 'failure', 'skipped', 'success', False),
    ('false', 'skipped', 'failure', 'success', False),
    ('false', 'skipped', 'skipped', 'failure', False),  # the script self-tests still count
    ('true', 'success', 'cancelled', 'success', False),
]


def rollup_problems(script, bash):
    found = []
    for dotnet, shards, analyzers, scripts, passes in ROLLUP:
        env = {**os.environ, 'dotnet': dotnet, 'result': shards, 'analyzers': analyzers, 'scripts': scripts,
               'clipboard': 'success', 'publication': 'success'}
        ran = subprocess.run([bash, '-c', script], env=env, capture_output=True, text=True)
        if (ran.returncode == 0) != passes:
            found.append(f'roll-up with dotnet={dotnet!r} shards={shards} analyzers={analyzers} scripts={scripts} '
                         f'{"failed" if passes else "passed"}')
    return found


def diff_problems(text, bash):
    """Runs dotnet_detect's own diff command on a scratch repo's merge commit that moves a .NET
    source file under docs/qa/ and changes a terminfo script, then the path rule on its output.
    A rename-detecting diff lists the move by its new path only, which the rule would skip."""
    match = re.search(r'changed="\$\((git diff [^)]*)\)"', jobs(text)['dotnet_detect'])
    if not match:
        return ['dotnet_detect assigns no git diff to `changed`']
    with tempfile.TemporaryDirectory() as tmp:
        env = {**os.environ, 'GIT_CONFIG_GLOBAL': os.devnull, 'GIT_CONFIG_NOSYSTEM': '1',
               'GIT_AUTHOR_NAME': 't', 'GIT_AUTHOR_EMAIL': 't@example.invalid',
               'GIT_COMMITTER_NAME': 't', 'GIT_COMMITTER_EMAIL': 't@example.invalid'}

        def git(*args):
            subprocess.run(['git', *args], cwd=tmp, env=env, check=True, capture_output=True)

        git('init', '-q', '-b', 'main')
        source = Path(tmp) / 'src/Ntilde.VT/AnsiParser.cs'
        source.parent.mkdir(parents=True)
        source.write_text('namespace Ntilde.VT;\n' + 'internal static class AnsiParser { }\n' * 20, encoding='utf-8')
        script = Path(tmp) / 'scripts/terminfo/x.mjs'
        script.parent.mkdir(parents=True)
        script.write_text('export const x = 1;\n', encoding='utf-8')
        git('add', '-A')
        git('commit', '-q', '-m', 'base')
        git('checkout', '-q', '-b', 'pr')
        (Path(tmp) / 'docs/qa').mkdir(parents=True)
        git('mv', 'src/Ntilde.VT/AnsiParser.cs', 'docs/qa/AnsiParser.cs')
        script.write_text('export const x = 2;\n', encoding='utf-8')
        git('commit', '-q', '-am', 'move')
        git('checkout', '-q', 'main')
        git('merge', '-q', '--no-ff', '-m', 'merge', 'pr')
        ran = subprocess.run([bash, '-c', f'{match[1]} | bash {SCRIPT.as_posix()}'], cwd=tmp, env=env, capture_output=True, text=True)
    return [] if ran.stdout == 'run=true\n' else [f'a merge moving src/ under docs/qa/ gave {ran.stdout.strip() or ran.stderr.strip()!r}']


def main():
    bash = find_bash()
    assert bash, 'bash not found'
    for paths, expected in CASES:
        out = subprocess.run([bash, str(SCRIPT)], input=''.join(p + '\n' for p in paths), capture_output=True, text=True)
        assert out.returncode == 0 and out.stdout == f'run={expected}\n', f'{paths}: {out.stdout!r} (exit {out.returncode}), wanted run={expected}'

    text = WORKFLOW.read_text(encoding='utf-8')
    problems = wiring(text)
    assert not problems, problems
    problems = diff_problems(text, bash)
    assert not problems, problems
    script = rollup_script(text)
    problems = rollup_problems(script, bash)
    assert not problems, problems

    build = jobs(text)['build']
    mutants = {
        'build without the gate': wiring(text.replace(build, build.replace(f'    if: {GATE}\n', '', 1), 1)),
        'a new ungated .NET job': wiring(text + '\n  late_tests:\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet test\n'),
        'a diff against the wrong commit': wiring(text.replace('git diff --no-renames --name-only HEAD^1 HEAD', 'git diff --name-only HEAD', 1)),
        'a diff that detects renames': diff_problems(text.replace('git diff --no-renames --name-only', 'git diff --name-only', 1), bash),
        'a roll-up that passes any skip': rollup_problems(script.replace('if [ "$dotnet" = false ] && ', 'if ', 1), bash),
        'a roll-up that ignores the shards': rollup_problems(script.replace('[ "$result" = success ] ||', 'true ||', 1), bash),
    }
    accepted = [label for label, found in mutants.items() if not found]
    assert not accepted, f'accepted: {accepted}'
    print(f'{len(CASES)} path cases, {len(GATED)} gated jobs, {len(ROLLUP)} roll-up verdicts; {len(mutants)} refusals passed')


if __name__ == '__main__':
    main()
