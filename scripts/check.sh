#!/bin/bash
# EnCopy's local checks, run after every change; six steps: the name
# guard, the one-key guard, the data-folder guard, the strings check, a
# type check of the whole app, and the native checks.
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/swift-sdk.sh

# 1. The upstream name is gone from code and scripts. Its licence does not
#    cover the name, so it may only appear where we credit it (README, LICENSE).
if grep -rIil 'tendedero' Package.swift Sources scripts .github --exclude=check.sh; then
    echo "FAIL: the upstream name is still in the files listed above" >&2
    exit 1
fi

# 2. ⌥C is the only global key: one HotKey( call outside HotKey.swift, the
#    Carbon registration only there, and no global monitor on key events.
hotkey_calls="$(grep -rn '[[:<:]]HotKey(' Sources/ | grep -v 'Sources/EnCopy/HotKey.swift' | wc -l | tr -d ' ')"
if [ "$hotkey_calls" != "1" ] \
    || [ -n "$(grep -rln 'RegisterEventHotKey' Sources/ | grep -v 'Sources/EnCopy/HotKey.swift')" ] \
    || grep -rn 'addGlobalMonitorForEvents' Sources/ | grep -qE 'keyDown|keyUp|flagsChanged'; then
    echo "FAIL: EnCopy must register exactly one global hot key" >&2
    exit 1
fi

# 3. The data folder is decided in one place, DataFolder.swift, so nothing
#    the development copy stores can land in the everyday copy's folder.
if grep -rnE 'applicationSupportDirectory|Application Support' Sources/ | grep -v '^Sources/EnCopy/DataFolder.swift:'; then
    echo "FAIL: only DataFolder.swift may name the Application Support folder" >&2
    exit 1
fi

# 4. Every L("...") has a translation in both languages, and no language
#    keeps an entry the code no longer uses.
swift scripts/check-strings.swift

# 5. The whole app type-checks. The native checks below compile only part of
#    it, with a stand-in Inbox, so app-only code could break unseen (T-030 did).
swiftc -typecheck -module-name EnCopy -target "$(uname -m)-apple-macosx14.0" Sources/EnCopy/*.swift

# 6. The native checks, without XCTest or the system clipboard. Watchers use
#    only generated files in private test folders, inside a home folder of
#    their own so the user's Application Support stays untouched.
CHECK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/encopy-checks.XXXXXX")"
trap 'rm -rf "$CHECK_DIR"; rm -f "$HOME/Library/Preferences/local.encopy.checks.plist"' EXIT
CHECK_APP="$CHECK_DIR/Checks.app"
mkdir -p "$CHECK_APP/Contents/MacOS" "$CHECK_APP/Contents/Resources" "$CHECK_DIR/files" "$CHECK_DIR/home"
cp -R Sources/EnCopy/Resources/en.lproj "$CHECK_APP/Contents/Resources/"
cat > "$CHECK_APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>local.encopy.checks</string>
<key>CFBundleName</key><string>EnCopy Checks</string>
<key>CFBundleExecutable</key><string>Checks</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>LSUIElement</key><true/>
</dict></plist>
PLIST
SOURCES=()
for name in Line LineView LinePanel PeggedView GrabArea Share Markup Trim QuickLook Localization ScreenshotWatcher SearchIndex NoteSheet Held Markdown HotKey SearchBar Paste DataFolder History ClipboardWatcher Clearing Motion MenuBarIcon; do
    SOURCES+=("Sources/EnCopy/$name.swift")
done
swiftc -module-name EnCopy -target "$(uname -m)-apple-macosx14.0" -parse-as-library "${SOURCES[@]}" scripts/checks.swift \
    -o "$CHECK_APP/Contents/MacOS/Checks"
CFFIXED_USER_HOME="$CHECK_DIR/home" "$CHECK_APP/Contents/MacOS/Checks" "$CHECK_DIR/files"
# cfprefsd writes the checks' empty preferences file shortly after the app
# exits; wait for it, then drop the domain so the trap leaves nothing.
for _ in $(seq 1 30); do
    [ -f "$HOME/Library/Preferences/local.encopy.checks.plist" ] && break
    sleep 0.1
done
defaults delete local.encopy.checks >/dev/null 2>&1 || true
