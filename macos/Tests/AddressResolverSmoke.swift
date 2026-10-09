import Foundation

@main
struct AddressResolverSmoke {
    static func main() {
        let cases: [(String, String)] = [
            ("https://example.com/path", "https://example.com/path"),
            ("example.com/path", "https://example.com/path"),
            ("localhost:4173/website/", "http://localhost:4173/website/")
        ]

        for (input, expected) in cases {
            guard AddressResolver.resolveInput(input)?.absoluteString == expected else {
                fatalError("Address resolution failed for: \(input)")
            }
        }

        guard let searchURL = AddressResolver.resolveInput("secure Veyra"),
              URLComponents(url: searchURL, resolvingAgainstBaseURL: false)?
                .queryItems?.first(where: { $0.name == "q" })?.value == "secure Veyra",
              let blockedScheme = AddressResolver.resolveInput("file:///etc/passwd"),
              blockedScheme.scheme == "https",
              URLComponents(url: blockedScheme, resolvingAgainstBaseURL: false)?
                .queryItems?.first(where: { $0.name == "q" })?.value == "file:///etc/passwd" else {
            fatalError("Search or blocked-scheme resolution failed")
        }

        print("Address resolver smoke test passed (4 cases).")
    }
}
