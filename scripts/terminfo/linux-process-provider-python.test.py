"""Synthetic signal/reap races only: patched waitpid/kill, no real process is signalled."""
import importlib.util
from pathlib import Path
import signal
import unittest
from unittest.mock import patch

source = Path(__file__).parent / "fixtures" / "linux-provider-pty.py"
spec = importlib.util.spec_from_file_location("linux_provider_fixture", source)
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)


class OwnedChildTests(unittest.TestCase):
    def test_pending_term_after_waitpid_sees_recorded_reap_and_never_signals(self):
        child = fixture.OwnedChild()
        child.pid = 42

        blocked = False

        def waitpid(_pid, _flags):
            self.assertTrue(blocked)
            return 42, 0

        def mask(how, _signals):
            nonlocal blocked
            if how == signal.SIG_BLOCK:
                blocked = True
            if how == signal.SIG_SETMASK:
                self.assertTrue(child.reaped)
                self.assertEqual(child.status, 0)
                raise SystemExit(143)
            return set()

        with patch.object(fixture.signal, "pthread_sigmask", side_effect=mask), patch.object(fixture.os, "waitpid", side_effect=waitpid):
            with self.assertRaises(SystemExit):
                child.poll()
        with patch.object(fixture.os, "kill") as kill:
            fixture.terminate_owned(child)
            kill.assert_not_called()

    def test_echild_marks_ownership_finished_before_any_signal(self):
        child = fixture.OwnedChild()
        child.pid = 42
        with patch.object(fixture.signal, "pthread_sigmask", return_value=set()), patch.object(fixture.os, "waitpid", side_effect=ChildProcessError), patch.object(fixture.os, "kill") as kill:
            fixture.terminate_owned(child)
            self.assertTrue(child.reaped)
            self.assertIsNone(child.status)
            kill.assert_not_called()

    def test_pending_term_after_fork_sees_recorded_owned_pid(self):
        child = fixture.OwnedChild()

        def mask(how, _signals):
            if how == signal.SIG_SETMASK:
                self.assertEqual(child.pid, 42)
                raise SystemExit(143)
            return set()

        with patch.object(fixture.signal, "pthread_sigmask", side_effect=mask), patch.object(fixture.os, "fork", return_value=42):
            with self.assertRaises(SystemExit):
                child.start(10, 11, "/known/node", "/known/script", "a" * 32)
        self.assertEqual(child.pid, 42)

    def test_live_unreaped_child_escalates_only_its_owned_pid(self):
        child = fixture.OwnedChild()
        child.pid = 42

        def wait(_seconds):
            if kill.call_count == 2:
                child.reaped = True
                child.status = 9
                return 9
            return None

        with patch.object(child, "poll", return_value=None), patch.object(child, "wait", side_effect=wait), patch.object(fixture.os, "kill") as kill:
            fixture.terminate_owned(child)
            self.assertEqual(kill.call_args_list, [unittest.mock.call(42, signal.SIGTERM), unittest.mock.call(42, signal.SIGKILL)])
        self.assertTrue(child.reaped)


if __name__ == "__main__":
    unittest.main()
