#!/usr/bin/python3
"""Privileged polkit helper: grant only the invoking local account one fixed group."""
import os
import subprocess
import sys


def main(arguments=None):
    arguments = sys.argv[1:] if arguments is None else arguments
    if arguments or os.geteuid() != 0:
        return 2
    try:
        caller = os.environ.get("PKEXEC_UID", "")
        if not caller.isascii() or not caller.isdecimal() or int(caller) <= 0:
            return 2
        import pwd
        import grp
        account = pwd.getpwuid(int(caller))
        if account.pw_shell.rsplit("/", 1)[-1] in ("false", "nologin"):
            return 2
        grp.getgrnam("nuxprint-agent")
        subprocess.run(["/usr/sbin/usermod", "--append", "--groups", "nuxprint-agent", "--", account.pw_name],
                       check=True, timeout=30, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                       env={"PATH": "/usr/sbin:/usr/bin:/sbin:/bin", "LANG": "C"})
        return 0
    except (KeyError, OSError, ValueError, subprocess.SubprocessError):
        print("Could not authorize this local account for NuxPrint.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
