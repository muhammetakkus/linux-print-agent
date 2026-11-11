#!/usr/bin/env bash
set -euo pipefail
if [[ $# != 1 ]]; then echo "Usage: sudo bash scripts/install.sh PATH_TO_PACKAGE.deb" >&2; exit 2; fi
if [[ "$(uname -s)" != Linux ]]; then
  echo 'This installer requires Debian/Ubuntu or Raspberry Pi OS Linux.' >&2; exit 2
fi
host_arch="$(dpkg --print-architecture)"
case "$host_arch" in amd64|arm64|armhf) ;; *) echo 'Supported architectures: amd64, arm64, armhf.' >&2; exit 2 ;; esac
if [[ $EUID != 0 ]]; then echo 'Run this installer using sudo.' >&2; exit 2; fi
package="$(realpath -- "$1")"
name="$(dpkg-deb -f "$package" Package)"
case "$name" in nuxprint-agent-desktop|nuxprint-agent-server) ;; *) echo 'Not a NuxPrint package.' >&2; exit 2 ;; esac
package_arch="$(dpkg-deb -f "$package" Architecture)"
if [[ "$package_arch" != "$host_arch" ]]; then
  echo "Package architecture $package_arch does not match OS architecture $host_arch." >&2; exit 2
fi
apt-get install "$package"
echo 'Open NuxPrint from your application menu. First use requests administrator approval for this local account; no logout is required.'
systemctl status nuxprint-agent.service --no-pager
