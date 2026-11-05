"""Actual private fork/pidfd controls; no App/desktop or personal process enumeration."""
import errno
import hashlib
import importlib.util
import os
from pathlib import Path
import signal
import sys
import time
import unittest
from unittest.mock import patch

if sys.platform != "linux":
    raise unittest.SkipTest("Actual kernel launch controls require Linux.")
SOURCE = Path(__file__).with_name("helper.py")
spec = importlib.util.spec_from_file_location("owned_launch_helper", SOURCE)
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class LaunchHelperTests(unittest.TestCase):
    def test_pinned_helper_source_matches_node_admission(self):
        expected = hashlib.sha256(SOURCE.read_bytes()).hexdigest()
        platform = SOURCE.with_name("platform.mjs").read_text()
        self.assertIn("export const helperSha256 = '" + expected + "';", platform)

    def test_duplicate_json_keys_are_refused(self):
        with self.assertRaises(ValueError):
            helper.unique([("seq", 1), ("seq", 2)])

    def test_deadline_refuses_noncanonical_and_expired_values(self):
        for value in (0, "01", "-1", "1", "9" * 21, "NaN", "1.0"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                helper.deadline(value)

    def test_runtime_clears_inherited_sigchld_ignore(self):
        old = signal.getsignal(signal.SIGCHLD)
        try:
            signal.signal(signal.SIGCHLD, signal.SIG_IGN)
            helper.runtime_check()
            self.assertEqual(signal.getsignal(signal.SIGCHLD), signal.SIG_DFL)
        finally:
            signal.signal(signal.SIGCHLD, old)

    def test_fork_failure_closes_every_partially_opened_pipe(self):
        child = helper.Child({})
        closed = []
        with patch.object(helper, "file_check"), patch.object(helper.os, "pipe2", side_effect=[(900, 901), OSError(errno.EMFILE, "controlled")]), patch.object(helper.os, "close", side_effect=closed.append), patch.object(helper.os, "fork") as fork:
            with self.assertRaises(OSError):
                child.prepare(helper.now() + helper.PHASE)
        self.assertEqual(closed, [900, 901])
        fork.assert_not_called()
        self.assertFalse(child.state["forked"])

    def test_repeated_close_never_retries_unknown_fds_or_manufactures_wait(self):
        child = helper.Child({})
        child.pidfd, child.output, child.gate, child.exec_error = 900, 901, 902, 903
        closed = []
        def close(fd):
            closed.append(fd)
            raise OSError(errno.EIO, "controlled close failure")
        with patch.object(helper.os, "close", side_effect=close):
            child.close()
            child.close()
        self.assertEqual(sorted(closed), [3, 900, 901, 902, 903])
        self.assertFalse(child.state["appReaped"])
        self.assertFalse(child.state["appLogClosed"])
        self.assertFalse(child.state["childPipesClosed"])

    def test_nonparent_echild_never_sets_a_consuming_wait_fact(self):
        helper.runtime_check()
        # A read-only pidfd for this test process cannot be waited on as our child.
        child = helper.Child({})
        child.pid = os.getpid()
        child.pidfd = os.pidfd_open(os.getpid(), 0)
        try:
            with self.assertRaises(ChildProcessError):
                child.reap()
            self.assertFalse(child.state["childReaped"])
            self.assertFalse(child.state["appReaped"])
            self.assertIsNone(child.state["wait"])
        finally:
            os.close(child.pidfd)
            child.pidfd = None

    def test_early_exited_original_child_is_retained_until_pidfd_acquisition(self):
        helper.runtime_check()
        child = helper.Child({})
        original_open = os.pidfd_open
        retained = []
        def after_zombie(pid, flags):
            until = helper.now() + helper.PHASE
            while helper.now() < until:
                result = os.waitid(os.P_PID, pid, os.WEXITED | os.WNOHANG | os.WNOWAIT)
                if result is not None:
                    retained.append((result.si_pid, result.si_status))
                    return original_open(pid, flags)
                time.sleep(0.001)
            self.fail("The controlled child did not reach its retained zombie state.")
        # Only the test-owned fork branch is replaced, before it can execute App bytes.
        child.child_main = lambda *_args: os._exit(7)
        try:
            with patch.object(helper, "file_check"), patch.object(helper.os, "pidfd_open", side_effect=after_zombie):
                with self.assertRaises(RuntimeError):
                    child.prepare(helper.now() + helper.PHASE)
            child.pump(False)
            self.assertEqual(retained, [(child.pid, 7)])
            self.assertTrue(child.state["pidfdOpened"])
            self.assertTrue(child.state["childReaped"])
            self.assertTrue(child.state["preExecChildReaped"])
            self.assertFalse(child.state["gateReleased"])
            self.assertFalse(child.state["appReaped"])
            self.assertEqual(child.state["wait"]["source"], "P_PIDFD")
            self.assertEqual(child.state["wait"]["status"], 7)
        finally:
            child.log_close_attempted = True  # No production log fd was installed in this unit seam.
            child.close()

    def test_failed_first_pidfd_reaps_only_original_unexecuted_direct_child(self):
        helper.runtime_check()
        child = helper.Child({})
        child.child_main = lambda *_args: os._exit(0)
        try:
            with patch.object(helper, "file_check"), patch.object(helper.os, "pidfd_open", side_effect=OSError(errno.EMFILE, "controlled")):
                with self.assertRaises(OSError):
                    child.prepare(helper.now() + helper.PHASE)
            self.assertTrue(child.state["preExecChildReaped"])
            self.assertFalse(child.state["appReaped"])
            self.assertFalse(child.state["pidfdOpened"])
            self.assertFalse(child.state["gateReleased"])
            self.assertEqual(child.state["wait"]["source"], "waitpid-preexec")
            self.assertEqual(child.state["wait"]["status"], 0)
        finally:
            child.log_close_attempted = True
            child.close()

    def test_signal_actions_are_fixed_original_pidfd_and_attempted_at_most_once(self):
        child = helper.Child({})
        child.pidfd = 900
        sent = []
        with patch.object(helper.signal, "pidfd_send_signal", side_effect=lambda fd, sig: sent.append((fd, sig))):
            for sig in (signal.SIGTERM, signal.SIGTERM, signal.SIGKILL, signal.SIGKILL):
                child.send(sig, helper.now() + helper.PHASE)
        self.assertEqual(sent, [(900, signal.SIGTERM), (900, signal.SIGKILL)])
        self.assertEqual(len(child.state["attempts"]), 2)

    def test_expired_signal_deadline_makes_no_kernel_signal_call(self):
        child = helper.Child({})
        child.pidfd = 900
        with patch.object(helper.signal, "pidfd_send_signal") as send:
            with self.assertRaises(TimeoutError):
                child.send(signal.SIGTERM, helper.now() - 1)
        send.assert_not_called()
        self.assertEqual(child.state["attempts"], [])

    def test_ready_pipe_eof_without_ready_refuses_setup(self):
        child = helper.Child({})
        read, write = os.pipe2(os.O_NONBLOCK | os.O_CLOEXEC)
        child.exec_error = read
        os.close(write)
        child.pump_exec()
        self.assertTrue(child.state["aborted"])
        self.assertFalse(child.state["preExecReady"])
        self.assertFalse(child.state["admitted"])
        self.assertFalse(child.state["appReaped"])
        self.assertIsNone(child.exec_error)

    def test_ready_and_cloexec_eof_are_not_app_admission_or_reaping(self):
        child = helper.Child({})
        read, write = os.pipe2(os.O_NONBLOCK | os.O_CLOEXEC)
        child.exec_error = read
        os.write(write, b"R")
        os.close(write)
        child.pump_exec()
        child.pump_exec()
        self.assertTrue(child.state["preExecReady"])
        self.assertTrue(child.state["execPipeClosed"])
        self.assertFalse(child.state["admitted"])
        self.assertFalse(child.state["appReaped"])

    def test_setup_errno_before_ready_never_claims_exec_or_app_wait(self):
        child = helper.Child({})
        read, write = os.pipe2(os.O_NONBLOCK | os.O_CLOEXEC)
        child.exec_error = read
        os.write(write, b"E" + helper.struct.pack("<I", errno.EINVAL))
        os.close(write)
        child.pump_exec()
        child.pump_exec()
        self.assertEqual(child.state["childErrno"], errno.EINVAL)
        self.assertTrue(child.state["aborted"])
        self.assertFalse(child.state["preExecReady"])
        self.assertFalse(child.state["gateReleased"])
        self.assertFalse(child.state["appReaped"])

    def test_repeated_fork_cannot_extend_original_hard_deadline(self):
        child = helper.Child({})
        child.pid = 900
        child.hard_deadline = 123
        with self.assertRaises(ValueError):
            child.prepare(helper.now() + helper.PHASE, helper.now() + 300000000000)
        self.assertEqual(child.hard_deadline, 123)

    def test_expired_setup_deadline_does_not_read_or_release_gate(self):
        child = helper.Child({})
        with patch.object(child, "pump_exec") as pump:
            with self.assertRaises(TimeoutError):
                child.await_ready(helper.now() - 1)
        pump.assert_not_called()
        self.assertFalse(child.state["gateReleased"])


if __name__ == "__main__":
    unittest.main()
