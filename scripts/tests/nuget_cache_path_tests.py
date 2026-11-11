#!/usr/bin/env python3
"""Checks that every NuGet cache in ci.yml caches the folder the job's dotnet actually uses.

On windows, .github/actions/dotnet-sdk points NUGET_PACKAGES at the workspace disk (see the step
there for why). A job that runs that action and then caches `~/.nuget/packages` would restore
into a folder nothing reads and, on a miss, save an empty one - CI stays green and only gets
slower. So in such a job the cache path must be NUGET_PACKAGES, falling back to the profile
folder where the variable is not set, and the action must run before the cache step.

Reads the real action and workflow, then proves the refusals on mutated copies.
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / '.github/workflows/ci.yml'
ACTION = ROOT / '.github/actions/dotnet-sdk/action.yml'
SDK = 'uses: ./.github/actions/dotnet-sdk'
FOLLOWS = "path: ${{ env.NUGET_PACKAGES || '~/.nuget/packages' }}"


def jobs(text):
    """Each top-level job's name and text."""
    matches = list(re.finditer(r'^  ([a-zA-Z_][\w-]*):\s*$', text, re.M))
    for index, match in enumerate(matches):
        yield match[1], text[match.start():matches[index + 1].start() if index + 1 < len(matches) else len(text)]


def nuget_caches(block):
    """(offset, text) of each step in `block` that caches NuGet packages."""
    steps = list(re.finditer(r'^      - ', block, re.M))
    for index, step in enumerate(steps):
        body = block[step.start():steps[index + 1].start() if index + 1 < len(steps) else len(block)]
        if re.search(r'uses: actions/cache(/restore|/save)?@', body) and re.search(r'^\s+key: nuget-', body, re.M):
            yield step.start(), body


def problems(workflow, action):
    """What is wrong with this workflow and action text, as a list of messages."""
    found = []
    # The runner evaluates `${{ }}` in an action's name and description too, where `env` is not
    # available - quoting the cache path there made the action fail to load on every OS.
    if '${{' in action.split('\nruns:', 1)[0]:
        found.append('the .NET SDK action has an expression in its name or description, which fails the action on load')
    step = re.search(r'- name: Keep NuGet packages on the workspace disk \(windows\)\n(.*?)(?=\n    - |\Z)', action, re.S)
    if not step:
        found.append('the .NET SDK action no longer sets NUGET_PACKAGES')
    else:
        if "if: runner.os == 'Windows'" not in step[1]:
            found.append('the .NET SDK action sets NUGET_PACKAGES beyond windows')
        if 'NUGET_PACKAGES=' not in step[1] or 'GITHUB_ENV' not in step[1] or 'RUNNER_TEMP' not in step[1]:
            found.append('the .NET SDK action does not put NUGET_PACKAGES under RUNNER_TEMP for later steps')
    checked = 0
    for name, block in jobs(workflow):
        sdk = block.find(SDK)
        for offset, body in nuget_caches(block):
            if sdk < 0:
                continue  # dotnet is set up some other way here; NUGET_PACKAGES is not in play
            checked += 1
            if offset < sdk:
                found.append(f'{name}: the NuGet cache step runs before the .NET SDK action has set NUGET_PACKAGES')
            if FOLLOWS not in body:
                found.append(f'{name}: the NuGet cache path is not NUGET_PACKAGES')
    if checked == 0:
        found.append('no NuGet cache step found after the .NET SDK action - the check went vacuous')
    return found, checked


def main():
    workflow = WORKFLOW.read_text(encoding='utf-8')
    action = ACTION.read_text(encoding='utf-8')
    found, checked = problems(workflow, action)
    assert not found, found

    first = workflow.index(FOLLOWS)
    reverted = workflow[:first] + 'path: ~/.nuget/packages' + workflow[first + len(FOLLOWS):]
    # The build job: its cache step moved above the .NET SDK action.
    build = dict(jobs(workflow))['build']
    sdk_step = '      - name: .NET SDK\n        uses: ./.github/actions/dotnet-sdk\n'
    assert sdk_step in build, 'the build job runs the .NET SDK action as its own step'
    moved = workflow.replace(build, build.replace(sdk_step, '', 1).replace('      - name: Build\n', sdk_step + '\n      - name: Build\n', 1), 1)
    mutants = {
        'a cache path left on the profile folder': (reverted, action),
        'a cache step before the .NET SDK action': (moved, action),
        'the action no longer sets NUGET_PACKAGES': (workflow, action.replace('NUGET_PACKAGES=', 'NUGET_PACKAGE_DIR=')),
        'the action sets it on every OS': (workflow, action.replace("      if: runner.os == 'Windows'\n      shell: pwsh", '      shell: pwsh', 1)),
        'an expression in the action description': (workflow, action.replace('whose path must follow', "whose path must be ${{ env.NUGET_PACKAGES }}, following", 1)),
    }
    accepted = [label for label, (w, a) in mutants.items() if not problems(w, a)[0]]
    assert not accepted, f'accepted: {accepted}'
    print(f'{checked} NuGet cache steps follow NUGET_PACKAGES; {len(mutants)} refusals passed')


if __name__ == '__main__':
    main()
