"""Deterministic kernel/clock seams only; these tests never address a real target."""
import importlib.util
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("pidfd_under_test", Path(__file__).with_name("pidfd-helper.py"))
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


@unittest.skipUnless(sys.platform == 'linux', 'pidfd helper kernel controls require Linux')
class DomainTests(unittest.TestCase):
    def test_partial_acquisition_closes_only_acquired_original_fd(self):
        target = helper.Targets()
        with patch.object(helper.os, "pidfd_open", side_effect=[77, OSError("gone")]) as opened, patch.object(helper.os, "close") as close:
            with self.assertRaises(OSError):
                target.open(40, 42)
            self.assertEqual(opened.call_args_list[0].args, (40, 0))
            self.assertEqual(opened.call_args_list[1].args, (42, 0))
            close.assert_called_once_with(77)
            self.assertTrue(target.closed)

    def test_close_failure_never_retries_potentially_reused_numeric_fd(self):
        target = helper.Targets()
        target.fds = {"app": 77}
        with patch.object(helper.os, "close", side_effect=OSError("uncertain")) as close:
            with self.assertRaises(RuntimeError):
                target.close()
            target.close()
            close.assert_called_once_with(77)
            self.assertFalse(target.closed)

    def test_deadline_is_checked_immediately_before_signal_syscall(self):
        target = helper.Targets()
        target.fds = {"app": 77, "worker": 78}
        target.poll = lambda: None
        with patch.object(helper, "now", return_value=100), patch.object(helper.signal, "pidfd_send_signal") as send:
            with self.assertRaises(TimeoutError):
                target.send("worker", "TERM", 100)
            send.assert_not_called()

    def test_fixed_term_kill_escalation_uses_same_original_worker_fd(self):
        target = helper.Targets()
        target.fds = {"app": 77, "worker": 78}
        target.poll = lambda: None
        target.wait = lambda _role, _deadline: None
        calls = []

        def send(fd, number, info, flags):
            calls.append((fd, number, info, flags))
            if number == helper.signal.SIGKILL:
                target.exited["worker"] = True

        with patch.object(helper, "now", return_value=100), patch.object(helper.signal, "pidfd_send_signal", side_effect=send):
            target.terminate("worker", 2000000000)
        self.assertEqual(calls, [(78, helper.signal.SIGTERM, None, 0), (78, helper.signal.SIGKILL, None, 0)])

    def test_hup_alone_does_not_claim_exit_or_reap(self):
        target = helper.Targets()
        target.fds = {"worker": 78}

        class Poll:
            def register(self, fd, events):
                self.fd = fd

            def poll(self, _timeout):
                return [(self.fd, helper.select.POLLHUP)]

        with patch.object(helper.select, "poll", Poll):
            target.poll()
        self.assertFalse(target.exited["worker"])
        self.assertFalse(hasattr(target, "workerReaped"))

    def test_poll_error_is_not_exit_observation(self):
        target = helper.Targets()
        target.fds = {"worker": 78}

        class Poll:
            def register(self, fd, events):
                pass

            def poll(self, _timeout):
                return [(78, helper.select.POLLNVAL)]

        with patch.object(helper.select, "poll", Poll), self.assertRaises(RuntimeError):
            target.poll()
        self.assertFalse(target.exited["worker"])


@unittest.skipUnless(sys.platform == 'linux', 'pidfd helper protocol controls require Linux')
class ProtocolTests(unittest.TestCase):
    def execute(self, suffix, expire=False, abort_fails=False):
        clock = [100]
        requests = [
            {"op": "open", "seq": 1, "appPid": 40, "workerPid": 42, "lifetimeMs": 20000},
            {"op": "clock", "seq": 2},
            {"op": "admit", "seq": 3, "deadlineNs": "1000000000", "lifetimeDeadlineNs": "18000000000"},
        ] + suffix

        class Transport:
            def __init__(self):
                self.responses = []

            def read(self, _deadline):
                if not requests:
                    if expire:
                        clock[0] = 18000000001
                    raise EOFError()
                return requests.pop(0)

            def write(self, value, deadline):
                self.responses.append(value)

        class Targets:
            def __init__(self):
                self.closed = False
                self.exited = {"app": False, "worker": False}
                self.aborts = 0
                self.abort_deadlines = []
                self.actions = []

            def open(self, app, worker):
                self.original = (app, worker)

            def poll(self):
                pass

            def observation(self):
                return {}

            def close(self):
                self.closed = True

            def abort(self, deadline):
                self.aborts += 1
                self.abort_deadlines.append(deadline)
                if abort_fails:
                    raise RuntimeError('synthetic abort failure')

            def terminate(self, role, deadline):
                self.actions.append(role)

        transport, targets = Transport(), Targets()
        with patch.object(helper, "Transport", return_value=transport), patch.object(helper, "Targets", return_value=targets), \
                patch.object(helper, "now", side_effect=lambda: clock[0]), patch.object(sys, "argv", ["test-helper"]):
            try:
                helper.run()
                error = None
            except (EOFError, TimeoutError, RuntimeError) as failure:
                error = failure.errors[0] if isinstance(failure, helper.LifecycleFailure) else failure
        return transport, targets, error

    def test_normal_close_disarms_admitted_eof_cleanup(self):
        _, target, error = self.execute([{"op": "clock", "seq": 4}, {"op": "close", "seq": 5, "deadlineNs": "1000000000"}])
        self.assertIsNone(error)
        self.assertTrue(target.closed)
        self.assertEqual(target.aborts, 0)
        self.assertEqual(target.actions, [])

    def test_admitted_eof_uses_original_cleanup_authority(self):
        _, target, error = self.execute([])
        self.assertIsInstance(error, EOFError)
        self.assertEqual(target.original, (40, 42))
        self.assertEqual(target.aborts, 1)
        self.assertLessEqual(target.abort_deadlines[0] - 100, helper.RESERVE_NS - helper.CLOSE_NS)
        self.assertTrue(target.closed)

    def test_no_signal_after_original_hard_lifetime_expired(self):
        _, target, error = self.execute([], expire=True)
        self.assertIsInstance(error, EOFError)
        self.assertEqual(target.aborts, 0)
        self.assertEqual(target.actions, [])
        self.assertTrue(target.closed)

    def test_expired_queued_action_does_not_execute(self):
        _, target, error = self.execute([{"op": "clock", "seq": 4}, {"op": "terminateWorker", "seq": 5, "deadlineNs": "99"}])
        self.assertIsInstance(error, TimeoutError)
        self.assertEqual(target.actions, [])
        self.assertEqual(target.aborts, 1)  # Separate pre-authorized overall abort reserve.

    def test_explicit_abort_failure_does_not_restart_with_a_fresh_budget(self):
        _, target, error = self.execute([{"op": "clock", "seq": 4}, {"op": "abort", "seq": 5, "deadlineNs": "1000000000"}], abort_fails=True)
        self.assertIsInstance(error, RuntimeError)
        self.assertEqual(target.aborts, 1)
        self.assertTrue(target.closed)

    def test_eof_primary_is_preserved_when_reserved_abort_also_fails(self):
        _, target, error = self.execute([], abort_fails=True)
        self.assertIsInstance(error, EOFError)
        self.assertEqual(target.aborts, 1)
        self.assertTrue(target.closed)


if __name__ == "__main__":
    unittest.main()
