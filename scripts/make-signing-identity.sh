#!/bin/bash
# Makes the identity this Mac's builds are signed with: a self-signed code
# signing certificate in a keychain of its own. Run it once. The login
# keychain and the keychain search list are not touched, and nothing asks for
# a password.
#
# The keychain's password is empty, so a build can unlock it by itself. The
# identity is worth what macOS grants to an app signed with it on this Mac,
# and anything already running as you could sign with it.
# Usage: scripts/make-signing-identity.sh
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/signing.sh

if [ -e "$SIGNING_KEYCHAIN" ]; then
  echo "$SIGNING_KEYCHAIN is already there"
  exit 0
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/encopy-signing.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
cat > "$WORK/cert.cnf" <<CNF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $SIGNING_NAME
[ext]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
CNF
# The system's openssl: its PKCS#12 files are the kind `security import` reads.
/usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -config "$WORK/cert.cnf" \
  -keyout "$WORK/key.pem" -out "$WORK/cert.pem" 2>/dev/null
/usr/bin/openssl pkcs12 -export -inkey "$WORK/key.pem" -in "$WORK/cert.pem" -name "$SIGNING_NAME" \
  -out "$WORK/identity.p12" -passout pass:encopy

security create-keychain -p "" "$SIGNING_KEYCHAIN"
chmod 600 "$SIGNING_KEYCHAIN"
# No lock after a while or on sleep; build-app.sh unlocks it after a restart.
security set-keychain-settings "$SIGNING_KEYCHAIN"
security import "$WORK/identity.p12" -k "$SIGNING_KEYCHAIN" -P encopy -T /usr/bin/codesign >/dev/null
# Lets codesign use the key without a prompt.
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "" "$SIGNING_KEYCHAIN" >/dev/null
echo "Made $SIGNING_NAME in $SIGNING_KEYCHAIN"
