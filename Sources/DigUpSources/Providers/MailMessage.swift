import Foundation

/// A mail as DigUp keeps it: who, when, what about, its text, and its attachments.
struct MailMessage {
    struct Attachment {
        var filename: String
        var bytes: [UInt8]
    }

    var subject = ""
    var from = ""
    var to = ""
    var cc = ""
    var date: Date?
    var messageID: String?
    var text = ""
    var attachments: [Attachment] = []

    /// The sender's name, or their address when the header gives no name: "Jean Dupont" for
    /// "\"Jean Dupont\" <jean@example.com>".
    var senderName: String { Self.displayName(from) }

    /// Reads Apple Mail's .emlx: a line with the message's length in bytes, the message (RFC 822), then a property list
    /// (flags, the date it was received).
    init?(emlx data: Data) {
        let bytes = [UInt8](data)
        guard let newline = bytes.firstIndex(of: MIME.lf),
              let length = Int(String(decoding: bytes[0..<newline], as: UTF8.self)
                .trimmingCharacters(in: .whitespaces)),
              length > 0 else { return nil }
        let start = newline + 1
        let end = min(bytes.count, start + length)
        self.init(rfc822: Array(bytes[start..<end]))
        if date == nil, end < bytes.count,
           let plist = try? PropertyListSerialization.propertyList(from: Data(bytes[end...]), format: nil)
            as? [String: Any] {
            if let received = plist["date-received"] as? Double ?? (plist["date-received"] as? Int).map(Double.init) {
                date = Date(timeIntervalSince1970: received)
            } else if let sent = plist["date-sent"] as? Double ?? (plist["date-sent"] as? Int).map(Double.init) {
                date = Date(timeIntervalSince1970: sent)
            }
        }
    }

    init(rfc822 bytes: [UInt8]) {
        let message = MIME.parse(bytes)
        subject = message.header("Subject")?.trimmingCharacters(in: .whitespaces) ?? ""
        from = message.header("From") ?? ""
        to = message.header("To") ?? ""
        cc = message.header("Cc") ?? ""
        date = message.header("Date").flatMap(Self.parseDate)
        messageID = message.header("Message-ID").map {
            $0.trimmingCharacters(in: CharacterSet(charactersIn: "<> \t"))
        }.flatMap { $0.isEmpty ? nil : $0 }
        var texts: [String] = []
        collect(message, texts: &texts, depth: 0)
        let joined = texts.joined(separator: "\n\n")
            .replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        text = Self.withoutQuotedReplies(joined)
        if text.count > 200_000 { text = String(text.prefix(200_000)) }
    }

    /// Walks the parts: the text you read (plain text, or HTML when that's all there is) and the attachments.
    private mutating func collect(_ part: MIMEPart, texts: inout [String], depth: Int) {
        guard depth < 12 else { return }
        let type = part.contentType.type
        if part.isAttachment, !type.hasPrefix("multipart/"), type != "message/rfc822" {
            if let name = part.filename {
                let bytes = part.decodedBody
                if !bytes.isEmpty { attachments.append(Attachment(filename: name, bytes: bytes)) }
            }
            return
        }
        switch type {
        case "multipart/alternative":
            let parts = part.subparts
            // The plain text when it says something; else the richest part (HTML, or a related group around it).
            if let plain = parts.first(where: { $0.contentType.type == "text/plain" && !$0.isAttachment }),
               plain.text.trimmingCharacters(in: .whitespacesAndNewlines).count > 20 {
                texts.append(plain.text)
                // Attachments can hide in the other alternatives (a related part's pictures): keep those.
                for other in parts where other.contentType.type.hasPrefix("multipart/") {
                    var ignored: [String] = []
                    collect(other, texts: &ignored, depth: depth + 1)
                }
            } else if let last = parts.last {
                collect(last, texts: &texts, depth: depth + 1)
                for other in parts.dropLast() where other.contentType.type.hasPrefix("multipart/") {
                    var ignored: [String] = []
                    collect(other, texts: &ignored, depth: depth + 1)
                }
            }
        case let multipart where multipart.hasPrefix("multipart/"):
            for child in part.subparts { collect(child, texts: &texts, depth: depth + 1) }
        case "message/rfc822":
            // A forwarded mail: its text belongs to this one.
            let inner = MailMessage(rfc822: part.decodedBody)
            var header = "Forwarded message"
            if !inner.from.isEmpty { header += "\nFrom: \(inner.from)" }
            if !inner.subject.isEmpty { header += "\nSubject: \(inner.subject)" }
            texts.append(header + "\n\n" + inner.text)
            attachments += inner.attachments
        case "text/plain":
            texts.append(part.text)
        case "text/html":
            texts.append(Self.plainText(fromHTML: part.text))
        case "text/calendar":
            break   // invitations: the event is in Calendar
        default:
            // An inline picture or file without a name: nothing to show for it.
            break
        }
    }

    // MARK: Helpers

    static func displayName(_ address: String) -> String {
        let first = address.split(separator: ",").first.map(String.init) ?? address
        if let open = first.firstIndex(of: "<") {
            let name = first[..<open].trimmingCharacters(in: CharacterSet(charactersIn: "\"' \t"))
            if !name.isEmpty { return name }
            let close = first.firstIndex(of: ">") ?? first.endIndex
            return String(first[first.index(after: open)..<close])
        }
        return first.trimmingCharacters(in: .whitespaces)
    }

    private static let dateFormatters: [DateFormatter] = [
        "EEE, d MMM yyyy HH:mm:ss Z", "d MMM yyyy HH:mm:ss Z", "EEE, d MMM yyyy HH:mm Z", "d MMM yyyy HH:mm Z",
        "EEE, d MMM yy HH:mm:ss Z", "EEE, d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm:ss zzz",
    ].map { format in
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = format
        return formatter
    }

    /// RFC 2822 dates as mailers really write them: with or without the weekday or seconds, a zone name or a
    /// "(CET)" comment.
    static func parseDate(_ text: String) -> Date? {
        var cleaned = text.replacingOccurrences(of: #"\([^)]*\)"#, with: "", options: .regularExpression)
            .trimmingCharacters(in: .whitespaces)
        cleaned = cleaned.replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
        for formatter in dateFormatters {
            if let date = formatter.date(from: cleaned) { return date }
        }
        return nil
    }

    /// Replies quote the whole thread below "On … wrote:"; that's the earlier mails again, which are indexed on their
    /// own. Lines quoted with ">" go too.
    static func withoutQuotedReplies(_ text: String) -> String {
        var kept: [Substring] = []
        for line in text.split(separator: "\n", omittingEmptySubsequences: false) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix(">") { continue }
            if isReplyMarker(trimmed) { break }
            kept.append(line)
        }
        // Runs of empty lines (HTML leaves many) become one.
        var result: [Substring] = []
        for line in kept {
            let empty = line.trimmingCharacters(in: .whitespaces).isEmpty
            if empty, let last = result.last, last.trimmingCharacters(in: .whitespaces).isEmpty { continue }
            result.append(line)
        }
        return result.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static let replyMarkers = try! NSRegularExpression(
        pattern: #"^(On .{4,200}wrote ?:|Le .{4,200}a écrit ?:|Am .{4,200}schrieb .{0,100}:|El .{4,200}escribió ?:|"#
            + #"-{2,} ?(Original Message|Message d'origine|Message original|Ursprüngliche Nachricht) ?-{2,})$"#,
        options: [.caseInsensitive])

    static func isReplyMarker(_ line: String) -> Bool {
        guard line.count >= 10, line.count <= 300 else { return false }
        return replyMarkers.firstMatch(in: line, range: NSRange(location: 0, length: (line as NSString).length)) != nil
    }

    /// Good enough for search: drops styles, scripts and comments, turns block ends into line breaks, strips tags and
    /// decodes entities.
    static func plainText(fromHTML html: String) -> String {
        var text = html
        for pattern in [#"(?is)<script\b.*?</script\s*>"#, #"(?is)<style\b.*?</style\s*>"#, #"(?s)<!--.*?-->"#,
                        #"(?is)<head\b.*?</head\s*>"#, #"(?is)<blockquote\b.*?</blockquote\s*>"#] {
            text = text.replacingOccurrences(of: pattern, with: " ", options: .regularExpression)
        }
        text = text.replacingOccurrences(of: #"(?i)<(br|/p|/div|/li|/h[1-6]|/tr|/table|/section|/article)\b[^>]*>"#,
                                         with: "\n", options: .regularExpression)
        text = text.replacingOccurrences(of: #"(?i)</t[dh]\s*>"#, with: " \t", options: .regularExpression)
        text = text.replacingOccurrences(of: #"<[^>]+>"#, with: " ", options: .regularExpression)
        text = decodeEntities(text)
        text = text.replacingOccurrences(of: #"[ \t ]+"#, with: " ", options: .regularExpression)
        text = text.replacingOccurrences(of: #" ?\n ?"#, with: "\n", options: .regularExpression)
        return text
    }

    private static let namedEntities: [String: String] = [
        "nbsp": " ", "amp": "&", "lt": "<", "gt": ">", "quot": "\"", "apos": "'", "eacute": "é", "egrave": "è",
        "ecirc": "ê", "agrave": "à", "acirc": "â", "ccedil": "ç", "ocirc": "ô", "ucirc": "û", "ugrave": "ù",
        "icirc": "î", "iuml": "ï", "euml": "ë", "Eacute": "É", "Egrave": "È", "Agrave": "À", "Ccedil": "Ç",
        "rsquo": "’", "lsquo": "‘", "rdquo": "”", "ldquo": "“", "hellip": "…", "ndash": "–", "mdash": "—",
        "euro": "€", "laquo": "«", "raquo": "»", "copy": "©", "reg": "®", "trade": "™", "deg": "°", "zwnj": "",
        "shy": "",
    ]

    private static let entityPattern = try! NSRegularExpression(pattern: #"&(#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[a-zA-Z]{2,8});"#)

    static func decodeEntities(_ text: String) -> String {
        guard text.contains("&") else { return text }
        let nsText = text as NSString
        var result = ""
        var last = 0
        for match in entityPattern.matches(in: text, range: NSRange(location: 0, length: nsText.length)) {
            result += nsText.substring(with: NSRange(location: last, length: match.range.location - last))
            let name = nsText.substring(with: match.range(at: 1))
            var replacement: String?
            if name.hasPrefix("#x") || name.hasPrefix("#X") {
                replacement = UInt32(name.dropFirst(2), radix: 16).flatMap(Unicode.Scalar.init).map { String($0) }
            } else if name.hasPrefix("#") {
                replacement = UInt32(name.dropFirst()).flatMap(Unicode.Scalar.init).map { String($0) }
            } else {
                replacement = namedEntities[name]
            }
            result += replacement ?? nsText.substring(with: match.range)
            last = match.range.location + match.range.length
        }
        result += nsText.substring(from: last)
        return result
    }
}
