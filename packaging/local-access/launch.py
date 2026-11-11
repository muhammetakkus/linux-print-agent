#!/usr/bin/python3
"""Start an unprivileged client with its authorized service group immediately."""
import os
import shlex
import subprocess
import sys

GROUP = "nuxprint-agent"
HELPER = "/usr/lib/nuxprint-agent/authorize_user.py"
CLIENTS = {"ui": "/opt/nuxprint-agent/ui/PrintAgent.UI", "cli": "/opt/nuxprint-agent/cli/PrintAgent.CLI"}


def user_access():
    import grp
    import pwd
    user = pwd.getpwuid(os.getuid())
    group = grp.getgrnam(GROUP).gr_gid
    effective = group in os.getgroups() or group == os.getegid()
    authorized = group in os.getgrouplist(user.pw_name, user.pw_gid)
    return effective, authorized


def show_error(message, graphical):
    print(message, file=sys.stderr)
    if graphical:
        try:
            subprocess.run(["/usr/bin/notify-send", "--", "NuxPrint Agent", message], timeout=5, check=False)
        except (OSError, subprocess.TimeoutExpired):
            pass


def main(arguments=None):
    arguments = list(sys.argv[1:] if arguments is None else arguments)
    if not arguments or arguments[0] not in CLIENTS:
        return 2
    component = arguments.pop(0)
    graphical = component == "ui"
    autostart = graphical and arguments[:1] == ["--autostart"]
    if autostart:
        arguments.pop(0)
    executable = CLIENTS[component]
    try:
        if os.geteuid() == 0:
            if graphical:
                show_error("Open NuxPrint from your normal desktop account, not as root.", True)
                return 2
            os.execv(executable, [executable] + arguments)
            return 0
        effective, authorized = user_access()
        if not effective and not authorized:
            if autostart:
                return 0
            command = ["/usr/bin/pkexec"]
            if graphical:
                command.append("--disable-internal-agent")
            command += ["/usr/bin/python3", HELPER]
            result = subprocess.run(command, timeout=120, check=False)
            if result.returncode != 0:
                show_error("NuxPrint access was not authorized. Open the app again to retry administrator approval.", graphical)
                return result.returncode
        if effective:
            os.execv(executable, [executable] + arguments)
        else:
            # sg reads the updated membership database, so the existing desktop session need not restart.
            command = "exec " + shlex.join([executable] + arguments)
            os.execv("/usr/bin/sg", ["/usr/bin/sg", GROUP, "-c", command])
        return 0
    except (OSError, KeyError, subprocess.TimeoutExpired) as error:
        show_error(f"NuxPrint could not start: {error}", graphical)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
