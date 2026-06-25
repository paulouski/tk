#!/usr/bin/env bash
# release.sh — bump version, test, build, update the LOCAL tk install, then tag & push.
#
# Local install is the part that matters: it builds the same artifact the GitHub
# release workflow ships and copies it over ~/.local/bin/tk, so you never have to
# round-trip through GitHub to update your own machine. The tag push also triggers
# .github/workflows/release.yml for the published binaries (nice-to-have).
#
# Usage: ./release.sh [patch|minor|major]   (default: minor — each release ≈ a feature)
set -euo pipefail

cd "$(dirname "$0")"

BUMP="${1:-minor}"
CSPROJ="Tk.csproj"
INSTALL_DIR="$HOME/.local/bin"
EXE="$INSTALL_DIR/tk"
BRANCH="$(git rev-parse --abbrev-ref HEAD)"

# --- current version from csproj ---
CUR=$(grep -oE '<Version>[0-9]+\.[0-9]+\.[0-9]+</Version>' "$CSPROJ" | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' || true)
[ -n "$CUR" ] || { echo "release: could not read <Version> from $CSPROJ" >&2; exit 1; }
IFS=. read -r MA MI PA <<< "$CUR"
case "$BUMP" in
  major) MA=$((MA + 1)); MI=0; PA=0 ;;
  minor) MI=$((MI + 1)); PA=0 ;;
  patch) PA=$((PA + 1)) ;;
  *) echo "usage: ./release.sh [patch|minor|major]" >&2; exit 1 ;;
esac
NEW="$MA.$MI.$PA"

echo "Release v$CUR -> v$NEW ($BUMP) on '$BRANCH'. Working tree:"
git status --short
read -r -p "Proceed? [y/N] " ans
[ "$ans" = "y" ] || { echo "aborted"; exit 1; }

# --- tests first: never ship a broken release ---
echo "==> tests"
dotnet test Tk.Tests/Tk.Tests.csproj -c Release --nologo

# --- bump version ---
sed -i '' "s|<Version>$CUR</Version>|<Version>$NEW</Version>|" "$CSPROJ"

# --- build the same artifact release.yml ships (framework-dependent single file) ---
echo "==> build"
rm -rf publish
dotnet publish "$CSPROJ" -c Release -r osx-arm64 --self-contained false -p:PublishSingleFile=true -o publish

# --- update the LOCAL install (do this before pushing so a network failure can't skip it) ---
echo "==> install -> $EXE"
mkdir -p "$INSTALL_DIR"
cp publish/tk "$EXE"
chmod +x "$EXE"
# cp invalidates the linker's ad-hoc Mach-O signature on Apple Silicon → the
# copied binary gets SIGKILL'd. Re-sign ad-hoc so it runs.
codesign --force --sign - "$EXE"
"$EXE" --version

# --- record the release: commit everything pending, tag, push ---
echo "==> commit & tag"
git add -A
git commit -m "Release v$NEW"
git tag "v$NEW"

echo "==> push (triggers GitHub release workflow)"
git push origin "$BRANCH"
git push origin "v$NEW"

echo "Done. Local tk is v$NEW; GitHub release building for the tag."
