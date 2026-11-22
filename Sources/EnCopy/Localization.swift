import Foundation

/// Looks up text in `Resources/<language>.lproj/Localizable.strings`, keyed by
/// the English text itself. macOS picks the language from the user's whole
/// list of preferred languages, and anything missing falls back to English.
///
/// The folders are copied into the app by `scripts/build-app.sh`.
func L(_ english: String) -> String {
    NSLocalizedString(english, comment: "")
}
