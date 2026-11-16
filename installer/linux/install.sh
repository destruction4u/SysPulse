#!/bin/sh
# SysPulse Agent for Linux (systemd).
#   Install or upgrade:  sudo sh install.sh
#   Show pairing code:   sudo /opt/syspulse-agent/syspulse-agent --pairing
#   Uninstall:           sudo sh install.sh --uninstall
set -eu

DEST=/opt/syspulse-agent
BIN="$DEST/syspulse-agent"
HERE=$(cd "$(dirname "$0")" && pwd)

if [ "$(id -u)" -ne 0 ]; then
    echo "Please run as root:  sudo sh $0 ${1:-}"
    exit 1
fi

if [ "${1:-}" = "--uninstall" ]; then
    if [ -x "$BIN" ]; then "$BIN" --uninstall || true; fi
    rm -rf "$DEST"
    echo "SysPulse Agent removed."
    exit 0
fi

if ! command -v systemctl >/dev/null 2>&1 || [ ! -d /run/systemd/system ]; then
    echo "SysPulse Agent needs systemd, which isn't running on this system."
    exit 1
fi

# The package must match this machine's CPU
case "$(uname -m)" in
    x86_64 | amd64) arch=x64 ;;
    aarch64 | arm64) arch=arm64 ;;
    *) echo "Unsupported CPU architecture: $(uname -m) (SysPulse Agent supports x86_64 and aarch64)."; exit 1 ;;
esac
pkg_arch=$(cat "$HERE/ARCH" 2>/dev/null || echo "$arch")
if [ "$pkg_arch" != "$arch" ]; then
    echo "This package is for $pkg_arch, but this machine is $arch. Download SysPulse-Agent-linux-$arch instead."
    exit 1
fi

# Upgrade: stop the running agent before replacing its file
if [ -x "$BIN" ]; then "$BIN" --stop-service >/dev/null 2>&1 || true; fi

install -d -m 755 "$DEST"
install -m 755 "$HERE/syspulse-agent" "$BIN"
[ -f "$HERE/LICENSE" ] && install -m 644 "$HERE/LICENSE" "$DEST/LICENSE"

# Creates the system user, config, certificate and systemd service, opens the firewall
# (ufw/firewalld, local subnet only) and prints the pairing details.
"$BIN" --install
