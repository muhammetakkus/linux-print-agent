"""Exercise launch and authorization decisions without modifying this computer."""
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import sys
import unittest
from unittest.mock import patch, Mock

ROOT = Path(__file__).resolve().parents[2]


def load(name):
    path = ROOT / "packaging" / "local-access" / f"{name}.py"
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class LaunchTests(unittest.TestCase):
    def test_existing_session_access_launches_without_elevation(self):
        launcher = load("launch")
        with patch.object(launcher, "user_access", return_value=(True, True)), patch.object(launcher.os, "geteuid", return_value=1000, create=True), patch.object(launcher.os, "execv") as execute, patch.object(launcher.subprocess, "run") as run:
            self.assertEqual(0, launcher.main(["ui"]))
            execute.assert_called_once_with("/opt/nuxprint-agent/ui/PrintAgent.UI", ["/opt/nuxprint-agent/ui/PrintAgent.UI"])
            run.assert_not_called()

    def test_first_launch_authorizes_once_then_activates_group_without_logout(self):
        launcher = load("launch")
        with patch.object(launcher, "user_access", return_value=(False, False)), patch.object(launcher.os, "geteuid", return_value=1000, create=True), patch.object(launcher.os, "execv") as execute, patch.object(launcher.subprocess, "run", return_value=SimpleNamespace(returncode=0)) as run:
            self.assertEqual(0, launcher.main(["ui"]))
            self.assertEqual(["/usr/bin/pkexec", "--disable-internal-agent", "/usr/bin/python3", "/usr/lib/nuxprint-agent/authorize_user.py"], run.call_args.args[0])
            self.assertEqual("/usr/bin/sg", execute.call_args.args[0])
            self.assertIn("nuxprint-agent", execute.call_args.args[1])

    def test_already_authorized_user_with_old_session_does_not_get_another_prompt(self):
        launcher = load("launch")
        with patch.object(launcher, "user_access", return_value=(False, True)), patch.object(launcher.os, "geteuid", return_value=1000, create=True), patch.object(launcher.os, "execv") as execute, patch.object(launcher.subprocess, "run") as run:
            launcher.main(["ui"])
            run.assert_not_called()
            self.assertEqual("/usr/bin/sg", execute.call_args.args[0])

    def test_cancelled_authorization_never_launches_application(self):
        launcher = load("launch")
        with patch.object(launcher, "user_access", return_value=(False, False)), patch.object(launcher.os, "geteuid", return_value=1000, create=True), patch.object(launcher.os, "execv") as execute, patch.object(launcher, "show_error"), patch.object(launcher.subprocess, "run", return_value=SimpleNamespace(returncode=126)):
            self.assertEqual(126, launcher.main(["ui"]))
            execute.assert_not_called()

    def test_autostart_does_not_request_access_for_other_local_users(self):
        launcher = load("launch")
        with patch.object(launcher, "user_access", return_value=(False, False)), patch.object(launcher.os, "geteuid", return_value=1000, create=True), patch.object(launcher.os, "execv") as execute, patch.object(launcher.subprocess, "run") as run:
            self.assertEqual(0, launcher.main(["ui", "--autostart"]))
            execute.assert_not_called()
            run.assert_not_called()

    def test_cli_arguments_are_quoted_instead_of_executed_by_group_shell(self):
        launcher = load("launch")
        import shlex
        arguments = ["login", "x'; touch /tmp/unwanted; '", "--password-stdin"]
        with patch.object(launcher, "user_access", return_value=(False, True)), patch.object(launcher.os, "geteuid", return_value=1000, create=True), patch.object(launcher.os, "execv") as execute:
            launcher.main(["cli"] + arguments)
            command = execute.call_args.args[1][-1]
            self.assertEqual(["exec", "/opt/nuxprint-agent/cli/PrintAgent.CLI"] + arguments, shlex.split(command))

    def test_ui_is_not_started_as_root(self):
        launcher = load("launch")
        with patch.object(launcher.os, "geteuid", return_value=0, create=True), patch.object(launcher.os, "execv") as execute, patch.object(launcher, "show_error"):
            self.assertNotEqual(0, launcher.main(["ui"]))
            execute.assert_not_called()


class AuthorizeTests(unittest.TestCase):
    def test_only_pkexec_callers_identity_can_be_granted_access(self):
        helper = load("authorize_user")
        pwd = SimpleNamespace(getpwuid=Mock(return_value=SimpleNamespace(pw_name="alice", pw_shell="/bin/bash")))
        grp = SimpleNamespace(getgrnam=Mock(return_value=SimpleNamespace(gr_gid=111)))
        with patch.dict(sys.modules, {"pwd": pwd, "grp": grp}), patch.dict(helper.os.environ, {"PKEXEC_UID": "1001"}, clear=True), patch.object(helper.os, "geteuid", return_value=0, create=True), patch.object(helper.subprocess, "run") as run:
            self.assertEqual(0, helper.main([]))
            pwd.getpwuid.assert_called_once_with(1001)
            self.assertEqual(["/usr/sbin/usermod", "--append", "--groups", "nuxprint-agent", "--", "alice"], run.call_args.args[0])

    def test_unprivileged_calls_extra_arguments_and_invalid_identity_are_rejected(self):
        for uid, caller, args in [(1000, "1001", []), (0, "0", []), (0, "-1", []), (0, "invalid", []), (0, "1001", ["another-user"])]:
            with self.subTest(uid=uid, caller=caller, args=args):
                helper = load("authorize_user")
                with patch.dict(helper.os.environ, {"PKEXEC_UID": caller}, clear=True), patch.object(helper.os, "geteuid", return_value=uid, create=True), patch.object(helper.subprocess, "run") as run:
                    self.assertNotEqual(0, helper.main(args))
                    run.assert_not_called()
