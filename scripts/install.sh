#!/bin/bash
# Builds EnCopy and installs it as /Applications/EnCopy.app, the copy for
# everyday use. Run it on main after a feature is merged.
set -euo pipefail
cd "$(dirname "$0")/.."

EDITION=daily scripts/build-app.sh
DEST="/Applications/EnCopy.app"

# Quit the running copy first — whichever copy it is, since both executables
# are named EnCopy. On SIGTERM it puts the system screenshot settings back;
# the new copy takes them over again when it opens. A copy that is showing a
# dialog does not quit this way, and killing it would skip that step, so stop
# and say so.
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
# A second bundle with the everyday copy's id must not lie around in build/
# where it can be opened by mistake.
rm -rf build/EnCopy.app
open "$DEST"
echo "Installed $DEST"
