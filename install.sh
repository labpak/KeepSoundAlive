#!/bin/sh
# KeepSoundAlive installer for Linux (no root needed).
#   curl -fsSL https://raw.githubusercontent.com/labpak/KeepSoundAlive/main/install.sh | sh
# Downloads the latest release into ~/.local/bin, adds an applications menu entry and starts it.
set -eu

case "$(uname -m)" in
    x86_64 | amd64) arch=x64 ;;
    aarch64 | arm64) arch=arm64 ;;
    *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

url="https://github.com/labpak/KeepSoundAlive/releases/latest/download/KeepSoundAlive-linux-$arch"
dir="${XDG_BIN_HOME:-$HOME/.local/bin}"
bin="$dir/KeepSoundAlive"

mkdir -p "$dir"
pkill -x KeepSoundAlive 2>/dev/null || true
echo "Downloading $url ..."
if command -v curl >/dev/null 2>&1; then
    curl -fL --progress-bar -o "$bin.tmp" "$url"
else
    wget -q --show-progress -O "$bin.tmp" "$url"
fi
chmod +x "$bin.tmp"
mv -f "$bin.tmp" "$bin"

"$bin" --add-to-menu
if [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
    nohup "$bin" >/dev/null 2>&1 &
    echo "KeepSoundAlive installed to $bin and started."
else
    echo "KeepSoundAlive installed to $bin. No graphical session detected; run '$bin --headless' to use it without a UI."
fi
