#!/bin/bash
# EnCopy's local checks, run after every change; three steps: the name
# guard, the strings check, and the native checks.
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/swift-sdk.sh

# 1. The upstream name is gone from code and scripts. Its licence does not
#    cover the name, so it may only appear where we credit it (README, LICENSE).
if grep -rIil 'tendedero' Package.swift Sources scripts .github --exclude=check.sh; then
    echo "FAIL: the upstream name is still in the files listed above" >&2
    exit 1
fi

# 2. Every L("...") has a translation in both languages, and no language
#    keeps an entry the code no longer uses.
swift scripts/check-strings.swift

# 3. The native checks, without XCTest or the system clipboard. Watchers use
#    only generated files in private test folders.
CHECK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/encopy-checks.XXXXXX")"
trap 'rm -rf "$CHECK_DIR"' EXIT
CHECK_APP="$CHECK_DIR/Checks.app"
mkdir -p "$CHECK_APP/Contents/MacOS" "$CHECK_APP/Contents/Resources" "$CHECK_DIR/files"
cp -R Sources/EnCopy/Resources/en.lproj "$CHECK_APP/Contents/Resources/"
cat > "$CHECK_APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>local.encopy.checks</string>
<key>CFBundleExecutable</key><string>Checks</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>LSUIElement</key><true/>
</dict></plist>
PLIST
SOURCES=()
for name in Line LineView LinePanel PeggedView GrabArea Share Markup Trim QuickLook Localization ScreenshotWatcher SearchIndex NoteSheet Held Markdown; do
    SOURCES+=("Sources/EnCopy/$name.swift")
done
swiftc -module-name EnCopy -target "$(uname -m)-apple-macosx14.0" -parse-as-library "${SOURCES[@]}" scripts/checks.swift \
    -o "$CHECK_APP/Contents/MacOS/Checks"
"$CHECK_APP/Contents/MacOS/Checks" "$CHECK_DIR/files"
