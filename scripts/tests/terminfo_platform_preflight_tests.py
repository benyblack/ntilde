"""The environment inventory must never turn hints into live qualification."""

from contextlib import redirect_stdout
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / "terminfo-platform-preflight.py"
SPEC = importlib.util.spec_from_file_location("terminfo_platform_preflight", SCRIPT)
preflight = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(preflight)


class PlatformPolicyTests(unittest.TestCase):
    def test_all_platforms_stay_unqualified_even_with_positive_hints(self):
        hints = {"toolsOnPath": {tool: True for tool in preflight.TOOLS},
                 "stdio": {"stdinIsTty": True, "stdoutIsTty": True},
                 "displayHints": {"x11VariablePresent": True, "waylandVariablePresent": True}}
        for platform in ("darwin", "linux", "win32", "freebsd", "unknown"):
            with self.subTest(platform=platform):
                report = preflight.build_report(platform, hints, "2026-11-04T00:00:00Z")
                self.assertEqual("unqualified", report["qualification"])
                self.assertIs(False, report["liveOwnershipVerified"])
                self.assertIn("app-worker-and-selected-output-ownership", report["unverifiedLiveGates"])
                self.assertIn("interruption-resistant-worker-exit-and-complete-cleanup", report["unverifiedLiveGates"])
                self.assertEqual(hints, report["hints"])

    def test_source_platform_policy_does_not_claim_other_backends(self):
        for platform, implemented in (("darwin", True), ("linux", False), ("win32", False), ("other", False)):
            with self.subTest(platform=platform):
                report = preflight.build_report(platform, {}, "fixed")
                self.assertEqual(implemented, report["adapterPlatformImplemented"])
                self.assertEqual(not implemented, bool(report["implementationBlockers"]))

    def test_platform_blockers_distinguish_linux_and_windows(self):
        linux = preflight.build_report("linux", {}, "fixed")["implementationBlockers"]
        windows = preflight.build_report("win32", {}, "fixed")["implementationBlockers"]
        self.assertIn("linux-process-start-and-pty-identity-not-implemented", linux)
        self.assertIn("windows-process-token-start-and-conpty-identity-not-implemented", windows)
        self.assertNotEqual(linux, windows)


class HintCollectionTests(unittest.TestCase):
    def test_environment_values_and_personal_paths_are_not_emitted(self):
        env = {"DISPLAY": "private-display-address", "WAYLAND_DISPLAY": "private-socket",
               "XDG_SESSION_TYPE": "private-arbitrary-value", "SSH_CONNECTION": "private-addresses",
               "TOKEN": "secret", "HOME": "/private/user"}
        with patch.dict(os.environ, env, clear=True), patch.object(sys, "platform", "darwin"), \
                patch.object(preflight.shutil, "which", return_value="/private/bin/node"):
            hints = preflight.collect_hints()
        serialized = json.dumps(hints)
        for value in env.values():
            self.assertNotIn(value, serialized)
        self.assertNotIn("/private/bin/node", serialized)
        self.assertTrue(hints["displayHints"]["x11VariablePresent"])
        self.assertTrue(hints["displayHints"]["sshVariablePresent"])
        self.assertEqual("other", hints["displayHints"]["xdgSessionType"])
        self.assertIsNone(hints["linuxFilesystemHints"])

    def test_known_session_types_are_only_hints(self):
        for value in ("x11", "wayland", "tty", "", "WAYLAND"):
            with self.subTest(value=value), patch.dict(os.environ, {"XDG_SESSION_TYPE": value}, clear=True):
                hints = preflight.collect_hints()
                report = preflight.build_report("linux", hints, "fixed")
                self.assertEqual(value.lower() or "unset", hints["displayHints"]["xdgSessionType"])
                self.assertEqual("unqualified", report["qualification"])

    def test_missing_tools_and_non_tty_do_not_raise_or_become_successful_qualification(self):
        with patch.object(preflight.shutil, "which", return_value=None), \
                patch.object(preflight, "_terminal_hint", return_value=False):
            report = preflight.build_report("win32", preflight.collect_hints(), "fixed")
        self.assertFalse(any(report["hints"]["toolsOnPath"].values()))
        self.assertEqual({"stdinIsTty": False, "stdoutIsTty": False}, report["hints"]["stdio"])
        self.assertEqual("unqualified", report["qualification"])

    def test_unavailable_stream_is_unknown(self):
        self.assertIsNone(preflight._terminal_hint(None))
        stream = unittest.mock.Mock()
        stream.isatty.side_effect = OSError("closed")
        self.assertIsNone(preflight._terminal_hint(stream))

    def test_only_linux_checks_own_proc_and_pts_metadata(self):
        with patch.object(sys, "platform", "linux"), patch.object(preflight.os, "access", return_value=False) as access, \
                patch.object(preflight, "_directory_hint", return_value=True) as directory, \
                patch.object(preflight.shutil, "which", return_value=None):
            hints = preflight.collect_hints()
        self.assertEqual({"procSelfStatReadable": False, "ptsDirectoryPresent": True}, hints["linuxFilesystemHints"])
        access.assert_called_once_with("/proc/self/stat", os.R_OK)
        directory.assert_called_once_with("/dev/pts")
        with patch.object(sys, "platform", "win32"), patch.object(preflight.os, "access") as access, \
                patch.object(preflight.shutil, "which", return_value=None):
            self.assertIsNone(preflight.collect_hints()["linuxFilesystemHints"])
        access.assert_not_called()


class CommandTests(unittest.TestCase):
    def test_json_stdout_is_inventory_not_a_qualification_exit_status(self):
        output = io.StringIO()
        with redirect_stdout(output), patch.object(sys, "platform", "linux"), \
                patch.object(preflight, "collect_hints", return_value={"toolsOnPath": {"node": False}}):
            exit_code = preflight.main([])
        report = json.loads(output.getvalue())
        self.assertEqual(0, exit_code)
        self.assertEqual("environment-inventory-only", report["purpose"])
        self.assertEqual("unqualified", report["qualification"])
        self.assertFalse(report["liveOwnershipVerified"])
        self.assertTrue(report["capturedAtUtc"].endswith("Z"))

    def test_cli_cannot_override_platform_or_mark_a_host_qualified(self):
        for args in (["--platform", "darwin"], ["--qualified"], ["--launch"]):
            with self.subTest(args=args), patch.object(sys, "stderr", io.StringIO()):
                with self.assertRaises(SystemExit) as result:
                    preflight.main(args)
                self.assertEqual(2, result.exception.code)


if __name__ == "__main__":
    unittest.main()
