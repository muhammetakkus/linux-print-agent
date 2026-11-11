#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
rid="${1:-linux-x64}"
case "$rid" in
  linux-x64|linux-arm64|linux-arm) ;;
  *) echo 'Supported targets: linux-x64, linux-arm64, linux-arm.' >&2; exit 2 ;;
esac
version="$(python3 -c 'import re; from pathlib import Path; print(re.search(r"AppVersion = \"([0-9.]+)\"", Path("src/PrintAgent.Common/Constants.cs").read_text()).group(1))')"
mkdir -p artifacts/publish
# Every release gets fresh staging; files left by earlier publishes cannot enter it.
publish_root="$(mktemp -d "$root/artifacts/publish/$version-$rid.XXXXXXXX")"
case "$publish_root" in "$root"/artifacts/publish/*) ;; *) echo 'Invalid staging directory.' >&2; exit 2 ;; esac
trap 'rm -rf -- "$publish_root"' EXIT
for component in Service CLI UI; do
  dotnet publish "src/PrintAgent.$component/PrintAgent.$component.csproj" \
    -c Release -r "$rid" --self-contained true -warnaserror \
    -o "$publish_root/$component"
done
package_root="$publish_root/packages"
python3 packaging/deb/build_deb.py --publish-root "$publish_root" --output "$package_root" --rid "$rid"
python3 packaging/deb/verify_deb.py "$package_root"/*.deb
mkdir -p artifacts/deb
mv -- "$package_root"/*.deb artifacts/deb/
python3 - <<'PY'
import hashlib
from pathlib import Path
root = Path("artifacts/deb")
lines = []
for package in sorted(root.glob("*.deb")):
    with package.open("rb") as file:
        lines.append(f"{hashlib.file_digest(file, 'sha256').hexdigest()}  {package.name}")
temporary = root / ".SHA256SUMS.tmp"
temporary.write_text("\n".join(lines) + "\n", encoding="utf-8")
temporary.replace(root / "SHA256SUMS")
PY
