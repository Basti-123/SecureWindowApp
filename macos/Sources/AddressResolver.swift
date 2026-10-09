import Foundation

enum AddressResolver {
    static func resolveInput(_ text: String) -> URL? {
        let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty else { return nil }

        if let components = URLComponents(string: value),
           let scheme = components.scheme?.lowercased(),
           scheme == "http" || scheme == "https",
           let url = components.url,
           isAllowed(url) {
            return url
        }

        if !value.contains(where: \.isWhitespace),
           (value.contains(".") || value.hasPrefix("localhost:")) {
            let candidate = value.hasPrefix("localhost:") ? "http://\(value)" : "https://\(value)"
            if let url = URL(string: candidate), isAllowed(url) { return url }
        }

        var search = URLComponents(string: "https://www.google.com/search")
        search?.queryItems = [URLQueryItem(name: "q", value: value)]
        return search?.url
    }

    static func isAllowed(_ url: URL) -> Bool {
        guard let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let host = url.host,
              !host.isEmpty else { return false }
        return true
    }
}
