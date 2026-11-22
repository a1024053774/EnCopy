# Sourced by build-app.sh and make-signing-identity.sh: the identity this
# Mac's builds are signed with, in a keychain of its own. macOS ties
# permissions such as Accessibility to the signature. A build signed with the
# same identity keeps them; an ad hoc build loses them at every rebuild.
SIGNING_KEYCHAIN="$HOME/Library/Keychains/encopy-signing.keychain-db"
SIGNING_NAME="EnCopy Local Signing"
