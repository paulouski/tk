#!/usr/bin/env bash
# release.sh — bump version, test, build, update the LOCAL tk install, then tag & push.
#
# Local install is the part that matters: it builds the same artifact the GitHub
# release workflow ships and copies it over ~/.local/bin/tk, so you never have to
# round-trip through GitHub to update your own machine. Those steps live in
# install-local.sh, which this script calls after the bump — run that one directly
# when you want the local update without cutting a release. The tag push also
# triggers .github/workflows/release.yml for the published binaries (nice-to-have).
#
# Usage: ./release.sh [patch|minor|major]   (default: minor — each release ≈ a feature)
set -euo pipefail

cd "$(dirname "$0")"

BUMP="${1:-minor}"
CSPROJ="Tk.csproj"
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

# --- bump version before building, so the artifact carries the new one ---
sed -i '' "s|<Version>$CUR</Version>|<Version>$NEW</Version>|" "$CSPROJ"

# --- test, build and update the local install (do this before pushing so a network
# --- failure can't skip it). install-local.sh owns those steps for both scripts.
./install-local.sh

# --- record the release: commit everything pending, tag, push ---
echo "==> commit & tag"
git add -A

# --- generate commit message via Claude (falls back to plain "Release vX" if unavailable) ---
COMMIT_MSG=""
if command -v claude > /dev/null 2>&1; then
  DIFFSTAT="$(git diff --staged --stat 2>/dev/null || true)"
  DIFF="$(git diff --staged 2>/dev/null | head -c 100000 || true)"
  if [ -n "$DIFF" ]; then
    COMMIT_MSG="$(printf '%s\n\n%s\n' "$DIFFSTAT" "$DIFF" | claude -p \
      'Ты пишешь сообщение git-коммита для релиза CLI-утилиты tk по приведённому staged-диффу. Выдай: первая строка — краткий императивный subject (≤72 символов, что сделано); затем пустая строка; затем 1-3 коротких булета «что изменилось → какой эффект». Только текст сообщения, без преамбул, без markdown-заголовков, без обрамляющих кавычек. КАТЕГОРИЧЕСКИ НЕ добавляй строки Co-Authored-By, «Generated with Claude», эмодзи-роботов или любые упоминания ассистента.' \
      2>/dev/null || true)"
  fi
fi

if [ -n "$COMMIT_MSG" ]; then
  echo "==> commit message:"
  echo "$COMMIT_MSG"
  echo "Release v$NEW"
  git commit -m "$COMMIT_MSG" -m "Release v$NEW"
else
  echo "==> commit message: Release v$NEW"
  git commit -m "Release v$NEW"
fi
git tag "v$NEW"

echo "==> push (triggers GitHub release workflow)"
git push origin "$BRANCH"
git push origin "v$NEW"

echo "Done. Local tk is v$NEW; GitHub release building for the tag."
