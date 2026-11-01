import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("run.py")
spec = importlib.util.spec_from_file_location("clipboard_qa_runner", SCRIPT)
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class RunnerTests(unittest.TestCase):
    def test_each_direction_has_distinct_bytes_and_aliases_preserve_binary(self):
        host, peer = runner.fixture(), runner.fixture(peer=True)
        self.assertEqual(host.keys(), peer.keys())
        for mime in host:
            self.assertNotEqual(host[mime], peer[mime], mime)
        for values in [host, peer]:
            self.assertEqual(values["application/x-ntilde-qa"], values["application/x-ntilde-qa-alias"])
            self.assertIn(b"\0", values["application/x-ntilde-qa"])
            self.assertEqual(b"\x89PNG\r\n\x1a\n", values["image/png"][:8])

    def test_personal_desktop_invocation_refuses_before_opening_any_files_or_processes(self):
        environment = dict(os.environ)
        environment.pop("CI", None)
        with tempfile.TemporaryDirectory() as temporary:
            report = Path(temporary) / "report.json"
            result = subprocess.run([sys.executable, str(SCRIPT), "--host", "missing-host", "--peer", "missing-peer", "--report", str(report)],
                                    env=environment, capture_output=True, text=True, timeout=5)
            self.assertEqual(2, result.returncode)
            self.assertIn("isolated CI desktop", result.stderr)
            self.assertFalse(report.exists())


if __name__ == "__main__":
    unittest.main()
