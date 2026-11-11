#!/usr/bin/env python3
"""Mutation checks for the required owned-private-clipboard CI evidence lane."""
from pathlib import Path
import re

WORKFLOW = Path(__file__).resolve().parents[2] / '.github/workflows/ci.yml'
TESTS = ['ownership', 'private-clipboard-request', 'private-clipboard-source',
         'private-clipboard-controller', 'private-clipboard-bridge',
         'private-clipboard-worker', 'private-clipboard-collector',
         'private-clipboard-capture', 'private-clipboard-errors', 'private-clipboard-receipt',
         # The schema12 receipt tests, split over three files so they run in parallel processes.
         'private-clipboard-receipt-refusals', 'private-clipboard-receipt-prefix']
TERMINFO = WORKFLOW.parents[2] / 'scripts/terminfo'


def jobs(text):
    """Preserve complete anchored top-level job blocks for exact required-lane checks."""
    matches = list(re.finditer(r'^  ([a-zA-Z_][a-zA-Z0-9_]*):\s*$', text, re.M))
    return {match[1]: text[match.start():matches[index + 1].start() if index + 1 < len(matches) else len(text)]
            for index, match in enumerate(matches)}


def validate(text):
    """Reject missing or suppressible private controls and any rollup that can accept a non-success result."""
    blocks = jobs(text)
    lane, rollup, scripts = (blocks.get(key, '') for key in ('private_clipboard_owned', 'unit_tests', 'script_self_tests'))
    assert lane and rollup and scripts, 'missing evidence or required-rollup job'
    assert not re.search(r'^    if:', lane, re.M), 'evidence job must run on every CI event'
    assert 'continue-on-error' not in lane, 'the evidence job cannot suppress a failed test step'
    assert 'timeout-minutes: 10' in lane and "node-version: '24'" in lane
    assert 'os: [windows-latest, ubuntu-latest]' in lane and 'fail-fast: false' in lane
    for name in TESTS:
        # Whole names: private-clipboard-receipt must not count as present through private-clipboard-receipt-prefix.
        assert re.search(r'[ /]' + re.escape(name) + r'\.test\.mjs(\s|$)', lane), 'missing test ' + name
    needs = re.search(r'^    needs: \[([^\]]+)\]', rollup, re.M)
    assert needs and 'private_clipboard_owned' in [value.strip() for value in needs[1].split(',')]
    assert 'if: ${{ always() }}' in rollup
    assert 'clipboard: ${{ needs.private_clipboard_owned.result }}' in rollup
    assert '[ "$clipboard" = success ] ||' in rollup, 'skip, cancellation and failure cannot qualify'
    assert '"$py" scripts/tests/private_clipboard_gate_tests.py' in scripts


def main():
    """Check the real workflow, then prove every deliberate weakening fails without running CI or a desktop."""
    real = WORKFLOW.read_text()
    validate(real)
    # No other job runs these files, so one this list does not name - a further split, say - would
    # never run in CI.
    unlisted = sorted(path.name for path in TERMINFO.glob('private-clipboard*.test.mjs') if path.name[:-len('.test.mjs')] not in TESTS)
    assert not unlisted, f'private clipboard test files the required lane does not run: {unlisted}'
    mutants = {
        'removed job': real.replace('\n  private_clipboard_owned:\n', '\n  private_clipboard_removed:\n'),
        'conditional job': real.replace('  private_clipboard_owned:\n', '  private_clipboard_owned:\n    if: false\n'),
        'missing prerequisite': real.replace('query_publication_guards, private_clipboard_owned, analyzers]', 'query_publication_guards, analyzers]'),
        'missing result': real.replace('needs.private_clipboard_owned.result', 'needs.private_clipboard_owned.outputs'),
        'ignored failure': real.replace('[ "$clipboard" = success ] ||', '[ true ] ||'),
        'lost full finalizer': real.replace(' scripts/terminfo/private-clipboard-receipt.test.mjs', ''),
        'lost finalizer refusals': real.replace(' scripts/terminfo/private-clipboard-receipt-refusals.test.mjs', ''),
        'lost finalizer prefix checks': real.replace(' scripts/terminfo/private-clipboard-receipt-prefix.test.mjs', ''),
        'ignored step failure': real.replace('name: Source-pinned callbacks, parent controls and complete schema12 receipt\n        shell: bash\n',
                                            'name: Source-pinned callbacks, parent controls and complete schema12 receipt\n        shell: bash\n        continue-on-error: true\n'),
    }
    for name, text in mutants.items():
        assert text != real, 'mutation did not apply: ' + name
        try:
            validate(text)
        except AssertionError:
            continue
        raise AssertionError('incorrect evidence wiring was accepted: ' + name)
    print(f'private clipboard required lane and {len(mutants)} wiring refusals passed')


if __name__ == '__main__':
    main()
