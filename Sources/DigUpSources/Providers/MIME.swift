import Foundation

/// A part of a MIME message: its headers and its body, still transfer-encoded.
struct MIMEPart {
    var headers: [(name: String, value: String)]
    var body: [UInt8]

    /// The first header with this name (case doesn't matter), unfolded, encoded words decoded.
    func header(_ name: String) -> String? {
        headers.first { $0.name.caseInsensitiveCompare(name) == .orderedSame }.map { MIME.decodeWords($0.value) }
    }

    /// "text/plain" and its parameters, lowercased type; "text/plain" when the header is missing (RFC 2045).
    var contentType: (type: String, parameters: [String: String]) {
        guard let raw = headers.first(where: { $0.name.caseInsensitiveCompare("Content-Type") == .orderedSame })?.value
        else { return ("text/plain", [:]) }
        let (value, parameters) = MIME.parameters(raw)
        return (value.lowercased().isEmpty ? "text/plain" : value.lowercased(), parameters)
    }

    var disposition: (value: String, parameters: [String: String]) {
        guard let raw = headers.first(where: { $0.name.caseInsensitiveCompare("Content-Disposition") == .orderedSame })?
            .value else { return ("", [:]) }
        let (value, parameters) = MIME.parameters(raw)
        return (value.lowercased(), parameters)
    }

    /// The file name an attachment carries (Content-Disposition's filename, or Content-Type's name).
    var filename: String? {
        let name = disposition.parameters["filename"] ?? contentType.parameters["name"]
        return name.map(MIME.decodeWords).flatMap { $0.isEmpty ? nil : ($0 as NSString).lastPathComponent }
    }

    var isAttachment: Bool {
        disposition.value == "attachment" || (filename != nil && !contentType.type.hasPrefix("multipart/"))
    }

    /// The body with its transfer encoding undone.
    var decodedBody: [UInt8] {
        let encoding = header("Content-Transfer-Encoding")?.trimmingCharacters(in: .whitespaces).lowercased() ?? ""
        switch encoding {
        case "base64": return MIME.decodeBase64(body)
        case "quoted-printable": return MIME.decodeQuotedPrintable(body, header: false)
        default: return body
        }
    }

    /// A text part's body as text, in its charset.
    var text: String {
        MIME.string(decodedBody, charset: contentType.parameters["charset"])
    }

    /// The parts of a multipart part (none for any other).
    var subparts: [MIMEPart] {
        guard contentType.type.hasPrefix("multipart/"), let boundary = contentType.parameters["boundary"],
              !boundary.isEmpty else { return [] }
        return MIME.split(body, boundary: boundary).map(MIME.parse)
    }
}

/// Just enough of RFC 2045–2047 and 2231 to read mail: headers, multipart bodies, base64 and quoted-printable,
/// charsets, encoded words and long or non-ASCII parameter values.
enum MIME {
    static let lf = UInt8(ascii: "\n"), cr = UInt8(ascii: "\r")

    /// A message (or a part of one): headers up to the first empty line, then the body.
    static func parse(_ bytes: [UInt8]) -> MIMEPart {
        var index = 0
        var headerEnd = bytes.count, bodyStart = bytes.count
        // An empty line: "\n\n" or "\r\n\r\n" (a message may start with its body when it has no headers at all).
        if bytes.first == lf { return MIMEPart(headers: [], body: Array(bytes.dropFirst())) }
        if bytes.starts(with: [cr, lf]) { return MIMEPart(headers: [], body: Array(bytes.dropFirst(2))) }
        while index < bytes.count {
            if bytes[index] == lf {
                var next = index + 1
                if next < bytes.count, bytes[next] == cr { next += 1 }
                if next < bytes.count, bytes[next] == lf {
                    headerEnd = index
                    bodyStart = next + 1
                    break
                }
                if next >= bytes.count {
                    headerEnd = index
                    bodyStart = bytes.count
                    break
                }
            }
            index += 1
        }
        let headers = parseHeaders(Array(bytes[0..<headerEnd]))
        return MIMEPart(headers: headers, body: bodyStart < bytes.count ? Array(bytes[bodyStart...]) : [])
    }

    /// Unfolded header lines. Header bytes are UTF-8 when they're valid UTF-8 (many mailers send raw UTF-8 subjects),
    /// Latin-1 otherwise.
    static func parseHeaders(_ bytes: [UInt8]) -> [(name: String, value: String)] {
        let text = String(bytes: bytes, encoding: .utf8) ?? String(bytes: bytes, encoding: .isoLatin1) ?? ""
        var headers: [(name: String, value: String)] = []
        // "\r\n" is one Character: split on it as well as on "\n".
        for rawLine in text.split(omittingEmptySubsequences: false, whereSeparator: { $0 == "\n" || $0 == "\r\n" }) {
            var line = Substring(rawLine)
            if line.hasSuffix("\r") { line = line.dropLast() }
            if let first = line.first, first == " " || first == "\t" {
                guard !headers.isEmpty else { continue }
                headers[headers.count - 1].value += " " + line.trimmingCharacters(in: .whitespaces)
            } else if let colon = line.firstIndex(of: ":") {
                let name = line[..<colon].trimmingCharacters(in: .whitespaces)
                let value = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
                if !name.isEmpty { headers.append((name, value)) }
            }
        }
        return headers
    }

    /// The parts between the "--boundary" lines.
    static func split(_ bytes: [UInt8], boundary: String) -> [[UInt8]] {
        let delimiter = Array(("--" + boundary).utf8)
        guard !delimiter.isEmpty else { return [] }
        var parts: [[UInt8]] = []
        var partStart: Int?
        var index = 0
        let count = bytes.count
        while index <= count - delimiter.count {
            let atLineStart = index == 0 || bytes[index - 1] == lf
            if atLineStart, bytes[index] == delimiter[0], matches(bytes, delimiter, at: index) {
                // The part before this delimiter ends at the line break before it.
                if let start = partStart {
                    var end = index
                    if end > start, bytes[end - 1] == lf { end -= 1 }
                    if end > start, bytes[end - 1] == cr { end -= 1 }
                    parts.append(Array(bytes[start..<max(start, end)]))
                }
                let after = index + delimiter.count
                let closing = after + 1 < count && bytes[after] == UInt8(ascii: "-") && bytes[after + 1] == UInt8(ascii: "-")
                if closing { return parts }
                // The rest of the delimiter line (transport padding) isn't part of the next part.
                var lineEnd = after
                while lineEnd < count, bytes[lineEnd] != lf { lineEnd += 1 }
                partStart = min(lineEnd + 1, count)
                index = partStart!
                continue
            }
            index += 1
        }
        // No closing delimiter (a cut-off message): what follows the last one is a part too.
        if let start = partStart, start < count { parts.append(Array(bytes[start...])) }
        return parts
    }

    private static func matches(_ bytes: [UInt8], _ pattern: [UInt8], at index: Int) -> Bool {
        guard index + pattern.count <= bytes.count else { return false }
        for offset in 0..<pattern.count where bytes[index + offset] != pattern[offset] { return false }
        return true
    }

    // MARK: Transfer encodings

    static func decodeBase64(_ bytes: [UInt8]) -> [UInt8] {
        var clean: [UInt8] = []
        clean.reserveCapacity(bytes.count)
        for byte in bytes {
            switch byte {
            case UInt8(ascii: "A")...UInt8(ascii: "Z"), UInt8(ascii: "a")...UInt8(ascii: "z"),
                 UInt8(ascii: "0")...UInt8(ascii: "9"), UInt8(ascii: "+"), UInt8(ascii: "/"):
                clean.append(byte)
            case UInt8(ascii: "-"): clean.append(UInt8(ascii: "+"))   // base64url, seen in the wild
            case UInt8(ascii: "_"): clean.append(UInt8(ascii: "/"))
            default: break   // line breaks, padding, junk
            }
        }
        // A last group of one character can't encode a byte.
        if clean.count % 4 == 1 { clean.removeLast() }
        while clean.count % 4 != 0 { clean.append(UInt8(ascii: "=")) }
        return Data(base64Encoded: Data(clean)).map { [UInt8]($0) } ?? []
    }

    /// Quoted-printable; in a header's encoded word (`header`), "_" is a space.
    static func decodeQuotedPrintable(_ bytes: [UInt8], header: Bool) -> [UInt8] {
        var out: [UInt8] = []
        out.reserveCapacity(bytes.count)
        var index = 0
        while index < bytes.count {
            let byte = bytes[index]
            if byte == UInt8(ascii: "=") {
                // A soft line break: "=" at the end of a line (maybe with trailing spaces).
                var next = index + 1
                while next < bytes.count, bytes[next] == UInt8(ascii: " ") || bytes[next] == UInt8(ascii: "\t") {
                    next += 1
                }
                if next < bytes.count, bytes[next] == cr { next += 1 }
                if next < bytes.count, bytes[next] == lf {
                    index = next + 1
                    continue
                }
                if next >= bytes.count { break }
                if index + 2 < bytes.count, let high = hex(bytes[index + 1]), let low = hex(bytes[index + 2]) {
                    out.append(high << 4 | low)
                    index += 3
                    continue
                }
                out.append(byte)
            } else if header, byte == UInt8(ascii: "_") {
                out.append(UInt8(ascii: " "))
            } else {
                out.append(byte)
            }
            index += 1
        }
        return out
    }

    private static func hex(_ byte: UInt8) -> UInt8? {
        switch byte {
        case UInt8(ascii: "0")...UInt8(ascii: "9"): byte - UInt8(ascii: "0")
        case UInt8(ascii: "A")...UInt8(ascii: "F"): byte - UInt8(ascii: "A") + 10
        case UInt8(ascii: "a")...UInt8(ascii: "f"): byte - UInt8(ascii: "a") + 10
        default: nil
        }
    }

    // MARK: Charsets

    /// Bytes as text in `charset` (UTF-8 when it's missing or unknown, Latin-1 when they aren't UTF-8 after all).
    static func string(_ bytes: [UInt8], charset: String?) -> String {
        if let charset, let encoding = encoding(charset), let text = String(bytes: bytes, encoding: encoding) {
            return text
        }
        if let text = String(bytes: bytes, encoding: .utf8) { return text }
        // Windows-1252 first: it's what "Latin-1" mail usually really is (curly quotes, €).
        return String(bytes: bytes, encoding: .windowsCP1252) ?? String(bytes: bytes, encoding: .isoLatin1) ?? ""
    }

    static func encoding(_ charset: String) -> String.Encoding? {
        let name = charset.trimmingCharacters(in: CharacterSet(charactersIn: "\"' ")).lowercased()
        switch name {
        case "utf-8", "utf8": return .utf8
        case "us-ascii", "ascii": return .utf8   // a superset, and mailers label UTF-8 as ASCII
        case "iso-8859-1", "latin1", "latin-1": return .windowsCP1252
        default: break
        }
        let cf = CFStringConvertIANACharSetNameToEncoding(name as CFString)
        guard cf != kCFStringEncodingInvalidId else { return nil }
        return String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(cf))
    }

    // MARK: Encoded words and parameters

    private static let wordPattern = try! NSRegularExpression(pattern: #"=\?([^?\s]+)\?([BbQq])\?([^?]*)\?="#)

    /// "=?UTF-8?Q?Caf=C3=A9?= au lait" → "Café au lait". The space between two encoded words isn't text (RFC 2047).
    static func decodeWords(_ text: String) -> String {
        guard text.contains("=?") else { return text }
        let nsText = text as NSString
        let matches = wordPattern.matches(in: text, range: NSRange(location: 0, length: nsText.length))
        guard !matches.isEmpty else { return text }
        var result = ""
        var last = 0
        var previousWasWord = false
        for match in matches {
            let between = nsText.substring(with: NSRange(location: last, length: match.range.location - last))
            if !(previousWasWord && between.allSatisfy(\.isWhitespace)) { result += between }
            var charset = nsText.substring(with: match.range(at: 1))
            if let star = charset.firstIndex(of: "*") { charset = String(charset[..<star]) }   // RFC 2231 language
            let mode = nsText.substring(with: match.range(at: 2)).uppercased()
            let payload = Array(nsText.substring(with: match.range(at: 3)).utf8)
            let bytes = mode == "B" ? decodeBase64(payload) : decodeQuotedPrintable(payload, header: true)
            result += string(bytes, charset: charset)
            last = match.range.location + match.range.length
            previousWasWord = true
        }
        result += nsText.substring(from: last)
        return result
    }

    /// `text/plain; charset="utf-8"; name*0*=utf-8''r%C3%A9; name*1*=sum%C3%A9.pdf` → ("text/plain", ["charset":
    /// "utf-8", "name": "résumé.pdf"]). Keys are lowercased.
    static func parameters(_ header: String) -> (value: String, parameters: [String: String]) {
        var pieces: [String] = []
        var current = ""
        var quoted = false
        var escaped = false
        for character in header {
            if escaped {
                current.append(character)
                escaped = false
            } else if character == "\\" && quoted {
                escaped = true
            } else if character == "\"" {
                quoted.toggle()
            } else if character == ";" && !quoted {
                pieces.append(current)
                current = ""
            } else {
                current.append(character)
            }
        }
        pieces.append(current)
        let value = pieces.first?.trimmingCharacters(in: .whitespaces) ?? ""

        var plain: [String: String] = [:]
        // RFC 2231: name*=charset'lang'value, and name*0, name*1*… for a value cut into pieces.
        var extended: [String: [(index: Int, value: String, encoded: Bool)]] = [:]
        for piece in pieces.dropFirst() {
            guard let equals = piece.firstIndex(of: "=") else { continue }
            let key = piece[..<equals].trimmingCharacters(in: .whitespaces).lowercased()
            let raw = piece[piece.index(after: equals)...].trimmingCharacters(in: .whitespaces)
            guard !key.isEmpty else { continue }
            if let star = key.firstIndex(of: "*") {
                let name = String(key[..<star])
                let rest = key[key.index(after: star)...]   // "", "0", "0*", "1"
                let encoded = rest.isEmpty || rest.hasSuffix("*")
                let number = Int(rest.trimmingCharacters(in: CharacterSet(charactersIn: "*"))) ?? 0
                extended[name, default: []].append((number, raw, encoded))
            } else {
                plain[key] = raw
            }
        }
        for (name, segments) in extended {
            var charset: String?
            var bytes: [UInt8] = []
            for (offset, piece) in segments.sorted(by: { $0.index < $1.index }).enumerated() {
                var text = piece.value
                if piece.encoded {
                    if offset == 0 {
                        let parts = text.split(separator: "'", maxSplits: 2, omittingEmptySubsequences: false)
                        if parts.count == 3 {
                            charset = String(parts[0])
                            text = String(parts[2])
                        }
                    }
                    bytes += percentDecoded(text)
                } else {
                    bytes += Array(text.utf8)
                }
            }
            plain[name] = string(bytes, charset: charset)
        }
        return (value, plain)
    }

    private static func percentDecoded(_ text: String) -> [UInt8] {
        let input = Array(text.utf8)
        var out: [UInt8] = []
        var index = 0
        while index < input.count {
            if input[index] == UInt8(ascii: "%"), index + 2 < input.count,
               let high = hex(input[index + 1]), let low = hex(input[index + 2]) {
                out.append(high << 4 | low)
                index += 3
            } else {
                out.append(input[index])
                index += 1
            }
        }
        return out
    }
}
