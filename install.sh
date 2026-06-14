#!/usr/bin/env bash
# tk install script for macOS (Apple Silicon) — downloads the latest release binary
# Run: curl -fsSL https://raw.githubusercontent.com/paulouski/tk/main/install.sh | bash

set -euo pipefail

REPO="paulouski/tk"
ASSET="tk-osx-arm64"
INSTALL_DIR="$HOME/.local/bin"
EXE="$INSTALL_DIR/tk"
DOWNLOAD_URL="https://github.com/$REPO/releases/latest/download/$ASSET"

info() { printf '\033[36m%s\033[0m\n' "$1"; }
ok()   { printf '\033[32m%s\033[0m\n' "$1"; }
warn() { printf '\033[33m%s\033[0m\n' "$1"; }
err()  { printf '\033[31m%s\033[0m\n' "$1" >&2; }

# --- Architecture check (only arm64 is prebuilt) ---
arch="$(uname -m)"
if [ "$arch" != "arm64" ]; then
    err "This installer ships an Apple Silicon (arm64) binary, but this machine is '$arch'."
    err "Build from source instead: see the README 'Build from source' section."
    exit 1
fi

# --- Check for .NET 8 Runtime ---
info "Checking for .NET 8 Runtime..."
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-runtimes 2>/dev/null | grep -q "Microsoft.NETCore.App 8\."; then
    warn ".NET 8 Runtime not found."
    if command -v brew >/dev/null 2>&1; then
        info "Attempting to install via Homebrew..."
        if ! brew install dotnet@8; then
            err "Homebrew install failed. Install .NET 8 manually: https://dotnet.microsoft.com/download/dotnet/8"
            exit 1
        fi
    else
        err "Homebrew not found. Install .NET 8 Runtime manually:"
        err "  https://dotnet.microsoft.com/download/dotnet/8"
        exit 1
    fi
fi

# --- Download binary ---
info "Downloading tk..."
mkdir -p "$INSTALL_DIR"
curl -fsSL "$DOWNLOAD_URL" -o "$EXE"
chmod +x "$EXE"
ok "Installed to $EXE"

# --- Add to PATH if not already there ---
case ":$PATH:" in
    *":$INSTALL_DIR:"*)
        ok "PATH already contains $INSTALL_DIR"
        ;;
    *)
        profile="$HOME/.zshrc"
        [ "${SHELL##*/}" = "bash" ] && profile="$HOME/.bashrc"
        line="export PATH=\"$INSTALL_DIR:\$PATH\"  # tk"
        if ! grep -qsF "$line" "$profile"; then
            printf '\n%s\n' "$line" >> "$profile"
        fi
        ok "Added $INSTALL_DIR to PATH in $profile"
        warn "Restart your terminal (or run: source $profile) for PATH changes to take effect."
        ;;
esac

# --- Install global Claude and AGENTS instructions ---
"$EXE" init

echo
ok "Done. Run 'tk --help' to verify."
