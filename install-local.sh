#!/usr/bin/env bash
# install-local.sh — build the working tree and update the LOCAL tk install. Nothing else.
#
# This is release.sh minus the release: no version bump, no commit, no tag, no push.
# Use it to run uncommitted work-in-progress as your everyday `tk` — the artifact is
# built exactly the way release.sh (and the GitHub workflow) builds it, so what you
# test locally is what would ship.
#
# Usage: ./install-local.sh
set -euo pipefail

cd "$(dirname "$0")"

CSPROJ="Tk.csproj"
INSTALL_DIR="$HOME/.local/bin"
EXE="$INSTALL_DIR/tk"

echo "==> tests"
dotnet test Tk.Tests/Tk.Tests.csproj -c Release --nologo

# --- same artifact release.sh ships (framework-dependent single file) ---
echo "==> build"
rm -rf publish
dotnet publish "$CSPROJ" -c Release -r osx-arm64 --self-contained false -p:PublishSingleFile=true -o publish

echo "==> install -> $EXE"
mkdir -p "$INSTALL_DIR"
cp publish/tk "$EXE"
chmod +x "$EXE"
# cp invalidates the linker's ad-hoc Mach-O signature on Apple Silicon → the
# copied binary gets SIGKILL'd. Re-sign ad-hoc so it runs.
codesign --force --sign - "$EXE"

# --- stop every running daemon, not just this workspace's ---
# Daemons are long-lived (30 min idle timeout) and each one is a copy of the binary
# that spawned it, so without this the old code keeps answering in every workspace
# that has a warm daemon. SIGTERM is the graceful path: LspDaemonCommand traps it and
# runs the normal cleanup (kills the Roslyn child, removes socket/pid files). The next
# tk command in each workspace pays one cold start.
echo "==> stopping running daemons"
if pkill -f 'tk __lsp-daemon' 2>/dev/null; then
    echo "daemons stopped (next command in each workspace cold-starts)"
else
    echo "no daemons running"
fi

echo
echo "Done. $("$EXE" --version) installed at $EXE"
