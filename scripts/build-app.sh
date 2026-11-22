#!/bin/bash
# Builds the app into ./build without needing Xcode.
# Usage: [EDITION=dev|daily] scripts/build-app.sh [debug|release]
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/swift-sdk.sh

CONFIG="${1:-release}"
# Two copies, told apart by name and bundle id, so each has its own settings,
# data folder and permissions. The development copy is the default: trying a
# build never touches what the everyday copy in /Applications keeps.
case "${EDITION:-dev}" in
  dev)   NAME="EnCopy Dev"; BUNDLE_ID="app.encopy.EnCopy.dev" ;;
  daily) NAME="EnCopy";     BUNDLE_ID="app.encopy.EnCopy" ;;
  *) echo "EDITION must be dev or daily" >&2; exit 1 ;;
esac
APP="build/$NAME.app"
VERSION="0.1.0"

# Builds one architecture and prints the binary's path.
build_arch() {
  local triple="$1-apple-macosx14.0"
  swift build -c "$CONFIG" --triple "$triple" >&2
  cp "$(swift build -c "$CONFIG" --triple "$triple" --show-bin-path)/EnCopy" "$OUT/EnCopy-$1"
}

# A universal binary, so it runs on Apple silicon and on Intel Macs, from
# macOS 14 Sonoma onwards.
OUT="$(mktemp -d "${TMPDIR:-/tmp}/encopy-build.XXXXXX")"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/encopy-build.XXXXXX")"
trap 'rm -rf "$OUT" "$WORK"' EXIT
build_arch arm64
build_arch x86_64
lipo -create "$OUT/EnCopy-arm64" "$OUT/EnCopy-x86_64" -output "$OUT/EnCopy"
BIN="$OUT/EnCopy"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/EnCopy"

# Icon
swift scripts/make-icon.swift "$WORK/icon.png"
ICONSET="$WORK/EnCopy.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s "$WORK/icon.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$WORK/icon.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/EnCopy.icns"

# The licence travels inside the bundle, so every copy carries the notice.
cp LICENSE "$APP/Contents/Resources/LICENSE"

# Translations: one folder per language, listed in Info.plist so macOS knows
# which languages the app speaks.
LANGUAGES=""
for dir in Sources/EnCopy/Resources/*.lproj; do
  cp -R "$dir" "$APP/Contents/Resources/"
  LANGUAGES="$LANGUAGES<string>$(basename "$dir" .lproj)</string>"
done

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>${NAME}</string>
  <key>CFBundleDisplayName</key><string>${NAME}</string>
  <key>CFBundleIdentifier</key><string>${BUNDLE_ID}</string>
  <key>CFBundleExecutable</key><string>EnCopy</string>
  <key>CFBundleIconFile</key><string>EnCopy</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleLocalizations</key><array>${LANGUAGES}</array>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSDesktopFolderUsageDescription</key>
  <string>EnCopy watches the folder where macOS saves your screenshots so it can hang them on the line.</string>
  <key>NSDocumentsFolderUsageDescription</key>
  <string>EnCopy watches the folder you chose so it can hang new images on the line.</string>
  <key>NSDownloadsFolderUsageDescription</key>
  <string>EnCopy watches the folder you chose so it can hang new images on the line.</string>
  <key>NSServices</key>
  <array>
    <dict>
      <key>NSMenuItem</key><dict><key>default</key><string>Hang on ${NAME}</string></dict>
      <key>NSMessage</key><string>hangOnLine</string>
      <key>NSPortName</key><string>EnCopy</string>
      <key>NSRequiredContext</key><dict/>
      <key>NSSendFileTypes</key><array><string>public.image</string><string>public.plain-text</string><string>public.rtf</string></array>
    </dict>
  </array>
</dict>
</plist>
PLIST

# Sign with a Developer ID when one is in the keychain (or SIGN_IDENTITY is
# set), with the hardened runtime and a secure timestamp that notarization
# requires. Without one, sign ad hoc so the app still runs locally.
IDENTITY="${SIGN_IDENTITY:-$(security find-identity -v -p codesigning 2>/dev/null | awk -F'"' '/Developer ID Application/{print $2; exit}')}"
if [ -n "$IDENTITY" ]; then
  codesign --force --options runtime --timestamp --sign "$IDENTITY" "$APP"
  echo "Signed with $IDENTITY"
else
  codesign --force --deep --sign - "$APP" >/dev/null
  echo "Signed ad hoc (no Developer ID found)"
fi
echo "Built $APP"
