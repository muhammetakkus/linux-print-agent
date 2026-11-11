#!/usr/bin/env python3
"""Do not report success when a test assembly is blocked during discovery."""
import argparse
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
MINIMUM = {"PrintAgent.Core.Tests": 48, "PrintAgent.Linux.Tests": 54, "PrintAgent.Desktop.Tests": 14}

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--name", default="build.trx")
    args = parser.parse_args()
    failed = False
    for suite, minimum in MINIMUM.items():
        path = ROOT / "tests" / suite / "TestResults" / args.name
        if not path.is_file():
            print(f"FAIL: {suite}: no TRX result")
            failed = True
            continue
        counters = ET.parse(path).find(".//{*}Counters")
        values = counters.attrib if counters is not None else {}
        passed, total = int(values.get("passed", 0)), int(values.get("total", 0))
        ok = passed >= minimum and passed == total and int(values.get("failed", 0)) == 0
        print(f"{'PASS' if ok else 'FAIL'}: {suite}: {passed}/{total}, expected at least {minimum}")
        failed |= not ok
    raise SystemExit(1 if failed else 0)
