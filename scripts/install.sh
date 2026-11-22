#!/bin/bash
# Builds EnCopy and installs it as /Applications/EnCopy.app, the copy for
# everyday use. Run it on main after a feature is merged.
set -euo pipefail
cd "$(dirname "$0")/.."

scripts/build-app.sh
DEST="/Applications/EnCopy.app"

# Quit the running copy first. On SIGTERM it puts the system screenshot
# settings back; the new copy takes them over again when it opens. A copy
# that is showing a dialog does not quit this way, and killing it would
# skip that step, so stop and say so.
if pgrep -x EnCopy >/dev/null; then
  pkill -TERM -x EnCopy
  for _ in $(seq 25); do
    pgrep -x EnCopy >/dev/null || break
    sleep 0.2
  done
  if pgrep -x EnCopy >/dev/null; then
    echo "EnCopy is still running. Close its open dialog, then run this again." >&2
    exit 1
  fi
fi

rm -rf "$DEST"
ditto build/EnCopy.app "$DEST"
open "$DEST"
echo "Installed $DEST"
