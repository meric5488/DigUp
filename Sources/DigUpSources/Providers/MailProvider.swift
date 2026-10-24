import Foundation

/// Apple Mail, from its own store in ~/Library/Mail (every account Mail has: iCloud, Gmail, Exchange…).
///
/// Mail keeps each message as an .emlx file. Reading them needs Full Disk Access, and nothing else: no account
/// password, no network, and it sees what Mail has downloaded. Each mail becomes a text file (subject, people, date,
/// text) named after its date, subject and sender; its attachments become files of their own beside it.
final class MailProvider: SourceProvider {
    let kind = SourceKind.mail

    static let mailDirectory = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Mail", isDirectory: true)

    /// Mailboxes never read: what you threw away or never sent, and Gmail's labels that hold copies of everything.
    static let skippedMailboxes: Set<String> = [
        "drafts", "brouillons", "outbox", "boîte d'envoi", "all mail", "tous les messages", "important", "starred",
        "suivis", "notes",
    ]
    static let junkAndTrash: Set<String> = [
        "junk", "junk e-mail", "junk email", "spam", "courrier indésirable", "indésirables", "trash",
        "deleted messages", "deleted items", "corbeille", "éléments supprimés", "bin", "papierkorb",
    ]

    /// Attachment types the engine reads.
    static let attachmentExtensions: Set<String> = [
        "pdf", "docx", "doc", "odt", "rtf", "txt", "md", "xlsx", "pptx", "ods", "odp",
        "jpg", "jpeg", "png", "heic", "heif", "tif", "tiff", "webp", "m4a", "mp3", "wav", "aac", "aiff", "mp4", "mov",
        "m4v",
    ]

    func accessProblem() -> String? {
        do {
            _ = try FileManager.default.contentsOfDirectory(atPath: Self.mailDirectory.path)
        } catch {
            return "Give DigUp Full Disk Access (System Settings → Privacy & Security → Full Disk Access) to read Mail."
        }
        if versionFolder() == nil { return "Mail has no messages on this Mac yet." }
        return nil
    }

    /// Mail's store: ~/Library/Mail/V10 (the number goes up with macOS versions; the newest one is in use).
    func versionFolder() -> URL? {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: Self.mailDirectory.path)) ?? []
        let versions = names.compactMap { name -> (Int, String)? in
            guard name.hasPrefix("V"), let number = Int(name.dropFirst()) else { return nil }
            return (number, name)
        }
        return versions.max { $0.0 < $1.0 }.map { Self.mailDirectory.appendingPathComponent($0.1, isDirectory: true) }
    }

    private struct Found {
        let url: URL
        let id: String          // the path under the store, which says where the mail is
        let mailbox: String     // "INBOX", "Archive › 2023"
        let modified: Date
        let size: Int
    }

    func sync(_ context: SyncContext) throws {
        guard let store = versionFolder() else { throw SourceError("Mail has no messages on this Mac yet") }
        let options = context.options
        let found = list(store, options: options)
        context.setTotal(found.count)
        let cutoff = options.mailYears > 0
            ? Calendar.current.date(byAdding: .year, value: -options.mailYears, to: Date()) : nil
        // What changes the files written for a mail (so changing it writes them again).
        let settings = "y\(options.mailYears)a\(options.mailAttachments ? 1 : 0)"
        for mail in found {
            if context.cancelled { return }
            let stamp = "\(Int(mail.modified.timeIntervalSince1970))-\(mail.size)-\(settings)"
            guard context.needsWriting(mail.id, stamp: stamp) else { continue }
            // A file last written before the cutoff holds a mail received before it.
            if let cutoff, mail.modified < cutoff {
                context.writer.put(nativeID: mail.id, stamp: stamp, files: [])
                continue
            }
            autoreleasepool {
                let files = materialize(mail, cutoff: cutoff, attachments: options.mailAttachments)
                context.writer.put(nativeID: mail.id, stamp: stamp, files: files)
            }
        }
        context.removeUnseen()
    }

    /// Every .emlx in the mailboxes that are read.
    private func list(_ store: URL, options: SourceOptions) -> [Found] {
        let keys: [URLResourceKey] = [.isDirectoryKey, .contentModificationDateKey, .fileSizeKey]
        guard let walker = FileManager.default.enumerator(at: store, includingPropertiesForKeys: keys,
                                                          options: [.skipsHiddenFiles],
                                                          errorHandler: { _, _ in true }) else { return [] }
        let storePath = store.standardizedFileURL.path
        var found: [Found] = []
        while let url = walker.nextObject() as? URL {
            autoreleasepool {
                let values = try? url.resourceValues(forKeys: Set(keys))
                let name = url.lastPathComponent
                if values?.isDirectory == true {
                    if name == "MailData" || name == "Attachments" {
                        walker.skipDescendants()
                    } else if name.hasSuffix(".mbox") {
                        let mailbox = String(name.dropLast(5)).lowercased()
                        if Self.skippedMailboxes.contains(mailbox)
                            || (!options.mailJunkAndTrash && Self.junkAndTrash.contains(mailbox)) {
                            walker.skipDescendants()
                        }
                    }
                    return
                }
                guard name.hasSuffix(".emlx") else { return }
                let path = url.standardizedFileURL.path
                guard path.hasPrefix(storePath + "/") else { return }
                let id = String(path.dropFirst(storePath.count + 1))
                found.append(Found(url: url, id: id, mailbox: Self.mailbox(of: id),
                                   modified: values?.contentModificationDate ?? .distantPast,
                                   size: values?.fileSize ?? 0))
            }
        }
        // Newest first: the mail you're most likely to look for is searchable soonest.
        return found.sorted { $0.modified > $1.modified }
    }

    /// "<account>/Archive.mbox/2023.mbox/<id>/Data/…/Messages/1234.emlx" → "Archive › 2023".
    static func mailbox(of id: String) -> String {
        let names = id.split(separator: "/").filter { $0.hasSuffix(".mbox") }.map { String($0.dropLast(5)) }
        return names.isEmpty ? "Mail" : names.joined(separator: " › ")
    }

    private func materialize(_ mail: Found, cutoff: Date?, attachments wantsAttachments: Bool) -> [SourceFile] {
        guard let data = try? Data(contentsOf: mail.url, options: .mappedIfSafe),
              let message = MailMessage(emlx: data) else {
            SourceLog.write("mail: can't read \(mail.id)")
            return []
        }
        let date = message.date ?? mail.modified
        if let cutoff, date < cutoff { return [] }

        let subject = message.subject.isEmpty ? "(no subject)" : message.subject
        let sender = message.senderName
        let day = SourceNames.day(date)
        let folders = [SourceNames.folder(mail.mailbox), SourceNames.year(date)]
        let openURL = message.messageID.flatMap(Self.link)
        let item = SourceItem(kind: .mail, nativeID: mail.id, title: subject,
                              subtitle: ["Mail", mail.mailbox, sender].filter { !$0.isEmpty }.joined(separator: " · "),
                              openURL: openURL, date: date)

        var names: [String] = []
        var files: [SourceFile] = []
        if wantsAttachments {
            for attachment in message.attachments where Self.isWorthKeeping(attachment.filename, size: attachment.bytes.count) {
                files.append(attachmentFile(attachment.filename, content: .data(Data(attachment.bytes)), day: day,
                                            folders: folders, subject: subject, sender: sender, mail: mail.id,
                                            openURL: openURL, date: date))
                names.append(attachment.filename)
            }
            // A mail Mail downloaded in part keeps its attachments in Attachments/<number>/ beside Messages/.
            for (name, url) in Self.partialAttachments(of: mail.url) where !names.contains(name) {
                let size = (try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0
                guard Self.isWorthKeeping(name, size: size) else { continue }
                files.append(attachmentFile(name, content: .copy(url), day: day, folders: folders, subject: subject,
                                            sender: sender, mail: mail.id, openURL: openURL, date: date))
                names.append(name)
            }
        }

        var text = subject + "\n"
        if !message.from.isEmpty { text += "From: \(message.from)\n" }
        if !message.to.isEmpty { text += "To: \(message.to)\n" }
        if !message.cc.isEmpty { text += "Cc: \(message.cc)\n" }
        text += "Date: \(SourceNames.readable(date))\n"
        text += "Mailbox: \(mail.mailbox)\n"
        if !names.isEmpty { text += "Attachments: \(names.joined(separator: ", "))\n" }
        text += "\n" + message.text + "\n"
        let name = SourceNames.file("\(day) \(subject) - \(sender)", extension: "txt")
        files.insert(SourceFile(folders: folders, name: name, content: .text(text), item: item), at: 0)
        return files
    }

    private func attachmentFile(_ filename: String, content: SourceFile.Content, day: String, folders: [String],
                                subject: String, sender: String, mail: String, openURL: String?, date: Date) -> SourceFile {
        let ext = (filename as NSString).pathExtension.lowercased()
        let stem = (filename as NSString).deletingPathExtension
        let item = SourceItem(kind: .mail, nativeID: mail, title: filename,
                              subtitle: "Mail attachment · \(subject) · \(sender)", openURL: openURL, date: date)
        return SourceFile(folders: folders + ["Attachments"], name: SourceNames.file("\(day) \(stem)", extension: ext),
                          content: content, item: item)
    }

    /// Small pictures in mail are logos, signatures and tracking pixels; documents and recordings are kept from 1 KB.
    static func isWorthKeeping(_ filename: String, size: Int) -> Bool {
        let ext = (filename as NSString).pathExtension.lowercased()
        guard attachmentExtensions.contains(ext) else { return false }
        let pictures: Set<String> = ["jpg", "jpeg", "png", "heic", "heif", "tif", "tiff", "webp"]
        return size >= (pictures.contains(ext) ? 40_000 : 1_000)
    }

    static func partialAttachments(of message: URL) -> [(String, URL)] {
        let file = message.lastPathComponent
        guard file.hasSuffix(".partial.emlx") else { return [] }
        let number = String(file.dropLast(".partial.emlx".count))
        let folder = message.deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Attachments/\(number)", isDirectory: true)
        guard let walker = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: [.isRegularFileKey],
                                                          options: [.skipsHiddenFiles]) else { return [] }
        var found: [(String, URL)] = []
        while let url = walker.nextObject() as? URL {
            if (try? url.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true {
                found.append((url.lastPathComponent, url))
            }
        }
        return found
    }

    /// Mail's own link to a message: message://%3C<Message-ID>%3E.
    static func link(_ messageID: String) -> String? {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "@.-_+=$")
        guard let encoded = messageID.addingPercentEncoding(withAllowedCharacters: allowed) else { return nil }
        return "message://%3C\(encoded)%3E"
    }
}
