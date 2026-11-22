#!/bin/bash
# Removes what the development copy (build/EnCopy Dev.app) keeps on this Mac:
# its settings and its data folder. The everyday copy in /Applications has
# its own and is not touched. The permissions macOS granted the development
# copy stay: they are tied to its signature, which the next build shares.
# To drop them too: tccutil reset All app.encopy.EnCopy.dev
set -euo pipefail

NAME="EnCopy Dev"
BUNDLE_ID="app.encopy.EnCopy.dev"

# Quit it first. On SIGTERM it puts the system screenshot settings back, and
# it reads them from the settings removed below. A copy that is showing a
# dialog does not quit this way, and killing it would skip that step, so stop
# and say so.
running() { pgrep -f "/$NAME\.app/Contents/MacOS/EnCopy" >/dev/null; }
if running; then
  pkill -TERM -f "/$NAME\.app/Contents/MacOS/EnCopy"
  for _ in $(seq 25); do
    running || break
    sleep 0.2
  done
  if running; then
    echo "$NAME is still running. Close its open dialog, then run this again." >&2
    exit 1
  fi
fi

# cfprefsd writes the copy's last change a moment after it quits. Wait for
# that, clear the settings through it, then remove the empty file it leaves.
sleep 1
defaults delete "$BUNDLE_ID" >/dev/null 2>&1 || true
rm -f "$HOME/Library/Preferences/$BUNDLE_ID.plist"
rm -rf "$HOME/Library/Application Support/$NAME"
echo "Removed the settings and data folder of $NAME"
