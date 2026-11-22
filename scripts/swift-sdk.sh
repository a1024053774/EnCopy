# Sourced by build-app.sh and check.sh: picks an SDK that can compile SwiftUI.
#
# Workaround. The Command Line Tools for macOS 27 ship an SDK whose SwiftUI
# needs a macro plugin that only Xcode includes, so `@State` does not compile.
# When that is the case and SDKROOT is not set, use the newest macOS 26 SDK
# installed beside it. Without this, neither script works on a Mac that has
# only the Command Line Tools. Remove this file, and the two lines that
# source it, once the probe below compiles with the default SDK.
if [ -z "${SDKROOT:-}" ]; then
  SDK_PROBE="$(mktemp -d "${TMPDIR:-/tmp}/encopy-sdk.XXXXXX")"
  cat > "$SDK_PROBE/probe.swift" <<'SWIFT'
import SwiftUI
struct Probe: View {
    @State private var on = false
    var body: some View { Text(on ? "a" : "b") }
}
SWIFT
  if ! swiftc -typecheck -target "$(uname -m)-apple-macosx14.0" "$SDK_PROBE/probe.swift" >/dev/null 2>&1; then
    SDKROOT="$(ls -d /Library/Developer/CommandLineTools/SDKs/MacOSX26*.sdk 2>/dev/null | sort -V | tail -1 || true)"
    if [ -z "$SDKROOT" ]; then
      echo "The default SDK cannot compile SwiftUI and no macOS 26 SDK is installed. Install Xcode, or set SDKROOT." >&2
      rm -rf "$SDK_PROBE"
      exit 1
    fi
    export SDKROOT
    echo "Using $SDKROOT" >&2
  fi
  rm -rf "$SDK_PROBE"
fi
