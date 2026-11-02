import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from frames import frame_binary, binary_payload, png_pixel, html_fragment

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
            self.assertIn("🧪", values["text/plain"].decode())

    def test_binary_frames_distinguish_payload_from_native_allocation_padding(self):
        payload = b"\0\xffbinary\0"
        framed = frame_binary(payload)
        self.assertEqual(payload, binary_payload(framed))
        self.assertEqual(payload, binary_payload(framed + b"\0" * 16, allow_padding=True))
        for invalid in [b"", b"\0\0", b"\xff" * 4, framed[:-1], framed + b"\0"]:
            with self.assertRaises(ValueError):
                binary_payload(invalid)

    def test_native_png_padding_does_not_change_the_verified_pixel(self):
        image = runner.fixture()["image/png"]
        self.assertEqual(b"\x12\x34\x56\xff", png_pixel(image + b"\0" * 16))
        for invalid in [image[:7], image[:-12], image[:20], image[:-1], image[:30] + b"bad" + image[33:]]:
            with self.assertRaises(ValueError):
                png_pixel(invalid)

    def test_native_html_oracle_checks_context_and_fragment_bounds(self):
        header = b"Version:1.0\r\nStartHTML:-1\r\nEndHTML:-1\r\nStartFragment:%010d\r\nEndFragment:%010d\r\n"
        size = len(header % (0, 0))
        data = header % (size, size + 2) + b"ok"
        self.assertEqual(b"ok", html_fragment(data))
        for invalid in [data.replace(b"EndHTML:-1", b"EndHTML:99"), data.replace(b"StartHTML", b"NoContext"), header % (0, 1)]:
            with self.assertRaises(ValueError):
                html_fragment(invalid)

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
