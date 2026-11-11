#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
python3 -m unittest discover -s tests/Packaging -p 'test_*.py'
dotnet build NuxPrint.Linux.sln -c Release -warnaserror
for suite in PrintAgent.Core.Tests PrintAgent.Linux.Tests PrintAgent.Desktop.Tests; do
  rm -f -- "tests/$suite/TestResults/build.trx"
done
dotnet test NuxPrint.Linux.sln -c Release --no-build --logger 'trx;LogFileName=build.trx'
python3 scripts/verify_tests.py --name build.trx
