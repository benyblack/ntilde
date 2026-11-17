#!/usr/bin/env python3
"""The real-bundle lane must execute and fail the branch-protected roll-up."""
import os
from pathlib import Path
import re
import shutil
import subprocess

from private_clipboard_gate_tests import jobs
from dotnet_gate_paths_tests import rollup_script

WORKFLOW = Path(__file__).resolve().parents[2] / '.github/workflows/ci.yml'


def validate(text):
    blocks = jobs(text)
    lane = blocks.get('terminfo_real_bundle', '')
    rollup = blocks['unit_tests']
    assert lane, 'missing real-bundle lane'
    assert not re.search(r'^    if:', lane, re.M), 'the evidence lane cannot skip'
    assert 'continue-on-error' not in lane
    assert "node-version: '24'" in lane
    assert "NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED: '1'" in lane
    for name in ('kcl', 'devq', 'mex', 'cell-size', 'osc5'):
        assert f'scripts/terminfo/{name}-grading-collector.test.mjs' in lane
    assert re.search(r'^    needs: \[[^\]]*\bterminfo_real_bundle\b', rollup, re.M)
    assert 'replay: ${{ needs.terminfo_real_bundle.result }}' in rollup
    assert '[ "$replay" = success ] ||' in rollup
    assert '"$py" scripts/tests/real_bundle_gate_tests.py' in blocks['script_self_tests']
    bash = shutil.which('bash')
    assert bash
    for status in ('success', 'failure', 'skipped', 'cancelled', ''):
        environment = {**os.environ, 'dotnet': 'false', 'result': 'skipped', 'analyzers': 'skipped',
                       'scripts': 'success', 'clipboard': 'success', 'publication': 'success', 'replay': status}
        result = subprocess.run([bash, '-c', rollup_script(text)], env=environment, capture_output=True)
        assert (result.returncode == 0) == (status == 'success'), f'roll-up admitted {status!r}'


def main():
    real = WORKFLOW.read_text()
    validate(real)
    mutants = [
        real.replace('          scripts/terminfo/osc5-grading-collector.test.mjs\n', ''),
        real.replace('  terminfo_real_bundle:\n', '  terminfo_real_bundle:\n    if: false\n'),
        real.replace("NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED: '1'", "NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED: '0'"),
        real.replace(', terminfo_real_bundle, analyzers]', ', analyzers]'),
        real.replace('needs.terminfo_real_bundle.result', 'needs.terminfo_real_bundle.outputs'),
        real.replace('[ "$replay" = success ] ||', 'true ||'),
    ]
    for text in mutants:
        assert text != real
        try:
            validate(text)
        except AssertionError:
            continue
        raise AssertionError('weakened real-bundle gate was accepted')
    print('real-bundle required lane, five result states and six wiring refusals passed')


if __name__ == '__main__':
    main()
