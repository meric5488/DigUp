import Compression
import DigUpKit
import Foundation

/// Apple Notes, from its own database (every account Notes has: iCloud, On My Mac, Gmail…).
///
/// Notes keeps everything in NoteStore.sqlite, each note's text gzipped protocol buffers. Reading it needs Full Disk
/// Access. Each note becomes a text file named after its title, in a folder named after its Notes folder; its pictures,
/// PDFs and recordings become files beside it. Locked notes and Recently Deleted are left out.
final class NotesProvider: SourceProvider {
    let kind = SourceKind.notes

    static let container = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Group Containers/group.com.apple.notes", isDirectory: true)
    static var database: URL { container.appendingPathComponent("NoteStore.sqlite") }

    static let attachmentExtensions: Set<String> = [
        "jpg", "jpeg", "png", "heic", "heif", "tif", "tiff", "webp", "gif", "pdf", "m4a", "mp3", "wav", "aac", "mov",
        "mp4", "m4v", "docx", "xlsx", "pptx", "txt", "md", "rtf",
    ]

    func accessProblem() -> String? {
        let fm = FileManager.default
        guard fm.fileExists(atPath: Self.container.path) || fm.fileExists(atPath: Self.database.path) else {
            return "Notes has no notes on this Mac yet."
        }
        guard let handle = try? FileHandle(forReadingFrom: Self.database) else {
            return "Give Fouineur Full Disk Access (System Settings → Privacy & Security → Full Disk Access) to read Notes."
        }
        try? handle.close()
        return nil
    }

    private struct Note {
        let pk: Int64
        let identifier: String
        let title: String
        let folder: String
        let modified: Date
        let created: Date?
        let data: Data?
    }

    private struct Media {
        let identifier: String
        let filename: String
    }

    func sync(_ context: SyncContext) throws {
        let copy = try copyDatabase()
        defer { try? FileManager.default.removeItem(at: copy.deletingLastPathComponent()) }
        let db = try Database(path: copy.path)
        let notesTable = try columns(db, "ZICCLOUDSYNCINGOBJECT")
        let storeUUID = try? first(db, "SELECT Z_UUID FROM Z_METADATA LIMIT 1")

        let notes = try readNotes(db, columns: notesTable)
        let media = (try? readMedia(db, columns: notesTable)) ?? [:]
        context.setTotal(notes.count)
        for note in notes {
            if context.cancelled { return }
            let pictures = media[note.pk] ?? []
            // A note's attachments can change without the note's date (a picture finishes downloading).
            let stamp = "\(note.modified.timeIntervalSinceReferenceDate)-\(pictures.count)"
            guard context.needsWriting(note.identifier, stamp: stamp) else { continue }
            autoreleasepool {
                let files = materialize(note, media: pictures, storeUUID: storeUUID)
                context.writer.put(nativeID: note.identifier, stamp: stamp, files: files)
            }
        }
        context.removeUnseen()
    }

    /// Notes has the database open (and writes it as it syncs): DigUp reads a copy, with its write-ahead log.
    private func copyDatabase() throws -> URL {
        let fm = FileManager.default
        let folder = fm.temporaryDirectory.appendingPathComponent("DigUpNotes-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        let target = folder.appendingPathComponent("NoteStore.sqlite")
        do {
            try fm.copyItem(at: Self.database, to: target)
        } catch {
            throw SourceError("Can't read Notes' database. Give Fouineur Full Disk Access.")
        }
        for suffix in ["-wal", "-shm"] {
            let source = URL(fileURLWithPath: Self.database.path + suffix)
            if fm.fileExists(atPath: source.path) {
                try? fm.copyItem(at: source, to: URL(fileURLWithPath: target.path + suffix))
            }
        }
        return target
    }

    private func columns(_ db: Database, _ table: String) throws -> Set<String> {
        var names = Set<String>()
        try db.query("PRAGMA table_info(\(table))") { row in
            if let name = row.text(1) { names.insert(name) }
        }
        return names
    }

    private func first(_ db: Database, _ sql: String) throws -> String? {
        var value: String?
        try db.query(sql) { row in value = row.text(0) }
        return value
    }

    /// The column names change between macOS versions (ZTITLE1, ZCREATIONDATE3…): each is used if it's there.
    private func readNotes(_ db: Database, columns: Set<String>) throws -> [Note] {
        guard columns.contains("ZTITLE1"), columns.contains("ZIDENTIFIER") else {
            throw SourceError("This version of Notes stores notes in a way Fouineur doesn't know yet")
        }
        let modified = ["ZMODIFICATIONDATE1", "ZMODIFICATIONDATE"].first(where: columns.contains) ?? "NULL"
        let created = ["ZCREATIONDATE3", "ZCREATIONDATE2", "ZCREATIONDATE1", "ZCREATIONDATE"]
            .first(where: columns.contains) ?? "NULL"
        var conditions = ["n.ZTITLE1 IS NOT NULL"]
        if columns.contains("ZISPASSWORDPROTECTED") { conditions.append("IFNULL(n.ZISPASSWORDPROTECTED, 0) = 0") }
        if columns.contains("ZMARKEDFORDELETION") { conditions.append("IFNULL(n.ZMARKEDFORDELETION, 0) = 0") }
        // Folder type 1 is Recently Deleted.
        if columns.contains("ZFOLDERTYPE") { conditions.append("IFNULL(f.ZFOLDERTYPE, 0) <> 1") }
        let folderTitle = columns.contains("ZTITLE2") ? "f.ZTITLE2" : "NULL"
        let folderJoin = columns.contains("ZFOLDER") ? "LEFT JOIN ZICCLOUDSYNCINGOBJECT f ON f.Z_PK = n.ZFOLDER"
            : "LEFT JOIN ZICCLOUDSYNCINGOBJECT f ON 0"
        let sql = """
            SELECT n.Z_PK, n.ZIDENTIFIER, n.ZTITLE1, \(folderTitle), n.\(modified), n.\(created), d.ZDATA
            FROM ZICCLOUDSYNCINGOBJECT n
            JOIN ZICNOTEDATA d ON d.ZNOTE = n.Z_PK
            \(folderJoin)
            WHERE \(conditions.joined(separator: " AND "))
            ORDER BY n.\(modified) DESC
            """
        var notes: [Note] = []
        try db.query(sql.replacingOccurrences(of: "n.NULL", with: "NULL")) { row in
            guard let identifier = row.text(1) else { return }
            notes.append(Note(pk: row.int(0), identifier: identifier, title: row.text(2) ?? "",
                              folder: row.text(3) ?? "Notes",
                              modified: Date(timeIntervalSinceReferenceDate: row.isNull(4) ? 0 : row.double(4)),
                              created: row.isNull(5) ? nil : Date(timeIntervalSinceReferenceDate: row.double(5)),
                              data: row.blob(6)))
        }
        return notes
    }

    /// Each note's attachments that have a file (pictures, scans, PDFs, recordings), by the note's row.
    private func readMedia(_ db: Database, columns: Set<String>) throws -> [Int64: [Media]] {
        guard columns.contains("ZNOTE"), columns.contains("ZMEDIA"), columns.contains("ZFILENAME") else { return [:] }
        var media: [Int64: [Media]] = [:]
        try db.query("""
            SELECT a.ZNOTE, m.ZIDENTIFIER, m.ZFILENAME
            FROM ZICCLOUDSYNCINGOBJECT a JOIN ZICCLOUDSYNCINGOBJECT m ON m.Z_PK = a.ZMEDIA
            WHERE a.ZNOTE IS NOT NULL AND m.ZIDENTIFIER IS NOT NULL AND m.ZFILENAME IS NOT NULL
            """) { row in
            guard let identifier = row.text(1), let filename = row.text(2) else { return }
            media[row.int(0), default: []].append(Media(identifier: identifier, filename: filename))
        }
        return media
    }

    private func materialize(_ note: Note, media: [Media], storeUUID: String?) -> [SourceFile] {
        let body = note.data.flatMap(Self.noteText) ?? ""
        let title = note.title.isEmpty ? "Untitled note" : note.title
        let folders = [SourceNames.folder(note.folder)]
        let item = SourceItem(kind: .notes, nativeID: note.identifier, title: title,
                              subtitle: "Notes · \(note.folder) · \(SourceNames.readable(note.modified, time: false))",
                              openURL: "notes://showNote?identifier=\(note.identifier)",
                              openScript: storeUUID.map {
                                  """
                                  tell application "Notes"
                                      show note id "x-coredata://\($0)/ICNote/p\(note.pk)"
                                      activate
                                  end tell
                                  """
                              }, date: note.modified)

        var files: [SourceFile] = []
        var names: [String] = []
        for picture in media {
            let ext = (picture.filename as NSString).pathExtension.lowercased()
            guard Self.attachmentExtensions.contains(ext), let url = Self.mediaFile(picture) else { continue }
            let stem = (picture.filename as NSString).deletingPathExtension
            let attachment = SourceItem(kind: .notes, nativeID: note.identifier, title: picture.filename,
                                        subtitle: "Notes attachment · \(title)", openURL: item.openURL,
                                        openScript: item.openScript, date: note.modified)
            files.append(SourceFile(folders: folders + ["Attachments"],
                                    name: SourceNames.file("\(title) - \(stem)", extension: ext),
                                    content: .copy(url), item: attachment))
            names.append(picture.filename)
        }

        var text = title + "\n"
        text += "Folder: \(note.folder)\n"
        text += "Modified: \(SourceNames.readable(note.modified))\n"
        if let created = note.created { text += "Created: \(SourceNames.readable(created))\n" }
        if !names.isEmpty { text += "Attachments: \(names.joined(separator: ", "))\n" }
        // The note's text starts with its title: no need to say it twice.
        var content = body.replacingOccurrences(of: "\u{FFFC}", with: "")
        if content.hasPrefix(note.title) { content = String(content.dropFirst(note.title.count)) }
        text += "\n" + content.trimmingCharacters(in: .whitespacesAndNewlines) + "\n"
        files.insert(SourceFile(folders: folders, name: SourceNames.file(title, extension: "txt"),
                                content: .text(text), item: item), at: 0)
        return files
    }

    /// Accounts/<account>/Media/<media identifier>/[<generation>/]<file name> (older Notes: Media/<identifier>/…).
    private static func mediaFile(_ media: Media) -> URL? {
        let fm = FileManager.default
        var bases: [URL] = [container.appendingPathComponent("Media/\(media.identifier)", isDirectory: true)]
        let accounts = container.appendingPathComponent("Accounts", isDirectory: true)
        for account in (try? fm.contentsOfDirectory(atPath: accounts.path)) ?? [] {
            bases.append(accounts.appendingPathComponent("\(account)/Media/\(media.identifier)", isDirectory: true))
        }
        for base in bases where fm.fileExists(atPath: base.path) {
            let direct = base.appendingPathComponent(media.filename)
            if fm.fileExists(atPath: direct.path) { return direct }
            if let walker = fm.enumerator(at: base, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles]) {
                while let url = walker.nextObject() as? URL {
                    if url.lastPathComponent == media.filename { return url }
                }
            }
            // The file under another name (a newer generation): any file there.
            if let walker = fm.enumerator(at: base, includingPropertiesForKeys: [.isRegularFileKey],
                                          options: [.skipsHiddenFiles]) {
                while let url = walker.nextObject() as? URL {
                    if (try? url.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true { return url }
                }
            }
        }
        return nil
    }

    // MARK: The note's text

    /// ZDATA: gzip around a protocol buffer, NoteStoreProto { 2: Document { 3: Note { 2: note_text } } }.
    static func noteText(_ data: Data) -> String? {
        guard let inflated = gunzip([UInt8](data)) else { return nil }
        guard let document = Protobuf.firstField(2, in: inflated[...]),
              let note = Protobuf.firstField(3, in: document),
              let text = Protobuf.firstField(2, in: note) else { return nil }
        return String(decoding: text, as: UTF8.self)
    }

    /// gzip (RFC 1952), or zlib (RFC 1950), around a raw deflate stream.
    static func gunzip(_ bytes: [UInt8]) -> [UInt8]? {
        var start = 0, end = bytes.count
        if bytes.count >= 18, bytes[0] == 0x1F, bytes[1] == 0x8B, bytes[2] == 8 {
            let flags = bytes[3]
            start = 10
            if flags & 0x04 != 0 {   // FEXTRA
                guard start + 2 <= bytes.count else { return nil }
                start += 2 + (Int(bytes[start]) | (Int(bytes[start + 1]) << 8))
            }
            if flags & 0x08 != 0 {   // FNAME
                while start < bytes.count, bytes[start] != 0 { start += 1 }
                start += 1
            }
            if flags & 0x10 != 0 {   // FCOMMENT
                while start < bytes.count, bytes[start] != 0 { start += 1 }
                start += 1
            }
            if flags & 0x02 != 0 { start += 2 }   // FHCRC
            end = bytes.count - 8   // CRC32 and size
        } else if bytes.count >= 6, bytes[0] & 0x0F == 8, (UInt16(bytes[0]) << 8 | UInt16(bytes[1])) % 31 == 0 {
            start = 2
            end = bytes.count - 4   // Adler-32
        }
        guard start < end else { return nil }
        return inflate(Array(bytes[start..<end]))
    }

    /// Raw deflate, through the Compression framework (COMPRESSION_ZLIB is raw deflate, without zlib's header).
    static func inflate(_ input: [UInt8]) -> [UInt8]? {
        guard !input.isEmpty else { return nil }
        let stream = UnsafeMutablePointer<compression_stream>.allocate(capacity: 1)
        defer { stream.deallocate() }
        guard compression_stream_init(stream, COMPRESSION_STREAM_DECODE, COMPRESSION_ZLIB) == COMPRESSION_STATUS_OK
        else { return nil }
        defer { compression_stream_destroy(stream) }
        let chunk = 64 * 1024
        let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: chunk)
        defer { buffer.deallocate() }
        var output: [UInt8] = []
        return input.withUnsafeBufferPointer { source -> [UInt8]? in
            guard let base = source.baseAddress else { return nil }
            stream.pointee.src_ptr = base
            stream.pointee.src_size = source.count
            while true {
                stream.pointee.dst_ptr = buffer
                stream.pointee.dst_size = chunk
                let status = compression_stream_process(stream, Int32(COMPRESSION_STREAM_FINALIZE.rawValue))
                let produced = chunk - stream.pointee.dst_size
                if produced > 0 { output.append(contentsOf: UnsafeBufferPointer(start: buffer, count: produced)) }
                switch status {
                case COMPRESSION_STATUS_OK:
                    if produced == 0 && stream.pointee.src_size == 0 { return output.isEmpty ? nil : output }
                    if output.count > 64 << 20 { return output }   // a runaway: enough of it
                case COMPRESSION_STATUS_END:
                    return output
                default:
                    return output.isEmpty ? nil : output
                }
            }
        }
    }
}

/// Just enough of the protocol buffer wire format to walk to a field.
enum Protobuf {
    /// The bytes of the first length-delimited field with this number, or nil.
    static func firstField(_ number: Int, in bytes: ArraySlice<UInt8>) -> ArraySlice<UInt8>? {
        var index = bytes.startIndex
        while index < bytes.endIndex {
            guard let key = varint(bytes, &index) else { return nil }
            let field = Int(key >> 3), wire = Int(key & 7)
            switch wire {
            case 0:
                guard varint(bytes, &index) != nil else { return nil }
            case 1:
                index += 8
            case 2:
                guard let length = varint(bytes, &index), length <= UInt64(bytes.endIndex - index) else { return nil }
                let end = index + Int(length)
                if field == number { return bytes[index..<end] }
                index = end
            case 5:
                index += 4
            default:
                return nil   // groups (3, 4) aren't used by Notes
            }
        }
        return nil
    }

    static func varint(_ bytes: ArraySlice<UInt8>, _ index: inout Int) -> UInt64? {
        var result: UInt64 = 0
        var shift: UInt64 = 0
        while index < bytes.endIndex, shift < 64 {
            let byte = bytes[index]
            index += 1
            result |= UInt64(byte & 0x7F) << shift
            if byte & 0x80 == 0 { return result }
            shift += 7
        }
        return nil
    }
}
